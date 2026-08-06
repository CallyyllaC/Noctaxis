namespace Noctaxis.Desktop.Services;

public enum MapRoadClassification { Motorway, ARoad, BRoad, OtherMajorRoad }
public enum MapWaterwayClassification { River, Canal, Stream }
public enum MapFeatureFetchStatus { Complete, CachedPrevious, PartialWithoutBuildings, Unavailable }

public sealed record MapFeatureFetchOutcome(
    MapFeatureFetchStatus Status,
    int RoadCount,
    int WaterwayCount,
    int BuildingCount,
    string? FailureCode,
    string? FailureReason,
    int? HttpStatusCode,
    bool TimedOut,
    bool ResponseTooLarge,
    bool ParseFailed,
    int AttemptCount,
    DateTimeOffset AttemptedAtUtc,
    bool FallbackAttempted = false)
{
    public static MapFeatureFetchOutcome Success(MapFeatureDataDocument data, MapFeatureFetchStatus status,
        int attemptCount = 1, string? failureCode = null, string? failureReason = null,
        int? httpStatusCode = null, bool timedOut = false, bool responseTooLarge = false,
        bool fallbackAttempted = false) => new(
        status, data.Roads.Length, data.Waterways.Length, data.Buildings.Length, failureCode, failureReason,
        httpStatusCode, timedOut, responseTooLarge, false, attemptCount, DateTimeOffset.UtcNow, fallbackAttempted);

    public static MapFeatureFetchOutcome Failure(string code, string reason, int attemptCount = 1,
        int? httpStatusCode = null, bool timedOut = false, bool responseTooLarge = false,
        bool parseFailed = false, bool fallbackAttempted = false) => new(
        MapFeatureFetchStatus.Unavailable, 0, 0, 0, code, reason, httpStatusCode, timedOut,
        responseTooLarge, parseFailed, attemptCount, DateTimeOffset.UtcNow, fallbackAttempted);
}

public sealed record MapFeatureCoordinate(double Latitude, double Longitude);

public sealed record MapRoadFeature(
    long Id,
    string ElementType,
    MapRoadClassification Classification,
    MapFeatureCoordinate[] Geometry,
    string? Highway,
    string? Reference,
    string? Name,
    string? Bridge,
    string? Tunnel,
    string? Layer);

public sealed record MapWaterwayFeature(
    long Id,
    string ElementType,
    MapWaterwayClassification Classification,
    MapFeatureCoordinate[] Geometry,
    string? Waterway,
    string? Name,
    string? Intermittent,
    string? Tunnel,
    string? Layer);

public sealed record MapFeatureRing(MapFeatureCoordinate[] Geometry, bool IsInner = false);

public sealed record MapBuildingFeature(
    long Id,
    string ElementType,
    MapFeatureRing[] Rings,
    string? Building,
    string? Name,
    MapFeatureCoordinate Centroid);

public sealed record MapFeatureSourceMetadata(
    string ProviderId,
    string ProviderName,
    string AttributionText,
    string AttributionUrl,
    string LicenceName,
    string LicenceUrl,
    string EndpointId,
    int QueryVersion,
    DateTimeOffset FetchedAtUtc,
    MapGeographicBounds Bounds);

public sealed record MapFeatureDataDocument(
    int SchemaVersion,
    Guid LocationId,
    MapFeatureSourceMetadata Source,
    MapRoadFeature[] Roads,
    MapWaterwayFeature[] Waterways,
    MapBuildingFeature[] Buildings,
    int IgnoredGeometryCount = 0)
{
    public int FeatureCount => Roads.Length + Waterways.Length + Buildings.Length;
}

public sealed record MapFeatureFetchResult(MapFeatureDataDocument? Data, MapFeatureFetchOutcome Outcome)
{
    public MapFeatureFetchResult(MapFeatureDataDocument? data, MapFeatureFetchStatus status, string? message = null)
        : this(data, data is not null
            ? MapFeatureFetchOutcome.Success(data, status)
            : MapFeatureFetchOutcome.Failure("unavailable", message ?? "Semantic map features are unavailable.")) { }

    public MapFeatureFetchStatus Status => Outcome.Status;
    public string? Message => Outcome.FailureReason;
    public bool HasFeatures => Data is not null && Data.FeatureCount > 0;
}

public interface IMapFeatureDataService
{
    Task<MapFeatureFetchResult> FetchAsync(Guid locationId, WebMercatorViewport viewport,
        CancellationToken cancellationToken);
}

public sealed class NullMapFeatureDataService : IMapFeatureDataService
{
    public Task<MapFeatureFetchResult> FetchAsync(Guid locationId, WebMercatorViewport viewport,
        CancellationToken cancellationToken) => Task.FromResult(
        new MapFeatureFetchResult(null, MapFeatureFetchOutcome.Failure(
            "not_configured", "Semantic map features are not configured.")));
}
