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
    double DecodeMilliseconds,
    // PNG decodes performed, including the decode that validates a cached file.
    long TileDecodes = 0);

/// <summary>A whole decoded Terrarium tile, or why it is unavailable.</summary>
public sealed record TerrariumTileResult(TerrariumTile? Tile, TerrainSampleStatus Status);

/// <summary>
/// Whole Terrarium tiles at any published zoom, for presentation (the Planner map's terrain relief).
/// Served by <see cref="TerrariumTerrainProvider"/> through the same download, validation and disk
/// cache as the terrain calculations, so the map and the calculations read the same elevation data
/// and a tile is downloaded and stored once whichever of them asks first.
/// </summary>
public interface ITerrariumTileSource
{
    /// <summary>The deepest zoom Terrarium publishes.</summary>
    const int MaximumZoom = 15;

    /// <summary>Changes whenever stored terrain is cleared, so anything derived from it can be discarded.</summary>
    long Generation { get; }

    /// <summary>
    /// The zoom the terrain calculations sample. Deeper Terrarium zooms are, in many regions, coarser
    /// sources enlarged in steps that shading would exaggerate, so presentation goes no deeper than
    /// this and shows the terrain the calculations actually use.
    /// </summary>
    int DetailZoom => 12;

    Task<TerrariumTileResult> GetTerrainTileAsync(TerrariumTileKey key, CancellationToken cancellationToken);
}

/// <summary>
/// Canonical terrain source backed by Mapzen/Tilezen Terrarium PNG tiles. Terrarium is a
/// composite bare-earth terrain product and includes bathymetry; negative values are valid data.
/// </summary>
public sealed class TerrariumTerrainProvider : ITerrainElevationProvider, ITerrariumTileSource
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
    private long _tileDecodes;

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
        Interlocked.Read(ref _decodeTicks) * 1_000d / Stopwatch.Frequency,
        Interlocked.Read(ref _tileDecodes));

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
        // Bound both coordinate storage and resident references, including adversarial scattered inputs.
        // The public result owns its storage; only this invocation retains the temporary tile references.
        var positions = new BulkPosition[Math.Min(1024, coordinates.Count)];
        var tiles = new Dictionary<TerrariumTileKey, TerrariumTileLoad?>();
        var offset = 0;
        cancellationToken.ThrowIfCancellationRequested();
        while (offset < coordinates.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tiles.Clear();
            var count = 0;
            while (count < positions.Length && offset + count < coordinates.Count && tiles.Count <= 92)
            {
                var address = LocatePixel(coordinates[offset + count].Normalised(), _options.Zoom);
                var x = address.GlobalPixelX - .5d;
                var y = address.GlobalPixelY - .5d;
                var position = new BulkPosition((long)Math.Floor(x), (long)Math.Floor(y),
                    x - Math.Floor(x), y - Math.Floor(y));
                positions[count++] = position;
                for (var corner = 0; corner < 4; corner++)
                    if (position.Weight(corner) > 1e-9)
                        tiles.TryAdd(GlobalPixel(position.X + (corner & 1),
                            position.Y + (corner >> 1), _options.Zoom).Tile, null);
            }
            var keys = tiles.Keys.ToArray();
            var loads = new TerrariumTileLoad?[keys.Length];
            await Parallel.ForAsync(0, keys.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            }, async (index, token) =>
                loads[index] = await GetTileAsync(keys[index], token).ConfigureAwait(false)).ConfigureAwait(false);
            for (var index = 0; index < keys.Length; index++) tiles[keys[index]] = loads[index];
            for (var local = 0; local < count; local++)
            {
                if ((local & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var position = positions[local];
                var weighted = 0d;
                var totalWeight = 0d;
                var missing = false;
                var error = false;
                for (var corner = 0; corner < 4; corner++)
                {
                    var weight = position.Weight(corner);
                    if (weight <= 1e-9) continue;
                    var pixel = GlobalPixel(position.X + (corner & 1), position.Y + (corner >> 1), _options.Zoom);
                    var load = tiles[pixel.Tile];
                    if (load?.Tile is null)
                    {
                        missing = true;
                        error |= load?.Status == TerrainSampleStatus.Error;
                        continue;
                    }
                    weighted += load.Tile[pixel.Column, pixel.Row] * weight;
                    totalWeight += weight;
                }
                var index = offset + local;
                if (missing || totalWeight < .999999)
                    statuses[index] = error ? TerrainSampleStatus.Error : TerrainSampleStatus.Unavailable;
                else
                {
                    values[index] = weighted / totalWeight;
                    statuses[index] = TerrainSampleStatus.Valid;
                }
                Interlocked.Increment(ref _samples);
            }
            offset += count;
        }
        var available = values.Count(value => value.HasValue);
        return new ElevationBatchResult(
            available == 0 ? EnvironmentalDataState.Unavailable :
            available == values.Length ? EnvironmentalDataState.Available : EnvironmentalDataState.Partial,
            values, SourceId, SourceVersion,
            available == values.Length ? "Terrarium elevation batch." :
            $"Terrarium elevation batch has {coordinates.Count - available} unavailable sample(s).",
            statuses);
    }

    private readonly record struct BulkPosition(long X, long Y, double FractionX, double FractionY)
    {
        public double Weight(int corner) => ((corner & 1) != 0 ? FractionX : 1 - FractionX) *
            ((corner & 2) != 0 ? FractionY : 1 - FractionY);
    }

    public async Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken)
    {
        var keys = EnvironmentalPerformanceDiagnostics.Measure("tile-discovery", () =>
        {
            var required = new HashSet<TerrariumTileKey>();
            for (var index = 0; index < coordinates.Count; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var address = LocatePixel(coordinates[index].Normalised(), _options.Zoom);
                var x = (long)Math.Floor(address.GlobalPixelX - .5d);
                var y = (long)Math.Floor(address.GlobalPixelY - .5d);
                required.Add(GlobalPixel(x, y, _options.Zoom).Tile);
                required.Add(GlobalPixel(x + 1, y, _options.Zoom).Tile);
                required.Add(GlobalPixel(x, y + 1, _options.Zoom).Tile);
                required.Add(GlobalPixel(x + 1, y + 1, _options.Zoom).Tile);
            }
            // Preloading fetches what is not stored yet, so its downloads overlap. A stored tile is left for
            // the calculation to load when it needs it: loading (validating) it here decoded every stored
            // tile of the profile, and the decoded cache cannot hold them all, so most were decoded again
            // by the calculation (PF-02).
            return required.Where(key => !_cache.Contains(Descriptor(key))).ToArray();
        });
        await Parallel.ForEachAsync(keys, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (key, token) => { _ = await GetTileAsync(key, token).ConfigureAwait(false); })
            .ConfigureAwait(false);
    }

    public long Generation => _cache.TerrainGeneration;
    public int DetailZoom => _options.Zoom;

    /// <summary>
    /// One whole tile at the requested zoom. It takes exactly the path elevation sampling takes
    /// (decoded-tile memory, failure back-off, disk cache and download), so nothing here can
    /// download or store a tile a second time.
    /// </summary>
    public async Task<TerrariumTileResult> GetTerrainTileAsync(TerrariumTileKey key, CancellationToken cancellationToken)
    {
        if (key.Zoom is < 0 or > ITerrariumTileSource.MaximumZoom) throw new ArgumentOutOfRangeException(nameof(key));
        var tiles = 1 << key.Zoom;
        if (key.X < 0 || key.X >= tiles || key.Y < 0 || key.Y >= tiles) throw new ArgumentOutOfRangeException(nameof(key));
        var load = await GetTileAsync(key, cancellationToken).ConfigureAwait(false);
        return load?.Tile is { } tile
            ? new TerrariumTileResult(tile, TerrainSampleStatus.Valid)
            : new TerrariumTileResult(null, load?.Status ?? TerrainSampleStatus.Unavailable);
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
        // Validation has to decode the PNG in full to prove it is intact, so the decoded tile is kept
        // and used instead of reading and decoding the same file a second time.
        var validated = new DecodedTileCapture();
        var result = await _cache.GetOrCreateDetailedAsync(descriptor,
            token => EnvironmentalHttpDownloader.DownloadDetailedAsync(_http,
                new Uri(new Uri(_options.BaseUrl), $"{key.Zoom}/{key.X}/{key.Y}.png"),
                MaximumTileBytes, token), path => TryDecode(path, validated), cancellationToken).ConfigureAwait(false);
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
        try
        {
            // A cache implementation that does not validate leaves nothing captured, so decode now.
            var tile = validated.Tile ?? Decode(result.Path!);
            _logger.LogDebug("Terrarium tile decoded: {Tile}, cacheHit={CacheHit}", key.Id, result.CacheHit);
            _recentFailures.TryRemove(key, out _);
            return new TerrariumTileLoad(tile, TerrainSampleStatus.Valid, result.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PngjException)
        {
            _logger.LogWarning(ex, "Terrarium PNG decode failed for {Tile}", key.Id);
            var failure = new TerrariumTileLoad(null, TerrainSampleStatus.Error,
                "Terrarium PNG could not be decoded.");
            _recentFailures[key] = (failure, DateTimeOffset.UtcNow +
                (_options.FailureRetryDelay ?? TimeSpan.FromSeconds(30)));
            return null;
        }
    }

    /// <summary>
    /// Cache validation for Terrarium tiles: a file is valid exactly when it decodes, which is the
    /// same test <see cref="TerrariumTile.IsValid"/> applies, and the decoded tile is retained for use.
    /// Validation may run twice for one load (a corrupt cached file, then the replacement download),
    /// so the most recent successful decode wins.
    /// </summary>
    private bool TryDecode(string path, DecodedTileCapture capture)
    {
        capture.Tile = null;
        try
        {
            capture.Tile = Decode(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PngjException)
        {
            _logger.LogDebug(ex, "Rejected a Terrarium PNG that could not be decoded: {Path}", path);
            return false;
        }
    }

    private TerrariumTile Decode(string path)
    {
        Interlocked.Increment(ref _tileDecodes);
        var timer = Stopwatch.StartNew();
        try { return TerrariumTile.Load(path); }
        finally
        {
            timer.Stop();
            Interlocked.Add(ref _decodeTicks, timer.ElapsedTicks);
        }
    }

    private sealed class DecodedTileCapture { public TerrariumTile? Tile; }

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

    /// <summary>A tile from row-major elevations in metres (256 x 256), as decoded from a Terrarium PNG.</summary>
    public static TerrariumTile FromElevations(float[] elevations)
    {
        ArgumentNullException.ThrowIfNull(elevations);
        if (elevations.Length != TerrariumTerrainOptions.TileSize * TerrariumTerrainOptions.TileSize)
            throw new ArgumentException("A Terrarium tile holds 256 x 256 elevations.", nameof(elevations));
        return new TerrariumTile((float[])elevations.Clone());
    }

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
