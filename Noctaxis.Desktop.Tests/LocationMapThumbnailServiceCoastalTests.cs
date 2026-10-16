using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Desktop.Mapping.Preview;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.Themes;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Sea-dominated saved locations. The legacy card artwork rejects frames that are mostly open sea, and
/// that rejection must never discard the source data the saved-location preview is built from.
/// </summary>
public sealed partial class LocationMapThumbnailServiceTests
{
    // Embleton Bay, Northumberland: the original coastal point whose acquisition failed. The coastline runs
    // just west of it, so the east of the frame (the legacy artwork's visible side) is open sea.
    private const double CoastalLatitude = 55.4930, CoastalLongitude = -1.6110;
    private const int CoastOffsetPixels = 40;

    private static SavedLocation CoastalLocation(Guid? id = null) =>
        Location(id ?? Guid.NewGuid(), CoastalLatitude, CoastalLongitude, "Embleton Bay");

    private static WebMercatorViewport CoastalViewport() => WebMercatorViewport.Create(
        new GeoCoordinate(CoastalLatitude, CoastalLongitude), LocationMapThumbnailService.TileZoom,
        LocationMapThumbnailService.TileSize, LocationMapThumbnailService.SourceWidth, LocationMapThumbnailService.SourceHeight);

    [Fact]
    public async Task CoastalSeaFrame_PersistsSourceDataWhenLegacyArtworkIsRejected_AndThePreviewRenders()
    {
        using var files = new TestDirectory();
        var location = CoastalLocation();
        var viewport = CoastalViewport();
        var tiles = new CoastalTileHandler(viewport.WorldPixelCentreX - CoastOffsetPixels);
        var features = new FakeFeatureService((id, v) => CoastalFeatures(id, v));
        var settlement = new CoastalSettlementService();
        using var service = CreateService(files, tiles, featureData: features, settlementData: settlement);

        var result = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
            CancellationToken.None);

        // 1. The required source data was acquired.
        Assert.NotNull(result);
        Assert.True(result.RefreshSucceeded);
        Assert.True(result.WasGenerated);
        var operation = Assert.IsType<SavedLocationMapRefreshResult>(result.Operation);
        Assert.True(operation.RasterSucceeded);
        Assert.Equal(MapFeatureFetchStatus.Complete, operation.Semantic.Status);
        Assert.Equal(EnvironmentalDataState.Available, operation.SettlementState);
        Assert.True(operation.IsComplete);

        // 2. The legacy artwork processor (kept for the calibration tool) still rejects the sea-dominated frame, and
        //    the service composes no artwork at all now that it is retired.
        var directory = Path.Combine(service.Value.StorageDirectory, location.Id.ToString("N"));
        var source = await File.ReadAllBytesAsync(Path.Combine(directory, "source.png"));
        Assert.Throws<LegacyArtworkRejectedException>(() => new SavedLocationMapImageProcessor().ProcessSettlement(
            source, CoastalFeatures(location.Id, viewport).Data, null, viewport, location.Id, out _));
        Assert.False(operation.ThumbnailSucceeded);
        Assert.True(operation.LegacyArtworkSkipped);
        Assert.True(result.Metadata.LegacyArtworkSkipped);
        Assert.Equal(LocationMapThumbnailService.LegacyArtworkRetiredReason, result.Metadata.ThumbnailSkippedReason);
        Assert.Null(result.Metadata.ThumbnailContentHash);

        // 3. The source data is persisted, hash-consistent, with no artwork and no staging leftovers.
        var metadata = JsonSerializer.Deserialize<SavedLocationThumbnailMetadata>(
            await File.ReadAllBytesAsync(Path.Combine(directory, "metadata.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(result.Metadata.ThumbnailSkippedReason, metadata.ThumbnailSkippedReason);
        Assert.Equal(HashOf(source), metadata.SourceContentHash);
        Assert.Equal(HashOf(await File.ReadAllBytesAsync(Path.Combine(directory, "features.json"))), metadata.FeatureContentHash);
        Assert.Equal(HashOf(await File.ReadAllBytesAsync(Path.Combine(directory, "settlement-field.bin.gz"))),
            metadata.SettlementContentHash);
        Assert.False(File.Exists(Path.Combine(directory, "thumbnail.png")));
        Assert.Empty(Directory.GetFiles(directory, ".*"));

        // Offline reuse: the saved data satisfies the cache with no acquisition of any kind.
        var offline = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        var offlineFeatures = new FakeFeatureService((id, v) => CoastalFeatures(id, v));
        var offlineSettlement = new CoastalSettlementService();
        using var reopened = CreateService(files, offline, featureData: offlineFeatures, settlementData: offlineSettlement);
        var metadataWriteTime = File.GetLastWriteTimeUtc(Path.Combine(directory, "metadata.json"));
        var cached = await reopened.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.UseCache,
            CancellationToken.None);
        Assert.NotNull(cached);
        Assert.True(cached.Metadata.LegacyArtworkSkipped);
        Assert.Equal(0, offline.RequestCount + offlineFeatures.RequestCount + offlineSettlement.RequestCount);
        Assert.Equal(metadataWriteTime, File.GetLastWriteTimeUtc(Path.Combine(directory, "metadata.json")));

        // 4. The preview scene builds from the persisted data: open sea, the coast road and the village light.
        var scene = await LocationPreviewSceneLoader.LoadAsync(Path.GetDirectoryName(cached.ImagePath)!, cached.Metadata,
            location.Coordinate, CancellationToken.None);
        Assert.True(scene.HasMapContent);
        Assert.True(scene.Water.Count(v => v > 128) > scene.Water.Length / 3, "the sea is classified as water");
        Assert.NotEmpty(scene.Lines);
        Assert.Contains(scene.Settlement, v => v > 0);

        // 5. The preview renderer renders it in every theme, appearance and colour-vision mode.
        var renditions = LocationPreviewExport.RenderMatrix(scene, new ThemeCatalogue(isAvailable: _ => true), 425, 240).ToArray();
        Assert.Equal(56, renditions.Length);
        foreach (var rendition in renditions)
        {
            using var bitmap = SKBitmap.Decode(rendition.Png);
            Assert.Equal((425, 240), (bitmap.Width, bitmap.Height));
            var luminance = bitmap.Pixels.Select(p => .2126 * p.Red + .7152 * p.Green + .0722 * p.Blue).ToArray();
            Assert.True(luminance.Max() - luminance.Min() > 40, $"{rendition.FileName} shows the scene");
        }
    }

    [Fact]
    public async Task CoastalRequiredSourceFailure_StillFailsAndCommitsNothing()
    {
        using var files = new TestDirectory();
        var location = CoastalLocation();
        var tiles = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using var service = CreateService(files, tiles, settlementData: new CoastalSettlementService());

        var result = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
            CancellationToken.None);

        Assert.Null(result);
        Assert.True(tiles.RequestCount >= 3);
        var directory = Path.Combine(service.Value.StorageDirectory, location.Id.ToString("N"));
        Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
    }

    [Fact]
    public async Task CoastalSkippedArtwork_LaterFeatureSettlementAndStyleRefreshesStillPersistSourceData()
    {
        using var files = new TestDirectory();
        var location = CoastalLocation();
        var viewport = CoastalViewport();
        var tiles = new CoastalTileHandler(viewport.WorldPixelCentreX - CoastOffsetPixels);
        var featuresAvailable = false;
        var features = new FakeFeatureService((id, v) => featuresAvailable
            ? CoastalFeatures(id, v)
            : new MapFeatureFetchResult(null, MapFeatureFetchStatus.Unavailable, "Overpass timed out"));
        using var service = CreateService(files, tiles, featureData: features, settlementData: new CoastalSettlementService());
        var directory = Path.Combine(service.Value.StorageDirectory, location.Id.ToString("N"));

        var first = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
            CancellationToken.None);
        Assert.NotNull(first);
        Assert.True(first.Metadata.LegacyArtworkSkipped);
        Assert.False(File.Exists(Path.Combine(directory, "features.json")));
        Assert.True(first.Operation!.IsDegraded);

        // Features arrive later: they are committed although the artwork is still rejected.
        featuresAvailable = true;
        var refreshed = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshFeatures,
            CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.RefreshSucceeded);
        Assert.Equal(MapFeatureFetchStatus.Complete, refreshed.Operation!.Semantic.Status);
        Assert.True(refreshed.Metadata.LegacyArtworkSkipped);
        Assert.Equal(HashOf(await File.ReadAllBytesAsync(Path.Combine(directory, "features.json"))),
            refreshed.Metadata.FeatureContentHash);

        var settlement = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSettlement,
            CancellationToken.None);
        Assert.NotNull(settlement);
        Assert.True(settlement.RefreshSucceeded);
        Assert.True(settlement.Operation!.LegacyArtworkSkipped);

        var restyled = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.ReapplyStyle,
            CancellationToken.None);
        Assert.NotNull(restyled);
        Assert.True(restyled.RefreshSucceeded);
        Assert.True(restyled.Metadata.LegacyArtworkSkipped);
        Assert.False(File.Exists(Path.Combine(directory, "thumbnail.png")));
        Assert.Empty(Directory.GetFiles(directory, ".*"));

        // A later failed refresh falls back to the saved data rather than losing the location.
        using var failing = CreateService(files, new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable),
            featureData: features, settlementData: new CoastalSettlementService());
        var fallback = await failing.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
            CancellationToken.None);
        Assert.NotNull(fallback);
        Assert.False(fallback.RefreshSucceeded);
        Assert.True(fallback.IsPreviousAsset);
        Assert.True(fallback.Operation!.LegacyArtworkSkipped);
        Assert.Equal(restyled.Metadata.SourceContentHash, fallback.Metadata.SourceContentHash);
    }

    [Fact]
    public async Task RejectedArtworkReplacesEarlierArtwork_SoStaleThumbnailNeverPairsWithNewData()
    {
        using var files = new TestDirectory();
        var location = CoastalLocation();
        var viewport = CoastalViewport();
        using (var inland = CreateService(files, new TileHandler(CreateTile()), settlementData: new CoastalSettlementService()))
        {
            var detailed = await inland.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
                CancellationToken.None);
            Assert.NotNull(detailed);
            Assert.True(detailed.Metadata.LegacyArtworkSkipped);
            // Artwork left by an earlier version, which the next commit must not pair with new data.
            File.Copy(Path.Combine(Path.GetDirectoryName(detailed.ImagePath)!, "source.png"), detailed.ImagePath);
        }

        using var coastal = CreateService(files, new CoastalTileHandler(viewport.WorldPixelCentreX - CoastOffsetPixels),
            settlementData: new CoastalSettlementService());
        var result = await coastal.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Metadata.LegacyArtworkSkipped);
        Assert.False(File.Exists(result.ImagePath));
        var cached = await coastal.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.UseCache,
            CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal(result.Metadata.SourceContentHash, cached.Metadata.SourceContentHash);
        Assert.True(cached.Metadata.LegacyArtworkSkipped);
    }

    private static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A coast road on the land side of the frame.</summary>
    private static MapFeatureFetchResult CoastalFeatures(Guid locationId, WebMercatorViewport viewport)
    {
        var coast = viewport.Width / 2 - CoastOffsetPixels;
        var start = viewport.Unproject(40, 60);
        var end = viewport.Unproject(coast - 30, viewport.Height - 60);
        var data = new MapFeatureDataDocument(OverpassMapFeatureDataService.FeatureSchemaVersion, locationId,
            new MapFeatureSourceMetadata("openstreetmap-overpass", "OpenStreetMap",
                "© OpenStreetMap contributors", "https://www.openstreetmap.org/copyright",
                "Open Database License (ODbL)", "https://opendatacommons.org/licenses/odbl/",
                "test-overpass", OverpassMapFeatureDataService.FeatureQueryVersion, DateTimeOffset.UnixEpoch,
                viewport.Bounds),
            [new MapRoadFeature(1, "way", MapRoadClassification.BRoad,
                [new(start.Latitude, start.Longitude), new(end.Latitude, end.Longitude)],
                "secondary", "B1339", null, null, null, null)], []);
        return new(data, new MapFeatureFetchOutcome(MapFeatureFetchStatus.Complete, 1, 0, null, null, 200,
            false, false, false, 1, DateTimeOffset.UtcNow, false));
    }

    /// <summary>OSM-style tiles for a north–south coastline: detailed land to the west, open sea to the east.</summary>
    private sealed class CoastalTileHandler(double coastWorldX) : HttpMessageHandler
    {
        private static readonly SKColor Sea = SKColor.Parse("#AAD3DF");
        private readonly byte[] _land = CreateTile();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // .../{z}/{x}/{y}.png
            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            var tileX = int.Parse(segments[^2]);
            using var land = SKBitmap.Decode(_land);
            using var tile = new SKBitmap(256, 256);
            using (var canvas = new SKCanvas(tile))
            {
                canvas.Clear(Sea);
                var landWidth = (float)Math.Clamp(coastWorldX - tileX * 256d, 0, 256);
                canvas.Save();
                canvas.ClipRect(SKRect.Create(landWidth, 256));
                canvas.DrawBitmap(land, 0, 0);
                canvas.Restore();
            }
            using var image = SKImage.FromBitmap(tile);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png.ToArray()) });
        }
    }

    /// <summary>A village on the land side of the frame, as the WSF settlement layer would record it.</summary>
    private sealed class CoastalSettlementService : ISettlementDataProvider
    {
        public int RequestCount { get; private set; }

        public Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var fractions = new float[request.Width * request.Height];
            var heights = new float[fractions.Length];
            for (var y = request.Height * 2 / 5; y < request.Height * 3 / 5; y++)
            for (var x = request.Width / 5; x < request.Width * 7 / 20; x++)
            {
                fractions[y * request.Width + x] = .7f;
                heights[y * request.Width + x] = 8;
            }
            return Task.FromResult(new EnvironmentalValue<SettlementRaster>(EnvironmentalDataState.Available,
                new SettlementRaster("test-wsf", "1", request, fractions, heights), "test-wsf", "1", "Synthetic WSF"));
        }
    }
}
