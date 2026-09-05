using System.Collections.Immutable;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using NodaTime;

namespace Noctaxis.Core.Terrain;

public enum TerrainDebugMapLoadState
{
    Disabled,
    Resolving,
    Ready,
    Unavailable
}

public sealed record TerrainDebugMapRequest(
    double RangeMetres = 20_000,
    int Width = 128,
    int Height = 128)
{
    public TerrainDebugMapRequest Normalised() => this with
    {
        RangeMetres = Math.Clamp(double.IsFinite(RangeMetres) ? RangeMetres : 20_000, 1_000, 50_000),
        Width = Math.Clamp(Width, 16, 256),
        Height = Math.Clamp(Height, 16, 256)
    };
}

public sealed record TerrainDebugMapSnapshot(
    GeoCoordinate Observer,
    double RangeMetres,
    int Width,
    int Height,
    IReadOnlyList<GeoCoordinate> Coordinates,
    IReadOnlyList<double?> RawTerrainElevationsMetres,
    IReadOnlyList<double?> SurfaceElevationsMetres,
    IReadOnlyList<LandCoverClass?> Classifications,
    IReadOnlyList<bool> AdjustedSamples,
    IReadOnlyList<TerrainSampleStatus> SampleStatuses,
    IReadOnlyList<string> TerrariumTiles,
    EnvironmentalDataState State,
    Instant GeneratedAt,
    string Message)
{
    public int CellCount => checked(Width * Height);
    public int Index(int row, int column) => checked(row * Width + column);
}

public interface ITerrainDebugMapService
{
    Task<TerrainDebugMapSnapshot> GetMapAsync(GeoCoordinate observer,
        TerrainDebugMapRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Bounded developer-only geographic grid sampler. It delegates all elevation and classification
/// work to the production surface resolver and therefore shares the normal tile/download caches.
/// </summary>
public sealed class TerrainDebugMapService(ITerrainSurfaceResolver surfaces) : ITerrainDebugMapService
{
    public async Task<TerrainDebugMapSnapshot> GetMapAsync(GeoCoordinate observer,
        TerrainDebugMapRequest request, CancellationToken cancellationToken)
    {
        var normalisedObserver = observer.Normalised();
        var normalisedRequest = request.Normalised();
        var coordinates = BuildGrid(normalisedObserver, normalisedRequest);
        var preload = surfaces.PreloadAsync(coordinates, cancellationToken);
        var classification = surfaces.GetClassificationsAsync(coordinates, cancellationToken);
        await Task.WhenAll(preload, classification).ConfigureAwait(false);
        var classifications = await classification.ConfigureAwait(false);
        var elevations = await surfaces.GetSurfaceElevationsAsync(
            coordinates, classifications, cancellationToken).ConfigureAwait(false);
        var tiles = coordinates.Select(coordinate =>
                TerrariumTerrainProvider.LocatePixel(coordinate, 12).Tile.Id)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        return new TerrainDebugMapSnapshot(normalisedObserver, normalisedRequest.RangeMetres,
            normalisedRequest.Width, normalisedRequest.Height, coordinates.ToImmutableArray(),
            elevations.RawTerrainElevationsMetres.ToImmutableArray(), elevations.SurfaceElevationsMetres.ToImmutableArray(),
            elevations.Classifications.ToImmutableArray(), elevations.AdjustedSamples.ToImmutableArray(),
            elevations.SampleStatuses.ToImmutableArray(), tiles.ToImmutableArray(), elevations.State,
            SystemClock.Instance.GetCurrentInstant(), elevations.Message);
    }

    internal static GeoCoordinate[] BuildGrid(GeoCoordinate observer, TerrainDebugMapRequest request)
    {
        var coordinates = new GeoCoordinate[checked(request.Width * request.Height)];
        for (var row = 0; row < request.Height; row++)
        for (var column = 0; column < request.Width; column++)
        {
            var east = ((column + .5) / request.Width * 2 - 1) * request.RangeMetres;
            var north = (1 - (row + .5) / request.Height * 2) * request.RangeMetres;
            coordinates[row * request.Width + column] =
                LocalTerrainMapProjection.FromLocalMetres(observer, east, north);
        }
        return coordinates;
    }
}

/// <summary>Small-extent north/east tangent approximation used only by the diagnostic map.</summary>
public static class LocalTerrainMapProjection
{
    public static GeoCoordinate FromLocalMetres(GeoCoordinate observer,
        double eastMetres, double northMetres)
    {
        var latitudeRadians = observer.Latitude * Angles.DegreesToRadians;
        var latitude = observer.Latitude + northMetres / HorizonService.MeanEarthRadiusMetres *
            Angles.RadiansToDegrees;
        var longitudeScale = HorizonService.MeanEarthRadiusMetres * Math.Cos(latitudeRadians);
        var longitude = Math.Abs(longitudeScale) < 1e-9
            ? observer.Longitude
            : observer.Longitude + eastMetres / longitudeScale * Angles.RadiansToDegrees;
        return new GeoCoordinate(latitude, Angles.NormaliseLongitude(longitude));
    }

    public static (double EastMetres, double NorthMetres) ToLocalMetres(
        GeoCoordinate observer, GeoCoordinate coordinate)
    {
        var latitudeRadians = observer.Latitude * Angles.DegreesToRadians;
        var north = (coordinate.Latitude - observer.Latitude) * Angles.DegreesToRadians *
                    HorizonService.MeanEarthRadiusMetres;
        var longitudeDelta = Angles.NormaliseSignedDegrees(coordinate.Longitude - observer.Longitude);
        var east = longitudeDelta * Angles.DegreesToRadians * HorizonService.MeanEarthRadiusMetres *
                   Math.Cos(latitudeRadians);
        return (east, north);
    }
}
