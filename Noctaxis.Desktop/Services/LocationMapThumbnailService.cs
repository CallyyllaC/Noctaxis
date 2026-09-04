using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Persistence;
using SkiaSharp;

namespace Noctaxis.Desktop.Services;

public sealed record MapTileSourceDefinition(
    string ProviderId,
    string ProviderName,
    string MapStyleId,
    string RequestTemplate,
    string SourceIdentifier,
    string AttributionText,
    string? AttributionUrl,
    string? LicenceName,
    string? LicenceUrl)
{
    public Uri TileUri(int zoom, int x, int y) => new(RequestTemplate
        .Replace("{z}", zoom.ToString(), StringComparison.Ordinal)
        .Replace("{x}", x.ToString(), StringComparison.Ordinal)
        .Replace("{y}", y.ToString(), StringComparison.Ordinal));

    public string ConfigurationHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{ProviderId}|{MapStyleId}|{SourceIdentifier}"))).ToLowerInvariant();
}

public interface IMapTileSourceProvider { MapTileSourceDefinition Current { get; } }

public sealed class DefaultMapTileSourceProvider : IMapTileSourceProvider
{
    public MapTileSourceDefinition Current { get; } = new(
        "openstreetmap-standard", "OpenStreetMap", "standard",
        "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
        "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
        "\u00A9 OpenStreetMap contributors", "https://www.openstreetmap.org/copyright",
        "Open Database License (ODbL)", "https://opendatacommons.org/licenses/odbl/");
}

public sealed record SavedLocationThumbnailMetadata(
    int SchemaVersion,
    Guid LocationId,
    double Latitude,
    double Longitude,
    int Zoom,
    int Width,
    int Height,
    string ProviderId,
    string ProviderName,
    string MapStyleId,
    string SourceIdentifier,
    string AttributionText,
    string? AttributionUrl,
    string? LicenceName,
    string? LicenceUrl,
    DateTimeOffset GeneratedAtUtc,
    int SourceZoom,
    double SourceCentreLatitude,
    double SourceCentreLongitude,
    int SourceImageWidth,
    int SourceImageHeight,
    int ThumbnailStyleVersion,
    string SourceConfigurationHash,
    string SourceRenderVersion,
    string ImageFileName,
    string SourceFileName = "source.png",
    string? SourceContentHash = null,
    string? ThumbnailContentHash = null,
    DateTimeOffset? ThumbnailGeneratedAtUtc = null,
    string? FeatureFileName = null,
    int? FeatureSchemaVersion = null,
    int? FeatureQueryVersion = null,
    string? FeatureProviderId = null,
    string? FeatureAttributionText = null,
    string? FeatureAttributionUrl = null,
    string? FeatureLicenceName = null,
    string? FeatureLicenceUrl = null,
    DateTimeOffset? FeatureGeneratedAtUtc = null,
    string? FeatureContentHash = null,
    MapGeographicBounds? FeatureBounds = null,
    int? FeatureCount = null,
    int? FeatureOverlayStyleVersion = null,
    string? FeatureFetchStatus = null,
    string? FeatureFailureCode = null,
    string? FeatureFailureReason = null,
    int? FeatureHttpStatusCode = null,
    bool FeatureFetchTimedOut = false,
    bool FeatureResponseTooLarge = false,
    bool FeatureParseFailed = false,
    int FeatureAttemptCount = 0,
    DateTimeOffset? FeatureLastAttemptedAtUtc = null,
    int? FeatureRoadCount = null,
    int? FeatureWaterwayCount = null,
    bool FeatureFallbackAttempted = false,
    string? SettlementProviderId = null,
    string? SettlementDatasetVersion = null,
    string? SettlementStatus = null,
    bool SettlementPartial = false,
    int? SettlementCellCount = null,
    int? SettlementOverlayStyleVersion = null,
    string? SettlementFileName = null,
    int? SettlementSchemaVersion = null,
    string? SettlementContentHash = null,
    GeoBounds? SettlementBounds = null,
    int? SettlementPresetVersion = null,
    string? SettlementStyleSettingsHash = null,
    string? SettlementStatusMessage = null,
    bool SettlementRendered = false,
    int? SettlementActiveCellCount = null,
    double? SettlementMaximumDensity = null,
    int? SettlementGeneratedStarCount = null,
    string? StyledInputHash = null,
    string? ThumbnailRendererId = null,
    int? ThumbnailRendererVersion = null)
{
    public string MapProviderId => ProviderId;
    public string MapProviderAttribution => AttributionText;
    public string? MapProviderAttributionUrl => AttributionUrl;
    public DateTimeOffset GeneratedAt => GeneratedAtUtc;
    public string SourceGenerationVersion => SourceRenderVersion;
    public int ThumbnailWidth => Width;
    public int ThumbnailHeight => Height;
}

public sealed record SavedLocationThumbnailResult(
    string ImagePath,
    SavedLocationThumbnailMetadata Metadata,
    bool RefreshSucceeded,
    bool WasGenerated,
    bool IsPreviousAsset = false,
    SavedLocationMapRefreshResult? Operation = null);

public sealed record SavedLocationMapRefreshResult(
    bool RasterSucceeded,
    bool RasterUsedPrevious,
    MapFeatureFetchOutcome Semantic,
    bool ThumbnailSucceeded,
    bool ThumbnailUsedPrevious,
    string? FailureReason,
    EnvironmentalDataState? SettlementState = null)
{
    public bool IsComplete => RasterSucceeded && ThumbnailSucceeded &&
        (Semantic.Status is MapFeatureFetchStatus.Complete or MapFeatureFetchStatus.CachedPrevious) &&
        SettlementState is EnvironmentalDataState.Available or EnvironmentalDataState.Cached or
            EnvironmentalDataState.Partial or EnvironmentalDataState.Empty;
    public bool IsDegraded => RasterSucceeded && ThumbnailSucceeded && !IsComplete;
}

public enum SavedLocationMapRefreshMode
{
    UseCache,
    RefreshSource,
    ReapplyStyle,
    RefreshFeatures,
    RefreshSettlement
}

public interface ILocationMapThumbnailService
{
    string StorageDirectory { get; }
    Task<SavedLocationThumbnailResult?> GetThumbnailAsync(
        SavedLocation location, bool forceRefresh, CancellationToken cancellationToken);
    Task<SavedLocationThumbnailResult?> GetThumbnailAsync(
        SavedLocation location, SavedLocationMapRefreshMode mode, CancellationToken cancellationToken);
}

public sealed record SlippyMapTile(int Zoom, int X, int Y, double FractionX, double FractionY)
{
    public static SlippyMapTile FromCoordinate(GeoCoordinate coordinate, int zoom)
    {
        if (zoom is < 0 or > 19) throw new ArgumentOutOfRangeException(nameof(zoom));
        var viewport = WebMercatorViewport.Create(coordinate, zoom, LocationMapThumbnailService.TileSize, 1, 1);
        var scale = 1 << zoom;
        var x = viewport.WorldPixelCentreX / LocationMapThumbnailService.TileSize;
        var y = viewport.WorldPixelCentreY / LocationMapThumbnailService.TileSize;
        var tileX = Math.Clamp((int)Math.Floor(x), 0, scale - 1);
        var tileY = Math.Clamp((int)Math.Floor(y), 0, scale - 1);
        return new SlippyMapTile(zoom, tileX, tileY, x - Math.Floor(x), y - Math.Floor(y));
    }
}

/// <summary>
/// Owns clean source-map capture and persisted card artwork. Files are ID-scoped, individually
/// atomically replaced, and content hashes let the loader reject interrupted or mixed generations.
/// </summary>
public sealed class LocationMapThumbnailService : ILocationMapThumbnailService
{
    public const int TileZoom = 13;
    public const int OutputWidth = SavedLocationMapImageProcessor.ThumbnailWidth;
    public const int OutputHeight = SavedLocationMapImageProcessor.ThumbnailHeight;
    public const int ThumbnailStyleVersion = SavedLocationMapImageProcessor.StyleVersion;
    public const string SourceRenderVersion = "direct-tile-mosaic-v3";
    public const int SourceWidth = 896;
    public const int SourceHeight = 504;
    public const int TileSize = 256;
    private const int MetadataSchemaVersion = 9;
    private const int MaximumCaptureAttempts = 3;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocationMapThumbnailService> _logger;
    private readonly IMapTileSourceProvider _sources;
    private readonly SavedLocationMapImageProcessor _processor;
    private readonly IMapFeatureDataService _featureData;
    private readonly ISettlementDataProvider _settlementData;
    private readonly IMapImageAcceleration _imageAcceleration;
    private readonly string _legacyStorageDirectory;
    private readonly ConcurrentDictionary<(string Provider, string Style, int Zoom, int X, int Y), byte[]> _tileCache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locationGates = new();
    private readonly SemaphoreSlim _tileRequestGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly JsonSerializerOptions _featureJson = new(JsonSerializerDefaults.Web);

    public LocationMapThumbnailService(HttpClient httpClient, ILogger<LocationMapThumbnailService> logger,
        IUserDataPathProvider paths, IMapTileSourceProvider sources, SavedLocationMapImageProcessor processor,
        IMapFeatureDataService? featureData = null, ISettlementDataProvider? settlementData = null,
        IMapImageAcceleration? imageAcceleration = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _sources = sources;
        _processor = processor;
        _featureData = featureData ?? new NullMapFeatureDataService();
        _settlementData = settlementData ?? new UnavailableSettlementDataProvider();
        _imageAcceleration = imageAcceleration ?? OpenCvMapImageAcceleration.Shared;
        var applicationData = paths.GetApplicationDataDirectory();
        StorageDirectory = Path.Combine(applicationData, "SavedLocationMaps");
        _legacyStorageDirectory = Path.Combine(applicationData, "SavedLocationThumbnails");
    }

    public string StorageDirectory { get; }

    public Task<SavedLocationThumbnailResult?> GetThumbnailAsync(SavedLocation location, bool forceRefresh,
        CancellationToken cancellationToken) => GetThumbnailAsync(location,
        forceRefresh ? SavedLocationMapRefreshMode.RefreshSource : SavedLocationMapRefreshMode.UseCache,
        cancellationToken);

    public async Task<SavedLocationThumbnailResult?> GetThumbnailAsync(SavedLocation location,
        SavedLocationMapRefreshMode mode, CancellationToken cancellationToken)
    {
        var gate = _locationGates.GetOrAdd(location.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (mode != SavedLocationMapRefreshMode.UseCache)
                await MigrateLegacyThumbnailAsync(location.Id, cancellationToken).ConfigureAwait(false);
            var previous = await TryLoadPreviousAsync(location.Id, location, cancellationToken).ConfigureAwait(false);
            try
            {
                if (mode == SavedLocationMapRefreshMode.UseCache)
                {
                    // Ordinary card loading never performs network acquisition. A persisted thumbnail
                    // with stale renderer/style identity is the one exception to read-only reuse: it is
                    // recomposed from the already-owned source/vector/settlement assets.
                    var complete = await TryLoadCompleteAsync(location, cancellationToken).ConfigureAwait(false);
                    if (complete is not null)
                    {
                        _logger.LogDebug("Saved-location map source cache reused at {SourcePath}", complete.SourcePath);
                        LogThumbnailRoute(complete.Metadata, complete.ThumbnailPath, generated: false);
                        return new SavedLocationThumbnailResult(complete.ThumbnailPath, complete.Metadata, true, false);
                    }
                    var metadata = await ReadMetadataAsync(location.Id, cancellationToken).ConfigureAwait(false);
                    var thumbnailPath = Path.Combine(LocationDirectory(location.Id), "thumbnail.png");
                    var staleReason = metadata is null ? null : StyledCacheIdentityIssue(metadata);
                    if (metadata is not null && CoordinatesMatch(metadata, location.Coordinate) &&
                        File.Exists(thumbnailPath) && staleReason is not null)
                    {
                        _logger.LogDebug(
                            "Cached saved-location thumbnail rejected as stale ({StaleReason}); recomposing locally without source acquisition",
                            staleReason);
                        var styleSource = await TryLoadStyleSourceAsync(location, cancellationToken)
                            .ConfigureAwait(false);
                        if (styleSource is not null)
                            return await ProcessAndCommitStyleAsync(styleSource, cancellationToken)
                                .ConfigureAwait(false);
                    }
                    return null;
                }
                else if (mode == SavedLocationMapRefreshMode.ReapplyStyle)
                {
                    var styleSource = await TryLoadStyleSourceAsync(location, cancellationToken).ConfigureAwait(false);
                    if (styleSource is null)
                        throw new InvalidDataException("No valid saved source.png is available for style reapplication.");
                    return await ProcessAndCommitStyleAsync(styleSource, cancellationToken).ConfigureAwait(false);
                }
                else if (mode == SavedLocationMapRefreshMode.RefreshFeatures)
                {
                    var styleSource = await TryLoadStyleSourceAsync(location, cancellationToken).ConfigureAwait(false);
                    if (styleSource is null)
                        throw new InvalidDataException(
                            "No valid saved source.png and viewport metadata are available for semantic refresh.");
                    return await RefreshFeaturesAndCommitAsync(location, styleSource, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (mode == SavedLocationMapRefreshMode.RefreshSettlement)
                {
                    var styleSource = await TryLoadStyleSourceAsync(location, cancellationToken).ConfigureAwait(false);
                    if (styleSource is null)
                        throw new InvalidDataException(
                            "No valid saved source.png and viewport metadata are available for settlement refresh.");
                    return await RefreshSettlementAndCommitAsync(location, styleSource, cancellationToken)
                        .ConfigureAwait(false);
                }
                return await GenerateSourceAndCommitAsync(location, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or
                                             InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex,
                    "Saved-location map operation {RefreshMode} failed for location {LocationId}",
                    mode, location.Id);
                if (previous is null) return null;
                if (mode == SavedLocationMapRefreshMode.UseCache &&
                    StyledCacheIdentityIssue(previous.Metadata) is not null)
                {
                    _logger.LogDebug(
                        "Stale renderer output for {LocationId} was not returned after local recomposition failed",
                        location.Id);
                    return null;
                }
                var semanticFailure = MapFeatureFetchOutcome.Failure(
                    mode is SavedLocationMapRefreshMode.RefreshFeatures or SavedLocationMapRefreshMode.RefreshSettlement
                        ? "semantic_refresh_failed" : "operation_failed",
                    mode is SavedLocationMapRefreshMode.RefreshFeatures or SavedLocationMapRefreshMode.RefreshSettlement
                        ? "the semantic overlay refresh could not be completed"
                        : "the map refresh could not be completed");
                return previous with
                {
                    RefreshSucceeded = false,
                    IsPreviousAsset = true,
                    Operation = new SavedLocationMapRefreshResult(false, true, semanticFailure,
                        true, true, ex.Message, mode == SavedLocationMapRefreshMode.RefreshSettlement
                            ? EnvironmentalDataState.Unavailable : null)
                };
            }
        }
        finally { gate.Release(); }
    }

    private async Task<SavedLocationThumbnailResult> GenerateSourceAndCommitAsync(
        SavedLocation location, CancellationToken cancellationToken)
    {
        var mapSource = _sources.Current;
        var viewport = WebMercatorViewport.Create(location.Coordinate, TileZoom, TileSize, SourceWidth, SourceHeight);
        var previousMetadata = await ReadMetadataAsync(location.Id, cancellationToken).ConfigureAwait(false);
        var previousSettlement = previousMetadata is null ? null :
            await TryLoadSettlementDataAsync(location.Id, viewport, previousMetadata, cancellationToken)
                .ConfigureAwait(false);
        _logger.LogInformation(
            "Saved-location source generation started for {LocationId} ({LocationName}); provider {ProviderId}, source version {SourceVersion}",
            location.Id, location.Name, mapSource.ProviderId, SourceRenderVersion);
        var sourcePng = await CaptureValidatedSourceAsync(viewport, mapSource, cancellationToken)
            .ConfigureAwait(false);
        var previousFeatures = await TryLoadFeatureDataAsync(location.Id, location.Coordinate, viewport,
            cancellationToken).ConfigureAwait(false);
        MapFeatureFetchResult featureFetch;
        try
        {
            featureFetch = await _featureData.FetchAsync(location.Id, viewport, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(ex, "OSM semantic overlay fetch failed for {LocationId}; using compatible cache or base map",
                location.Id);
            featureFetch = new MapFeatureFetchResult(null, MapFeatureFetchStatus.Unavailable, ex.Message);
        }

        ExistingFeatureAssets? features = null;
        var semanticOutcome = featureFetch.Outcome;
        if (featureFetch.Data is not null && featureFetch.Data.LocationId == location.Id &&
            BoundsMatch(featureFetch.Data.Source.Bounds, viewport.Bounds))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(featureFetch.Data, _featureJson);
            features = new ExistingFeatureAssets(featureFetch.Data, bytes);
        }
        else if (featureFetch.Data is not null)
        {
            _logger.LogWarning(
                "Discarded OSM feature result with mismatched ownership or bounds for {LocationId}", location.Id);
            semanticOutcome = MapFeatureFetchOutcome.Failure("geometry_validation_failed",
                "feature ownership or geographic bounds did not match the raster viewport",
                featureFetch.Outcome.AttemptCount, fallbackAttempted: featureFetch.Outcome.FallbackAttempted);
        }
        if (features is null && previousFeatures is not null)
        {
            features = previousFeatures;
            semanticOutcome = semanticOutcome with
            {
                Status = MapFeatureFetchStatus.CachedPrevious,
                RoadCount = previousFeatures.Data.Roads.Length,
                WaterwayCount = previousFeatures.Data.Waterways.Length
            };
            _logger.LogInformation("Compatible OSM feature cache retained for {LocationId}", location.Id);
        }
        else if (features is null)
            _logger.LogInformation("Generating saved-location thumbnail without a semantic overlay for {LocationId}",
                location.Id);

        var settlement = await FetchSettlementAsync(viewport, cancellationToken).ConfigureAwait(false);
        if (!settlement.HasValue && previousSettlement is not null)
        {
            var previousWasEmpty = previousMetadata?.SettlementStatus == nameof(EnvironmentalDataState.Empty);
            settlement = new EnvironmentalValue<SettlementRaster>(
                previousWasEmpty ? EnvironmentalDataState.Empty : EnvironmentalDataState.Cached,
                previousSettlement.Data, previousSettlement.Data.DatasetId, previousSettlement.Data.DatasetVersion,
                $"Compatible saved WSF derivative retained after refresh failure: {settlement.Message}");
            _logger.LogWarning(
                "WSF acquisition failed for {LocationId}; retaining compatible saved derivative ({SettlementState})",
                location.Id, settlement.State);
        }
        var settlementBytes = settlement.Value is null ? null : SettlementRasterCodec.Encode(settlement.Value);

        _logger.LogDebug(
            "Processing saved-location thumbnail using style {StyleVersion}, feature overlay {OverlayVersion}, and {ImageBackend} ({NativeThreads} native threads)",
            ThumbnailStyleVersion, SavedLocationMapImageProcessor.FeatureOverlayStyleVersion,
            _imageAcceleration.Status.Backend, _imageAcceleration.Status.NativeThreadCount);
        var thumbnailPng = _processor.ProcessSettlement(sourcePng, features?.Data, settlement.Value, viewport,
            location.Id, out var settlementRender);
        var now = DateTimeOffset.UtcNow;
        var featureSource = features?.Data.Source;
        var metadata = new SavedLocationThumbnailMetadata(
            MetadataSchemaVersion, location.Id, location.Coordinate.Latitude, location.Coordinate.Longitude,
            TileZoom, OutputWidth, OutputHeight, mapSource.ProviderId, mapSource.ProviderName,
            mapSource.MapStyleId, mapSource.SourceIdentifier, mapSource.AttributionText,
            mapSource.AttributionUrl, mapSource.LicenceName, mapSource.LicenceUrl, now, TileZoom,
            location.Coordinate.Latitude, location.Coordinate.Longitude, SourceWidth, SourceHeight,
            ThumbnailStyleVersion, mapSource.ConfigurationHash, SourceRenderVersion, "thumbnail.png",
            "source.png", Hash(sourcePng), Hash(thumbnailPng), now,
            features is null ? null : "features.json",
            features?.Data.SchemaVersion, featureSource?.QueryVersion, featureSource?.ProviderId,
            featureSource?.AttributionText, featureSource?.AttributionUrl, featureSource?.LicenceName,
            featureSource?.LicenceUrl, featureSource?.FetchedAtUtc, features is null ? null : Hash(features.Bytes),
            featureSource?.Bounds, features?.Data.FeatureCount,
            SavedLocationMapImageProcessor.FeatureOverlayStyleVersion, semanticOutcome.Status.ToString(),
            semanticOutcome.FailureCode, semanticOutcome.FailureReason, semanticOutcome.HttpStatusCode,
            semanticOutcome.TimedOut, semanticOutcome.ResponseTooLarge, semanticOutcome.ParseFailed,
            semanticOutcome.AttemptCount, semanticOutcome.AttemptedAtUtc, semanticOutcome.RoadCount,
            semanticOutcome.WaterwayCount, semanticOutcome.FallbackAttempted);
        metadata = ApplySettlementOutcome(metadata, settlement, settlementRender, settlementBytes);
        var operation = new SavedLocationMapRefreshResult(true, false, semanticOutcome, true, false, null,
            settlement.State);
        var result = await CommitFullAssetSetAsync(location.Id, sourcePng, features?.Bytes, settlementBytes,
                thumbnailPng, metadata,
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Saved-location source and thumbnail completed at {Directory}; source {SourceVersion}, style {StyleVersion}",
            LocationDirectory(location.Id), SourceRenderVersion, ThumbnailStyleVersion);
        LogThumbnailRoute(result.Metadata, result.ImagePath, generated: true);
        return result with { Operation = operation };
    }

    private async Task<SavedLocationThumbnailResult> RefreshFeaturesAndCommitAsync(
        SavedLocation location, ExistingMapAssets assets, CancellationToken cancellationToken)
    {
        var viewport = new WebMercatorViewport(assets.Metadata.SourceZoom, TileSize,
            assets.Metadata.SourceImageWidth, assets.Metadata.SourceImageHeight,
            assets.Metadata.SourceCentreLatitude, assets.Metadata.SourceCentreLongitude);
        _logger.LogInformation(
            "Semantic-only saved-location refresh started for {LocationId} ({LocationName}); source {SourcePath} remains unchanged",
            assets.Metadata.LocationId, location.Name, assets.SourcePath);

        MapFeatureFetchResult fetch;
        try
        {
            fetch = await _featureData.FetchAsync(assets.Metadata.LocationId, viewport, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic-only feature request failed for {LocationId}", assets.Metadata.LocationId);
            fetch = new MapFeatureFetchResult(null,
                MapFeatureFetchOutcome.Failure("request_failed", "the semantic feature request failed"));
        }

        if (fetch.Data is not null &&
            (fetch.Data.LocationId != assets.Metadata.LocationId || !BoundsMatch(fetch.Data.Source.Bounds, viewport.Bounds)))
        {
            fetch = new MapFeatureFetchResult(null,
                MapFeatureFetchOutcome.Failure("geometry_validation_failed",
                    "feature ownership or geographic bounds did not match the raster viewport",
                    fetch.Outcome.AttemptCount, fallbackAttempted: fetch.Outcome.FallbackAttempted));
        }

        var settlement = CachedSettlement(assets.Settlement, assets.Metadata);

        if (fetch.Data is not null)
        {
            try
            {
                var featureBytes = JsonSerializer.SerializeToUtf8Bytes(fetch.Data, _featureJson);
                var thumbnailBytes = _processor.ProcessSettlement(assets.SourceBytes, fetch.Data,
                    settlement.Value, viewport, location.Id, out var settlementRender);
                var metadata = ApplyFeatureOutcome(assets.Metadata, fetch.Outcome, fetch.Data, featureBytes,
                    thumbnailBytes);
                metadata = ApplySettlementOutcome(metadata, settlement, settlementRender,
                    assets.Settlement?.Bytes);
                await CommitFilesAtomicallyAsync(LocationDirectory(metadata.LocationId),
                [
                    ("features.json", featureBytes),
                    ("thumbnail.png", thumbnailBytes),
                    ("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, _json)))
                ], cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Semantic-only refresh completed for {LocationId}: {Status}; roads {RoadCount}, waterways {WaterwayCount}",
                    metadata.LocationId, fetch.Outcome.Status, fetch.Outcome.RoadCount,
                    fetch.Outcome.WaterwayCount);
                return new SavedLocationThumbnailResult(assets.ThumbnailPath, metadata, true, true, false,
                    new SavedLocationMapRefreshResult(true, true, fetch.Outcome, true, false, null,
                        settlement.State));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or
                                             UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex,
                    "Semantic feature data was fetched but could not be composed or persisted for {LocationId}; previous assets retained",
                    assets.Metadata.LocationId);
                fetch = new MapFeatureFetchResult(null, MapFeatureFetchOutcome.Failure(
                    ex is InvalidDataException ? "thumbnail_processing_failed" : "persistence_failed",
                    ex is InvalidDataException
                        ? "the semantic thumbnail could not be validated"
                        : "the semantic assets could not be persisted",
                    fetch.Outcome.AttemptCount, fallbackAttempted: fetch.Outcome.FallbackAttempted));
            }
        }

        var retained = assets.Features;
        var retainedOutcome = fetch.Outcome;
        if (retained is not null)
        {
            retainedOutcome = retainedOutcome with
            {
                Status = MapFeatureFetchStatus.CachedPrevious,
                RoadCount = retained.Data.Roads.Length,
                WaterwayCount = retained.Data.Waterways.Length
            };
        }
        var failedMetadata = ApplyFeatureOutcome(assets.Metadata, retainedOutcome,
            retained?.Data, retained?.Bytes, thumbnailBytes: null, preserveThumbnailMetadata: true);
        await CommitFilesAtomicallyAsync(LocationDirectory(failedMetadata.LocationId),
            [("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(failedMetadata, _json)))],
            cancellationToken).ConfigureAwait(false);
        var thumbnailUsable = File.Exists(assets.ThumbnailPath);
        return new SavedLocationThumbnailResult(assets.ThumbnailPath, failedMetadata, thumbnailUsable, false,
            thumbnailUsable, new SavedLocationMapRefreshResult(true, true, retainedOutcome,
                thumbnailUsable, thumbnailUsable, retainedOutcome.FailureReason));
    }

    private async Task<SavedLocationThumbnailResult> ProcessAndCommitStyleAsync(
        ExistingMapAssets assets, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reapplying saved-location map style {StyleVersion} from {SourcePath}",
            ThumbnailStyleVersion, assets.SourcePath);
        var viewport = new WebMercatorViewport(assets.Metadata.SourceZoom, TileSize,
            assets.Metadata.SourceImageWidth, assets.Metadata.SourceImageHeight,
            assets.Metadata.SourceCentreLatitude, assets.Metadata.SourceCentreLongitude);
        var settlement = CachedSettlement(assets.Settlement, assets.Metadata);
        var thumbnailPng = _processor.ProcessSettlement(assets.SourceBytes, assets.Features?.Data,
            settlement.Value, viewport, assets.Metadata.LocationId, out var settlementRender);
        var metadata = assets.Metadata with
        {
            SchemaVersion = MetadataSchemaVersion,
            Width = OutputWidth,
            Height = OutputHeight,
            ThumbnailStyleVersion = ThumbnailStyleVersion,
            ImageFileName = "thumbnail.png",
            SourceFileName = "source.png",
            SourceContentHash = Hash(assets.SourceBytes),
            ThumbnailContentHash = Hash(thumbnailPng),
            ThumbnailGeneratedAtUtc = DateTimeOffset.UtcNow,
            FeatureOverlayStyleVersion = SavedLocationMapImageProcessor.FeatureOverlayStyleVersion
        };
        metadata = ApplySettlementOutcome(metadata, settlement, settlementRender, assets.Settlement?.Bytes);
        await CommitFilesAtomicallyAsync(LocationDirectory(metadata.LocationId),
            [("thumbnail.png", thumbnailPng), ("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, _json)))],
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved-location thumbnail style completed at {ThumbnailPath}", assets.ThumbnailPath);
        LogThumbnailRoute(metadata, assets.ThumbnailPath, generated: true);
        return new SavedLocationThumbnailResult(assets.ThumbnailPath, metadata, true, true);
    }

    private async Task<SavedLocationThumbnailResult> RefreshSettlementAndCommitAsync(
        SavedLocation location, ExistingMapAssets assets, CancellationToken cancellationToken)
    {
        var viewport = new WebMercatorViewport(assets.Metadata.SourceZoom, TileSize,
            assets.Metadata.SourceImageWidth, assets.Metadata.SourceImageHeight,
            assets.Metadata.SourceCentreLatitude, assets.Metadata.SourceCentreLongitude);
        _logger.LogInformation(
            "WSF settlement refresh started for {LocationId} ({LocationName}); source and OSM vector features remain unchanged",
            location.Id, location.Name);
        var settlement = await FetchSettlementAsync(viewport, cancellationToken).ConfigureAwait(false);
        if (settlement.HasValue)
        {
            var settlementBytes = SettlementRasterCodec.Encode(settlement.Value!);
            var thumbnailBytes = _processor.ProcessSettlement(assets.SourceBytes, assets.Features?.Data,
                settlement.Value, viewport, location.Id, out var settlementRender);
            var metadata = ApplySettlementOutcome(assets.Metadata with
            {
                ThumbnailContentHash = Hash(thumbnailBytes),
                ThumbnailGeneratedAtUtc = DateTimeOffset.UtcNow
            }, settlement, settlementRender, settlementBytes);
            await CommitFilesAtomicallyAsync(LocationDirectory(location.Id),
            [
                ("settlement-field.bin.gz", settlementBytes),
                ("thumbnail.png", thumbnailBytes),
                ("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, _json)))
            ], cancellationToken).ConfigureAwait(false);
            var core = CoreOutcomeFromAssets(assets.Features);
            _logger.LogInformation(
                "WSF settlement refresh completed for {LocationId}: {Status}, {DensityCells} active cells, maximum mass {MaximumDensity}",
                location.Id, settlement.State, settlementRender?.DensityCellCount ?? 0,
                settlementRender?.MaximumDensityBeforeClamping ?? 0);
            return new SavedLocationThumbnailResult(assets.ThumbnailPath, metadata, true, true, false,
                new SavedLocationMapRefreshResult(true, true, core, true, false, null, settlement.State));
        }

        _logger.LogWarning("WSF settlement refresh unavailable for {LocationId}; previous thumbnail retained",
            location.Id);
        var retainedSettlement = assets.Settlement is null ? settlement :
            new EnvironmentalValue<SettlementRaster>(EnvironmentalDataState.Cached, assets.Settlement.Data,
                assets.Settlement.Data.DatasetId, assets.Settlement.Data.DatasetVersion,
                $"Compatible saved WSF derivative retained after refresh failure: {settlement.Message}");
        var failedMetadata = ApplySettlementOutcome(assets.Metadata, retainedSettlement, null, null,
            preserveSettlementMetadata: true, thumbnailRendered: false);
        await CommitFilesAtomicallyAsync(LocationDirectory(location.Id),
            [("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(failedMetadata, _json)))],
            cancellationToken).ConfigureAwait(false);
        var thumbnailUsable = File.Exists(assets.ThumbnailPath);
        return new SavedLocationThumbnailResult(assets.ThumbnailPath, failedMetadata, thumbnailUsable, false,
            thumbnailUsable, new SavedLocationMapRefreshResult(true, true,
                CoreOutcomeFromAssets(assets.Features), thumbnailUsable, thumbnailUsable,
                settlement.Message, settlement.State));
    }

    private static SavedLocationThumbnailMetadata ApplyFeatureOutcome(
        SavedLocationThumbnailMetadata metadata,
        MapFeatureFetchOutcome outcome,
        MapFeatureDataDocument? data,
        byte[]? featureBytes,
        byte[]? thumbnailBytes,
        bool preserveThumbnailMetadata = false)
    {
        var source = data?.Source;
        return metadata with
        {
            SchemaVersion = MetadataSchemaVersion,
            FeatureFileName = data is null ? null : "features.json",
            FeatureSchemaVersion = data?.SchemaVersion,
            FeatureQueryVersion = source?.QueryVersion,
            FeatureProviderId = source?.ProviderId,
            FeatureAttributionText = source?.AttributionText,
            FeatureAttributionUrl = source?.AttributionUrl,
            FeatureLicenceName = source?.LicenceName,
            FeatureLicenceUrl = source?.LicenceUrl,
            FeatureGeneratedAtUtc = source?.FetchedAtUtc,
            FeatureContentHash = featureBytes is null ? null : Hash(featureBytes),
            FeatureBounds = source?.Bounds,
            FeatureCount = data?.FeatureCount,
            FeatureOverlayStyleVersion = SavedLocationMapImageProcessor.FeatureOverlayStyleVersion,
            FeatureFetchStatus = outcome.Status.ToString(),
            FeatureFailureCode = outcome.FailureCode,
            FeatureFailureReason = outcome.FailureReason,
            FeatureHttpStatusCode = outcome.HttpStatusCode,
            FeatureFetchTimedOut = outcome.TimedOut,
            FeatureResponseTooLarge = outcome.ResponseTooLarge,
            FeatureParseFailed = outcome.ParseFailed,
            FeatureAttemptCount = outcome.AttemptCount,
            FeatureLastAttemptedAtUtc = outcome.AttemptedAtUtc,
            FeatureRoadCount = outcome.RoadCount,
            FeatureWaterwayCount = outcome.WaterwayCount,
            FeatureFallbackAttempted = outcome.FallbackAttempted,
            ThumbnailContentHash = preserveThumbnailMetadata || thumbnailBytes is null
                ? metadata.ThumbnailContentHash
                : Hash(thumbnailBytes),
            ThumbnailGeneratedAtUtc = preserveThumbnailMetadata || thumbnailBytes is null
                ? metadata.ThumbnailGeneratedAtUtc
                : DateTimeOffset.UtcNow
        };
    }

    private SavedLocationThumbnailMetadata ApplySettlementOutcome(
        SavedLocationThumbnailMetadata metadata,
        EnvironmentalValue<SettlementRaster> settlement,
        SettlementRenderDiagnostics? render,
        byte[]? settlementBytes,
        bool preserveSettlementMetadata = false,
        bool thumbnailRendered = true)
    {
        var updated = metadata with
        {
            SchemaVersion = MetadataSchemaVersion,
            SettlementProviderId = settlement.SourceId,
            SettlementDatasetVersion = settlement.SourceVersion,
            SettlementStatus = settlement.State.ToString(),
            SettlementStatusMessage = settlement.Message,
            SettlementPartial = settlement.State == EnvironmentalDataState.Partial ||
                                settlement.Value?.IsPartial == true,
            SettlementCellCount = settlement.Value?.CellCount,
            SettlementOverlayStyleVersion = SavedLocationMapImageProcessor.SettlementGlowStyleVersion,
            SettlementPresetVersion = _processor.SettlementPresetVersion,
            SettlementStyleSettingsHash = _processor.SettlementStyleSettingsHash,
            SettlementFileName = preserveSettlementMetadata || settlement.Value is null
                ? metadata.SettlementFileName : "settlement-field.bin.gz",
            SettlementSchemaVersion = preserveSettlementMetadata || settlement.Value is null
                ? metadata.SettlementSchemaVersion : SettlementRasterCodec.SchemaVersion,
            SettlementContentHash = preserveSettlementMetadata || settlementBytes is null
                ? metadata.SettlementContentHash : Hash(settlementBytes),
            SettlementBounds = preserveSettlementMetadata || settlement.Value is null
                ? metadata.SettlementBounds : settlement.Value.Grid.Bounds,
            SettlementRendered = render?.SettlementRendered ??
                (preserveSettlementMetadata ? metadata.SettlementRendered : false),
            SettlementActiveCellCount = render?.ActiveSettlementCellCount ??
                (preserveSettlementMetadata ? metadata.SettlementActiveCellCount : null),
            SettlementMaximumDensity = render?.MaximumDensityBeforeClamping ??
                (preserveSettlementMetadata ? metadata.SettlementMaximumDensity : null),
            SettlementGeneratedStarCount = render?.GeneratedStarCount ??
                (preserveSettlementMetadata ? metadata.SettlementGeneratedStarCount : null),
            ThumbnailRendererId = thumbnailRendered
                ? SavedLocationMapImageProcessor.RendererId : metadata.ThumbnailRendererId,
            ThumbnailRendererVersion = thumbnailRendered
                ? SavedLocationMapImageProcessor.RendererVersion : metadata.ThumbnailRendererVersion
        };
        return updated with { StyledInputHash = ComputeStyledInputHash(updated) };
    }

    private static EnvironmentalValue<SettlementRaster> CachedSettlement(ExistingSettlementAssets? assets,
        SavedLocationThumbnailMetadata metadata) =>
        assets is null
            ? EnvironmentalValue<SettlementRaster>.Unavailable(WsfSettlementDataProvider.SourceId,
                WsfSettlementDataProvider.SourceVersion, "No compatible saved WSF settlement derivative is available.")
            : new EnvironmentalValue<SettlementRaster>(
                metadata.SettlementStatus == nameof(EnvironmentalDataState.Empty)
                    ? EnvironmentalDataState.Empty : EnvironmentalDataState.Cached,
                assets.Data, assets.Data.DatasetId, assets.Data.DatasetVersion,
                metadata.SettlementStatus == nameof(EnvironmentalDataState.Empty)
                    ? "Saved valid zero-settlement WSF derivative reused."
                    : "Saved WSF settlement derivative reused.");

    private async Task<EnvironmentalValue<SettlementRaster>> FetchSettlementAsync(
        WebMercatorViewport viewport, CancellationToken cancellationToken)
    {
        var bounds = viewport.Bounds;
        var request = new GeoRasterRequest(new GeoBounds(bounds.South, bounds.West, bounds.North, bounds.East),
            viewport.Width * SettlementDensityBuilder.Supersampling,
            viewport.Height * SettlementDensityBuilder.Supersampling,
            GeoRasterProjection.WebMercator);
        try
        {
            return await _settlementData.GetSettlementAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            _logger.LogWarning(ex, "WSF settlement grid could not be acquired for viewport {Bounds}", bounds);
            var state = ex is InvalidDataException
                ? EnvironmentalDataState.InvalidRaster
                : EnvironmentalDataState.SourceUnavailable;
            return new EnvironmentalValue<SettlementRaster>(state, null, WsfSettlementDataProvider.SourceId,
                WsfSettlementDataProvider.SourceVersion,
                state == EnvironmentalDataState.InvalidRaster
                    ? "WSF settlement raster failed validation."
                    : "The WSF settlement source could not be reached.");
        }
    }

    private static MapFeatureFetchOutcome CoreOutcomeFromAssets(ExistingFeatureAssets? features) =>
        features is null
            ? MapFeatureFetchOutcome.Failure("core_features_unavailable",
                "Road and water overlays are unavailable.", attemptCount: 0)
            : new MapFeatureFetchOutcome(MapFeatureFetchStatus.CachedPrevious,
                features.Data.Roads.Length, features.Data.Waterways.Length,
                null, "Compatible road and water overlays reused.", null,
                false, false, false, 0, DateTimeOffset.UtcNow);

    private async Task<SavedLocationThumbnailResult> CommitFullAssetSetAsync(Guid locationId, byte[] sourcePng,
        byte[]? featureBytes, byte[]? settlementBytes, byte[] thumbnailPng,
        SavedLocationThumbnailMetadata metadata,
        CancellationToken cancellationToken)
    {
        var directory = LocationDirectory(locationId);
        var files = new List<(string FileName, byte[] Bytes)>
        {
            ("source.png", sourcePng),
            ("thumbnail.png", thumbnailPng),
            ("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, _json)))
        };
        if (featureBytes is not null) files.Insert(1, ("features.json", featureBytes));
        if (settlementBytes is not null) files.Insert(featureBytes is null ? 1 : 2,
            ("settlement-field.bin.gz", settlementBytes));
        await CommitFilesAtomicallyAsync(directory, files, cancellationToken).ConfigureAwait(false);
        if (featureBytes is null) TryDelete(Path.Combine(directory, "features.json"));
        // Legacy building caches are left untouched but no longer referenced by v6 metadata.
        // This makes migration non-destructive while ensuring they cannot feed WSF rendering.
        return new SavedLocationThumbnailResult(Path.Combine(directory, "thumbnail.png"), metadata, true, true);
    }

    private async Task<byte[]> CaptureValidatedSourceAsync(WebMercatorViewport viewport,
        MapTileSourceDefinition source, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= MaximumCaptureAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var png = await CaptureCentredMapAsync(viewport, source, forceTileRefresh: attempt > 1,
                    timeout.Token).ConfigureAwait(false);
                stopwatch.Stop();
                var validation = _processor.ValidateSource(png);
                _logger.LogInformation(
                    "Saved-location tile/render wait completed in {ElapsedMilliseconds} ms; attempt {Attempt}/{MaximumAttempts}; valid {IsValid}; {Reason}",
                    stopwatch.ElapsedMilliseconds, attempt, MaximumCaptureAttempts, validation.IsValid, validation.Reason);
                if (validation.IsValid) return png;
                lastFailure = new InvalidDataException(validation.Reason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                stopwatch.Stop();
                lastFailure = ex;
                _logger.LogWarning(ex,
                    "Saved-location source attempt {Attempt}/{MaximumAttempts} failed after {ElapsedMilliseconds} ms",
                    attempt, MaximumCaptureAttempts, stopwatch.ElapsedMilliseconds);
            }
            if (attempt < MaximumCaptureAttempts)
            {
                _logger.LogInformation("Retrying saved-location source generation after incomplete capture");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new InvalidDataException("Map source did not produce a usable image after bounded retries.", lastFailure);
    }

    private async Task<byte[]> CaptureCentredMapAsync(WebMercatorViewport viewport, MapTileSourceDefinition source,
        bool forceTileRefresh, CancellationToken cancellationToken)
    {
        var captureLeft = viewport.WorldPixelLeft;
        var captureTop = viewport.WorldPixelTop;
        var firstTileX = (int)Math.Floor(captureLeft / TileSize);
        var firstTileY = (int)Math.Floor(captureTop / TileSize);
        var lastTileX = (int)Math.Floor((captureLeft + viewport.Width - 1) / TileSize);
        var lastTileY = (int)Math.Floor((captureTop + viewport.Height - 1) / TileSize);
        var tileCountX = lastTileX - firstTileX + 1;
        var tileCountY = lastTileY - firstTileY + 1;
        using var mosaic = new SKBitmap(tileCountX * TileSize, tileCountY * TileSize,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var mosaicCanvas = new SKCanvas(mosaic);
        mosaicCanvas.Clear(SKColors.Transparent);
        var scale = 1 << viewport.Zoom;
        for (var tileY = firstTileY; tileY <= lastTileY; tileY++)
        {
            if (tileY < 0 || tileY >= scale) continue;
            for (var rawTileX = firstTileX; rawTileX <= lastTileX; rawTileX++)
            {
                var tileX = ((rawTileX % scale) + scale) % scale;
                var bytes = await GetTileAsync(source, viewport.Zoom, tileX, tileY, forceTileRefresh, cancellationToken)
                    .ConfigureAwait(false);
                using var tile = SKBitmap.Decode(bytes) ??
                                 throw new InvalidDataException("Map source returned an unreadable tile.");
                mosaicCanvas.DrawBitmap(tile, (rawTileX - firstTileX) * TileSize,
                    (tileY - firstTileY) * TileSize);
            }
        }
        using var capture = new SKBitmap(viewport.Width, viewport.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var captureCanvas = new SKCanvas(capture);
        var cropX = (float)(captureLeft - firstTileX * TileSize);
        var cropY = (float)(captureTop - firstTileY * TileSize);
        captureCanvas.DrawBitmap(mosaic,
            new SKRect(cropX, cropY, cropX + viewport.Width, cropY + viewport.Height),
            SKRect.Create(viewport.Width, viewport.Height));
        using var image = SKImage.FromBitmap(capture);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 96);
        return encoded.ToArray();
    }

    private async Task<byte[]> GetTileAsync(MapTileSourceDefinition source, int zoom, int x, int y,
        bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = (source.ProviderId, source.MapStyleId, zoom, x, y);
        if (!forceRefresh && _tileCache.TryGetValue(key, out var cached)) return cached;
        await _tileRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (forceRefresh) _tileCache.TryRemove(key, out _);
            else if (_tileCache.TryGetValue(key, out cached)) return cached;
            using var response = await _httpClient.GetAsync(source.TileUri(zoom, x, y),
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 64) throw new InvalidDataException("Map source returned an incomplete tile payload.");
            _tileCache[key] = bytes;
            return bytes;
        }
        finally { _tileRequestGate.Release(); }
    }

    private async Task<ExistingMapAssets?> TryLoadCompleteAsync(SavedLocation location,
        CancellationToken cancellationToken)
    {
        var assets = await TryLoadStyleSourceAsync(location, cancellationToken).ConfigureAwait(false);
        if (assets is null || !File.Exists(assets.ThumbnailPath)) return null;
        var staleReason = StyledCacheIdentityIssue(assets.Metadata);
        if (staleReason is not null)
        {
            _logger.LogDebug("Cached saved-location thumbnail rejected: {StaleReason}", staleReason);
            return null;
        }
        var settlementIssue = SettlementCacheCompletenessIssue(assets.Metadata, assets.Settlement);
        if (settlementIssue is not null)
        {
            _logger.LogDebug("Cached saved-location thumbnail rejected as degraded: {StaleReason}", settlementIssue);
            return null;
        }
        var thumbnail = await File.ReadAllBytesAsync(assets.ThumbnailPath, cancellationToken).ConfigureAwait(false);
        var validation = _processor.ValidateThumbnail(thumbnail);
        if (!validation.IsValid)
        {
            _logger.LogDebug("Cached saved-location thumbnail rejected as invalid: {Reason}", validation.Reason);
            return null;
        }
        if (!HashMatches(thumbnail, assets.Metadata.ThumbnailContentHash))
        {
            _logger.LogDebug("Cached saved-location thumbnail rejected because its content hash does not match");
            return null;
        }
        return assets with { ThumbnailBytes = thumbnail };
    }

    private string? StyledCacheIdentityIssue(SavedLocationThumbnailMetadata metadata)
    {
        if (!string.Equals(metadata.ThumbnailRendererId, SavedLocationMapImageProcessor.RendererId,
                StringComparison.Ordinal))
            return $"renderer '{metadata.ThumbnailRendererId ?? "unspecified"}' is not '{SavedLocationMapImageProcessor.RendererId}'";
        if (metadata.ThumbnailRendererVersion != SavedLocationMapImageProcessor.RendererVersion)
            return $"renderer version {metadata.ThumbnailRendererVersion?.ToString() ?? "unspecified"} is stale";
        if (metadata.ThumbnailStyleVersion != ThumbnailStyleVersion)
            return $"thumbnail style version {metadata.ThumbnailStyleVersion} is stale";
        if (metadata.SettlementOverlayStyleVersion != SavedLocationMapImageProcessor.SettlementGlowStyleVersion)
            return $"settlement overlay version {metadata.SettlementOverlayStyleVersion?.ToString() ?? "unspecified"} is stale";
        if (metadata.SettlementPresetVersion != _processor.SettlementPresetVersion)
            return $"settlement preset version {metadata.SettlementPresetVersion?.ToString() ?? "unspecified"} is stale";
        if (!string.Equals(metadata.SettlementStyleSettingsHash, _processor.SettlementStyleSettingsHash,
                StringComparison.Ordinal))
            return "settlement style settings identity is stale";
        var expectedInputHash = ComputeStyledInputHash(metadata);
        return string.Equals(metadata.StyledInputHash, expectedInputHash, StringComparison.Ordinal)
            ? null : "styled source-input identity is stale";
    }

    private static string? SettlementCacheCompletenessIssue(SavedLocationThumbnailMetadata metadata,
        ExistingSettlementAssets? settlement)
    {
        if (!Enum.TryParse<EnvironmentalDataState>(metadata.SettlementStatus, out var state))
            return "settlement status is missing or unrecognised";
        return state switch
        {
            EnvironmentalDataState.Available or EnvironmentalDataState.Cached or EnvironmentalDataState.Partial
                when settlement is null => "usable WSF settlement metadata has no compatible derivative",
            EnvironmentalDataState.Available or EnvironmentalDataState.Cached or EnvironmentalDataState.Partial
                when !metadata.SettlementRendered => "usable WSF settlement did not complete galaxy rendering",
            EnvironmentalDataState.Empty when settlement is null =>
                "valid empty WSF coverage has no compatible derivative",
            EnvironmentalDataState.Empty when metadata.SettlementRendered =>
                "empty WSF coverage was incorrectly marked as settlement-rendered",
            EnvironmentalDataState.Unavailable or EnvironmentalDataState.InvalidData or EnvironmentalDataState.Error or
                EnvironmentalDataState.SourceUnavailable or EnvironmentalDataState.InvalidRaster or
                EnvironmentalDataState.TileAbsent => "WSF settlement coverage is unavailable",
            _ => null
        };
    }

    private static string ComputeStyledInputHash(SavedLocationThumbnailMetadata metadata)
    {
        var settlementIdentity = metadata.SettlementStatus == nameof(EnvironmentalDataState.Empty)
            ? $"empty:{metadata.SettlementContentHash ?? "missing"}"
            : metadata.SettlementContentHash ?? $"degraded:{metadata.SettlementStatus ?? "unknown"}";
        var canonical = string.Join('|', metadata.SourceContentHash,
            metadata.FeatureContentHash ?? "absent:" + metadata.FeatureFetchStatus,
            settlementIdentity, metadata.SettlementProviderId, metadata.SettlementDatasetVersion,
            metadata.SourceCentreLatitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            metadata.SourceCentreLongitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            metadata.SourceZoom, metadata.SourceImageWidth, metadata.SourceImageHeight,
            SavedLocationMapImageProcessor.RendererId, SavedLocationMapImageProcessor.RendererVersion,
            metadata.ThumbnailStyleVersion, metadata.SettlementPresetVersion,
            metadata.SettlementStyleSettingsHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private void LogThumbnailRoute(SavedLocationThumbnailMetadata metadata, string thumbnailPath, bool generated) =>
        _logger.LogDebug(
            "SavedLocationThumbnail: Renderer={Renderer}; Style={Style}; StyleVersion={StyleVersion}; SettlementSource={SettlementSource}; SettlementCells={SettlementCellCount}; SettlementRendered={SettlementRendered}; Roads={RoadCount}; Waterways={WaterwayCount}; Thumbnail={Thumbnail}; Generated={Generated}",
            metadata.ThumbnailRendererId, _processor.SettlementPresetName,
            metadata.ThumbnailStyleVersion, metadata.SettlementProviderId,
            metadata.SettlementCellCount ?? 0, metadata.SettlementRendered, metadata.FeatureRoadCount ?? 0,
            metadata.FeatureWaterwayCount ?? 0, thumbnailPath, generated);

    private async Task<ExistingMapAssets?> TryLoadStyleSourceAsync(SavedLocation location,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(location.Id, cancellationToken).ConfigureAwait(false);
        if (metadata is null || !CoordinatesMatch(metadata, location.Coordinate)) return null;
        var directory = LocationDirectory(location.Id);
        var sourcePath = Path.Combine(directory, Path.GetFileName(metadata.SourceFileName ?? "source.png"));
        if (!File.Exists(sourcePath)) return null;
        var source = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var validation = _processor.ValidateSource(source);
        _logger.LogDebug("Saved source validation at {SourcePath}: {IsValid}; {Reason}",
            sourcePath, validation.IsValid, validation.Reason);
        if (!validation.IsValid || !HashMatches(source, metadata.SourceContentHash)) return null;
        var viewport = new WebMercatorViewport(metadata.SourceZoom, TileSize,
            metadata.SourceImageWidth, metadata.SourceImageHeight,
            metadata.SourceCentreLatitude, metadata.SourceCentreLongitude);
        var features = await TryLoadFeatureDataAsync(location.Id, location.Coordinate, viewport, cancellationToken)
            .ConfigureAwait(false);
        var settlement = await TryLoadSettlementDataAsync(location.Id, viewport, metadata, cancellationToken)
            .ConfigureAwait(false);
        return new ExistingMapAssets(metadata, sourcePath, Path.Combine(directory, "thumbnail.png"), source, null,
            features, settlement);
    }

    private async Task<ExistingFeatureAssets?> TryLoadFeatureDataAsync(Guid locationId, GeoCoordinate coordinate,
        WebMercatorViewport viewport, CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(locationId, cancellationToken).ConfigureAwait(false);
        if (metadata is null || !CoordinatesMatch(metadata, coordinate) ||
            string.IsNullOrWhiteSpace(metadata.FeatureFileName) ||
            metadata.FeatureSchemaVersion != OverpassMapFeatureDataService.FeatureSchemaVersion ||
            metadata.FeatureQueryVersion != OverpassMapFeatureDataService.FeatureQueryVersion ||
            metadata.FeatureBounds is null || !BoundsMatch(metadata.FeatureBounds, viewport.Bounds))
            return null;
        var path = Path.Combine(LocationDirectory(locationId), Path.GetFileName(metadata.FeatureFileName));
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!HashMatches(bytes, metadata.FeatureContentHash)) return null;
            var data = JsonSerializer.Deserialize<MapFeatureDataDocument>(bytes, _featureJson);
            if (data is null || data.LocationId != locationId ||
                data.SchemaVersion != OverpassMapFeatureDataService.FeatureSchemaVersion ||
                data.Source.QueryVersion != OverpassMapFeatureDataService.FeatureQueryVersion ||
                !BoundsMatch(data.Source.Bounds, viewport.Bounds))
                return null;
            _logger.LogDebug(
                "OSM feature cache hit for {LocationId}; {FeatureCount} features, query {QueryVersion}, overlay {OverlayVersion}",
                locationId, data.FeatureCount, data.Source.QueryVersion,
                SavedLocationMapImageProcessor.FeatureOverlayStyleVersion);
            return new ExistingFeatureAssets(data, bytes);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not load OSM feature cache for {LocationId}", locationId);
            return null;
        }
    }

    private async Task<ExistingSettlementAssets?> TryLoadSettlementDataAsync(Guid locationId,
        WebMercatorViewport viewport, SavedLocationThumbnailMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(metadata.SettlementFileName) ||
            metadata.SettlementSchemaVersion != SettlementRasterCodec.SchemaVersion ||
            metadata.SettlementBounds is null ||
            string.IsNullOrWhiteSpace(metadata.SettlementProviderId) ||
            string.IsNullOrWhiteSpace(metadata.SettlementDatasetVersion))
            return null;
        var path = Path.Combine(LocationDirectory(locationId), Path.GetFileName(metadata.SettlementFileName));
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!HashMatches(bytes, metadata.SettlementContentHash)) return null;
            var data = SettlementRasterCodec.Decode(bytes);
            var expected = new GeoBounds(viewport.Bounds.South, viewport.Bounds.West,
                viewport.Bounds.North, viewport.Bounds.East);
            if (data.DatasetId != metadata.SettlementProviderId ||
                data.DatasetVersion != metadata.SettlementDatasetVersion ||
                data.Grid.Bounds != expected ||
                data.Grid.Width != viewport.Width * SettlementDensityBuilder.Supersampling ||
                data.Grid.Height != viewport.Height * SettlementDensityBuilder.Supersampling ||
                data.Grid.Projection != GeoRasterProjection.WebMercator) return null;
            return new ExistingSettlementAssets(data, bytes);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not load WSF settlement derivative for {LocationId}", locationId);
            return null;
        }
    }

    private async Task<SavedLocationThumbnailResult?> TryLoadPreviousAsync(Guid locationId, SavedLocation location,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(locationId, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;
        var directory = LocationDirectory(locationId);
        var preferred = Path.Combine(directory, "thumbnail.png");
        var path = File.Exists(preferred)
            ? preferred
            : Path.Combine(directory, Path.GetFileName(metadata.ImageFileName ?? string.Empty));
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var validation = _processor.ValidateThumbnail(bytes);
        if (!validation.IsValid) return null;
        return new SavedLocationThumbnailResult(path, metadata, true, false);
    }

    private async Task<SavedLocationThumbnailMetadata?> ReadMetadataAsync(Guid locationId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(LocationDirectory(locationId), "metadata.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8 * 1024, FileOptions.Asynchronous);
            var metadata = await JsonSerializer.DeserializeAsync<SavedLocationThumbnailMetadata>(stream, _json,
                cancellationToken).ConfigureAwait(false);
            return metadata?.LocationId == locationId ? metadata : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not load saved-location map metadata for {LocationId}", locationId);
            return null;
        }
    }

    private async Task MigrateLegacyThumbnailAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var targetMetadata = Path.Combine(LocationDirectory(locationId), "metadata.json");
        if (File.Exists(targetMetadata)) return;
        var legacyDirectory = Path.Combine(_legacyStorageDirectory, locationId.ToString("N"));
        var legacyMetadataPath = Path.Combine(legacyDirectory, "metadata.json");
        if (!File.Exists(legacyMetadataPath)) return;
        try
        {
            var metadata = JsonSerializer.Deserialize<SavedLocationThumbnailMetadata>(
                await File.ReadAllTextAsync(legacyMetadataPath, cancellationToken).ConfigureAwait(false), _json);
            if (metadata is null || metadata.LocationId != locationId) return;
            var legacyImage = Path.Combine(legacyDirectory, Path.GetFileName(metadata.ImageFileName));
            if (!File.Exists(legacyImage)) return;
            var bytes = await File.ReadAllBytesAsync(legacyImage, cancellationToken).ConfigureAwait(false);
            if (!_processor.ValidateThumbnail(bytes).IsValid) return;
            var migrated = metadata with
            {
                SchemaVersion = MetadataSchemaVersion,
                ImageFileName = "thumbnail.png",
                SourceFileName = "source.png",
                ThumbnailContentHash = Hash(bytes)
            };
            await CommitFilesAtomicallyAsync(LocationDirectory(locationId),
            [
                ("thumbnail.png", bytes),
                ("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(migrated, _json)))
            ], cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Migrated legacy saved-location thumbnail for {LocationId}", locationId);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Legacy saved-location thumbnail migration failed for {LocationId}", locationId);
        }
    }

    private static async Task CommitFilesAtomicallyAsync(string directory,
        IReadOnlyList<(string FileName, byte[] Bytes)> files, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var operation = Guid.NewGuid().ToString("N");
        var staged = files.Select(file => (
            Target: Path.Combine(directory, file.FileName),
            Temporary: Path.Combine(directory, $".{file.FileName}.{operation}.tmp"),
            Backup: Path.Combine(directory, $".{file.FileName}.{operation}.bak"),
            file.Bytes)).ToArray();
        try
        {
            foreach (var file in staged)
                await File.WriteAllBytesAsync(file.Temporary, file.Bytes, cancellationToken).ConfigureAwait(false);
            foreach (var file in staged)
                if (File.Exists(file.Target)) File.Copy(file.Target, file.Backup, true);
            try
            {
                foreach (var file in staged) File.Move(file.Temporary, file.Target, true);
            }
            catch
            {
                foreach (var file in staged)
                    if (File.Exists(file.Backup)) File.Move(file.Backup, file.Target, true);
                throw;
            }
        }
        finally
        {
            foreach (var file in staged)
            {
                TryDelete(file.Temporary);
                TryDelete(file.Backup);
            }
        }
    }

    private string LocationDirectory(Guid locationId) => Path.Combine(StorageDirectory, locationId.ToString("N"));
    private static bool CoordinatesMatch(SavedLocationThumbnailMetadata metadata, GeoCoordinate coordinate) =>
        BitConverter.DoubleToInt64Bits(metadata.Latitude) == BitConverter.DoubleToInt64Bits(coordinate.Latitude) &&
        BitConverter.DoubleToInt64Bits(metadata.Longitude) == BitConverter.DoubleToInt64Bits(coordinate.Longitude);
    private static bool BoundsMatch(MapGeographicBounds first, MapGeographicBounds second) =>
        Math.Abs(first.South - second.South) < 1e-9 && Math.Abs(first.West - second.West) < 1e-9 &&
        Math.Abs(first.North - second.North) < 1e-9 && Math.Abs(first.East - second.East) < 1e-9;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool HashMatches(byte[] bytes, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || Hash(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase);
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ExistingMapAssets(SavedLocationThumbnailMetadata Metadata, string SourcePath,
        string ThumbnailPath, byte[] SourceBytes, byte[]? ThumbnailBytes, ExistingFeatureAssets? Features,
        ExistingSettlementAssets? Settlement);
    private sealed record ExistingFeatureAssets(MapFeatureDataDocument Data, byte[] Bytes);
    private sealed record ExistingSettlementAssets(SettlementRaster Data, byte[] Bytes);
}

public sealed class NullLocationMapThumbnailService : ILocationMapThumbnailService
{
    public string StorageDirectory => string.Empty;
    public Task<SavedLocationThumbnailResult?> GetThumbnailAsync(SavedLocation location, bool forceRefresh,
        CancellationToken cancellationToken) => Task.FromResult<SavedLocationThumbnailResult?>(null);
    public Task<SavedLocationThumbnailResult?> GetThumbnailAsync(SavedLocation location,
        SavedLocationMapRefreshMode mode, CancellationToken cancellationToken) =>
        Task.FromResult<SavedLocationThumbnailResult?>(null);
}
