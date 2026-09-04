using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;
using NodaTime;

namespace Noctaxis.Core.Environment;

internal readonly record struct DegreeTile(int South, int West)
{
    public GeoBounds Bounds => new(South, West, South + 1, West + 1);
    public string WsfName => $"{(West >= 0 ? 'e' : 'w')}{Math.Abs(West):000}_{(South + 1 >= 0 ? 'n' : 's')}{Math.Abs(South + 1):00}_{(West + 1 >= 0 ? 'e' : 'w')}{Math.Abs(West + 1):000}_{(South >= 0 ? 'n' : 's')}{Math.Abs(South):00}";
    public static DegreeTile At(GeoCoordinate coordinate) => new((int)Math.Floor(coordinate.Latitude), (int)Math.Floor(coordinate.Longitude));
}

public sealed class WsfSettlementDataProvider(
    IWsfCoverageSource coverageSource,
    ILogger<WsfSettlementDataProvider> logger) : ISettlementDataProvider
{
    public const string SourceId = "wsf-3d";
    public const string SourceVersion = "v02";

    public async Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Width <= 0 || request.Height <= 0 || request.Width > 4096 || request.Height > 4096)
            throw new ArgumentOutOfRangeException(nameof(request));
        var count = checked(request.Width * request.Height);
        var fractions = new float[count];
        var heights = new float[count];
        var tileRasters = new Dictionary<DegreeTile, (WsfCoverageResult Fraction, WsfCoverageResult Height)>();
        var valid = 0;
        var occupied = 0;
        foreach (var tile in RequiredTiles(request.Bounds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = new WsfCoverageChunk(tile.WsfName, tile.Bounds);
            var fractionTask = coverageSource.GetCoverageAsync(chunk, WsfCoverageLayer.BuildingFraction,
                cancellationToken);
            var heightTask = coverageSource.GetCoverageAsync(chunk, WsfCoverageLayer.BuildingHeight,
                cancellationToken);
            await Task.WhenAll(fractionTask, heightTask).ConfigureAwait(false);
            tileRasters[tile] = (await fractionTask.ConfigureAwait(false), await heightTask.ConfigureAwait(false));
        }
        for (var index = 0; index < count; index++)
        {
            if ((index & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var coordinate = request.CoordinateAt(index % request.Width, index / request.Width);
            var tile = TileForSample(request.Bounds, coordinate);
            if (!tileRasters.TryGetValue(tile, out var rasters) || !rasters.Fraction.HasRaster) continue;
            var fractionRaster = rasters.Fraction.Raster!;
            var fractionValue = fractionRaster.Interpolate(fractionRaster.Metadata.Bounds ?? tile.Bounds,
                coordinate.Latitude, coordinate.Longitude,
                value => IsNoData(fractionRaster, value, 255) || value < 0 || value > 100);
            if (!fractionValue.HasValue) continue;
            fractions[index] = (float)NormalizeBuildingFraction(fractionValue.Value);
            if (fractions[index] > 0) occupied++;
            if (rasters.Height.HasRaster)
            {
                var heightRaster = rasters.Height.Raster!;
                var heightValue = heightRaster.Interpolate(heightRaster.Metadata.Bounds ?? tile.Bounds,
                    coordinate.Latitude, coordinate.Longitude,
                    value => IsNoData(heightRaster, value, -32767) || value < 0 || value > 5000);
                heights[index] = heightValue.HasValue ? (float)NormalizeBuildingHeight(heightValue.Value) : 0;
            }
            valid++;
        }
        if (valid == 0)
        {
            var failureState = AggregateFailureState(tileRasters.Values.Select(pair => pair.Fraction));
            var message = failureState switch
            {
                EnvironmentalDataState.TileAbsent => "DLR WCS explicitly reports no WSF source chunk for this area.",
                EnvironmentalDataState.InvalidRaster => "WSF coverage was acquired but failed scientific raster validation.",
                _ => "The DLR WSF coverage service is currently unavailable."
            };
            logger.LogWarning("WSF settlement grid unavailable: {State}, {Width}x{Height}, chunks={Chunks}",
                failureState, request.Width, request.Height, tileRasters.Count);
            return new EnvironmentalValue<SettlementRaster>(failureState, null, SourceId, SourceVersion, message);
        }
        var fractionComplete = tileRasters.Values.All(pair => pair.Fraction.HasRaster);
        var heightComplete = tileRasters.Values.All(pair => pair.Height.HasRaster);
        var partial = !fractionComplete || !heightComplete || valid < count;
        var state = occupied == 0 && fractionComplete && valid == count
            ? EnvironmentalDataState.Empty
            : partial ? EnvironmentalDataState.Partial
            : tileRasters.Values.All(pair => pair.Fraction.CacheHit && pair.Height.CacheHit)
                ? EnvironmentalDataState.Cached
                : EnvironmentalDataState.Available;
        logger.LogInformation(
            "WSF settlement grid generated: {Width}x{Height}, {Valid}/{Total} valid cells, {Occupied} occupied cells, state={State}",
            request.Width, request.Height, valid, count, occupied, state);
        return new EnvironmentalValue<SettlementRaster>(state,
            new SettlementRaster(SourceId, SourceVersion, request, fractions, heights, partial), SourceId, SourceVersion,
            state switch
            {
                EnvironmentalDataState.Empty => "Valid WSF coverage contains no settlement in the requested area.",
                EnvironmentalDataState.Partial => "WSF settlement grid has partial source coverage.",
                EnvironmentalDataState.Cached => "WSF settlement grid loaded from shared source-chunk cache.",
                _ => "WSF settlement grid generated from validated scientific coverage."
            },
            RetrievedAt: SystemClock.Instance.GetCurrentInstant());
    }

    public static double NormalizeBuildingFraction(double rawPercent)
    {
        if (!double.IsFinite(rawPercent) || rawPercent < 0 || rawPercent > 100)
            throw new InvalidDataException("WSF Building Fraction is outside the documented 0..100 range.");
        return Math.Clamp(rawPercent / 100d, 0, 1);
    }

    public static double NormalizeBuildingHeight(double rawHeight)
    {
        if (!double.IsFinite(rawHeight) || rawHeight < 0 || rawHeight > 5000)
            throw new InvalidDataException("WSF Building Height is outside the supported raw range.");
        return Math.Clamp(rawHeight * .1d, 0, 500);
    }

    private static IEnumerable<DegreeTile> RequiredTiles(GeoBounds bounds)
    {
        var south = (int)Math.Floor(bounds.South);
        var north = (int)Math.Floor(Math.BitDecrement(bounds.North));
        foreach (var (west, east) in LongitudeRanges(bounds))
        for (var latitude = south; latitude <= north; latitude++)
        for (var longitude = west; longitude <= east; longitude++)
            yield return new DegreeTile(latitude, longitude);

        static IEnumerable<(int West, int East)> LongitudeRanges(GeoBounds value)
        {
            if (value.West <= value.East)
            {
                yield return ((int)Math.Floor(value.West), (int)Math.Floor(Math.BitDecrement(value.East)));
                yield break;
            }
            yield return ((int)Math.Floor(value.West), 179);
            yield return (-180, (int)Math.Floor(Math.BitDecrement(value.East)));
        }
    }

    private static bool IsNoData(GeoTiffRaster raster, double value, double fallback) =>
        Math.Abs(value - (raster.Metadata.NoDataValue ?? fallback)) <= 1e-6;

    private static DegreeTile TileForSample(GeoBounds requestBounds, GeoCoordinate coordinate)
    {
        // GeoRasterRequest includes the north/east edge. Those coordinates belong to the final
        // requested chunk, not the adjacent degree that was deliberately excluded by RequiredTiles.
        var latitude = coordinate.Latitude == requestBounds.North
            ? Math.BitDecrement(coordinate.Latitude)
            : coordinate.Latitude;
        var longitude = coordinate.Longitude;
        var normalizedEast = requestBounds.East >= 180 ? requestBounds.East - 360 : requestBounds.East;
        if (longitude == normalizedEast)
        {
            var unwrappedEast = requestBounds.East + (requestBounds.West > requestBounds.East ? 360 : 0);
            longitude = Math.BitDecrement(unwrappedEast);
            if (longitude >= 180) longitude -= 360;
        }
        return DegreeTile.At(new GeoCoordinate(latitude, longitude));
    }

    private static EnvironmentalDataState AggregateFailureState(IEnumerable<WsfCoverageResult> results)
    {
        var states = results.Select(result => result.State).ToArray();
        if (states.Contains(EnvironmentalDataState.InvalidRaster)) return EnvironmentalDataState.InvalidRaster;
        if (states.Contains(EnvironmentalDataState.SourceUnavailable)) return EnvironmentalDataState.SourceUnavailable;
        if (states.Length > 0 && states.All(state => state == EnvironmentalDataState.TileAbsent))
            return EnvironmentalDataState.TileAbsent;
        return EnvironmentalDataState.SourceUnavailable;
    }
}

public sealed class WorldCoverLandCoverProvider(
    HttpClient http,
    IEnvironmentalTileCache cache,
    ILogger<WorldCoverLandCoverProvider> logger) : ILandCoverProvider
{
    public const string SourceId = "esa-worldcover";
    public const string SourceVersion = "2021-v200";
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _tiles = new(StringComparer.Ordinal);

    public async Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate,
        CancellationToken cancellationToken)
    {
        var batch = await GetLandCoversAsync([coordinate], cancellationToken).ConfigureAwait(false);
        var value = batch.Classifications[0];
        if (!value.HasValue)
            return EnvironmentalValue<LandCoverClass>.Unavailable(SourceId, SourceVersion,
                "ESA WorldCover classification is unavailable at this coordinate.");
        return new EnvironmentalValue<LandCoverClass>(EnvironmentalDataState.Available,
            value.Value, SourceId, SourceVersion, "ESA WorldCover classification.");
    }

    public async Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates,
        CancellationToken cancellationToken)
    {
        var values = new LandCoverClass?[coordinates.Count];
        foreach (var group in coordinates.Select((coordinate, index) =>
                     (coordinate, index, tile: DescribeTile(coordinate))).GroupBy(item => item.tile.Tile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = await GetTilePathAsync(group.Key, cancellationToken).ConfigureAwait(false);
            if (path is null) continue;
            var items = group.ToArray();
            try
            {
                var sampled = GeoTiffRaster.ReadNearestBatch(path, items[0].tile.Bounds,
                    items.Select(item => item.coordinate).ToArray(), sample => sample <= 0);
                for (var index = 0; index < items.Length; index++)
                {
                    var sample = sampled[index];
                    if (sample.HasValue && Enum.IsDefined(typeof(LandCoverClass), (int)sample.Value))
                        values[items[index].index] = (LandCoverClass)(int)sample.Value;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                logger.LogWarning(ex, "WorldCover selective decode failed for {Tile}", group.Key);
            }
        }
        var available = values.Count(value => value.HasValue);
        var state = available == 0 ? EnvironmentalDataState.Unavailable :
            available == values.Length ? EnvironmentalDataState.Available : EnvironmentalDataState.Partial;
        return new LandCoverBatchResult(state, values, SourceId, SourceVersion,
            available == 0 ? "ESA WorldCover classification is unavailable." :
            available == values.Length ? "ESA WorldCover classification batch." :
            "ESA WorldCover classification has partial coverage.");
    }

    private async Task<string?> GetTilePathAsync(string tile, CancellationToken cancellationToken)
    {
        var lazy = _tiles.GetOrAdd(tile,
            _ => new Lazy<Task<string?>>(() => LoadTilePathAsync(tile, CancellationToken.None)));
        var path = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (path is null) _tiles.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(tile, lazy));
        return path;
    }

    private async Task<string?> LoadTilePathAsync(string tile, CancellationToken cancellationToken)
    {
        var name = $"ESA_WorldCover_10m_2021_v200_{tile}_Map";
        var result = await cache.GetOrCreateAsync(
            new EnvironmentalTileDescriptor(SourceId, SourceVersion, "land-cover", tile, "tif"),
            token => EnvironmentalHttpDownloader.DownloadAsync(http,
                new Uri($"https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/{name}.tif"),
                100 * 1024 * 1024, token), GeoTiffRaster.IsValid, cancellationToken).ConfigureAwait(false);
        return result.IsAvailable ? result.Path : null;
    }

    private static (string Tile, GeoBounds Bounds) DescribeTile(GeoCoordinate coordinate)
    {
        var south = (int)Math.Floor(coordinate.Latitude / 3) * 3;
        var west = (int)Math.Floor(coordinate.Longitude / 3) * 3;
        return ($"{(south >= 0 ? 'N' : 'S')}{Math.Abs(south):00}{(west >= 0 ? 'E' : 'W')}{Math.Abs(west):000}",
            new GeoBounds(south, west, south + 3, west + 3));
    }
}


