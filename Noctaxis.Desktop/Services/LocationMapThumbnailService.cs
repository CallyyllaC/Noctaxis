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

/// <summary>Provenance of a saved location's map source, recorded in its metadata.</summary>
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
    public string ConfigurationHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{ProviderId}|{MapStyleId}|{SourceIdentifier}"))).ToLowerInvariant();
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
    int? ThumbnailRendererVersion = null,
    string? ThumbnailSkippedReason = null,
    string? SourceFormat = null,
    int? SourceTileCount = null,
    int? SourceEmptyTileCount = null)
{
    /// <summary>
    /// The source is a vector-derived landscape (<see cref="SavedLocationLandscapeCodec"/>). Without a format the
    /// source is an OpenStreetMap Standard raster capture (<c>source.png</c>) from before schema 10 (a later
    /// feature-, settlement- or style-only refresh keeps it), still read and classified by colour until the
    /// location's map data is next acquired.
    /// </summary>
    public bool HasVectorLandscape => SourceFormat == SavedLocationLandscapeCodec.Format;

    /// <summary>
    /// The legacy card artwork was rejected when these assets were composed, so there is deliberately no
    /// <c>thumbnail.png</c>. The source data is still complete and feeds the saved-location preview.
    /// </summary>
    public bool LegacyArtworkSkipped => ThumbnailSkippedReason is not null;
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
    EnvironmentalDataState? SettlementState = null,
    bool LegacyArtworkSkipped = false)
{
    // The legacy artwork is optional: a frame it rejects still leaves complete source data behind.
    private bool AssetsUsable => RasterSucceeded && (ThumbnailSucceeded || LegacyArtworkSkipped);
    public bool IsComplete => AssetsUsable &&
        (Semantic.Status is MapFeatureFetchStatus.Complete or MapFeatureFetchStatus.CachedPrevious) &&
        SettlementState is EnvironmentalDataState.Available or EnvironmentalDataState.Cached or
            EnvironmentalDataState.Partial or EnvironmentalDataState.Empty;
    public bool IsDegraded => AssetsUsable && !IsComplete;
}

public enum SavedLocationMapRefreshMode
{
    UseCache,
    RefreshSource,
    ReapplyStyle,
    RefreshFeatures,
    RefreshSettlement,
    /// <summary>
    /// For bulk work over many locations: a location whose saved map data is complete and valid is reused as it is,
    /// earlier OpenStreetMap raster captures included; only a location without it is acquired (as
    /// <see cref="RefreshSource"/>). Bulk regeneration never re-downloads map data that is already held: OpenFreeMap's
    /// terms forbid automated collection beyond what the user asks for.
    /// </summary>
    AcquireMissing
}

public interface ILocationMapThumbnailService
{
    string StorageDirectory { get; }
    Task<SavedLocationThumbnailResult?> GetThumbnailAsync(
        SavedLocation location, bool forceRefresh, CancellationToken cancellationToken);
    Task<SavedLocationThumbnailResult?> GetThumbnailAsync(
        SavedLocation location, SavedLocationMapRefreshMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// Removes everything stored for a saved location (map imagery, features, settlement field and the
    /// metadata holding its exact coordinates), current and legacy. Call only once the location's
    /// deletion has been saved. Generation still running for it cannot recreate the files afterwards.
    /// </summary>
    Task DeleteLocationAssetsAsync(Guid locationId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Removes stored assets of locations that are not in <paramref name="savedLocationIds"/> and were
    /// last written before <paramref name="olderThanUtc"/>. The caller must pass the complete set read
    /// from saved state, never defaults that stand in after a failed load. Returns how many were removed.
    /// </summary>
    Task<int> RemoveOrphanedAssetsAsync(IReadOnlySet<Guid> savedLocationIds, DateTime olderThanUtc,
        CancellationToken cancellationToken) => Task.FromResult(0);
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
/// Owns each saved location's map data: the vector-derived landscape (water, vegetation, built-up land), roads and
/// waterways, and the WSF settlement field, all from bounded requests for the location's own map frame. Files are
/// ID-scoped, individually atomically replaced, and content hashes let the loader reject interrupted or mixed
/// generations. A failed acquisition leaves the previous files as they were.
/// </summary>
public sealed class LocationMapThumbnailService : ILocationMapThumbnailService
{
    public const int TileZoom = 13;
    public const int OutputWidth = SavedLocationMapImageProcessor.ThumbnailWidth;
    public const int OutputHeight = SavedLocationMapImageProcessor.ThumbnailHeight;
    public const int ThumbnailStyleVersion = SavedLocationMapImageProcessor.StyleVersion;
    public const string SourceRenderVersion = "openfreemap-landscape-v1";
    public const int SourceWidth = 896;
    public const int SourceHeight = 504;
    public const int TileSize = 256;
    /// <summary>10: the source is an OpenFreeMap landscape, not an OpenStreetMap raster capture.</summary>
    private const int MetadataSchemaVersion = 10;
    /// <summary>The source file of metadata before schema 10, an OpenStreetMap raster capture.</summary>
    public const string LegacyRasterSourceFileName = "source.png";
    private readonly ILogger<LocationMapThumbnailService> _logger;
    private readonly ISavedLocationLandscapeSource _landscape;
    private readonly SavedLocationMapImageProcessor _processor;
    private readonly IMapFeatureDataService _featureData;
    private readonly ISettlementDataProvider _settlementData;
    private readonly IMapImageAcceleration _imageAcceleration;
    private readonly string _legacyStorageDirectory;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locationGates = new();
    // Advanced by each deletion of a location's assets. A request that began before the deletion (queued
    // or running) sees a different value and writes nothing; a later request, e.g. after the location is
    // imported again, works normally.
    private readonly ConcurrentDictionary<Guid, long> _deletionEpochs = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly JsonSerializerOptions _featureJson = new(JsonSerializerDefaults.Web);

    public LocationMapThumbnailService(ILogger<LocationMapThumbnailService> logger,
        IUserDataPathProvider paths, ISavedLocationLandscapeSource landscape, SavedLocationMapImageProcessor processor,
        IMapFeatureDataService? featureData = null, ISettlementDataProvider? settlementData = null,
        IMapImageAcceleration? imageAcceleration = null)
    {
        _logger = logger;
        _landscape = landscape;
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
        var epoch = DeletionEpoch(location.Id);
        var gate = _locationGates.GetOrAdd(location.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Deleted while this request waited for the location's gate.
            if (DeletionEpoch(location.Id) != epoch) return null;
            if (mode == SavedLocationMapRefreshMode.AcquireMissing)
            {
                var held = await TryLoadCompleteAsync(location, cancellationToken).ConfigureAwait(false);
                if (held is not null)
                    return new SavedLocationThumbnailResult(held.ThumbnailPath, held.Metadata, true, false);
                mode = SavedLocationMapRefreshMode.RefreshSource;
            }
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
                        (File.Exists(thumbnailPath) || metadata.LegacyArtworkSkipped) && staleReason is not null)
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
                        throw new InvalidDataException("No valid saved map source is available for style reapplication.");
                    return await ProcessAndCommitStyleAsync(styleSource, cancellationToken).ConfigureAwait(false);
                }
                else if (mode == SavedLocationMapRefreshMode.RefreshFeatures)
                {
                    var styleSource = await TryLoadStyleSourceAsync(location, cancellationToken).ConfigureAwait(false);
                    if (styleSource is null)
                        throw new InvalidDataException(
                            "No valid saved map source and viewport metadata are available for semantic refresh.");
                    return await RefreshFeaturesAndCommitAsync(location, styleSource, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (mode == SavedLocationMapRefreshMode.RefreshSettlement)
                {
                    var styleSource = await TryLoadStyleSourceAsync(location, cancellationToken).ConfigureAwait(false);
                    if (styleSource is null)
                        throw new InvalidDataException(
                            "No valid saved map source and viewport metadata are available for settlement refresh.");
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
                        !previous.Metadata.LegacyArtworkSkipped, !previous.Metadata.LegacyArtworkSkipped, ex.Message,
                        mode == SavedLocationMapRefreshMode.RefreshSettlement ? EnvironmentalDataState.Unavailable : null,
                        previous.Metadata.LegacyArtworkSkipped)
                };
            }
        }
        finally { gate.Release(); }
    }

    private async Task<SavedLocationThumbnailResult> GenerateSourceAndCommitAsync(
        SavedLocation location, CancellationToken cancellationToken)
    {
        var mapSource = _landscape.Definition;
        var viewport = WebMercatorViewport.Create(location.Coordinate, TileZoom, TileSize, SourceWidth, SourceHeight);
        var previousMetadata = await ReadMetadataAsync(location.Id, cancellationToken).ConfigureAwait(false);
        var previousSettlement = previousMetadata is null ? null :
            await TryLoadSettlementDataAsync(location.Id, viewport, previousMetadata, cancellationToken)
                .ConfigureAwait(false);
        _logger.LogInformation(
            "Saved-location source generation started for {LocationId} ({LocationName}); provider {ProviderId}, source version {SourceVersion}",
            location.Id, location.Name, mapSource.ProviderId, SourceRenderVersion);
        var landscape = await AcquireLandscapeAsync(viewport, cancellationToken).ConfigureAwait(false);
        var sourcePng = SavedLocationLandscapeCodec.Encode(landscape);
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
            _logger.LogWarning(ex, "Road and water feature fetch failed for {LocationId}; using compatible cache or base map",
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
                "Discarded map feature result with mismatched ownership or bounds for {LocationId}", location.Id);
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
            _logger.LogInformation("Compatible map feature cache retained for {LocationId}", location.Id);
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
        var artwork = ComposeLegacyArtwork(sourcePng, features?.Data, settlement.Value, viewport, location.Id);
        var now = DateTimeOffset.UtcNow;
        var featureSource = features?.Data.Source;
        var metadata = new SavedLocationThumbnailMetadata(
            MetadataSchemaVersion, location.Id, location.Coordinate.Latitude, location.Coordinate.Longitude,
            TileZoom, OutputWidth, OutputHeight, mapSource.ProviderId, mapSource.ProviderName,
            mapSource.MapStyleId, mapSource.SourceIdentifier, mapSource.AttributionText,
            mapSource.AttributionUrl, mapSource.LicenceName, mapSource.LicenceUrl, now, TileZoom,
            location.Coordinate.Latitude, location.Coordinate.Longitude, SourceWidth, SourceHeight,
            ThumbnailStyleVersion, mapSource.ConfigurationHash, SourceRenderVersion, "thumbnail.png",
            SavedLocationLandscapeCodec.FileName, Hash(sourcePng), null, null,
            features is null ? null : "features.json",
            features?.Data.SchemaVersion, featureSource?.QueryVersion, featureSource?.ProviderId,
            featureSource?.AttributionText, featureSource?.AttributionUrl, featureSource?.LicenceName,
            featureSource?.LicenceUrl, featureSource?.FetchedAtUtc, features is null ? null : Hash(features.Bytes),
            featureSource?.Bounds, features?.Data.FeatureCount,
            SavedLocationMapImageProcessor.FeatureOverlayStyleVersion, semanticOutcome.Status.ToString(),
            semanticOutcome.FailureCode, semanticOutcome.FailureReason, semanticOutcome.HttpStatusCode,
            semanticOutcome.TimedOut, semanticOutcome.ResponseTooLarge, semanticOutcome.ParseFailed,
            semanticOutcome.AttemptCount, semanticOutcome.AttemptedAtUtc, semanticOutcome.RoadCount,
            semanticOutcome.WaterwayCount, semanticOutcome.FallbackAttempted)
        {
            SourceFormat = SavedLocationLandscapeCodec.Format,
            SourceTileCount = landscape.TileCount,
            SourceEmptyTileCount = landscape.EmptyTileCount
        };
        metadata = WithArtwork(ApplySettlementOutcome(metadata, settlement, artwork.Render, settlementBytes), artwork);
        var operation = new SavedLocationMapRefreshResult(true, false, semanticOutcome, artwork.Png is not null, false,
            null, settlement.State, artwork.Png is null);
        var result = await CommitFullAssetSetAsync(location.Id, metadata.SourceFileName, sourcePng, features?.Bytes,
                settlementBytes,
                artwork.Png, metadata,
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Saved-location source data completed at {Directory}; source {SourceVersion}, style {StyleVersion}, legacy artwork {Artwork}",
            LocationDirectory(location.Id), SourceRenderVersion, ThumbnailStyleVersion,
            artwork.Png is null ? "skipped" : "written");
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
                var artwork = ComposeLegacyArtwork(assets.SourceBytes, fetch.Data, settlement.Value, viewport,
                    location.Id);
                var metadata = ApplyFeatureOutcome(assets.Metadata, fetch.Outcome, fetch.Data, featureBytes,
                    artwork.Png);
                metadata = WithArtwork(ApplySettlementOutcome(metadata, settlement, artwork.Render,
                    assets.Settlement?.Bytes), artwork);
                await CommitWithArtworkAsync(metadata, [("features.json", featureBytes)], artwork, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Semantic-only refresh completed for {LocationId}: {Status}; roads {RoadCount}, waterways {WaterwayCount}",
                    metadata.LocationId, fetch.Outcome.Status, fetch.Outcome.RoadCount,
                    fetch.Outcome.WaterwayCount);
                return new SavedLocationThumbnailResult(assets.ThumbnailPath, metadata, true, true, false,
                    new SavedLocationMapRefreshResult(true, true, fetch.Outcome, artwork.Png is not null, false, null,
                        settlement.State, artwork.Png is null));
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
        var skipped = !thumbnailUsable && failedMetadata.LegacyArtworkSkipped;
        return new SavedLocationThumbnailResult(assets.ThumbnailPath, failedMetadata, thumbnailUsable || skipped, false,
            thumbnailUsable || skipped, new SavedLocationMapRefreshResult(true, true, retainedOutcome,
                thumbnailUsable, thumbnailUsable, retainedOutcome.FailureReason, LegacyArtworkSkipped: skipped));
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
        var artwork = ComposeLegacyArtwork(assets.SourceBytes, assets.Features?.Data, settlement.Value, viewport,
            assets.Metadata.LocationId);
        var metadata = assets.Metadata with
        {
            SchemaVersion = MetadataSchemaVersion,
            Width = OutputWidth,
            Height = OutputHeight,
            ThumbnailStyleVersion = ThumbnailStyleVersion,
            ImageFileName = "thumbnail.png",
            SourceContentHash = Hash(assets.SourceBytes),
            FeatureOverlayStyleVersion = SavedLocationMapImageProcessor.FeatureOverlayStyleVersion
        };
        metadata = WithArtwork(ApplySettlementOutcome(metadata, settlement, artwork.Render, assets.Settlement?.Bytes),
            artwork);
        await CommitWithArtworkAsync(metadata, [], artwork, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved-location thumbnail style completed at {ThumbnailPath}; legacy artwork {Artwork}",
            assets.ThumbnailPath, artwork.Png is null ? "skipped" : "written");
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
            "WSF settlement refresh started for {LocationId} ({LocationName}); landscape and map features remain unchanged",
            location.Id, location.Name);
        var settlement = await FetchSettlementAsync(viewport, cancellationToken).ConfigureAwait(false);
        if (settlement.HasValue)
        {
            var settlementBytes = SettlementRasterCodec.Encode(settlement.Value!);
            var artwork = ComposeLegacyArtwork(assets.SourceBytes, assets.Features?.Data, settlement.Value, viewport,
                location.Id);
            var metadata = WithArtwork(ApplySettlementOutcome(assets.Metadata, settlement, artwork.Render,
                settlementBytes), artwork);
            await CommitWithArtworkAsync(metadata, [("settlement-field.bin.gz", settlementBytes)], artwork,
                cancellationToken).ConfigureAwait(false);
            var core = CoreOutcomeFromAssets(assets.Features);
            _logger.LogInformation(
                "WSF settlement refresh completed for {LocationId}: {Status}, {DensityCells} active cells, maximum mass {MaximumDensity}",
                location.Id, settlement.State, artwork.Render?.DensityCellCount ?? 0,
                artwork.Render?.MaximumDensityBeforeClamping ?? 0);
            return new SavedLocationThumbnailResult(assets.ThumbnailPath, metadata, true, true, false,
                new SavedLocationMapRefreshResult(true, true, core, artwork.Png is not null, false, null,
                    settlement.State, artwork.Png is null));
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
        var skipped = !thumbnailUsable && failedMetadata.LegacyArtworkSkipped;
        return new SavedLocationThumbnailResult(assets.ThumbnailPath, failedMetadata, thumbnailUsable || skipped, false,
            thumbnailUsable || skipped, new SavedLocationMapRefreshResult(true, true,
                CoreOutcomeFromAssets(assets.Features), thumbnailUsable, thumbnailUsable,
                settlement.Message, settlement.State, skipped));
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

    /// <summary>Why no <c>thumbnail.png</c> is composed: the legacy card artwork is retired.</summary>
    public const string LegacyArtworkRetiredReason =
        "Legacy card artwork is retired; the saved-location preview is built from the source data.";

    /// <summary>
    /// The legacy card artwork (<c>thumbnail.png</c>) is retired: no view shows it, and composing it was the costly
    /// galaxy pipeline (several large blurs per location). Every acquisition and style reapplication now records the
    /// artwork as skipped, which the cache already treats as complete source data for the preview; an earlier
    /// <c>thumbnail.png</c> is removed with the next commit, as for any skipped artwork. The renderer itself remains
    /// for the galaxy calibration tool.
    /// </summary>
    private static LegacyArtwork ComposeLegacyArtwork(byte[] sourcePng, MapFeatureDataDocument? features,
        SettlementRaster? settlement, WebMercatorViewport viewport, Guid locationId) =>
        new(null, null, LegacyArtworkRetiredReason);

    private static SavedLocationThumbnailMetadata WithArtwork(SavedLocationThumbnailMetadata metadata,
        LegacyArtwork artwork) => artwork.Png is null
        ? metadata with
        {
            ThumbnailContentHash = null, ThumbnailGeneratedAtUtc = null, ThumbnailSkippedReason = artwork.SkippedReason
        }
        : metadata with
        {
            ThumbnailContentHash = Hash(artwork.Png), ThumbnailGeneratedAtUtc = DateTimeOffset.UtcNow,
            ThumbnailSkippedReason = null
        };

    /// <summary>
    /// Commits <paramref name="files"/>, the artwork when there is one, and the metadata together. Skipped
    /// artwork removes any earlier <c>thumbnail.png</c>, which no longer matches the committed data.
    /// </summary>
    private async Task CommitWithArtworkAsync(SavedLocationThumbnailMetadata metadata,
        IReadOnlyList<(string FileName, byte[] Bytes)> files, LegacyArtwork artwork, CancellationToken cancellationToken)
    {
        var directory = LocationDirectory(metadata.LocationId);
        var all = files.ToList();
        if (artwork.Png is not null) all.Add(("thumbnail.png", artwork.Png));
        all.Add(("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, _json))));
        await CommitFilesAtomicallyAsync(directory, all, cancellationToken).ConfigureAwait(false);
        if (artwork.Png is null) TryDelete(Path.Combine(directory, "thumbnail.png"));
    }

    private async Task<SavedLocationThumbnailResult> CommitFullAssetSetAsync(Guid locationId, string sourceFileName,
        byte[] sourcePng,
        byte[]? featureBytes, byte[]? settlementBytes, byte[]? thumbnailPng,
        SavedLocationThumbnailMetadata metadata,
        CancellationToken cancellationToken)
    {
        var directory = LocationDirectory(locationId);
        var files = new List<(string FileName, byte[] Bytes)>
        {
            (sourceFileName, sourcePng),
            ("metadata.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, _json)))
        };
        if (thumbnailPng is not null) files.Insert(1, ("thumbnail.png", thumbnailPng));
        if (featureBytes is not null) files.Insert(1, ("features.json", featureBytes));
        if (settlementBytes is not null) files.Insert(featureBytes is null ? 1 : 2,
            ("settlement-field.bin.gz", settlementBytes));
        await CommitFilesAtomicallyAsync(directory, files, cancellationToken).ConfigureAwait(false);
        if (featureBytes is null) TryDelete(Path.Combine(directory, "features.json"));
        if (thumbnailPng is null) TryDelete(Path.Combine(directory, "thumbnail.png"));
        // The OpenStreetMap raster capture is removed only now that its replacement is committed.
        if (!string.Equals(sourceFileName, LegacyRasterSourceFileName, StringComparison.Ordinal))
            TryDelete(Path.Combine(directory, LegacyRasterSourceFileName));
        // Legacy building caches are left untouched but no longer referenced by v6 metadata.
        // This makes migration non-destructive while ensuring they cannot feed WSF rendering.
        return new SavedLocationThumbnailResult(Path.Combine(directory, "thumbnail.png"), metadata, true, true);
    }

    private async Task<SavedLocationLandscape> AcquireLandscapeAsync(WebMercatorViewport viewport,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var landscape = await _landscape.AcquireAsync(viewport, Mapping.Preview.LocationPreviewScene.MaskScale,
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Saved-location landscape acquired in {ElapsedMilliseconds} ms from {TileCount} vector tiles ({EmptyTileCount} without data)",
            stopwatch.ElapsedMilliseconds, landscape.TileCount, landscape.EmptyTileCount);
        // Tiles that report no data say nothing about the ground; a blank landscape would draw land that may be sea.
        if (landscape.HasNoData)
            throw new MapFeatureRequestException("no_vector_data", "the vector tiles hold no data for this map frame");
        return landscape;
    }

    private async Task<ExistingMapAssets?> TryLoadCompleteAsync(SavedLocation location,
        CancellationToken cancellationToken)
    {
        var assets = await TryLoadStyleSourceAsync(location, cancellationToken).ConfigureAwait(false);
        if (assets is null) return null;
        var artworkSkipped = assets.Metadata.LegacyArtworkSkipped;
        if (!artworkSkipped && !File.Exists(assets.ThumbnailPath)) return null;
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
        // The legacy artwork was rejected for this frame: the validated source data is the complete cache.
        if (artworkSkipped) return assets;
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
            // Galaxy rendering is part of the legacy artwork, so it is only expected when that artwork exists.
            EnvironmentalDataState.Available or EnvironmentalDataState.Cached or EnvironmentalDataState.Partial
                when !metadata.SettlementRendered && !metadata.LegacyArtworkSkipped =>
                "usable WSF settlement did not complete galaxy rendering",
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

    internal static string ComputeStyledInputHash(SavedLocationThumbnailMetadata metadata)
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
        var sourcePath = Path.Combine(directory, Path.GetFileName(metadata.SourceFileName ?? LegacyRasterSourceFileName));
        if (!File.Exists(sourcePath)) return null;
        var source = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var viewport = new WebMercatorViewport(metadata.SourceZoom, TileSize,
            metadata.SourceImageWidth, metadata.SourceImageHeight,
            metadata.SourceCentreLatitude, metadata.SourceCentreLongitude);
        var issue = SourceIssue(metadata, source, viewport);
        _logger.LogDebug("Saved source validation at {SourcePath}: {Issue}", sourcePath, issue ?? "valid");
        if (issue is not null || !HashMatches(source, metadata.SourceContentHash)) return null;
        var features = await TryLoadFeatureDataAsync(location.Id, location.Coordinate, viewport, cancellationToken)
            .ConfigureAwait(false);
        var settlement = await TryLoadSettlementDataAsync(location.Id, viewport, metadata, cancellationToken)
            .ConfigureAwait(false);
        return new ExistingMapAssets(metadata, sourcePath, Path.Combine(directory, "thumbnail.png"), source, null,
            features, settlement);
    }

    /// <summary>Why a saved source cannot be used, or null. Both the landscape and an earlier raster capture are read.</summary>
    private string? SourceIssue(SavedLocationThumbnailMetadata metadata, byte[] source, WebMercatorViewport viewport)
    {
        if (!metadata.HasVectorLandscape)
        {
            var validation = _processor.ValidateSource(source);
            return validation.IsValid ? null : validation.Reason;
        }
        var landscape = SavedLocationLandscapeCodec.TryDecode(source);
        if (landscape is null) return "the landscape file is unreadable";
        return landscape.Width == Math.Max(1, viewport.Width / Mapping.Preview.LocationPreviewScene.MaskScale) &&
               landscape.Height == Math.Max(1, viewport.Height / Mapping.Preview.LocationPreviewScene.MaskScale) &&
               BoundsMatch(landscape.Bounds, viewport.Bounds)
            ? null
            : "the landscape does not cover the saved map frame";
    }

    /// <summary>Road and water documents that can be drawn: OpenFreeMap-derived, or an earlier Overpass document.</summary>
    internal static bool IsCompatibleFeatureDocument(int? schemaVersion, int? queryVersion) =>
        (schemaVersion == OpenFreeMapMapFeatureDataService.FeatureSchemaVersion &&
         queryVersion == OpenFreeMapMapFeatureDataService.FeatureQueryVersion) ||
        (schemaVersion == LegacyOverpassFeatureSchemaVersion && queryVersion == LegacyOverpassFeatureQueryVersion);

    /// <summary>The road and water documents written from the public Overpass API before schema 10. They stay
    /// readable, so existing previews keep their line work offline, and are replaced at the next acquisition.</summary>
    internal const int LegacyOverpassFeatureSchemaVersion = 1;
    internal const int LegacyOverpassFeatureQueryVersion = 1;

    private async Task<ExistingFeatureAssets?> TryLoadFeatureDataAsync(Guid locationId, GeoCoordinate coordinate,
        WebMercatorViewport viewport, CancellationToken cancellationToken)
    {
        var metadata = await ReadMetadataAsync(locationId, cancellationToken).ConfigureAwait(false);
        if (metadata is null || !CoordinatesMatch(metadata, coordinate) ||
            string.IsNullOrWhiteSpace(metadata.FeatureFileName) ||
            !IsCompatibleFeatureDocument(metadata.FeatureSchemaVersion, metadata.FeatureQueryVersion) ||
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
                data.SchemaVersion != metadata.FeatureSchemaVersion ||
                data.Source.QueryVersion != metadata.FeatureQueryVersion ||
                !BoundsMatch(data.Source.Bounds, viewport.Bounds))
                return null;
            _logger.LogDebug(
                "Map feature cache hit for {LocationId}; {FeatureCount} features from {ProviderId}, query {QueryVersion}, overlay {OverlayVersion}",
                locationId, data.FeatureCount, data.Source.ProviderId, data.Source.QueryVersion,
                SavedLocationMapImageProcessor.FeatureOverlayStyleVersion);
            return new ExistingFeatureAssets(data, bytes);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not load map feature cache for {LocationId}", locationId);
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
        if (metadata.LegacyArtworkSkipped)
        {
            // No artwork by design; the previous assets are usable while their source data is intact.
            var source = Path.Combine(directory, Path.GetFileName(metadata.SourceFileName ?? LegacyRasterSourceFileName));
            if (!File.Exists(source) ||
                !HashMatches(await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false),
                    metadata.SourceContentHash))
                return null;
            return new SavedLocationThumbnailResult(preferred, metadata, true, false);
        }
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

    public async Task DeleteLocationAssetsAsync(Guid locationId, CancellationToken cancellationToken)
    {
        if (locationId == Guid.Empty) throw new ArgumentException("A saved location has a non-empty identifier.", nameof(locationId));
        // Marked first, so nothing queued behind the gate starts; then the gate waits for a generation
        // already running, so its files exist before they are removed rather than appearing after.
        _deletionEpochs.AddOrUpdate(locationId, 1, (_, epoch) => epoch + 1);
        var gate = _locationGates.GetOrAdd(locationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeleteManagedLocationDirectory(StorageDirectory, locationId);
            DeleteManagedLocationDirectory(_legacyStorageDirectory, locationId);
            _logger.LogInformation("Removed stored map assets for deleted location {LocationId}", locationId);
        }
        finally { gate.Release(); }
    }

    public async Task<int> RemoveOrphanedAssetsAsync(IReadOnlySet<Guid> savedLocationIds, DateTime olderThanUtc,
        CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var root in new[] { StorageDirectory, _legacyStorageDirectory })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Only folders this service names (a GUID in "N" form) are considered; anything else is left alone.
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || id == Guid.Empty ||
                    savedLocationIds.Contains(id)) continue;
                // Recent folders may belong to a location saved since the state was read (for example by
                // another instance); they are kept until a later start finds them still unreferenced.
                if (Directory.GetLastWriteTimeUtc(directory) >= olderThanUtc) continue;
                var gate = _locationGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (DeleteManagedLocationDirectory(root, id)) removed++;
                }
                finally { gate.Release(); }
            }
        }
        if (removed > 0) _logger.LogInformation("Removed stored map assets of {Count} deleted locations", removed);
        return removed;
    }

    /// <summary>
    /// Deletes <c>root/&lt;id:N&gt;</c> and nothing outside it. A link (symbolic link or junction) is removed
    /// as a link, never followed, at the folder itself or anywhere below it. Returns whether it existed.
    /// </summary>
    private bool DeleteManagedLocationDirectory(string root, Guid locationId)
    {
        var fullRoot = Path.GetFullPath(root);
        var directory = Path.GetFullPath(Path.Combine(fullRoot, locationId.ToString("N")));
        if (!string.Equals(Path.GetDirectoryName(directory), fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Location asset path escaped its managed directory.");
        var info = new DirectoryInfo(directory);
        if (!info.Exists) return false;
        try
        {
            DeleteWithoutFollowingLinks(info);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file held open now is removed by the orphan sweep at a later start.
            _logger.LogWarning(ex, "Could not remove all stored map assets at {Path}", directory);
            return false;
        }
    }

    private static void DeleteWithoutFollowingLinks(DirectoryInfo directory)
    {
        if (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            directory.Delete(recursive: false);
            return;
        }
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo child) DeleteWithoutFollowingLinks(child);
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        directory.Delete(recursive: false);
    }

    private long DeletionEpoch(Guid locationId) => _deletionEpochs.TryGetValue(locationId, out var epoch) ? epoch : 0;

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
    private sealed record LegacyArtwork(byte[]? Png, SettlementRenderDiagnostics? Render, string? SkippedReason);
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
