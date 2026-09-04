using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;
using NodaTime;

namespace Noctaxis.Core.Environment;

public enum EnvironmentalDataState
{
    Available,
    Cached,
    Partial,
    Unavailable,
    InvalidData,
    Error,
    /// <summary>A valid coverage was decoded and contains no effective data values.</summary>
    Empty,
    /// <summary>The upstream coverage service explicitly reports that the geographic chunk is absent.</summary>
    TileAbsent,
    /// <summary>The source could not be reached or returned a transient/service failure.</summary>
    SourceUnavailable,
    /// <summary>The acquired raster failed structural or scientific-value validation.</summary>
    InvalidRaster,
    /// <summary>The requested position is an intentional water surface rather than missing elevation data.</summary>
    Water
}

public sealed record EnvironmentalValue<T>(
    EnvironmentalDataState State,
    T? Value,
    string SourceId,
    string SourceVersion,
    string Message,
    Instant? DataTimestamp = null,
    Instant? RetrievedAt = null)
{
    public bool HasValue => Value is not null && State is EnvironmentalDataState.Available
        or EnvironmentalDataState.Cached or EnvironmentalDataState.Partial or EnvironmentalDataState.Empty
        or EnvironmentalDataState.Water;

    public static EnvironmentalValue<T> Unavailable(string sourceId, string version, string message) =>
        new(EnvironmentalDataState.Unavailable, default, sourceId, version, message);
}

public readonly record struct GeoBounds(double South, double West, double North, double East)
{
    public bool Contains(double latitude, double longitude) => latitude >= South && latitude <= North &&
        (West <= East ? longitude >= West && longitude <= East : longitude >= West || longitude <= East);
}

public enum GeoRasterProjection { Geographic, WebMercator }

public sealed record GeoRasterRequest(
    GeoBounds Bounds,
    int Width,
    int Height,
    GeoRasterProjection Projection = GeoRasterProjection.Geographic)
{
    public GeoCoordinate CoordinateAt(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) throw new ArgumentOutOfRangeException();
        var fx = Width == 1 ? .5 : x / (double)(Width - 1);
        var fy = Height == 1 ? .5 : y / (double)(Height - 1);
        var longitudeSpan = Bounds.East >= Bounds.West
            ? Bounds.East - Bounds.West
            : Bounds.East + 360 - Bounds.West;
        var longitude = Bounds.West + longitudeSpan * fx;
        if (longitude >= 180) longitude -= 360;
        var latitude = Projection == GeoRasterProjection.WebMercator
            ? MercatorLatitude(fy)
            : Bounds.North + (Bounds.South - Bounds.North) * fy;
        return new GeoCoordinate(latitude, longitude);
    }

    private double MercatorLatitude(double fraction)
    {
        static double Project(double latitude)
        {
            var radians = Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180;
            return Math.Asinh(Math.Tan(radians));
        }
        var north = Project(Bounds.North);
        var south = Project(Bounds.South);
        return Math.Atan(Math.Sinh(north + (south - north) * fraction)) * 180 / Math.PI;
    }
}

[Flags]
public enum EnvironmentLayer
{
    None = 0,
    TerrainElevation = 1 << 0,
    LandCover = 1 << 1,
    Settlement = 1 << 2,
    LightPollution = 1 << 3,
    Aurora = 1 << 4
}

public sealed record LocationEnvironmentRequest(
    GeoCoordinate Coordinate,
    EnvironmentLayer Layers,
    GeoRasterRequest? SettlementArea = null);

public sealed record LocationEnvironment(
    GeoCoordinate Coordinate,
    EnvironmentalValue<double>? TerrainElevation,
    EnvironmentalValue<LandCoverClass>? LandCover,
    EnvironmentalValue<SettlementRaster>? Settlement,
    EnvironmentalValue<LightPollutionSample>? LightPollution,
    EnvironmentalValue<AuroraEnvironment>? Aurora);

/// <summary>
/// Immutable environmental state consumed by the Planner. Static source failures are represented
/// independently so terrain, land cover and settlement can degrade without blocking one another.
/// </summary>
public sealed record PlannerEnvironmentSnapshot(
    GeoCoordinate ObserverCoordinate,
    EnvironmentalValue<double> GroundElevation,
    EnvironmentalValue<LandCoverClass> LandCover,
    EnvironmentalValue<SettlementRaster> Settlement,
    TerrainHorizonProfile HorizonProfile,
    Instant GeneratedAt)
{
    public string ActiveSourceDescription => HorizonProfile.Status;
}

public interface IPlannerEnvironmentService
{
    Task<PlannerEnvironmentSnapshot> GetSnapshotAsync(GeoCoordinate observer,
        CancellationToken cancellationToken);
    Task<PlannerEnvironmentSnapshot> GetSnapshotAsync(GeoCoordinate observer,
        TerrainProfileRequest terrainRequest, CancellationToken cancellationToken) =>
        GetSnapshotAsync(observer, cancellationToken);
    async Task<TerrainHorizonProfile> GetPriorityHorizonAsync(GeoCoordinate observer,
        IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(observer, cancellationToken).ConfigureAwait(false)).HorizonProfile;
    Task<TerrainHorizonProfile> GetPriorityHorizonAsync(GeoCoordinate observer,
        TerrainProfileRequest terrainRequest, IReadOnlyList<double> bearings,
        CancellationToken cancellationToken) =>
        GetPriorityHorizonAsync(observer, bearings, cancellationToken);
}

public enum LandCoverClass
{
    Unknown = 0,
    TreeCover = 10,
    Shrubland = 20,
    Grassland = 30,
    Cropland = 40,
    BuiltUp = 50,
    BareOrSparseVegetation = 60,
    SnowAndIce = 70,
    PermanentWater = 80,
    HerbaceousWetland = 90,
    Mangroves = 95,
    MossAndLichen = 100
}

public sealed record SettlementRaster(
    string DatasetId,
    string DatasetVersion,
    GeoRasterRequest Grid,
    float[] BuildingFraction,
    float[] BuildingHeightMetres,
    bool IsPartial = false)
{
    public int CellCount => checked(Grid.Width * Grid.Height);
}

public sealed record LightPollutionSample(
    double RadianceNanoWattsPerSquareCentimetreSteradian,
    double? BrightestDirectionDegrees = null,
    double? DarkestDirectionDegrees = null);

public sealed record AuroraEnvironment(
    double? ProbabilityOrIntensity,
    double? PlanetaryKp,
    Instant? ForecastTimestamp,
    Instant? DataTimestamp);

public interface ILocationEnvironmentService
{
    Task<LocationEnvironment> GetAsync(LocationEnvironmentRequest request, CancellationToken cancellationToken);
}
