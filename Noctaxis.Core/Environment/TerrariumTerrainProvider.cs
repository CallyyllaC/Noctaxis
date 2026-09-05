using System.Diagnostics;
using Hjg.Pngcs;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;
using NodaTime;

namespace Noctaxis.Core.Environment;

public sealed record TerrariumTerrainOptions(
    int Zoom = 12,
    int DecodedTileCapacity = 96,
    string BaseUrl = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/",
    TimeSpan? FailureRetryDelay = null)
{
    public const int TileSize = 256;
    public const double MaximumMercatorLatitude = 85.0511287798066;

    public void Validate()
    {
        if (Zoom is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(Zoom));
        if (DecodedTileCapacity < 1) throw new ArgumentOutOfRangeException(nameof(DecodedTileCapacity));
        if (FailureRetryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FailureRetryDelay));
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Terrarium base URL must be an absolute HTTPS URL.", nameof(BaseUrl));
    }
}

public readonly record struct TerrariumTileKey(int Zoom, int X, int Y)
{
    public string Id => $"{Zoom}/{X}/{Y}";
}

public readonly record struct TerrariumPixelAddress(
    TerrariumTileKey Tile,
    int Column,
    int Row,
    double GlobalPixelX,
    double GlobalPixelY);

public sealed record TerrariumProviderMetrics(
    long TileLoads,
    long DecodedCacheHits,
    long Samples,
    long UnavailableTiles,
    double DecodeMilliseconds);

/// <summary>
/// Canonical terrain source backed by Mapzen/Tilezen Terrarium PNG tiles. Terrarium is a
/// composite bare-earth terrain product and includes bathymetry; negative values are valid data.
/// </summary>
public sealed class TerrariumTerrainProvider : ITerrainElevationProvider
{
    public const string SourceId = "mapzen-terrarium";
    public const string SourceVersion = "elevation-tiles-prod-undated";
    private const int MaximumTileBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IEnvironmentalTileCache _cache;
    private readonly ILogger<TerrariumTerrainProvider> _logger;
    private readonly TerrariumTerrainOptions _options;
    private readonly BoundedDecodedRasterCache<TerrariumTileKey, TerrariumTileLoad> _tiles;
    private long _cacheGeneration;
    private readonly object _generationGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TerrariumTileKey,
        (TerrariumTileLoad Failure, DateTimeOffset RetryAt)> _recentFailures = new();
    private long _tileLoads;
    private long _decodedCacheRequests;
    private long _decodedCacheHits;
    private long _samples;
    private long _unavailableTiles;
    private long _decodeTicks;

    public TerrariumTerrainProvider(HttpClient http, IEnvironmentalTileCache cache,
        ILogger<TerrariumTerrainProvider> logger, TerrariumTerrainOptions? options = null)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
        _options = options ?? new TerrariumTerrainOptions();
        _options.Validate();
        _tiles = new BoundedDecodedRasterCache<TerrariumTileKey, TerrariumTileLoad>(
            _options.DecodedTileCapacity);
        cache.RegisterTerrainInvalidation(() => { _tiles.Clear(); _recentFailures.Clear(); });
    }

    public TerrariumTerrainOptions Options => _options;
    public TerrariumProviderMetrics Metrics => new(
        Interlocked.Read(ref _tileLoads), Interlocked.Read(ref _decodedCacheHits),
        Interlocked.Read(ref _samples), Interlocked.Read(ref _unavailableTiles),
        Interlocked.Read(ref _decodeTicks) * 1_000d / Stopwatch.Frequency);

    public async Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken) =>
        (await GetElevationSampleAsync(coordinate, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<ElevationSampleResult> GetElevationSampleAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var sample = await SampleAsync(coordinate.Normalised(), cancellationToken, true).ConfigureAwait(false);
        var value = sample.ElevationMetres is double elevation
            ? new EnvironmentalValue<double>(EnvironmentalDataState.Available, elevation, SourceId,
                SourceVersion, "Mapzen/Tilezen Terrarium terrain elevation.",
                RetrievedAt: SystemClock.Instance.GetCurrentInstant())
            : new EnvironmentalValue<double>(sample.State, default, SourceId, SourceVersion, sample.Message);
        var metres = ResolutionMetres(coordinate.Latitude, _options.Zoom);
        var diagnostics = new ElevationSampleDiagnostics(SourceId, SourceVersion,
            string.Join(", ", sample.Tiles.Select(tile => tile.Id)),
            sample.CellDescription,
            $"Web Mercator zoom {_options.Zoom}, 256 px tile; approximately {metres:F1} m/pixel at this latitude",
            "Orthometric metres; composite source datums follow Tilezen source attribution",
            sample.RawSamples, sample.ElevationMetres,
            sample.ElevationMetres.HasValue ? TerrainSampleStatus.Valid : sample.Status,
            sample.Message, IsInterpolated: sample.IsInterpolated,
            NativeResolutionMetres: metres,
            Quality: "Composite bare-earth terrain; ocean values can be bathymetric and must not be clamped");
        _logger.LogInformation(
            "Terrarium elevation at {Latitude:F6},{Longitude:F6}: elevation={Elevation}, state={State}, tiles={Tiles}",
            coordinate.Latitude, coordinate.Longitude, sample.ElevationMetres, sample.State, diagnostics.Tile);
        return new ElevationSampleResult(value, diagnostics);
    }

    public async Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken)
    {
        var values = new double?[coordinates.Count];
        var statuses = new TerrainSampleStatus[coordinates.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, coordinates.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = cancellationToken
        }, async (index, token) =>
        {
            var sample = await SampleAsync(coordinates[index].Normalised(), token, false).ConfigureAwait(false);
            values[index] = sample.ElevationMetres;
            statuses[index] = sample.ElevationMetres.HasValue ? TerrainSampleStatus.Valid : sample.Status;
        }).ConfigureAwait(false);
        var available = values.Count(value => value.HasValue);
        return new ElevationBatchResult(
            available == 0 ? EnvironmentalDataState.Unavailable :
            available == values.Length ? EnvironmentalDataState.Available : EnvironmentalDataState.Partial,
            values, SourceId, SourceVersion,
            available == values.Length ? "Terrarium elevation batch." :
            $"Terrarium elevation batch has {coordinates.Count - available} unavailable sample(s).",
            statuses);
    }

    public async Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken)
    {
        var keys = EnvironmentalPerformanceDiagnostics.Measure("tile-discovery", () =>
            coordinates.SelectMany(coordinate => RequiredTiles(coordinate.Normalised(), _options.Zoom))
                .Distinct().ToArray());
        await Parallel.ForEachAsync(keys, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (key, token) => { _ = await GetTileAsync(key, token).ConfigureAwait(false); })
            .ConfigureAwait(false);
    }

    public static double DecodeElevation(byte red, byte green, byte blue) =>
        red * 256d + green + blue / 256d - 32_768d;

    public static TerrariumPixelAddress LocatePixel(GeoCoordinate coordinate, int zoom)
    {
        if (zoom is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(zoom));
        var latitude = Math.Clamp(coordinate.Latitude,
            -TerrariumTerrainOptions.MaximumMercatorLatitude,
            TerrariumTerrainOptions.MaximumMercatorLatitude);
        var longitude = Angles.NormaliseLongitude(coordinate.Longitude);
        var tiles = 1 << zoom;
        var globalSize = (double)tiles * TerrariumTerrainOptions.TileSize;
        var globalX = (longitude + 180d) / 360d * globalSize;
        if (globalX >= globalSize) globalX = 0;
        var latitudeRadians = latitude * Angles.DegreesToRadians;
        var globalY = (1d - Math.Asinh(Math.Tan(latitudeRadians)) / Math.PI) * .5d * globalSize;
        globalY = Math.Clamp(globalY, 0, Math.BitDecrement(globalSize));
        var column = (int)Math.Floor(globalX) % TerrariumTerrainOptions.TileSize;
        var row = (int)Math.Floor(globalY) % TerrariumTerrainOptions.TileSize;
        return new TerrariumPixelAddress(new TerrariumTileKey(zoom,
            (int)(globalX / TerrariumTerrainOptions.TileSize),
            (int)(globalY / TerrariumTerrainOptions.TileSize)), column, row, globalX, globalY);
    }

    public static double ResolutionMetres(double latitude, int zoom) =>
        2 * Math.PI * HorizonService.MeanEarthRadiusMetres *
        Math.Cos(Math.Clamp(latitude, -TerrariumTerrainOptions.MaximumMercatorLatitude,
            TerrariumTerrainOptions.MaximumMercatorLatitude) * Angles.DegreesToRadians) /
        (TerrariumTerrainOptions.TileSize * (1 << zoom));

    private async Task<TerrariumInterpolation> SampleAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken, bool includeDiagnostics)
    {
        Interlocked.Increment(ref _samples);
        var address = LocatePixel(coordinate, _options.Zoom);
        // Pixels describe cell centres. Subtracting half a pixel makes interpolation continuous
        // across tile boundaries and avoids snapping a boundary coordinate to either tile.
        var x = address.GlobalPixelX - .5d;
        var y = address.GlobalPixelY - .5d;
        var x0 = (long)Math.Floor(x);
        var y0 = (long)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        List<TerrainGridSample>? raw = includeDiagnostics ? new List<TerrainGridSample>(4) : null;
        HashSet<TerrariumTileKey>? tileKeys = includeDiagnostics ? [] : null;
        var weighted = 0d;
        var totalWeight = 0d;
        var missing = false;
        var hasError = false;
        TerrariumTileKey? previousKey = null;
        TerrariumTileLoad? previousLoad = null;
        for (var index = 0; index < 4; index++)
        {
            var east = (index & 1) != 0;
            var south = (index & 2) != 0;
            var weight = (east ? fx : 1 - fx) * (south ? fy : 1 - fy);
            if (weight <= 1e-9) continue;
            var pixel = GlobalPixel(x0 + (east ? 1 : 0), y0 + (south ? 1 : 0), _options.Zoom);
            tileKeys?.Add(pixel.Tile);
            var load = previousKey == pixel.Tile
                ? previousLoad
                : await GetTileAsync(pixel.Tile, cancellationToken).ConfigureAwait(false);
            previousKey = pixel.Tile;
            previousLoad = load;
            if (load?.Tile is null)
            {
                missing = true;
                hasError |= load?.Status == TerrainSampleStatus.Error;
                raw?.Add(new TerrainGridSample(coordinate, pixel.Row, pixel.Column, null,
                    weight, load?.Status ?? TerrainSampleStatus.Unavailable));
                continue;
            }
            var elevation = load.Tile[pixel.Column, pixel.Row];
            weighted += elevation * weight;
            totalWeight += weight;
            raw?.Add(new TerrainGridSample(coordinate, pixel.Row, pixel.Column, elevation,
                weight, TerrainSampleStatus.Valid));
        }
        var description = $"global pixels ({x0},{y0}) to ({x0 + 1},{y0 + 1}); fractions x={fx:F4}, y={fy:F4}";
        if (missing || totalWeight < .999999)
            return new TerrariumInterpolation(null,
                hasError ? EnvironmentalDataState.InvalidRaster : EnvironmentalDataState.Unavailable,
                hasError ? TerrainSampleStatus.Error : TerrainSampleStatus.Unavailable,
                tileKeys?.ToArray() ?? [], raw ?? [], description, true,
                hasError ? "Terrarium interpolation failed because a contributing PNG is corrupt or invalid." :
                    "Terrarium interpolation is unavailable because one or more contributing tiles could not be loaded.");
        return new TerrariumInterpolation(weighted / totalWeight, EnvironmentalDataState.Available,
            TerrainSampleStatus.Valid, tileKeys?.ToArray() ?? [], raw ?? [], description,
            fx > 1e-12 || fy > 1e-12,
            "Bilinear interpolation of four Terrarium pixel centres; negative elevations are retained.");
    }

    private async Task<TerrariumTileLoad?> GetTileAsync(TerrariumTileKey key,
        CancellationToken cancellationToken)
    {
        var generation = _cache.TerrainGeneration;
        lock (_generationGate)
        {
            if (_cacheGeneration != generation)
            { _tiles.Clear(); _recentFailures.Clear(); _cacheGeneration = generation; }
        }
        _cache.TouchTerrain(Descriptor(key));
        Interlocked.Increment(ref _decodedCacheRequests);
        if (_tiles.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _decodedCacheHits);
            return cached;
        }
        if (_recentFailures.TryGetValue(key, out var failure))
        {
            if (DateTimeOffset.UtcNow < failure.RetryAt) return failure.Failure;
            _recentFailures.TryRemove(key, out _);
        }
        var loaded = await _tiles.GetOrCreateAsync(key, _ => LoadTileAsync(key, generation, CancellationToken.None),
            cancellationToken).ConfigureAwait(false);
        return loaded ?? (_recentFailures.TryGetValue(key, out var currentFailure)
            ? currentFailure.Failure : null);
    }

    private static EnvironmentalTileDescriptor Descriptor(TerrariumTileKey key) => new(SourceId, SourceVersion,
        $"z{key.Zoom}", $"{key.X}-{key.Y}", "png");

    private async Task<TerrariumTileLoad?> LoadTileAsync(TerrariumTileKey key, long generation,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _tileLoads);
        _logger.LogInformation("Terrarium tile requested: {Tile}", key.Id);
        var descriptor = Descriptor(key);
        using var lease = await _cache.LeaseTerrainAsync(descriptor, generation, cancellationToken).ConfigureAwait(false);
        var result = await _cache.GetOrCreateDetailedAsync(descriptor,
            token => EnvironmentalHttpDownloader.DownloadDetailedAsync(_http,
                new Uri(new Uri(_options.BaseUrl), $"{key.Zoom}/{key.X}/{key.Y}.png"),
                MaximumTileBytes, token), TerrariumTile.IsValid, cancellationToken).ConfigureAwait(false);
        if (!result.IsAvailable)
        {
            Interlocked.Increment(ref _unavailableTiles);
            _logger.LogWarning("Terrarium tile unavailable: {Tile}, state={State}, status={HttpStatus}, message={Message}",
                key.Id, result.State, result.HttpStatusCode, result.Message);
            var failure = new TerrariumTileLoad(null, result.State is EnvironmentalDataState.InvalidData or
                EnvironmentalDataState.InvalidRaster ? TerrainSampleStatus.Error : TerrainSampleStatus.Unavailable,
                result.Message);
            _recentFailures[key] = (failure, DateTimeOffset.UtcNow +
                (_options.FailureRetryDelay ?? TimeSpan.FromSeconds(30)));
            return null;
        }
        var timer = Stopwatch.StartNew();
        try
        {
            var tile = TerrariumTile.Load(result.Path!);
            timer.Stop();
            Interlocked.Add(ref _decodeTicks, timer.ElapsedTicks);
            _logger.LogDebug("Terrarium tile decoded: {Tile}, cacheHit={CacheHit}, elapsed={Elapsed:F1}ms",
                key.Id, result.CacheHit, timer.Elapsed.TotalMilliseconds);
            _recentFailures.TryRemove(key, out _);
            return new TerrariumTileLoad(tile, TerrainSampleStatus.Valid, result.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PngjException)
        {
            timer.Stop();
            Interlocked.Add(ref _decodeTicks, timer.ElapsedTicks);
            _logger.LogWarning(ex, "Terrarium PNG decode failed for {Tile}", key.Id);
            var failure = new TerrariumTileLoad(null, TerrainSampleStatus.Error,
                "Terrarium PNG could not be decoded.");
            _recentFailures[key] = (failure, DateTimeOffset.UtcNow +
                (_options.FailureRetryDelay ?? TimeSpan.FromSeconds(30)));
            return null;
        }
    }

    private static IReadOnlyList<TerrariumTileKey> RequiredTiles(GeoCoordinate coordinate, int zoom)
    {
        var address = LocatePixel(coordinate, zoom);
        var x = (long)Math.Floor(address.GlobalPixelX - .5d);
        var y = (long)Math.Floor(address.GlobalPixelY - .5d);
        return new[] { GlobalPixel(x, y, zoom).Tile, GlobalPixel(x + 1, y, zoom).Tile,
            GlobalPixel(x, y + 1, zoom).Tile, GlobalPixel(x + 1, y + 1, zoom).Tile };
    }

    private static TerrariumPixelAddress GlobalPixel(long x, long y, int zoom)
    {
        var tileCount = 1 << zoom;
        var globalSize = (long)tileCount * TerrariumTerrainOptions.TileSize;
        x = ((x % globalSize) + globalSize) % globalSize;
        y = Math.Clamp(y, 0, globalSize - 1);
        return new TerrariumPixelAddress(new TerrariumTileKey(zoom,
            (int)(x / TerrariumTerrainOptions.TileSize),
            (int)(y / TerrariumTerrainOptions.TileSize)),
            (int)(x % TerrariumTerrainOptions.TileSize), (int)(y % TerrariumTerrainOptions.TileSize), x, y);
    }

    private sealed record TerrariumTileLoad(TerrariumTile? Tile, TerrainSampleStatus Status, string Message);
    private sealed record TerrariumInterpolation(double? ElevationMetres, EnvironmentalDataState State,
        TerrainSampleStatus Status, IReadOnlyList<TerrariumTileKey> Tiles,
        IReadOnlyList<TerrainGridSample> RawSamples, string CellDescription, bool IsInterpolated,
        string Message);
}

public sealed class TerrariumTile
{
    private readonly float[] _elevations;

    private TerrariumTile(float[] elevations) => _elevations = elevations;

    public double this[int column, int row] => _elevations[row * TerrariumTerrainOptions.TileSize + column];

    public static bool IsValid(string path)
    {
        try { _ = Load(path); return true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PngjException) { return false; }
    }

    public static TerrariumTile Load(string path)
    {
        using var stream = File.OpenRead(path);
        var reader = new PngReader(stream) { ShouldCloseStream = false };
        if (reader.ImgInfo.Cols != TerrariumTerrainOptions.TileSize ||
            reader.ImgInfo.Rows != TerrariumTerrainOptions.TileSize ||
            reader.ImgInfo.BitDepth != 8 || reader.ImgInfo.Channels < 3)
            throw new InvalidDataException("Terrarium PNG must be 256 x 256, 8-bit RGB or RGBA.");
        var values = new float[TerrariumTerrainOptions.TileSize * TerrariumTerrainOptions.TileSize];
        for (var row = 0; row < TerrariumTerrainOptions.TileSize; row++)
        {
            var scanline = reader.ReadRowInt(row).Scanline;
            for (var column = 0; column < TerrariumTerrainOptions.TileSize; column++)
            {
                var offset = column * reader.ImgInfo.Channels;
                values[row * TerrariumTerrainOptions.TileSize + column] = (float)
                    TerrariumTerrainProvider.DecodeElevation((byte)scanline[offset],
                        (byte)scanline[offset + 1], (byte)scanline[offset + 2]);
            }
        }
        reader.End();
        return new TerrariumTile(values);
    }
}
