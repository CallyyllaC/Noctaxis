using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BruTile;
using Noctaxis.Core.Domain;
using Noctaxis.Core.LightPollution;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class LightPollutionMapTests
{
    [Fact]
    public async Task NearestParityOrientationBoundariesWrappingAndBilinearNeighbours()
    {
        await using var paths = new AtlasTestData();
        using var http = Offline(); var store = new LorenzInstallation(paths, http);
        var directory = await paths.SeedAsync(store);
        await File.WriteAllBytesAsync(Path.Combine(directory, "binary", LorenzAtlas.FileName(1, 1)), AtlasTestData.Gzip(AtlasTestData.Encode((_, _) => 10)));
        await File.WriteAllBytesAsync(Path.Combine(directory, "binary", LorenzAtlas.FileName(72, 1)), AtlasTestData.Gzip(AtlasTestData.Encode((_, _) => 30)));
        for (var ty = 13; ty <= 14; ty++) for (var tx = 36; tx <= 37; tx++)
        {
            var bytes = AtlasTestData.Gzip(AtlasTestData.Encode((x, y) => 1000 + (tx - 36) * 600 + x + (ty - 13) * 600 + y));
            await File.WriteAllBytesAsync(Path.Combine(directory, "binary", LorenzAtlas.FileName(tx, ty)), bytes);
        }
        await using var provider = new LightPollutionDataProvider(store);
        foreach (var point in new[] { (-4.995833333333, -4.995833333333), (-2.5, -2.5), (-5d, -5d),
                     (-5d, -.00001), (-.00001, -5d), (-.00001, -.00001), (0d, 0d), (0d, -1d), (-1d, 0d) })
        {
            var globalX = (int)Math.Floor((point.Item2 + 5) * 120);
            var globalY = (int)Math.Floor((point.Item1 + 5) * 120);
            Assert.Equal(LorenzAtlas.ToLpi(1000 + globalX + globalY), (await provider.SampleAsync(point.Item1, point.Item2))!.Value, 5);
        }
        // At the source tile junction interpolation spans four distinct files/cells.
        var expected = (LorenzAtlas.ToLpi(2198) + 2 * LorenzAtlas.ToLpi(2199) + LorenzAtlas.ToLpi(2200)) / 4;
        Assert.InRange(Math.Abs((await provider.SampleAsync(0, 0, true))!.Value - expected) / expected, 0, 1e-12);
        foreach (var lon in new[] { -180d, 180d, 540d, -540d })
            Assert.Equal(await provider.SampleAsync(-65, -180), await provider.SampleAsync(-65, lon));
        Assert.Equal((LorenzAtlas.ToLpi(10) + LorenzAtlas.ToLpi(30)) / 2,
            (await provider.SampleAsync(-64, 180, true))!.Value, 12);
        Assert.NotNull(await provider.SampleAsync(-65, 40));
        Assert.NotNull(await provider.SampleAsync(74.999999, 40));
        Assert.Null(await provider.SampleAsync(75, 40));
        Assert.Null(await provider.SampleAsync(-65.000001, 40));
        Assert.Null(await provider.SampleAsync(double.NaN, 40));
        // Read more than the numerical capacity, and confirm bounded retention.
        for (var x = 1; x <= 40; x++) await provider.SampleAsync(-60, -180 + (x - 1) * 5);
        Assert.Equal(24, provider.DecodedTileCount);
    }

    [Fact]
    public async Task RasterNoDataIsTransparentCacheIsPaletteKeyedAndRemovalClearsCaches()
    {
        await using var paths = new AtlasTestData();
        using var http = Offline(); var store = new LorenzInstallation(paths, http);
        await paths.SeedAsync(store);
        await using var provider = new LightPollutionDataProvider(store);
        using var raster = new LightPollutionRasterSource(provider, store, new GrayscaleLightPollutionColorMap());
        var info = new TileInfo { Index = new TileIndex(0, 0, 0) };
        var png = await raster.GetTileAsync(info);
        Assert.NotNull(png); Assert.Same(png, await raster.GetTileAsync(info));
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal((byte)0, bitmap.GetPixel(128, 0).Alpha);
        Assert.Equal((byte)255, bitmap.GetPixel(128, 128).Alpha);
        var color = bitmap.GetPixel(128, 128); Assert.Equal(color.Red, color.Green); Assert.Equal(color.Green, color.Blue);
        Assert.Equal(1, raster.CachedTileCount); Assert.True(provider.HasOverview);
        Assert.Equal(8, raster.Schema.Resolutions.Keys.Max());
        await store.RemoveAsync();
        Assert.Equal(0, raster.CachedTileCount); Assert.False(provider.HasOverview);
        Assert.Null(await raster.GetTileAsync(info));
    }

    [AvaloniaFact]
    public async Task RegisteredLayerTransitionsInPlaceAndPreferencesAndAttributionFollowRuntime()
    {
        await using var paths = new AtlasTestData();
        using var http = Offline(); var store = new LorenzInstallation(paths, http);
        var view = new PlannerMapView();
        view.ConfigureLightPollution(store);
        var map = view.CompositionForTesting;
        map.Map.Layers.First().Enabled = false;
        var composition = map.Composition;
        var layer = map.Map.Layers.Last();
        Assert.Equal(2, map.Map.Layers.Count);
        Assert.Equal(MapLayerGroup.Environmental, composition.Layers.Single(x => x.Id == "light-pollution").Group);
        Assert.False(layer.Enabled);
        var window = new Window { Content = view, Width = 600, Height = 400 };
        try
        {
            window.Show();
            await paths.SeedAsync(store); Dispatcher.UIThread.RunJobs();
            Assert.Equal(MapLayerAvailability.Available, map.Runtime["light-pollution"].Availability);
            Assert.False(layer.Enabled); // Explicit visibility remains off until requested.
            view.LightPollutionPreferences = new(true, .35);
            Assert.True(layer.Enabled); Assert.Equal(.35, layer.Opacity);
            Assert.Contains(LorenzAtlas.Name, view.AttributionForTesting!);
            var preferences = MapLayerPreferences.Capture(map.Runtime);
            var stored = JsonSerializer.Deserialize<MapLayerPreference[]>(JsonSerializer.Serialize(preferences))!;
            map.Runtime.SetVisibility("light-pollution", false);
            Assert.DoesNotContain(LorenzAtlas.Name, view.AttributionForTesting!);
            MapLayerPreferences.Restore(map.Runtime, stored); Assert.True(layer.Enabled);
            for (var i = 0; i < 3; i++)
            {
                window.Content = null; window.Content = view;
                map.Map.Navigator.CenterOnAndZoomTo(new(i * 100, 0), 100, 0);
                Assert.Same(composition, map.Composition); Assert.Same(layer, map.Map.Layers.Last());
                Assert.Equal(2, map.Map.Layers.Count);
            }
            await store.RemoveAsync(); Dispatcher.UIThread.RunJobs();
            Assert.False(layer.Enabled); Assert.Same(composition, map.Composition);
            Assert.DoesNotContain(LorenzAtlas.Name, view.AttributionForTesting!);
        }
        finally { window.Close(); await view.ReleaseLightPollutionAsync(); }
    }
    private static HttpClient Offline() => new(new LightPollutionInstallationTests.Handler((_, _) => throw new Exception("Raster/sample/startup must not access network.")));
}
