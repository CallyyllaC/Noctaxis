using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Services;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class LocationMapThumbnailServiceTests
{
    [Fact]
    public void SlippyMapTile_UsesStandardWebMercatorTileCoordinates()
    {
        var tile = SlippyMapTile.FromCoordinate(new GeoCoordinate(0, 0), 1);

        Assert.Equal(1, tile.X);
        Assert.Equal(1, tile.Y);
        Assert.Equal(0, tile.FractionX, 10);
        Assert.Equal(0, tile.FractionY, 10);
    }

    [Fact]
    public async Task Thumbnail_IsPersistedWithLocationAndMapOriginMetadata()
    {
        using var files = new TestDirectory();
        var handler = new TileHandler(CreateTile());
        using var service = CreateService(files, handler);
        var location = Location(Guid.NewGuid(), 51.8903, -2.6004, "Wye Valley");

        var result = await service.Value.GetThumbnailAsync(location,
            SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.RefreshSucceeded);
        Assert.True(result.WasGenerated);
        Assert.Contains(location.Id.ToString("N"), result.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ImagePath));
        var assetDirectory = Path.GetDirectoryName(result.ImagePath)!;
        var sourcePath = Path.Combine(assetDirectory, "source.png");
        var metadataPath = Path.Combine(assetDirectory, "metadata.json");
        Assert.Equal("thumbnail.png", Path.GetFileName(result.ImagePath));
        Assert.True(File.Exists(sourcePath));
        var metadata = JsonSerializer.Deserialize<SavedLocationThumbnailMetadata>(await File.ReadAllTextAsync(metadataPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(metadata);
        Assert.Equal(location.Id, metadata.LocationId);
        Assert.Equal(location.Coordinate.Latitude, metadata.SourceCentreLatitude);
        Assert.Equal(location.Coordinate.Longitude, metadata.SourceCentreLongitude);
        Assert.Equal(13, LocationMapThumbnailService.TileZoom);
        Assert.Equal(13, metadata.SourceZoom);
        Assert.Equal("openstreetmap-standard", metadata.ProviderId);
        Assert.Equal("OpenStreetMap", metadata.ProviderName);
        Assert.Equal("standard", metadata.MapStyleId);
        Assert.Contains("OpenStreetMap", metadata.AttributionText);
        Assert.Equal(8, SavedLocationMapImageProcessor.StyleVersion);
        Assert.Equal(SavedLocationMapImageProcessor.StyleVersion,
            LocationMapThumbnailService.ThumbnailStyleVersion);
        Assert.Equal(LocationMapThumbnailService.ThumbnailStyleVersion, metadata.ThumbnailStyleVersion);
        Assert.False(string.IsNullOrWhiteSpace(metadata.SourceConfigurationHash));
        Assert.False(string.IsNullOrWhiteSpace(metadata.SourceContentHash));
        Assert.False(string.IsNullOrWhiteSpace(metadata.ThumbnailContentHash));
        Assert.Equal("source.png", metadata.SourceFileName);
        Assert.Equal("thumbnail.png", metadata.ImageFileName);
        Assert.DoesNotContain("key=", await File.ReadAllTextAsync(metadataPath), StringComparison.OrdinalIgnoreCase);

        using var source = SKBitmap.Decode(sourcePath);
        Assert.Equal(LocationMapThumbnailService.SourceWidth, source.Width);
        Assert.Equal(LocationMapThumbnailService.SourceHeight, source.Height);
        Assert.Equal(255, source.GetPixel(source.Width / 2, source.Height / 2).Alpha);
        using var rendered = SKBitmap.Decode(result.ImagePath);
        Assert.Equal(LocationMapThumbnailService.OutputWidth, rendered.Width);
        Assert.Equal(LocationMapThumbnailService.OutputHeight, rendered.Height);
        Assert.True(rendered.GetPixel(0, rendered.Height / 2).Alpha < 32);
        Assert.True(rendered.GetPixel(rendered.Width - 1, rendered.Height / 2).Alpha >= 200);
        Assert.True(rendered.GetPixel(rendered.Width / 2, rendered.Height / 2).Alpha > 0);
    }

    [Fact]
    public async Task ExistingThumbnail_IsLoadedFromDiskWithoutNetworkRequest()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.6101, -0.4325, "Home");
        var firstHandler = new TileHandler(CreateTile());
        string originalPath;
        using (var first = CreateService(files, firstHandler))
            originalPath = (await first.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None))!.ImagePath;

        var offline = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using var second = CreateService(files, offline);
        var cached = await second.Value.GetThumbnailAsync(location, false, CancellationToken.None);

        Assert.NotNull(cached);
        Assert.False(cached.WasGenerated);
        Assert.Equal(originalPath, cached.ImagePath);
        Assert.Equal(0, offline.RequestCount);
    }

    [Fact]
    public async Task NearbyLocations_HaveDistinctIdOwnedAssets()
    {
        using var files = new TestDirectory();
        var handler = new TileHandler(CreateTile());
        using var service = CreateService(files, handler);
        var home = Location(Guid.NewGuid(), 53.6101, -0.4325, "Home");
        var quarry = Location(Guid.NewGuid(), 53.6020, -0.4400, "Elsham Quarry");

        var homeResult = await service.Value.GetThumbnailAsync(home,
            SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);
        var quarryResult = await service.Value.GetThumbnailAsync(quarry,
            SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);

        Assert.NotNull(homeResult);
        Assert.NotNull(quarryResult);
        Assert.NotEqual(Path.GetDirectoryName(homeResult.ImagePath), Path.GetDirectoryName(quarryResult.ImagePath));
        Assert.Equal(home.Id, homeResult.Metadata.LocationId);
        Assert.Equal(quarry.Id, quarryResult.Metadata.LocationId);
        Assert.Equal(home.Coordinate, new GeoCoordinate(homeResult.Metadata.Latitude, homeResult.Metadata.Longitude));
        Assert.Equal(quarry.Coordinate, new GeoCoordinate(quarryResult.Metadata.Latitude, quarryResult.Metadata.Longitude));
    }

    [Fact]
    public async Task CoordinateChange_InvalidatesAssetForSameLocationId()
    {
        using var files = new TestDirectory();
        var handler = new TileHandler(CreateTile());
        using var service = CreateService(files, handler);
        var id = Guid.NewGuid();
        var original = Location(id, 51.50, -0.12, "London");
        var moved = original with { Coordinate = new GeoCoordinate(51.51, -0.13) };

        var first = await service.Value.GetThumbnailAsync(original,
            SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);
        var second = await service.Value.GetThumbnailAsync(moved,
            SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second.WasGenerated);
        Assert.Equal(first.ImagePath, second.ImagePath);
        Assert.Equal(moved.Coordinate.Latitude, second.Metadata.Latitude);
        Assert.Equal(moved.Coordinate.Longitude, second.Metadata.Longitude);
    }

    [Fact]
    public async Task FailedForcedRefresh_PreservesPreviousImageAndMetadata()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 54.5, -3.1, "Lakes");
        string originalPath;
        byte[] originalSource;
        byte[] originalThumbnail;
        string originalMetadata;
        using (var first = CreateService(files, new TileHandler(CreateTile())))
        {
            originalPath = (await first.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None))!.ImagePath;
            originalSource = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(originalPath)!, "source.png"));
            originalThumbnail = await File.ReadAllBytesAsync(originalPath);
            originalMetadata = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(originalPath)!, "metadata.json"));
        }

        using var failing = CreateService(files, new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable));
        var result = await failing.Value.GetThumbnailAsync(location, true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.RefreshSucceeded);
        Assert.True(result.IsPreviousAsset);
        Assert.Equal(originalPath, result.ImagePath);
        Assert.True(File.Exists(originalPath));
        Assert.Equal(originalSource, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(originalPath)!, "source.png")));
        Assert.Equal(originalThumbnail, await File.ReadAllBytesAsync(originalPath));
        Assert.Equal(originalMetadata, await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(originalPath)!, "metadata.json")));
    }

    [Fact]
    public async Task ProviderChange_IsHistoricalUntilForcedRefreshThenUpdatesOriginMetadata()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 50.8, -1.1, "Coast");
        var originalSource = new TestSourceProvider("provider-a", "Style A");
        using (var original = CreateService(files, new TileHandler(CreateTile()), originalSource))
            await original.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);

        var currentSource = new TestSourceProvider("provider-b", "Style B");
        var offlineHandler = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using (var cachedService = CreateService(files, offlineHandler, currentSource))
        {
            var historical = await cachedService.Value.GetThumbnailAsync(location, false, CancellationToken.None);
            Assert.NotNull(historical);
            Assert.Equal("provider-a", historical.Metadata.ProviderId);
            Assert.Equal(0, offlineHandler.RequestCount);
        }

        using var refreshedService = CreateService(files, new TileHandler(CreateTile()), currentSource);
        var refreshed = await refreshedService.Value.GetThumbnailAsync(location, true, CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.RefreshSucceeded);
        Assert.Equal("provider-b", refreshed.Metadata.ProviderId);
        Assert.Equal("Style B", refreshed.Metadata.ProviderName);
    }

    [Fact]
    public void SourceValidation_RejectsBlankOrUniformCaptures()
    {
        var processor = new SavedLocationMapImageProcessor();

        var black = CreateUniformImage(LocationMapThumbnailService.SourceWidth,
            LocationMapThumbnailService.SourceHeight, SKColors.Black);
        var placeholder = CreateUniformImage(LocationMapThumbnailService.SourceWidth,
            LocationMapThumbnailService.SourceHeight, SKColor.Parse("#D8D3C8"));

        Assert.False(processor.ValidateSource(black).IsValid);
        Assert.False(processor.ValidateSource(placeholder).IsValid);
    }

    [Fact]
    public void MapImageProcessor_AcceptsAndProcessesDetailedSource()
    {
        var processor = new SavedLocationMapImageProcessor();
        var source = CreateDetailedSource();

        var sourceValidation = processor.ValidateSource(source);
        Assert.True(sourceValidation.IsValid, sourceValidation.Reason);
        var thumbnail = processor.Process(source);
        var thumbnailValidation = processor.ValidateThumbnail(thumbnail);
        Assert.True(thumbnailValidation.IsValid, thumbnailValidation.Reason);
    }

    [Fact]
    public void MapImageProcessor_PreservesVisibleMapDetailOutsidePin()
    {
        var processor = new SavedLocationMapImageProcessor();
        var thumbnail = processor.Process(CreateDetailedSource());
        using var bitmap = SKBitmap.Decode(thumbnail);

        var metrics = MeasureVisibleMapOutsidePin(bitmap);

        Assert.True(metrics.OpaqueSamples > 10_000,
            $"Only {metrics.OpaqueSamples} opaque map samples were found.");
        Assert.True(metrics.MeanLuminance >= 28,
            $"Mean map luminance was {metrics.MeanLuminance:F2}.");
        Assert.True(metrics.LuminanceDeviation >= 3.5,
            $"Map luminance deviation was {metrics.LuminanceDeviation:F2}.");
        Assert.True(metrics.MeanEdgeDifference >= .65,
            $"Mean map edge difference was {metrics.MeanEdgeDifference:F2}.");
    }

    [Fact]
    public async Task InvalidSource_IsRetriedAndNeverCommitted()
    {
        using var files = new TestDirectory();
        var handler = new TileHandler(CreateUniformImage(256, 256, SKColors.Black));
        using var service = CreateService(files, handler);
        var location = Location(Guid.NewGuid(), 52.1, -1.4, "Blank tiles");

        var result = await service.Value.GetThumbnailAsync(location,
            SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);

        Assert.Null(result);
        Assert.True(handler.RequestCount >= 3);
        var directory = Path.Combine(service.Value.StorageDirectory, location.Id.ToString("N"));
        Assert.False(File.Exists(Path.Combine(directory, "source.png")));
        Assert.False(File.Exists(Path.Combine(directory, "thumbnail.png")));
        Assert.False(File.Exists(Path.Combine(directory, "metadata.json")));
    }

    [Fact]
    public async Task ReapplyStyle_UsesPersistedSourceWithoutNetworkAndKeepsSourceBytes()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 51.7, -2.2, "Ridge");
        string sourcePath;
        byte[] originalSource;
        SavedLocationThumbnailMetadata originalMetadata;
        using (var first = CreateService(files, new TileHandler(CreateTile())))
        {
            var generated = await first.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);
            Assert.NotNull(generated);
            sourcePath = Path.Combine(Path.GetDirectoryName(generated.ImagePath)!, "source.png");
            originalSource = await File.ReadAllBytesAsync(sourcePath);
            originalMetadata = generated.Metadata;
        }

        var offlineHandler = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using var second = CreateService(files, offlineHandler);
        var reapplied = await second.Value.GetThumbnailAsync(location,
            SavedLocationMapRefreshMode.ReapplyStyle, CancellationToken.None);

        Assert.NotNull(reapplied);
        Assert.True(reapplied.RefreshSucceeded);
        Assert.True(reapplied.WasGenerated);
        Assert.Equal(0, offlineHandler.RequestCount);
        Assert.Equal(originalSource, await File.ReadAllBytesAsync(sourcePath));
        Assert.Equal(LocationMapThumbnailService.ThumbnailStyleVersion, reapplied.Metadata.ThumbnailStyleVersion);
        Assert.Equal(originalMetadata.ProviderId, reapplied.Metadata.ProviderId);
        Assert.Equal(originalMetadata.MapStyleId, reapplied.Metadata.MapStyleId);
        Assert.Equal(originalMetadata.SourceConfigurationHash, reapplied.Metadata.SourceConfigurationHash);
        Assert.Equal(originalMetadata.SourceContentHash, reapplied.Metadata.SourceContentHash);
        Assert.Equal(originalMetadata.GeneratedAtUtc, reapplied.Metadata.GeneratedAtUtc);
        Assert.NotNull(reapplied.Metadata.ThumbnailGeneratedAtUtc);
    }

    [Fact]
    public async Task UseCache_WithValidSourceButMissingThumbnail_DoesNotReprocessOrWrite()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 52.4, -1.8, "Read-only cache");
        string directory;
        byte[] sourceBytes;
        byte[] metadataBytes;
        DateTime sourceWriteTime;
        DateTime metadataWriteTime;
        using (var first = CreateService(files, new TileHandler(CreateTile())))
        {
            var generated = await first.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);
            Assert.NotNull(generated);
            directory = Path.GetDirectoryName(generated.ImagePath)!;
            var sourcePath = Path.Combine(directory, "source.png");
            var metadataPath = Path.Combine(directory, "metadata.json");
            sourceBytes = await File.ReadAllBytesAsync(sourcePath);
            metadataBytes = await File.ReadAllBytesAsync(metadataPath);
            sourceWriteTime = File.GetLastWriteTimeUtc(sourcePath);
            metadataWriteTime = File.GetLastWriteTimeUtc(metadataPath);
            File.Delete(generated.ImagePath);
        }

        var offline = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using var second = CreateService(files, offline);
        var result = await second.Value.GetThumbnailAsync(location,
            SavedLocationMapRefreshMode.UseCache, CancellationToken.None);

        var persistedSource = Path.Combine(directory, "source.png");
        var persistedMetadata = Path.Combine(directory, "metadata.json");
        Assert.Null(result);
        Assert.Equal(0, offline.RequestCount);
        Assert.False(File.Exists(Path.Combine(directory, "thumbnail.png")));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(persistedSource));
        Assert.Equal(metadataBytes, await File.ReadAllBytesAsync(persistedMetadata));
        Assert.Equal(sourceWriteTime, File.GetLastWriteTimeUtc(persistedSource));
        Assert.Equal(metadataWriteTime, File.GetLastWriteTimeUtc(persistedMetadata));
    }

    [Fact]
    public async Task RefreshSource_PersistsOwnedSemanticFeaturesAndProvenance()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Feature map");
        var featureService = new FakeFeatureService((id, viewport) =>
            new MapFeatureFetchResult(CreateFeatures(id, viewport), MapFeatureFetchStatus.Complete));
        using var service = CreateService(files, new TileHandler(CreateTile()), featureData: featureService);

        var result = await service.Value.GetThumbnailAsync(location,
            SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, featureService.RequestCount);
        var directory = Path.GetDirectoryName(result.ImagePath)!;
        var featurePath = Path.Combine(directory, "features.json");
        Assert.True(File.Exists(featurePath));
        Assert.Equal("features.json", result.Metadata.FeatureFileName);
        Assert.Equal(OverpassMapFeatureDataService.FeatureSchemaVersion, result.Metadata.FeatureSchemaVersion);
        Assert.Equal(OverpassMapFeatureDataService.FeatureQueryVersion, result.Metadata.FeatureQueryVersion);
        Assert.Equal("openstreetmap-overpass", result.Metadata.FeatureProviderId);
        Assert.Contains("OpenStreetMap", result.Metadata.FeatureAttributionText);
        Assert.Equal(MapFeatureFetchStatus.Complete.ToString(), result.Metadata.FeatureFetchStatus);
        Assert.Equal(1, result.Metadata.FeatureRoadCount);
        Assert.Equal(0, result.Metadata.FeatureWaterwayCount);
        Assert.Equal(0, result.Metadata.FeatureBuildingCount);
        Assert.Equal(1, result.Metadata.FeatureAttemptCount);
        Assert.NotNull(result.Metadata.FeatureLastAttemptedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(result.Metadata.FeatureContentHash));
        var persisted = JsonSerializer.Deserialize<MapFeatureDataDocument>(await File.ReadAllTextAsync(featurePath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(persisted);
        Assert.Equal(location.Id, persisted.LocationId);
        Assert.Equal(result.Metadata.FeatureBounds, persisted.Source.Bounds);
    }

    [Fact]
    public async Task UseCacheAndReapplyStyle_ReusePersistedFeaturesWithoutFeatureRequests()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Cached features");
        var initialFeatures = new FakeFeatureService((id, viewport) =>
            new MapFeatureFetchResult(CreateFeatures(id, viewport), MapFeatureFetchStatus.Complete));
        string featurePath;
        byte[] featureBytes;
        DateTime featureWriteTime;
        DateTimeOffset? featureGeneratedAt;
        using (var initial = CreateService(files, new TileHandler(CreateTile()), featureData: initialFeatures))
        {
            var generated = await initial.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
                CancellationToken.None);
            featurePath = Path.Combine(Path.GetDirectoryName(generated!.ImagePath)!, "features.json");
            featureBytes = await File.ReadAllBytesAsync(featurePath);
            featureWriteTime = File.GetLastWriteTimeUtc(featurePath);
            featureGeneratedAt = generated.Metadata.FeatureGeneratedAtUtc;
        }

        var offlineFeatures = new FakeFeatureService((_, _) => throw new InvalidOperationException("Network must not run."));
        var offlineTiles = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using var cached = CreateService(files, offlineTiles, featureData: offlineFeatures);

        var cacheResult = await cached.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.UseCache,
            CancellationToken.None);
        Assert.Equal(featureBytes, await File.ReadAllBytesAsync(featurePath));
        Assert.Equal(featureWriteTime, File.GetLastWriteTimeUtc(featurePath));
        var reapplied = await cached.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.ReapplyStyle,
            CancellationToken.None);

        Assert.NotNull(cacheResult);
        Assert.NotNull(reapplied);
        Assert.Equal(0, offlineFeatures.RequestCount);
        Assert.Equal(0, offlineTiles.RequestCount);
        Assert.Equal(SavedLocationMapImageProcessor.FeatureOverlayStyleVersion,
            reapplied.Metadata.FeatureOverlayStyleVersion);
        Assert.Equal(featureGeneratedAt, reapplied.Metadata.FeatureGeneratedAtUtc);
        Assert.Equal(featureBytes, await File.ReadAllBytesAsync(featurePath));
    }

    [Fact]
    public async Task FailedFeatureRefresh_PreservesCompatibleFeatureFileAndReportsCachedPrevious()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Feature fallback");
        byte[] originalFeatures;
        using (var initial = CreateService(files, new TileHandler(CreateTile()), featureData:
                   new FakeFeatureService((id, viewport) =>
                       new MapFeatureFetchResult(CreateFeatures(id, viewport), MapFeatureFetchStatus.Complete))))
        {
            var first = await initial.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
                CancellationToken.None);
            originalFeatures = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(first!.ImagePath)!,
                "features.json"));
        }

        var unavailable = new FakeFeatureService((_, _) =>
            new MapFeatureFetchResult(null, MapFeatureFetchStatus.Unavailable, "Overpass unavailable"));
        using var refreshed = CreateService(files, new TileHandler(CreateTile()), featureData: unavailable);
        var result = await refreshed.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.RefreshSucceeded);
        Assert.Equal(MapFeatureFetchStatus.CachedPrevious.ToString(), result.Metadata.FeatureFetchStatus);
        Assert.Equal(originalFeatures, await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(result.ImagePath)!,
            "features.json")));
    }

    [Fact]
    public async Task CoordinateChange_InvalidatesOldFeaturesWhenRefreshCannotReplaceThem()
    {
        using var files = new TestDirectory();
        var id = Guid.NewGuid();
        var original = Location(id, 53.61, -0.43, "Original feature bounds");
        using (var initial = CreateService(files, new TileHandler(CreateTile()), featureData:
                   new FakeFeatureService((locationId, viewport) =>
                       new MapFeatureFetchResult(CreateFeatures(locationId, viewport), MapFeatureFetchStatus.Complete))))
            await initial.Value.GetThumbnailAsync(original, SavedLocationMapRefreshMode.RefreshSource,
                CancellationToken.None);

        var moved = original with { Coordinate = new GeoCoordinate(53.7, -0.2) };
        using var refreshed = CreateService(files, new TileHandler(CreateTile()), featureData:
            new FakeFeatureService((_, _) => new MapFeatureFetchResult(null, MapFeatureFetchStatus.Unavailable)));
        var result = await refreshed.Value.GetThumbnailAsync(moved, SavedLocationMapRefreshMode.RefreshSource,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result.Metadata.FeatureFileName);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(result.ImagePath)!, "features.json")));
        Assert.Equal(MapFeatureFetchStatus.Unavailable.ToString(), result.Metadata.FeatureFetchStatus);
    }

    [Fact]
    public async Task RefreshFeatures_ReusesSourceWithoutRasterRequestsAndCreatesMissingFeatureAsset()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.56636517088528, -0.5063319788975258, "Castlethorpe Bridge");
        using (var initial = CreateService(files, new TileHandler(CreateTile()), featureData:
                   new FakeFeatureService((_, _) => new MapFeatureFetchResult(null,
                       MapFeatureFetchOutcome.Failure("timeout", "the Overpass request timed out", timedOut: true)))))
            await initial.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
                CancellationToken.None);

        var directory = Path.Combine(files.Path, "SavedLocationMaps", location.Id.ToString("N"));
        var sourcePath = Path.Combine(directory, "source.png");
        var sourceBefore = await File.ReadAllBytesAsync(sourcePath);
        var sourceTime = File.GetLastWriteTimeUtc(sourcePath);
        var tiles = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        var features = new FakeFeatureService((id, viewport) =>
            new MapFeatureFetchResult(CreateFeatures(id, viewport), MapFeatureFetchStatus.Complete));
        using var refreshed = CreateService(files, tiles, featureData: features);

        var result = await refreshed.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshFeatures,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, tiles.RequestCount);
        Assert.Equal(1, features.RequestCount);
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(sourcePath));
        Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(sourcePath));
        Assert.True(File.Exists(Path.Combine(directory, "features.json")));
        Assert.Equal(MapFeatureFetchStatus.Complete, result.Operation!.Semantic.Status);
        Assert.True(result.Operation.RasterUsedPrevious);
        Assert.Equal(1, result.Metadata.FeatureRoadCount);
    }

    [Fact]
    public async Task RefreshFeatures_FailurePreservesCompatibleFeaturesAndThumbnailAndRecordsDiagnostics()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Retained features");
        using (var initial = CreateService(files, new TileHandler(CreateTile()), featureData:
                   new FakeFeatureService((id, viewport) =>
                       new MapFeatureFetchResult(CreateFeatures(id, viewport), MapFeatureFetchStatus.Complete))))
            await initial.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
                CancellationToken.None);

        var directory = Path.Combine(files.Path, "SavedLocationMaps", location.Id.ToString("N"));
        var featurePath = Path.Combine(directory, "features.json");
        var thumbnailPath = Path.Combine(directory, "thumbnail.png");
        var featuresBefore = await File.ReadAllBytesAsync(featurePath);
        var thumbnailBefore = await File.ReadAllBytesAsync(thumbnailPath);
        var failure = new MapFeatureFetchOutcome(MapFeatureFetchStatus.Unavailable, 0, 0, 0,
            "timeout", "the Overpass request timed out", 504, true, false, false, 2,
            DateTimeOffset.UtcNow, true);
        var tiles = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using var refreshed = CreateService(files, tiles, featureData:
            new FakeFeatureService((_, _) => new MapFeatureFetchResult(null, failure)));

        var result = await refreshed.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshFeatures,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, tiles.RequestCount);
        Assert.Equal(featuresBefore, await File.ReadAllBytesAsync(featurePath));
        Assert.Equal(thumbnailBefore, await File.ReadAllBytesAsync(thumbnailPath));
        Assert.Equal(MapFeatureFetchStatus.CachedPrevious.ToString(), result.Metadata.FeatureFetchStatus);
        Assert.Equal("timeout", result.Metadata.FeatureFailureCode);
        Assert.True(result.Metadata.FeatureFetchTimedOut);
        Assert.Equal(2, result.Metadata.FeatureAttemptCount);
        Assert.True(result.Operation!.ThumbnailUsedPrevious);
    }

    [Fact]
    public async Task RefreshFeatures_FailureWithoutPreviousFeaturesPreservesBaseThumbnailAndRecordsUnavailable()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Base only");
        var initialFailure = MapFeatureFetchOutcome.Failure("http_error", "Overpass returned HTTP 503",
            httpStatusCode: 503);
        using (var initial = CreateService(files, new TileHandler(CreateTile()), featureData:
                   new FakeFeatureService((_, _) => new MapFeatureFetchResult(null, initialFailure))))
            await initial.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
                CancellationToken.None);
        var directory = Path.Combine(files.Path, "SavedLocationMaps", location.Id.ToString("N"));
        var thumbnailPath = Path.Combine(directory, "thumbnail.png");
        var thumbnailBefore = await File.ReadAllBytesAsync(thumbnailPath);
        var retryFailure = MapFeatureFetchOutcome.Failure("parse_failed", "the feature response could not be parsed",
            parseFailed: true);
        using var refreshed = CreateService(files,
            new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable), featureData:
            new FakeFeatureService((_, _) => new MapFeatureFetchResult(null, retryFailure)));

        var result = await refreshed.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshFeatures,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(thumbnailBefore, await File.ReadAllBytesAsync(thumbnailPath));
        Assert.False(File.Exists(Path.Combine(directory, "features.json")));
        Assert.Equal(MapFeatureFetchStatus.Unavailable.ToString(), result.Metadata.FeatureFetchStatus);
        Assert.True(result.Metadata.FeatureParseFailed);
        Assert.Equal("parse_failed", result.Metadata.FeatureFailureCode);
    }

    [Fact]
    public async Task RefreshBuildings_UsesCachedRasterAndCoreFeaturesWithoutTheirNetworkRequests()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.5664, -0.5063, "Castlethorpe Bridge");
        var initialBuildings = new FakeBuildingService((id, viewport, _) =>
            BuildingResult(id, viewport, 2, BuildingStarStatus.Complete));
        using (var initial = CreateService(files, new TileHandler(CreateTile()),
                   featureData: new FakeFeatureService((id, viewport) => CompleteFeatures(id, viewport)),
                   buildingData: initialBuildings))
            Assert.NotNull(await initial.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, default));

        var directory = Path.Combine(files.Path, "SavedLocationMaps", location.Id.ToString("N"));
        var sourceBefore = await File.ReadAllBytesAsync(Path.Combine(directory, "source.png"));
        var featuresBefore = await File.ReadAllBytesAsync(Path.Combine(directory, "features.json"));
        var offlineTiles = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        var core = new FakeFeatureService((_, _) => throw new InvalidOperationException("Core fetch is forbidden."));
        var buildings = new FakeBuildingService((id, viewport, force) =>
            BuildingResult(id, viewport, 5, BuildingStarStatus.Complete));
        using var refresh = CreateService(files, offlineTiles, featureData: core, buildingData: buildings);

        var result = await refresh.Value.GetThumbnailAsync(location,
            SavedLocationMapRefreshMode.RefreshBuildings, default);

        Assert.NotNull(result);
        Assert.Equal(BuildingStarStatus.Complete, result.Operation!.Buildings!.Status);
        Assert.Equal(0, offlineTiles.RequestCount);
        Assert.Equal(0, core.RequestCount);
        Assert.Equal(1, buildings.RequestCount);
        Assert.True(buildings.ForceRefreshValues.Single());
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(Path.Combine(directory, "source.png")));
        Assert.Equal(featuresBefore, await File.ReadAllBytesAsync(Path.Combine(directory, "features.json")));
        Assert.Equal(5, result.Metadata.BuildingCount);
        Assert.True(File.Exists(Path.Combine(directory, "buildings.json")));
    }

    [Fact]
    public async Task FailedBuildingRefresh_PreservesPreviousBuildingAggregateAndThumbnail()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.5664, -0.5063, "Castlethorpe Bridge");
        using (var initial = CreateService(files, new TileHandler(CreateTile()),
                   featureData: new FakeFeatureService((id, viewport) => CompleteFeatures(id, viewport)),
                   buildingData: new FakeBuildingService((id, viewport, _) =>
                       BuildingResult(id, viewport, 3, BuildingStarStatus.Complete))))
            Assert.NotNull(await initial.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, default));
        var directory = Path.Combine(files.Path, "SavedLocationMaps", location.Id.ToString("N"));
        var buildingsPath = Path.Combine(directory, "buildings.json");
        var thumbnailPath = Path.Combine(directory, "thumbnail.png");
        var buildingsBefore = await File.ReadAllBytesAsync(buildingsPath);
        var thumbnailBefore = await File.ReadAllBytesAsync(thumbnailPath);
        var failed = new FakeBuildingService((_, _, _) => new BuildingFeatureFetchResult(null,
            BuildingFeatureFetchOutcome.Unavailable("timeout", "the building request timed out",
                timedOut: true)));
        using var refresh = CreateService(files, new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable),
            featureData: new FakeFeatureService((_, _) => throw new InvalidOperationException()),
            buildingData: failed);

        var result = await refresh.Value.GetThumbnailAsync(location,
            SavedLocationMapRefreshMode.RefreshBuildings, default);

        Assert.NotNull(result);
        Assert.Equal(BuildingStarStatus.Cached, result.Operation!.Buildings!.Status);
        Assert.Equal("timeout", result.Metadata.BuildingFailureCode);
        Assert.Equal(buildingsBefore, await File.ReadAllBytesAsync(buildingsPath));
        Assert.Equal(thumbnailBefore, await File.ReadAllBytesAsync(thumbnailPath));
    }

    [Fact]
    public async Task RegenerateMapImage_ReusesCompatibleBuildingAggregate()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.5664, -0.5063, "Castlethorpe Bridge");
        using (var initial = CreateService(files, new TileHandler(CreateTile()),
                   featureData: new FakeFeatureService((id, viewport) => CompleteFeatures(id, viewport)),
                   buildingData: new FakeBuildingService((id, viewport, _) =>
                       BuildingResult(id, viewport, 4, BuildingStarStatus.Complete))))
            Assert.NotNull(await initial.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, default));

        var mustNotFetch = new FakeBuildingService((_, _, _) =>
            throw new InvalidOperationException("A compatible building aggregate must be reused."));
        using var regenerated = CreateService(files, new TileHandler(CreateTile()),
            featureData: new FakeFeatureService((id, viewport) => CompleteFeatures(id, viewport)),
            buildingData: mustNotFetch);

        var result = await regenerated.Value.GetThumbnailAsync(location,
            SavedLocationMapRefreshMode.RefreshSource, default);

        Assert.NotNull(result);
        Assert.Equal(0, mustNotFetch.RequestCount);
        Assert.Equal(BuildingStarStatus.Cached, result.Operation!.Buildings!.Status);
        Assert.Equal(4, result.Metadata.BuildingCount);
    }

    [Fact]
    public async Task UseCacheAndReapplyStyle_NeverRequestBuildingData()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.5664, -0.5063, "Castlethorpe Bridge");
        using (var initial = CreateService(files, new TileHandler(CreateTile()),
                   featureData: new FakeFeatureService((id, viewport) => CompleteFeatures(id, viewport)),
                   buildingData: new FakeBuildingService((id, viewport, _) =>
                       BuildingResult(id, viewport, 2, BuildingStarStatus.Complete))))
            Assert.NotNull(await initial.Value.GetThumbnailAsync(location,
                SavedLocationMapRefreshMode.RefreshSource, default));

        var forbidden = new FakeBuildingService((_, _, _) =>
            throw new InvalidOperationException("Network-free modes must not request buildings."));
        var offlineTiles = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using var cached = CreateService(files, offlineTiles,
            featureData: new FakeFeatureService((_, _) => throw new InvalidOperationException()),
            buildingData: forbidden);

        Assert.NotNull(await cached.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.UseCache, default));
        Assert.NotNull(await cached.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.ReapplyStyle, default));
        Assert.Equal(0, forbidden.RequestCount);
        Assert.Equal(0, offlineTiles.RequestCount);
    }

    private static SavedLocation Location(Guid id, double latitude, double longitude, string name) =>
        new(id, name, new GeoCoordinate(latitude, longitude), "UTC");

    private static ServiceLease CreateService(TestDirectory files, TileHandler handler,
        IMapTileSourceProvider? source = null, IMapFeatureDataService? featureData = null,
        IBuildingFeatureDataService? buildingData = null)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var service = new LocationMapThumbnailService(client, NullLogger<LocationMapThumbnailService>.Instance,
            new TestPathProvider(files.Path), source ?? new DefaultMapTileSourceProvider(),
            new SavedLocationMapImageProcessor(), featureData, buildingData);
        return new ServiceLease(service, client);
    }

    private static MapFeatureDataDocument CreateFeatures(Guid locationId, WebMercatorViewport viewport)
    {
        var start = viewport.Unproject(80, 100);
        var end = viewport.Unproject(viewport.Width - 80, 115);
        return new MapFeatureDataDocument(OverpassMapFeatureDataService.FeatureSchemaVersion, locationId,
            new MapFeatureSourceMetadata("openstreetmap-overpass", "OpenStreetMap",
                "© OpenStreetMap contributors", "https://www.openstreetmap.org/copyright",
                "Open Database License (ODbL)", "https://opendatacommons.org/licenses/odbl/",
                "test-overpass", OverpassMapFeatureDataService.FeatureQueryVersion, DateTimeOffset.UnixEpoch,
                viewport.Bounds),
            [new MapRoadFeature(1, "way", MapRoadClassification.ARoad,
                [new(start.Latitude, start.Longitude), new(end.Latitude, end.Longitude)],
                "primary", "A15", null, null, null, null)], [], []);
    }

    private static MapFeatureFetchResult CompleteFeatures(Guid locationId, WebMercatorViewport viewport) =>
        new(CreateFeatures(locationId, viewport), new MapFeatureFetchOutcome(
            MapFeatureFetchStatus.Complete, 1, 0, 0, null, null, 200, false, false,
            false, 1, DateTimeOffset.UtcNow, false));

    private static BuildingFeatureFetchResult BuildingResult(Guid locationId,
        WebMercatorViewport viewport, int count, BuildingStarStatus status)
    {
        var values = Enumerable.Range(0, count).Select(index =>
        {
            var coordinate = viewport.Unproject(200 + index * 15, 160 + index * 8);
            return new BuildingStarFeature("way", index + 1, coordinate.Latitude,
                coordinate.Longitude, "house", 2, null);
        }).ToArray();
        var document = new BuildingFeatureDocument(OverpassBuildingFeatureDataService.SchemaVersion,
            locationId, viewport.CentreLatitude, viewport.CentreLongitude, viewport.Zoom,
            viewport.Width, viewport.Height, viewport.Bounds,
            new BuildingFeatureSourceMetadata("openstreetmap-overpass-buildings", "OpenStreetMap",
                "© OpenStreetMap contributors", "https://www.openstreetmap.org/copyright",
                "Open Database License (ODbL)", "https://opendatacommons.org/licenses/odbl/",
                "test", OverpassBuildingFeatureDataService.QueryVersion, DateTimeOffset.UtcNow),
            values, false, []);
        return new BuildingFeatureFetchResult(document, new BuildingFeatureFetchOutcome(status, count,
            null, null, 200, false, false, 1, DateTimeOffset.UtcNow, false, 0, 1, 1,
            0, status == BuildingStarStatus.Cached ? 1 : 0, 0,
            status == BuildingStarStatus.Complete ? 1 : 0));
    }

    private static byte[] CreateTile()
    {
        using var bitmap = new SKBitmap(256, 256);
        using var canvas = new SKCanvas(bitmap);
        using (var terrain = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(256, 256),
                [SKColor.Parse("#C9D3BC"), SKColor.Parse("#E2D5BD"), SKColor.Parse("#B8C9B5")],
                [0, .52f, 1], SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(SKRect.Create(256, 256), terrain);
        using var minorRoad = new SKPaint
        {
            Color = SKColor.Parse("#A99F91"), StrokeWidth = 2.5f, IsAntialias = true
        };
        for (var offset = -160; offset < 320; offset += 34)
            canvas.DrawLine(offset, 256, offset + 180, 0, minorRoad);
        using var road = new SKPaint { Color = SKColor.Parse("#6E7E90"), StrokeWidth = 12, IsAntialias = true };
        canvas.DrawLine(0, 210, 256, 40, road);
        using var river = new SKPaint { Color = SKColor.Parse("#7195B0"), StrokeWidth = 18, IsAntialias = true };
        canvas.DrawArc(new SKRect(30, 20, 230, 250), 35, 205, false, river);
        using var boundary = new SKPaint
        {
            Color = SKColor.Parse("#8B9B82"), StrokeWidth = 1.5f, IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([5, 4], 0)
        };
        canvas.DrawCircle(62, 78, 45, boundary);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateUniformImage(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateDetailedSource()
    {
        using var tile = SKBitmap.Decode(CreateTile());
        using var bitmap = new SKBitmap(LocationMapThumbnailService.SourceWidth,
            LocationMapThumbnailService.SourceHeight);
        using var canvas = new SKCanvas(bitmap);
        for (var y = 0; y < bitmap.Height; y += tile.Height)
        for (var x = 0; x < bitmap.Width; x += tile.Width)
            canvas.DrawBitmap(tile, x, y);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static ArtworkMetrics MeasureVisibleMapOutsidePin(SKBitmap bitmap)
    {
        var startX = bitmap.Width / 2;
        const int step = 2;
        var opaque = 0;
        var edgeSamples = 0;
        double sum = 0;
        double squaredSum = 0;
        double edgeSum = 0;
        for (var y = 0; y < bitmap.Height; y += step)
        for (var x = startX; x < bitmap.Width; x += step)
        {
            if (x >= bitmap.Width / 2 - 28 && x <= bitmap.Width / 2 + 28 &&
                y >= bitmap.Height / 2 - 38 && y <= bitmap.Height / 2 + 38)
                continue;
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.Alpha < 200) continue;
            opaque++;
            var luminance = Luminance(pixel);
            sum += luminance;
            squaredSum += luminance * luminance;
            if (x + step >= bitmap.Width) continue;
            var adjacent = bitmap.GetPixel(x + step, y);
            if (adjacent.Alpha < 200) continue;
            edgeSum += Math.Abs(luminance - Luminance(adjacent));
            edgeSamples++;
        }
        var mean = sum / Math.Max(1, opaque);
        var deviation = Math.Sqrt(Math.Max(0, squaredSum / Math.Max(1, opaque) - mean * mean));
        return new ArtworkMetrics(opaque, mean, deviation, edgeSum / Math.Max(1, edgeSamples));
    }

    private static double Luminance(SKColor pixel) =>
        .2126 * pixel.Red + .7152 * pixel.Green + .0722 * pixel.Blue;

    private sealed record ArtworkMetrics(int OpaqueSamples, double MeanLuminance,
        double LuminanceDeviation, double MeanEdgeDifference);

    private sealed class TileHandler(byte[] tile, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(tile)
            });
        }
    }

    private sealed class TestPathProvider(string path) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => path;
    }

    private sealed class TestSourceProvider(string id, string name) : IMapTileSourceProvider
    {
        public MapTileSourceDefinition Current { get; } = new(id, name, "test-style",
            "https://tiles.test/{z}/{x}/{y}.png", "test-style-source", $"Attribution for {name}",
            "https://example.test/attribution", "Test licence", "https://example.test/licence");
    }

    private sealed class FakeFeatureService(
        Func<Guid, WebMercatorViewport, MapFeatureFetchResult> fetch) : IMapFeatureDataService
    {
        public int RequestCount { get; private set; }

        public Task<MapFeatureFetchResult> FetchAsync(Guid locationId, WebMercatorViewport viewport,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(fetch(locationId, viewport));
        }
    }

    private sealed class FakeBuildingService(
        Func<Guid, WebMercatorViewport, bool, BuildingFeatureFetchResult> fetch)
        : IBuildingFeatureDataService
    {
        public string SharedCacheDirectory => string.Empty;
        public int RequestCount { get; private set; }
        public List<bool> ForceRefreshValues { get; } = [];

        public Task<BuildingFeatureFetchResult> FetchAsync(Guid locationId, string locationName,
            WebMercatorViewport viewport, bool forceRefresh, CancellationToken cancellationToken)
        {
            RequestCount++;
            ForceRefreshValues.Add(forceRefresh);
            return Task.FromResult(fetch(locationId, viewport, forceRefresh));
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Noctaxis.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }

    private sealed class ServiceLease(LocationMapThumbnailService value, HttpClient client) : IDisposable
    {
        public LocationMapThumbnailService Value { get; } = value;
        public void Dispose() => client.Dispose();
    }
}
