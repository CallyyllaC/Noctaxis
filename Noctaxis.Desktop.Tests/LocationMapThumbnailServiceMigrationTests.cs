using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Mapping.Preview;
using Noctaxis.Desktop.Mapping.Vector;
using Noctaxis.Desktop.Services;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Saved locations whose map data predates the OpenFreeMap landscape (metadata schema 9: an OpenStreetMap raster
/// capture in source.png, roads and waterways from Overpass) keep loading, offline and unchanged, until a requested
/// acquisition commits a complete replacement; only then is the raster capture removed.
/// </summary>
public sealed partial class LocationMapThumbnailServiceTests
{
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public async Task LegacyRasterAssets_StayCompleteOfflineAndStillBuildTheirPreview()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Legacy");
        var directory = await WriteLegacyRasterAssetsAsync(files, location);
        var metadataBefore = await File.ReadAllBytesAsync(Path.Combine(directory, "metadata.json"));

        var offline = new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable);
        using var service = CreateService(files, offline,
            featureData: new FakeFeatureService((_, _) => throw new InvalidOperationException("No acquisition on load.")));
        var cached = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.UseCache, CancellationToken.None);
        var bulk = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.AcquireMissing, CancellationToken.None);

        Assert.NotNull(cached);
        Assert.False(cached.WasGenerated);
        Assert.False(cached.Metadata.HasVectorLandscape);
        // Bulk regeneration counts the earlier capture as held: it does not download a replacement for every location.
        Assert.NotNull(bulk);
        Assert.False(bulk.WasGenerated);
        Assert.Equal(0, offline.RequestCount);
        Assert.Equal(metadataBefore, await File.ReadAllBytesAsync(Path.Combine(directory, "metadata.json")));

        var scene = await LocationPreviewSceneLoader.LoadAsync(directory, cached.Metadata, location.Coordinate, CancellationToken.None);
        Assert.True(scene.Water.Take(scene.MaskWidth / 4).Any(v => v > 200), "the raster's water is still classified");
        Assert.Contains(scene.Vegetation, v => v > 200);
        Assert.NotEmpty(scene.Lines); // The Overpass roads stay drawable.
    }

    [Fact]
    public async Task LegacyRasterAssets_AreKeptWhenTheirReplacementCannotBeAcquired()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Legacy offline");
        var directory = await WriteLegacyRasterAssetsAsync(files, location);
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);

        using var service = CreateService(files, new TileHandler(CreateTile(), HttpStatusCode.ServiceUnavailable));
        var result = await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.RefreshSucceeded);
        Assert.True(result.IsPreviousAsset);
        var after = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, bytes) in before) Assert.Equal(bytes, after[name]);
        Assert.False(File.Exists(Path.Combine(directory, SavedLocationLandscapeCodec.FileName)));
    }

    [Fact]
    public async Task LegacyRasterAssets_AreReplacedByAVectorGenerationOnlyOnceItIsCommitted()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Legacy online");
        var directory = await WriteLegacyRasterAssetsAsync(files, location);
        var tiles = new TileHandler(CreateTile());
        var repository = new VectorTileRepository(tiles);
        var service = new LocationMapThumbnailService(NullLogger<LocationMapThumbnailService>.Instance,
            new TestPathProvider(files.Path), new OpenFreeMapLandscapeSource(repository), new SavedLocationMapImageProcessor(),
            new OpenFreeMapMapFeatureDataService(repository, NullLogger<OpenFreeMapMapFeatureDataService>.Instance),
            new FakeSettlementService(available: true));

        var result = await service.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.RefreshSucceeded);
        Assert.True(result.Operation!.IsComplete);
        Assert.True(result.Metadata.HasVectorLandscape);
        Assert.Equal(OpenFreeMapMapFeatureDataService.ProviderId, result.Metadata.FeatureProviderId);
        Assert.False(File.Exists(Path.Combine(directory, LocationMapThumbnailService.LegacyRasterSourceFileName)));
        Assert.True(File.Exists(Path.Combine(directory, SavedLocationLandscapeCodec.FileName)));
        Assert.Empty(Directory.GetFiles(directory, ".*"));
        // The landscape and the roads and waterways came from one read of each tile.
        Assert.Equal(result.Metadata.SourceTileCount, tiles.RequestCount);
        Assert.True(result.Metadata.FeatureRoadCount > 0);
        Assert.True(result.Metadata.FeatureWaterwayCount > 0);

        var scene = await LocationPreviewSceneLoader.LoadAsync(directory, result.Metadata, location.Coordinate, CancellationToken.None);
        Assert.Contains(scene.Water, v => v > 200);
        Assert.Contains(scene.Vegetation, v => v > 200);
        Assert.Contains(scene.BuiltUp, v => v > 200);
        Assert.Contains(scene.Lines, line => line.Class == PreviewLineClass.River);
        Assert.Contains(scene.Lines, line => line.Class == PreviewLineClass.Major);
        Assert.Contains(scene.Settlement, v => v > 0); // WSF still supplies the settlement field.
    }

    [Fact]
    public async Task ACancelledAcquisitionLeavesTheSavedDataUntouched()
    {
        using var files = new TestDirectory();
        var location = Location(Guid.NewGuid(), 53.61, -0.43, "Cancelled");
        var directory = await WriteLegacyRasterAssetsAsync(files, location);
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        var gated = new GatedTileHandler(CreateTile());
        using var service = CreateService(files, gated);
        using var cancellation = new CancellationTokenSource();

        var acquisition = service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource, cancellation.Token);
        await gated.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        gated.Release.TrySetResult();
        var after = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (name, bytes) in before) Assert.Equal(bytes, after[name]);
    }

    /// <summary>
    /// Writes a location's map data as schema 9 wrote it: an OpenStreetMap Standard raster capture and an Overpass
    /// road and water document, with a current WSF field. Returns the location's asset directory.
    /// </summary>
    private static async Task<string> WriteLegacyRasterAssetsAsync(TestDirectory files, SavedLocation location)
    {
        SavedLocationThumbnailMetadata generated;
        using (var current = CreateService(files, new TileHandler(CreateTile()),
                   featureData: new FakeFeatureService((id, viewport) => LegacyOverpassFeatures(id, viewport))))
            generated = (await current.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource,
                CancellationToken.None))!.Metadata;
        var directory = Path.Combine(files.Path, "SavedLocationMaps", location.Id.ToString("N"));
        File.Delete(Path.Combine(directory, SavedLocationLandscapeCodec.FileName));
        var png = OpenStreetMapStandardCapture();
        await File.WriteAllBytesAsync(Path.Combine(directory, LocationMapThumbnailService.LegacyRasterSourceFileName), png);
        var legacy = generated with
        {
            SchemaVersion = 9, ProviderId = "openstreetmap-standard", ProviderName = "OpenStreetMap", MapStyleId = "standard",
            SourceIdentifier = "openstreetmap-standard-raster",
            SourceRenderVersion = "direct-tile-mosaic-v3", SourceFileName = LocationMapThumbnailService.LegacyRasterSourceFileName,
            SourceContentHash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),
            SourceFormat = null, SourceTileCount = null, SourceEmptyTileCount = null
        };
        legacy = legacy with { StyledInputHash = LocationMapThumbnailService.ComputeStyledInputHash(legacy) };
        await File.WriteAllTextAsync(Path.Combine(directory, "metadata.json"), JsonSerializer.Serialize(legacy, MetadataJson));
        Assert.False(legacy.HasVectorLandscape);
        Assert.Equal(1, legacy.FeatureSchemaVersion);
        return directory;
    }

    private static MapFeatureFetchResult LegacyOverpassFeatures(Guid locationId, WebMercatorViewport viewport)
    {
        var start = viewport.Unproject(80, 100);
        var end = viewport.Unproject(viewport.Width - 80, 115);
        var data = new MapFeatureDataDocument(1, locationId,
            new MapFeatureSourceMetadata("openstreetmap-overpass", "OpenStreetMap", "© OpenStreetMap contributors",
                "https://www.openstreetmap.org/copyright", "Open Database License (ODbL)",
                "https://opendatacommons.org/licenses/odbl/", "overpass-api-de", 1, DateTimeOffset.UnixEpoch, viewport.Bounds),
            [new MapRoadFeature(11, "way", MapRoadClassification.ARoad,
                [new(start.Latitude, start.Longitude), new(end.Latitude, end.Longitude)], "primary", "A15", null, null, null, null)],
            []);
        return new MapFeatureFetchResult(data, MapFeatureFetchStatus.Complete);
    }

    /// <summary>A frame in OpenStreetMap Standard colours: water to the west, residential land, woodland to the east.</summary>
    private static byte[] OpenStreetMapStandardCapture()
    {
        using var bitmap = new SKBitmap(LocationMapThumbnailService.SourceWidth, LocationMapThumbnailService.SourceHeight);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(242, 239, 233));
        using (var water = new SKPaint { Color = new SKColor(170, 211, 223) }) canvas.DrawRect(0, 0, 280, 504, water);
        using (var houses = new SKPaint { Color = new SKColor(224, 223, 223) }) canvas.DrawRect(320, 120, 560, 380, houses);
        using (var wood = new SKPaint { Color = new SKColor(173, 209, 158) }) canvas.DrawRect(620, 0, 896, 504, wood);
        using var road = new SKPaint { Color = new SKColor(247, 250, 191), StrokeWidth = 6, IsAntialias = true };
        for (var x = 0; x < 896; x += 64) canvas.DrawLine(x, 0, x + 200, 504, road);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
