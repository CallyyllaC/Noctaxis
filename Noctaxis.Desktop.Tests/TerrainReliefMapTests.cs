using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BruTile;
using BruTile.Predefined;
using Mapsui;
using Mapsui.Layers;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.ViewModels;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainReliefMapTests
{
    private const double WorldHalf = 20037508.342789244;

    // ---- Composition ----------------------------------------------------------------------------

    [Fact]
    public void GroupsRunFromGeographyThroughEnvironmentAndReferenceToPlanningAndInteraction()
    {
        var composition = LightPollutionMapBinding.CreateComposition().Composition;
        var ids = composition.Layers.Select(x => x.Id).ToArray();
        Assert.Equal(new[]
        {
            "basemap", TerrainReliefMapBinding.LayerId, LightPollutionMapBinding.LayerId,
            PlannerMapComposition.BasemapReferenceId, "environmental-shading", "camera-framing", "celestial-rays", "observer-pin"
        }, ids);
        var groups = composition.Layers.Select(x => x.Group).ToArray();
        Assert.Equal(groups.Order(), groups);
        Assert.Equal(new[] { MapLayerGroup.Basemap, MapLayerGroup.Environmental, MapLayerGroup.Reference,
            MapLayerGroup.Planning, MapLayerGroup.Interaction }, groups.Distinct());
        Assert.All(new[] { TerrainReliefMapBinding.LayerId, LightPollutionMapBinding.LayerId },
            id => Assert.Equal(MapLayerGroup.Environmental, composition.Layers.Single(x => x.Id == id).Group));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeLayersFollowTheSemanticOrderWhateverOrderTheyAreInstalledIn(bool lightPollutionFirst)
    {
        var map = new PlannerMapComposition(new SplitBasemap(),
            [LightPollutionMapBinding.Definition, TerrainReliefMapBinding.Definition]);
        var terrain = new MemoryLayer { Name = "terrain" };
        var lightPollution = new MemoryLayer { Name = "light-pollution" };
        if (lightPollutionFirst) map.InstallNativeOverlay(LightPollutionMapBinding.LayerId, [lightPollution]);
        map.InstallNativeOverlay(TerrainReliefMapBinding.LayerId, [terrain]);
        if (!lightPollutionFirst) map.InstallNativeOverlay(LightPollutionMapBinding.LayerId, [lightPollution]);

        Assert.Equal(new[] { "geography", "terrain", "light-pollution", "reference" }, map.Map.Layers.Select(x => x.Name));
        Assert.Equal(map.Map.Layers, map.NativeLayerOrder.Select(x => x.Layer));
        map.Map.Dispose();
    }

    [Fact]
    public void ReplacingTheBasemapPutsEachOfItsPassesBackInItsOwnPlace()
    {
        var map = new PlannerMapComposition(new SplitBasemap(), [TerrainReliefMapBinding.Definition]);
        map.InstallNativeOverlay(TerrainReliefMapBinding.LayerId, [new MemoryLayer { Name = "terrain" }]);
        map.ReplaceBasemap(new SplitBasemap("new-"));
        Assert.Equal(new[] { "new-geography", "terrain", "new-reference" }, map.Map.Layers.Select(x => x.Name));
        // A basemap that cannot separate its reference simply has nothing in the reference slot.
        map.ReplaceBasemap(new WholeBasemap());
        Assert.Equal(new[] { "whole", "terrain" }, map.Map.Layers.Select(x => x.Name));
        map.ReplaceBasemap(new SplitBasemap("again-"));
        Assert.Equal(new[] { "again-geography", "terrain", "again-reference" }, map.Map.Layers.Select(x => x.Name));
        map.Map.Dispose();
    }

    [AvaloniaFact]
    public void TerrainReliefIsNotDrawnOverSatelliteImageryAndReturnsWithTheStreetBasemap()
    {
        var street = new SplitBasemap();
        var map = new PlannerMapComposition(street, [TerrainReliefMapBinding.Definition, LightPollutionMapBinding.Definition]);
        var binding = new TerrainReliefMapBinding(map, new FakeElevation(), () => 1);
        var layers = new PlannerLayersViewModel(map.Runtime);
        Assert.True(binding.Layer.Enabled);
        Assert.Contains(TerrainReliefMapBinding.AttributionText, map.Runtime.AttributionText);

        map.ReplaceBasemap(new RasterImageryBasemap(new(BasemapKind.Satellite, new("Imagery", "Imagery credit")), () => new BlankImagery()));
        Assert.False(map.Runtime[TerrainReliefMapBinding.LayerId].IsActive);
        Assert.False(binding.Layer.Enabled);
        Assert.True(layers.ShowTerrainRelief); // The user's choice is kept, only not drawn.
        Assert.True(layers.IsTerrainReliefOverImagery);
        Assert.DoesNotContain(TerrainReliefMapBinding.AttributionText, map.Runtime.AttributionText);
        // Imagery keeps its position at the bottom; nothing else moved.
        Assert.Equal(new[] { "basemap", TerrainReliefMapBinding.LayerId }, map.NativeLayerOrder.Select(x => x.Id));

        map.ReplaceBasemap(street);
        Assert.True(map.Runtime[TerrainReliefMapBinding.LayerId].IsRenderable);
        Assert.True(binding.Layer.Enabled);
        Assert.False(layers.IsTerrainReliefOverImagery);
        map.Map.Dispose();
    }

    // ---- Source and caching ---------------------------------------------------------------------

    [Fact]
    public async Task ReliefReadsOnlyTheSharedTerrariumSourceAndReusesItsShading()
    {
        var elevation = new FakeElevation { Tiles = EastwardSlope };
        var source = new TerrainReliefTileSource(elevation);

        var png = await source.RenderAsync(Info(10, 500, 340), null);
        using var image = SKBitmap.Decode(png);
        Assert.Equal(256, image.Width);
        // The tile and its eight neighbours, each requested once, and nothing else.
        Assert.Equal(9, elevation.Requests.Count);
        Assert.Equal(9, elevation.Requests.Distinct().Count());
        Assert.All(elevation.Requests, key => Assert.Equal(10, key.Zoom));

        await source.RenderAsync(Info(10, 500, 340), null);
        Assert.Equal(1, source.Metrics.ReliefComputations);
        Assert.Equal(1, source.Metrics.ReliefCacheHits);
        Assert.Equal(9, elevation.Requests.Count);

        // Clearing stored terrain retires everything shaded from it.
        elevation.Generation++;
        await source.RenderAsync(Info(10, 500, 340), null);
        Assert.Equal(2, source.Metrics.ReliefComputations);
    }

    [Fact]
    public async Task ZoomsDeeperThanTheCalculationsDetailEnlargeTheirRelief()
    {
        // The map shows the terrain the calculations sample (zoom 12), never Terrarium's deeper,
        // often merely enlarged, tiles.
        var elevation = new FakeElevation { Tiles = EastwardSlope };
        var source = new TerrainReliefTileSource(elevation);
        var png = await source.RenderAsync(Info(17, 32 * 2000 + 3, 32 * 1300 + 1), null);
        using var image = SKBitmap.Decode(png);
        Assert.Equal(256, image.Width);
        Assert.True(image.GetPixel(128, 128).Alpha > 0);
        Assert.Contains(new TerrariumTileKey(12, 2000, 1300), elevation.Requests);
        Assert.All(elevation.Requests, key => Assert.Equal(12, key.Zoom));
    }

    [Fact]
    public async Task MissingCorruptOrFailingElevationLeavesNoReliefAndNeverThrows()
    {
        var elevation = new FakeElevation();
        var source = new TerrainReliefTileSource(elevation);
        Assert.Null(await source.RenderAsync(Info(10, 500, 340), null));
        Assert.Equal(1, source.Metrics.UnavailableTiles);

        elevation.Failure = new IOException("disk");
        Assert.Null(await source.RenderAsync(Info(10, 500, 340), null));
        elevation.Failure = null;

        // Unavailability is not remembered: the tile appears once its elevation does.
        elevation.Tiles = key => key == new TerrariumTileKey(10, 500, 340) ? EastwardSlope(key) : null;
        var png = await source.RenderAsync(Info(10, 500, 340), null);
        using var image = SKBitmap.Decode(png);
        // Every neighbour is missing, so the edges continue the tile's own slope: no rim, no seam.
        var centre = image.GetPixel(128, 128);
        Assert.True(centre.Alpha > 0);
        foreach (var (x, y) in new[] { (0, 0), (255, 0), (0, 255), (255, 255), (0, 128), (255, 128), (128, 0), (128, 255) })
            Assert.InRange(image.GetPixel(x, y).Alpha, centre.Alpha - 2, centre.Alpha + 2);
    }

    [Fact]
    public async Task TilesTheMapHasLeftAreAbandonedBeforeTheyAreShaded()
    {
        var elevation = new FakeElevation { Tiles = EastwardSlope };
        var source = new TerrainReliefTileSource(elevation);
        Assert.Null(await source.RenderAsync(Info(10, 500, 340), () => false));
        Assert.Equal(new[] { new TerrariumTileKey(10, 500, 340) }, elevation.Requests); // no neighbours
        Assert.Equal(0, source.Metrics.ReliefComputations);
        Assert.Equal(1, source.Metrics.AbandonedTiles);

        var layer = new TerrainReliefTileLayer(source);
        var view = Info(10, 500, 340).Extent;
        layer.ViewportChanged(new FetchInfo(new MSection(new MRect(view.MinX, view.MinY, view.MaxX, view.MaxY), Resolution(10))));
        Assert.True(layer.IsWanted(Info(10, 500, 340)));
        Assert.True(layer.IsWanted(Info(10, 501, 341)));   // the one-tile margin
        Assert.True(layer.IsWanted(Info(9, 250, 170)));    // coarser, used while zooming
        Assert.False(layer.IsWanted(Info(10, 520, 340)));  // panned away
        Assert.False(layer.IsWanted(Info(11, 1000, 680))); // left behind by zooming out
        layer.Dispose();
    }

    // ---- Appearance -----------------------------------------------------------------------------

    [Fact]
    public void ReliefIsNeutralLightAndShadeAndFlatGroundIsFullyTransparent()
    {
        using (var flat = SKBitmap.Decode(TerrainReliefTileSource.Encode(new float[256 * 256], 1, 0, 0)))
            Assert.All(flat.Pixels, pixel => Assert.Equal(0, pixel.Alpha));
        using (var lit = SKBitmap.Decode(TerrainReliefTileSource.Encode(Filled(.2f), 1, 0, 0)))
        {
            var pixel = lit.GetPixel(10, 10);
            Assert.True(pixel.Alpha > 0);
            Assert.Equal((255, 255, 255), (pixel.Red, pixel.Green, pixel.Blue));
        }
        using (var shaded = SKBitmap.Decode(TerrainReliefTileSource.Encode(Filled(-1), 1, 0, 0)))
        {
            var pixel = shaded.GetPixel(10, 10);
            Assert.Equal((0, 0, 0), (pixel.Red, pixel.Green, pixel.Blue));
            // Even the steepest shade stays translucent, so the land beneath still reads.
            Assert.InRange(pixel.Alpha, 1, (int)Math.Round(255 * TerrainReliefTileSource.MaximumAlpha));
        }
    }

    [AvaloniaFact]
    public void TerrainReliefFollowsPreferencesTheThemeAndAttribution()
    {
        var themes = ((App)Application.Current!).Themes!;
        var view = new PlannerMapView();
        foreach (var layer in view.CompositionForTesting.Map.Layers) layer.Enabled = false; // Offline basemap.
        view.ConfigureTerrainRelief(new FakeElevation { Tiles = EastwardSlope });
        var runtime = view.CompositionForTesting.Runtime;
        var native = view.TerrainReliefForTesting!.Layer;
        var window = new Window { Content = view, Width = 500, Height = 400 };
        try
        {
            window.Show();
            themes.Apply(new());
            Dispatcher.UIThread.RunJobs();
            // Shown by default, restrained.
            Assert.True(runtime[TerrainReliefMapBinding.LayerId].IsRenderable);
            Assert.Equal(TerrainReliefPreferences.DefaultOpacity, native.Opacity, 6);
            Assert.Contains(TerrainReliefMapBinding.AttributionText, view.AttributionForTesting);

            view.TerrainReliefPreferences = new(true, .4);
            Assert.Equal(.4, native.Opacity, 6);
            // High Contrast halves the relief without changing the user's own setting.
            themes.Apply(new("builtin.highcontrast"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(.5, view.TerrainReliefStrengthForTesting);
            Assert.Equal(.2, native.Opacity, 6);
            Assert.Equal(.4, runtime[TerrainReliefMapBinding.LayerId].Opacity);
            themes.Apply(new());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(.4, native.Opacity, 6);

            view.TerrainReliefPreferences = new(false, .4);
            Assert.False(native.Enabled);
            Assert.DoesNotContain(TerrainReliefMapBinding.AttributionText, view.AttributionForTesting);
        }
        finally { window.Close(); themes.Apply(new()); }
    }

    [AvaloniaFact]
    public void LayersPanelTurnsTerrainReliefOnAndOffAndSetsItsOpacity()
    {
        var view = new PlannerMapView();
        var controls = new PlannerLayersControl { DataContext = view.Layers };
        var window = new Window { Content = controls, Width = 600, Height = 600 };
        var edits = 0;
        view.Layers.PreferencesEdited += (_, _) => edits++;
        try
        {
            window.Show();
            var button = controls.FindControl<Button>("LayersButton")!;
            button.Flyout!.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var toggle = controls.FindControl<CheckBox>("TerrainReliefToggle")!;
            var slider = controls.FindControl<Slider>("TerrainReliefOpacitySlider")!;
            var opacity = controls.FindControl<StackPanel>("TerrainReliefOpacityControls")!;
            Assert.True(toggle.IsChecked);
            Assert.Equal(TerrainReliefPreferences.DefaultOpacity * 100, slider.Value, 6);
            slider.Value = 25;
            Assert.Equal(.25, view.CompositionForTesting.Runtime[TerrainReliefMapBinding.LayerId].Opacity, 6);
            toggle.IsChecked = false;
            Assert.False(view.Layers.ShowTerrainRelief);
            Assert.False(opacity.IsVisible);
            Assert.Equal(2, edits);
            Assert.Equal("Terrain relief opacity", Avalonia.Automation.AutomationProperties.GetName(slider));
        }
        finally { window.Close(); }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static float[] Filled(float value) => Enumerable.Repeat(value, 256 * 256).ToArray();

    // Ground rising steadily eastwards everywhere: lit from the north-west sun.
    private static TerrariumTile? EastwardSlope(TerrariumTileKey key)
    {
        var values = new float[256 * 256];
        for (var row = 0; row < 256; row++)
            for (var column = 0; column < 256; column++)
                values[row * 256 + column] = (float)(((key.X % 64) * 256 + column) * 4.0);
        return TerrariumTile.FromElevations(values);
    }

    private static double Resolution(int level) => 2 * WorldHalf / 256 / Math.Pow(2, level);

    private static TileInfo Info(int level, int column, int row)
    {
        var size = 2 * WorldHalf / Math.Pow(2, level);
        var minX = -WorldHalf + column * size;
        var maxY = WorldHalf - row * size;
        return new TileInfo { Index = new TileIndex(column, row, level), Extent = new Extent(minX, maxY - size, minX + size, maxY) };
    }

    private sealed class FakeElevation : ITerrariumTileSource
    {
        public long Generation { get; set; }
        public Func<TerrariumTileKey, TerrariumTile?> Tiles { get; set; } = _ => null;
        public Exception? Failure { get; set; }
        public ConcurrentQueue<TerrariumTileKey> Requests { get; } = new();

        public Task<TerrariumTileResult> GetTerrainTileAsync(TerrariumTileKey key, CancellationToken cancellationToken)
        {
            Requests.Enqueue(key);
            if (Failure is { } failure) throw failure;
            var tile = Tiles(key);
            return Task.FromResult(new TerrariumTileResult(tile, tile is null ? TerrainSampleStatus.Unavailable : TerrainSampleStatus.Valid));
        }
    }

    private sealed class SplitBasemap(string prefix = "") : IMapsuiBasemap
    {
        public BasemapDefinition Definition { get; } = new(BasemapKind.Street, new("Split street", "Street credit"));
        public IReadOnlyList<ILayer> CreateLayers() => [new MemoryLayer { Name = prefix + "whole" }];
        public BasemapLayers CreateLayerSet() =>
            new([new MemoryLayer { Name = prefix + "geography" }], [new MemoryLayer { Name = prefix + "reference" }]);
    }

    private sealed class WholeBasemap : IMapsuiBasemap
    {
        public BasemapDefinition Definition { get; } = new(BasemapKind.Street, new("Whole street", "Street credit"));
        public IReadOnlyList<ILayer> CreateLayers() => [new MemoryLayer { Name = "whole" }];
    }

    private sealed class BlankImagery : ILocalTileSource
    {
        public ITileSchema Schema { get; } = new GlobalSphericalMercator();
        public string Name => "Blank imagery";
        public Attribution Attribution { get; } = new("Imagery credit");
        public Task<byte[]?> GetTileAsync(TileInfo tileInfo) => Task.FromResult<byte[]?>(null);
    }
}
