using Avalonia.Media;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Desktop.Mapping.Preview;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.Themes;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>The themed saved-location preview pipeline: scene building, rendering and export.</summary>
public sealed class LocationPreviewRendererTests
{
    private static readonly Guid LocationId = Guid.Parse("015cb354-786a-45ab-acd4-6f8502813731");
    private static readonly GeoCoordinate Location = new(53.556, -.485);
    private static readonly ThemeCatalogue Catalogue = new(isAvailable: _ => true);

    public static IEnumerable<object[]> Variants => AppearanceTests.Variants;

    /// <summary>A synthetic location: OSM-style land-use raster, a road, a river and a settlement cluster.</summary>
    internal static LocationPreviewScene Scene()
    {
        var viewport = WebMercatorViewport.Create(Location, 13, 256, 896, 504);
        using var source = new SKBitmap(896, 504);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(new SKColor(242, 239, 233));
            using var paint = new SKPaint();
            paint.Color = new SKColor(170, 211, 223); canvas.DrawRect(0, 0, 220, 504, paint);
            paint.Color = new SKColor(173, 209, 158); canvas.DrawRect(620, 300, 276, 204, paint);
            paint.Color = new SKColor(224, 223, 223); canvas.DrawRect(400, 200, 120, 90, paint);
        }
        using var image = SKImage.FromBitmap(source);
        var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        MapFeatureCoordinate At(double x, double y) { var c = viewport.Unproject(x, y); return new(c.Latitude, c.Longitude); }
        var bounds = new MapGeographicBounds(viewport.Bounds.South, viewport.Bounds.West, viewport.Bounds.North, viewport.Bounds.East);
        var features = new MapFeatureDataDocument(1, LocationId,
            new("test", "Test", "© Test", "https://example.invalid", "Test", "https://example.invalid", "test", 1, DateTimeOffset.UnixEpoch, bounds),
            [new(1, "way", MapRoadClassification.ARoad, [At(0, 250), At(448, 252), At(896, 330)], "primary", null, null, null, null, null),
             new(2, "way", MapRoadClassification.BRoad, [At(448, 0), At(460, 504)], "secondary", null, null, null, null, null)],
            [new(3, "way", MapWaterwayClassification.River, [At(300, 0), At(260, 504)], "river", null, null, null, null)]);
        var grid = new GeoRasterRequest(new GeoBounds(bounds.South, bounds.West, bounds.North, bounds.East), 1792, 1008, GeoRasterProjection.WebMercator);
        var fraction = new float[grid.Width * grid.Height];
        var heights = new float[fraction.Length];
        for (var y = 380; y < 620; y++)
        for (var x = 800; x < 1100; x++) { fraction[y * grid.Width + x] = .6f; heights[y * grid.Width + x] = 8; }
        var settlement = new SettlementRaster("wsf", "test", grid, fraction, heights);
        return LocationPreviewSceneBuilder.Build(LocationId, Location, viewport, png, features, settlement);
    }

    private static LocationPreviewStyle Style(string id, ColourVisionMode mode, AppearanceMode appearance) =>
        LocationPreviewExport.StyleFor(Catalogue, id, appearance, mode);

    private static double Luminance(SKColor c) => ColourScience.Luminance(Color.FromRgb(c.Red, c.Green, c.Blue));

    [Fact]
    public void SceneIsBuiltFromTheCachedMapAssetsDeterministically()
    {
        var scene = Scene();
        Assert.Equal(scene.Fingerprint, Scene().Fingerprint);
        Assert.True(scene.HasMapContent);
        // Areas read back from the OSM Standard fills, in their places.
        byte At(byte[] mask, int x, int y) => mask[y / 2 * scene.MaskWidth + x / 2];
        Assert.Equal(255, At(scene.Water, 100, 250));
        Assert.Equal(0, At(scene.Water, 700, 100));
        Assert.Equal(255, At(scene.Vegetation, 760, 420));
        Assert.Equal(255, At(scene.BuiltUp, 460, 245));
        Assert.Contains(scene.Lines, l => l.Class == PreviewLineClass.Major);
        Assert.Contains(scene.Lines, l => l.Class == PreviewLineClass.River);
        Assert.NotEmpty(scene.Lights);
        Assert.All(scene.Lights, l => Assert.InRange(l.X, 350, 600));
        Assert.InRange(scene.Pin.X, 447, 449);
    }

    [Fact]
    public void AtmosphereIsDeterministicAndIgnoresLightAtTheSite()
    {
        var first = Scene().Atmosphere;
        var second = Scene().Atmosphere;
        Assert.Equal(first.NebulaPrimary, second.NebulaPrimary);
        Assert.Equal(first.NebulaSecondary, second.NebulaSecondary);
        Assert.Equal(first.Stars, second.Stars);
        Assert.Equal(LocationPreviewAtmosphere.StarCount, first.Stars.Count);
        // The synthetic settlement surrounds the site: light at the site has no direction.
        Assert.Empty(first.LightSources);
        // Haze gathers where the settlement light is.
        var scene = Scene();
        byte At(byte[] field, int x, int y) => field[y / 2 * scene.MaskWidth + x / 2];
        Assert.True(At(first.NebulaPrimary, 475, 250) > At(first.NebulaPrimary, 100, 60));
        // Another location has another sky.
        var elsewhere = LocationPreviewScene.Empty(Guid.NewGuid()).Atmosphere;
        Assert.NotEqual(first.Stars, elsewhere.Stars);
        Assert.Empty(elsewhere.LightSources);
    }

    [Fact]
    public void RendersAreDeterministicForTheSameSceneAndTheme()
    {
        var scene = Scene();
        var style = Style(ThemeCatalogue.DefaultId, ColourVisionMode.None, AppearanceMode.Dark);
        Assert.Equal(LocationPreviewRenderer.RenderPng(scene, style, 400, 240), LocationPreviewRenderer.RenderPng(Scene(), style, 400, 240));
    }

    [Theory, MemberData(nameof(Variants))]
    public void PreviewFollowsTheThemeAndKeepsCardTextReadable(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var scene = Scene();
        var style = Style(id, mode, appearance);
        var palette = Catalogue.Resolve(new(id, mode, AppearanceMode: appearance)).Palette;
        using var bitmap = LocationPreviewRenderer.Render(scene, style, 400, 240);
        var pixels = Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, y))).ToArray();
        var mean = pixels.Average(Luminance);
        var label = $"{id}/{appearance}/{mode}";
        // Light themes stay airy, dark themes stay nocturnal.
        if (appearance == AppearanceMode.Light) Assert.True(mean > .5, $"{label}: mean luminance {mean:F2}");
        else Assert.True(mean < .12, $"{label}: mean luminance {mean:F2}");
        // Its ground is the theme's own map land, not a fixed palette (scene point 700,100 is open land).
        var corner = bitmap.GetPixel(320, 48);
        Assert.True(ColourScience.DeltaE(Color.FromRgb(corner.Red, corner.Green, corner.Blue), Color.Parse(palette[MapThemeTokens.Land])) < 25,
            $"{label}: edge #{corner.Red:X2}{corner.Green:X2}{corner.Blue:X2} is far from the theme land");

        // Card text crosses at most the first fifth of the preview, under a scrim of at least half the card
        // colour (the card's gradient at its centre line). It must stay readable over the worst pixel there.
        var card = Color.Parse(palette[LocationCardTokens.Surface]);
        foreach (var x in Enumerable.Range(0, bitmap.Width / 5))
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            var p = bitmap.GetPixel(x, y);
            byte Mix(byte under, byte over) => (byte)Math.Round(under * .5 + over * .5);
            var seen = Color.FromRgb(Mix(p.Red, card.R), Mix(p.Green, card.G), Mix(p.Blue, card.B));
            Assert.True(ColourScience.Contrast(Color.Parse(palette["PrimaryText"]), seen) >= 4.5, $"{label}: name over preview at {x},{y}");
            Assert.True(ColourScience.Contrast(Color.Parse(palette["SecondaryText"]), seen) >= 4.5, $"{label}: metadata over preview at {x},{y}");
        }

        var land = Color.Parse(palette[MapThemeTokens.Land]);
        // The instrument (scan ring, ticks, brackets) is legible against the ground it is drawn over.
        Assert.True(ColourScience.Contrast(Color.Parse(palette[LocationCardTokens.PreviewInstrument]), land) >= 3, $"{label}: instrument on land");
    }

    [Fact]
    public void EveryFamilyModeAndColourVisionVariantHasItsOwnPreview()
    {
        var scene = Scene();
        var renditions = LocationPreviewExport.RenderMatrix(scene, Catalogue, 256, 144).ToArray();
        Assert.Equal(48, renditions.Length);
        Assert.Equal(48, renditions.Select(r => r.FileName).Distinct().Count());
        string Hash(LocationPreviewRendition r) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(r.Png));
        // Families and Light/Dark always differ.
        var normal = renditions.Where(r => r.ColourVision == ColourVisionMode.None).ToArray();
        Assert.Equal(normal.Length, normal.Select(Hash).Distinct().Count());
        // A colour-vision variant that re-authors map areas changes the preview with it.
        Assert.NotEqual(Hash(renditions.Single(r => r.ThemeId == ThemeCatalogue.DefaultId && r.Appearance == AppearanceMode.Dark && r.ColourVision == ColourVisionMode.None)),
            Hash(renditions.Single(r => r.ThemeId == ThemeCatalogue.DefaultId && r.Appearance == AppearanceMode.Dark && r.ColourVision == ColourVisionMode.Tritanopia)));
    }

    [Fact]
    public void HighContrastDrawsCrispStructureWithoutAtmosphere()
    {
        var scene = Scene();
        foreach (var appearance in new[] { AppearanceMode.Dark, AppearanceMode.Light })
        {
            var style = Style("builtin.highcontrast", ColourVisionMode.None, appearance);
            Assert.True(style.IsHighContrast);
            using var bitmap = LocationPreviewRenderer.Render(scene, style, 400, 240);
            // No vignette: the far corner is still the pure land colour.
            Assert.Equal(style.Land.WithAlpha(255), bitmap.GetPixel(bitmap.Width - 1, 0));
            // Major roads are cased and opaque in the road colour.
            var palette = Catalogue.Resolve(new("builtin.highcontrast", AppearanceMode: appearance)).Palette;
            Assert.True(ColourScience.Contrast(Color.Parse(palette[MapThemeTokens.RoadMajor]), Color.Parse(palette[MapThemeTokens.Land])) >= 4.5);
        }
    }

    [Fact]
    public void EmptySceneStillRendersAThemedPlaceholder()
    {
        var scene = LocationPreviewScene.Empty(LocationId);
        Assert.False(scene.HasMapContent);
        var style = Style("supporter.foxfire", ColourVisionMode.None, AppearanceMode.Light);
        using var bitmap = LocationPreviewRenderer.Render(scene, style, 200, 120);
        var edge = bitmap.GetPixel(0, 60);
        Assert.True(ColourScience.DeltaE(Color.FromRgb(edge.Red, edge.Green, edge.Blue), Color.FromRgb(style.Land.Red, style.Land.Green, style.Land.Blue)) < 25);
    }

    [Theory, MemberData(nameof(Variants))]
    public void PreviewHasNoCentrePinOnlyTheInstrumentLock(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        // No map content near the site, so anything glyph-like at the centre would be a pin.
        var scene = LocationPreviewScene.Empty(LocationId);
        var style = Style(id, mode, appearance);
        using var bitmap = LocationPreviewRenderer.Render(scene, style, 400, 240);
        Color At(int x, int y) { var p = bitmap.GetPixel(x, y); return Color.FromRgb(p.Red, p.Green, p.Blue); }
        var centre = Enumerable.Range(194, 13).SelectMany(x => Enumerable.Range(112, 16).Select(y => At(x, y))).ToArray();
        var reference = At(200, 120);
        Assert.All(centre, c => Assert.True(ColourScience.DeltaE(c, reference) < 8, $"{id}/{appearance}/{mode}: glyph at the centre"));
        // The lock brackets are still drawn around the site, in the instrument colour.
        var unit = 240 / LocationPreviewRenderer.ReferenceHeight;
        var corner = At((int)Math.Round(200 - 21 * unit + 2), (int)Math.Round(120 - 21 * unit));
        Assert.True(ColourScience.DeltaE(corner, reference) > 8, $"{id}/{appearance}/{mode}: lock bracket missing");
    }

    [Fact]
    public void MatrixExportWritesOneFilePerVariant()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = LocationPreviewExport.WriteMatrix(Scene(), Catalogue, directory, 128, 72);
            Assert.Equal(48, paths.Count);
            Assert.All(paths, path => Assert.True(new FileInfo(path).Length > 0));
            Assert.Contains(paths, p => Path.GetFileName(p) == "noctaxis-dark-none.png");
            Assert.Contains(paths, p => Path.GetFileName(p) == "highcontrast-light-tritanopia.png");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SceneCacheRoundTripsAndIsIgnoredWhenItsInputsChangeOrItIsDamaged()
    {
        var scene = Scene();
        var bytes = LocationPreviewSceneCodec.Encode(scene, "inputs-a");
        var decoded = LocationPreviewSceneCodec.TryDecode(bytes, "inputs-a");
        Assert.NotNull(decoded);
        Assert.Equal(scene.Fingerprint, decoded.Fingerprint);
        var style = Style("supporter.noctaxis-neon", ColourVisionMode.None, AppearanceMode.Dark);
        // A cached scene renders exactly as the freshly built one.
        Assert.Equal(LocationPreviewRenderer.RenderPng(scene, style, 320, 180), LocationPreviewRenderer.RenderPng(decoded, style, 320, 180));
        Assert.Null(LocationPreviewSceneCodec.TryDecode(bytes, "inputs-b"));
        Assert.Null(LocationPreviewSceneCodec.TryDecode(bytes[..(bytes.Length / 2)], "inputs-a"));
        Assert.Null(LocationPreviewSceneCodec.TryDecode([1, 2, 3], "inputs-a"));
    }

    [Fact]
    public async Task LoaderBuildsFromCachedAssetsThenReusesItsSceneUntilAnInputChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-scene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var viewport = WebMercatorViewport.Create(Location, 13, 256, 896, 504);
            using (var source = new SKBitmap(896, 504))
            {
                using (var canvas = new SKCanvas(source)) { canvas.Clear(new SKColor(242, 239, 233)); using var water = new SKPaint { Color = new SKColor(170, 211, 223) }; canvas.DrawRect(0, 0, 300, 504, water); }
                using var image = SKImage.FromBitmap(source);
                await File.WriteAllBytesAsync(Path.Combine(directory, "source.png"), image.Encode(SKEncodedImageFormat.Png, 100).ToArray());
            }
            var metadata = new SavedLocationThumbnailMetadata(1, LocationId, Location.Latitude, Location.Longitude, 13, 512, 280,
                "osm", "OSM", "standard", "test", "© test", null, null, null, DateTimeOffset.UnixEpoch, 13,
                viewport.CentreLatitude, viewport.CentreLongitude, 896, 504, 1, "hash", "render", "thumbnail.png");
            var first = await LocationPreviewSceneLoader.LoadAsync(directory, metadata, Location, CancellationToken.None);
            byte At(byte[] mask, int x, int y) => mask[y / 2 * first.MaskWidth + x / 2];
            Assert.Equal(255, At(first.Water, 100, 250));
            var cache = Path.Combine(directory, LocationPreviewSceneCodec.FileName);
            Assert.True(File.Exists(cache));

            // A second load reuses the cache even if the source has gone...
            File.Delete(Path.Combine(directory, "source.png"));
            var second = await LocationPreviewSceneLoader.LoadAsync(directory, metadata, Location, CancellationToken.None);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            // ...but a changed input (here a new source hash) rebuilds from what is on disk now.
            var third = await LocationPreviewSceneLoader.LoadAsync(directory, metadata with { SourceContentHash = "changed" }, Location, CancellationToken.None);
            Assert.NotEqual(first.Fingerprint, third.Fingerprint);
            Assert.All(third.Water, value => Assert.Equal(0, value));
        }
        finally { Directory.Delete(directory, true); }
    }
}
