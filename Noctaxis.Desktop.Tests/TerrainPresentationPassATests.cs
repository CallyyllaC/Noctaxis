using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Extensions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Terrain;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed partial class EnvironmentalOverlayTests
{
    [AvaloniaFact]
    public void PlannerMinimapContainerAllowsWheelTrackpadAndPanInputThroughToMapsui()
    {
        var window = new Noctaxis.Desktop.Views.MainWindow();
        var view = window.FindControl<NoctaxisMapView>("PlannerMap")!;
        view.MapControlForTesting.Map!.Layers.Clear();
        window.GetLogicalDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var navigator = view.MapControlForTesting.Map.Navigator;
            navigator.OverrideResolutions = new[] { 1000d, 500, 100, 50, 10 };
            // Exercise native input routing without wall-clock animation timing. The separate
            // worker-thread test covers the viewport notifications emitted by animations.
            navigator.MouseWheelAnimation.Duration = 0;
            navigator.ZoomTo(100, 0);
            var container = window.FindControl<Border>("PlannerTerrainMinimap")!;
            var mini = container.GetVisualDescendants().OfType<LocalTerrainMap>().Single();
            mini.Map = DebugMap(view.Observer);
            mini.LoadState = TerrainDebugMapLoadState.Ready;
            using var rendered = new RenderTargetBitmap(new PixelSize(208, 208), new Vector(96, 96));
            rendered.Render(mini);
            var bitmap = mini.DisplayBitmap;
            var snapshot = mini.Map;
            using var frame = window.CaptureRenderedFrame();
            var point = container.TranslatePoint(new Point(40, 60), window)!.Value;
            Assert.False(container.IsHitTestVisible);
            var beforeRadius = mini.DisplayedRadiusMetres;
            window.MouseWheel(point, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(50, navigator.Viewport.Resolution);
            Assert.True(mini.DisplayedRadiusMetres < beforeRadius);
            // Four fractional trackpad deltas must reach Mapsui's existing accumulation path.
            for (var i = 0; i < 4; i++) window.MouseWheel(point, new Vector(0, -.25));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(100, navigator.Viewport.Resolution);
            Assert.Equal(beforeRadius, mini.DisplayedRadiusMetres);
            var beforePan = navigator.Viewport;
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(35, 20), Avalonia.Input.RawInputModifiers.LeftMouseButton);
            window.MouseUp(point + new Vector(35, 20), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(beforePan.CenterX, navigator.Viewport.CenterX);
            Assert.Equal(100, navigator.Viewport.Resolution);
            rendered.Render(mini);
            Assert.Same(snapshot, mini.Map);
            Assert.Same(bitmap, mini.DisplayBitmap);
            Assert.Equal(1, mini.RasterBuildCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ViewportObserverAcceptsRenderThreadUpdatesWithoutInterruptingNavigation()
    {
        var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        view.MapControlForTesting.Map!.Layers.Clear();
        var mini = new LocalTerrainMap { Width = 208, Height = 208, MainViewportHeightPixels = 208, Map = DebugMap(view.Observer),
            LoadState = TerrainDebugMapLoadState.Ready, IsHitTestVisible = false };
        using var binding = mini.Bind(LocalTerrainMap.MetresPerPixelProperty,
            new Binding(nameof(view.GroundMetresPerPixel)) { Source = view });
        var window = new Window { Width = 800, Height = 600,
            Content = new Grid { Children = { view, mini } } };
        try
        {
            window.Show();
            var navigator = view.MapControlForTesting.Map.Navigator;
            using var rendered = new RenderTargetBitmap(new PixelSize(208, 208), new Vector(96, 96));
            rendered.Render(mini);
            var snapshot = mini.Map;
            var bitmap = mini.DisplayBitmap;
            foreach (var resolution in new[] { 100d, 20, 2000, 50 })
            {
                // Mapsui also raises ViewportChanged from its render/animation thread.
                await Task.Run(() => navigator.CenterOnAndZoomTo(new MPoint(1200, 3400), resolution, 0));
                Dispatcher.UIThread.RunJobs();
                rendered.Render(mini);
                Assert.Equal(resolution, navigator.Viewport.Resolution);
                Assert.Equal(1200, navigator.Viewport.CenterX);
                Assert.Equal(3400, navigator.Viewport.CenterY);
                Assert.Equal(resolution * Math.Cos(53 * Angles.DegreesToRadians), view.GroundMetresPerPixel, 8);
                Assert.InRange(mini.DisplayedRadiusMetres, 1_000, 20_000);
                Assert.Same(snapshot, mini.Map);
                Assert.Same(bitmap, mini.DisplayBitmap);
            }
            Assert.Equal(1, mini.RasterBuildCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FoVEdgesRemainDistinctAboveObstructionTexture()
    {
        var guide = new CameraFramingGuide(105, 70, 70, 140, CameraFramingDirectionSource.PrimaryTarget);
        var sector = new GeoSector(Observer, 105, 70, 500_000);
        var view = new NoctaxisMapView
        {
            Observer = Observer, Snapshot = Snapshot(Observer), FramingGuide = guide,
            FramingVisibility = Visibility(sector, null, (0, 100), (70, 100)),
            FramingSettings = new CameraFramingSettings(ShadingOpacityPercent: 10, LineThickness: 1.25),
            ShowCameraOverlay = true
        };
        view.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = 512, Height = 512, Content = view };
        try
        {
            window.Show();
            var centre = WebMercator.FromWgs84(Observer);
            view.MapControlForTesting.Map.Navigator.CenterOnAndZoomTo(new MPoint(centre.X, centre.Y), 10, 0);
            Dispatcher.UIThread.RunJobs();
            using var rendered = new RenderTargetBitmap(new PixelSize(512, 512), new Vector(96, 96));
            rendered.Render(view.OverlayForTesting);
            if (System.Environment.GetEnvironmentVariable("NOCTAXIS_PASS_A_CAPTURE") is { Length: > 0 } capture)
            {
                Directory.CreateDirectory(capture);
                rendered.Save(Path.Combine(capture, "fov-boundary.png"), new PngBitmapEncoderOptions());
            }
            using var bitmap = new WriteableBitmap(new PixelSize(512, 512), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var buffer = bitmap.Lock();
            foreach (var bearing in new[] { 70d, 140 })
            {
                rendered.CopyPixels(buffer);
                var world = WebMercator.FromWgs84(Angles.Destination(Observer, bearing, 1000));
                var edge = view.MapControlForTesting.Map.Navigator.Viewport.WorldToScreen(world.X, world.Y);
                var strongestColour = 0;
                for (var y = (int)edge.Y - 3; y <= (int)edge.Y + 3; y++)
                for (var x = (int)edge.X - 3; x <= (int)edge.X + 3; x++)
                {
                    var offset = y * buffer.RowBytes + x * 4;
                    var blue = Marshal.ReadByte(buffer.Address, offset);
                    var green = Marshal.ReadByte(buffer.Address, offset + 1);
                    var red = Marshal.ReadByte(buffer.Address, offset + 2);
                    strongestColour = Math.Max(strongestColour,
                        Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)));
                }
                Assert.True(strongestColour >= 35, $"FoV edge at {bearing}° / {edge} lost colour contrast: {strongestColour}.");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeatherTreatmentRetainsStrongColourContrastThroughTerrain(bool obstructed)
    {
        var inside = PresentationPixels(obstructed, false, 2);
        var outside = PresentationPixels(obstructed, true, 2);
        for (var pixel = 0; pixel < 64 * 64; pixel++)
        {
            var i = pixel * 4;
            // Weather still removes the base hue; terrain contributes its own independent hue.
            Assert.True(inside[i + 2] - inside[i + 1] >= 18);
            Assert.InRange(outside[i + 2] - outside[i + 1], obstructed ? 15 : 0, obstructed ? 20 : 1);
            Assert.InRange(outside[i] - outside[i + 1], obstructed ? 17 : 0, obstructed ? 22 : 1);
            Assert.InRange(inside[i + 3], 1, 179);
            Assert.Equal(inside[3], inside[i + 3]); // Continuous tint, no periodic pattern.
        }
    }

    [AvaloniaFact]
    public void ContinuousTintIsStableAcrossMapResolutions()
    {
        var close = PresentationPixels(true, false, 2);
        var wide = PresentationPixels(true, false, 12);
        for (var i = 3; i < close.Length; i += 4)
            Assert.Equal(close[i], wide[i]);
    }

    private static byte[] PresentationPixels(bool obstructed, bool beyond, double resolution)
    {
        var sector = new GeoSector(Observer, 90, 40, 500_000);
        double? hit = obstructed ? 100 : null;
        var state = new EnvironmentalOverlayStateCoordinator(profileTextureWidth: 32)
            .Update(Observer, sector, Visibility(sector, beyond ? 1000 : 50_000, (0, hit), (40, hit)), ProfileKey());
        var sample = WebMercator.FromWgs84(Angles.Destination(Observer, 90, 10_000));
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(sample.X, sample.Y, resolution, 0, 64, 64),
            64, 64, EnvironmentalRenderParameters.Default);
        using var host = new EnvironmentalOverlayTestControl(state, frame, Colors.DeepPink) { Width = 64, Height = 64 };
        host.Measure(new Size(64, 64));
        host.Arrange(new Rect(0, 0, 64, 64));
        using var rendered = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        rendered.Render(host);
        using var bitmap = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        rendered.CopyPixels(buffer);
        var pixels = new byte[64 * 64 * 4];
        for (var row = 0; row < 64; row++) Marshal.Copy(buffer.Address + row * buffer.RowBytes, pixels, row * 256, 256);
        return pixels;
    }

    [AvaloniaFact]
    public void MinimapCropClampsAtSourceAndReusesOneDisplayBitmap()
    {
        var snapshot = DebugMap(new GeoCoordinate(53, -1));
        var control = new LocalTerrainMap { Width = 208, Height = 208, MainViewportHeightPixels = 208, Map = snapshot,
            Observer = snapshot.Observer, LoadState = TerrainDebugMapLoadState.Ready };
        control.Measure(new Size(208, 208));
        control.Arrange(new Rect(0, 0, 208, 208));
        using var rendered = new RenderTargetBitmap(new PixelSize(208, 208), new Vector(96, 96));
        rendered.Render(control);
        var displayBitmap = control.DisplayBitmap;
        Assert.NotNull(displayBitmap);
        foreach (var scale in new[] { 1000d, 500, 200, 100, 50, 10 })
        {
            control.MetresPerPixel = scale;
            rendered.Render(control);
            Assert.Equal(Math.Clamp(scale * 260, 1_000, 20_000), control.DisplayedRadiusMetres, 8);
            Assert.Equal(snapshot.Width / 2d, control.SourceRectangle.Center.X, 8);
            Assert.Equal(snapshot.Height / 2d, control.SourceRectangle.Center.Y, 8);
            Assert.InRange(control.SourceRectangle.Left, 0, snapshot.Width / 2d);
            Assert.InRange(control.SourceRectangle.Right, snapshot.Width / 2d, snapshot.Width);
            Assert.InRange(control.ScaleBarMetres, .1, control.DisplayedRadiusMetres / 2);
            Assert.Same(snapshot, control.Map);
            Assert.Same(displayBitmap, control.DisplayBitmap);
        }
        Assert.Equal(1, control.RasterBuildCount);
        Assert.True(control.ScaleBarMetres < 10_000);
    }

    [AvaloniaFact]
    public void MinimapLocalContrastFollowsVisibleCropWithoutReplacingSnapshotOrBitmap()
    {
        var map = ReliefMap(new GeoCoordinate(53, -1), (row, column) =>
            Math.Abs(row - 8) <= 2 && Math.Abs(column - 8) <= 2 ? 2 + row * .05 : 40 + row + column);
        var control = new LocalTerrainMap
        {
            Width = 208, Height = 208, MainViewportHeightPixels = 208,
            MetresPerPixel = 20, Map = map, Observer = map.Observer,
            LoadState = TerrainDebugMapLoadState.Ready
        };
        control.Measure(new Size(208, 208));
        control.Arrange(new Rect(0, 0, 208, 208));
        using var rendered = new RenderTargetBitmap(new PixelSize(208, 208), new Vector(96, 96));

        rendered.Render(control);
        var snapshot = control.Map;
        var bitmap = control.DisplayBitmap;
        var outerPercentile = control.VisibleFifthPercentileMetres;
        Assert.NotNull(outerPercentile);

        control.MetresPerPixel = 10;
        rendered.Render(control);
        Assert.Same(snapshot, control.Map);
        Assert.Same(bitmap, control.DisplayBitmap);
        Assert.Equal(1, control.RasterBuildCount);
        Assert.NotEqual(outerPercentile, control.VisibleFifthPercentileMetres);
        Assert.InRange(control.EffectiveLocalContrastGain, 0, 5);
        Assert.InRange(control.EffectiveLocalContrastBlend, .4, .8);
    }

    [AvaloniaFact]
    public void MinimapLocalContrastIsBoundedForFlatReliefAndIgnoresWaterAndOutliers()
    {
        var waterAndOutlier = ReliefMap(new GeoCoordinate(53, -1), (row, column) =>
            row == 0 && column == 0 ? 10_000 : 10 + ((row + column) % 3),
            (row, column) => row < 4 ? Noctaxis.Core.Environment.LandCoverClass.PermanentWater : Noctaxis.Core.Environment.LandCoverClass.Grassland);
        var control = new LocalTerrainMap
        {
            Width = 208, Height = 208, MainViewportHeightPixels = 208,
            MetresPerPixel = 100, Map = waterAndOutlier, Observer = waterAndOutlier.Observer,
            LoadState = TerrainDebugMapLoadState.Ready
        };
        control.Measure(new Size(208, 208));
        control.Arrange(new Rect(0, 0, 208, 208));
        using var rendered = new RenderTargetBitmap(new PixelSize(208, 208), new Vector(96, 96));
        rendered.Render(control);

        Assert.Equal(192, control.VisibleValidCellCount);
        Assert.InRange(control.VisibleNinetyFifthPercentileMetres!.Value, 10, 13);
        Assert.Equal(6, control.VisibleEffectiveElevationRangeMetres);
        Assert.InRange(control.EffectiveLocalContrastGain, 0, 5);
        Assert.InRange(control.EffectiveLocalContrastBlend, .4, .8);

        var flat = ReliefMap(new GeoCoordinate(53, -1), (_, _) => 2);
        control.Map = flat;
        rendered.Render(control);
        Assert.Equal(6, control.VisibleEffectiveElevationRangeMetres);
        Assert.Equal(.8, control.EffectiveLocalContrastBlend);
    }

    [AvaloniaFact]
    public void MinimapStrongerTonesSeparateLowReliefAndReduceLocalWeightForMountains()
    {
        var control = new LocalTerrainMap { Width = 208, Height = 208,
            LoadState = TerrainDebugMapLoadState.Ready };
        control.Measure(new Size(208, 208));
        control.Arrange(new Rect(0, 0, 208, 208));
        using var rendered = new RenderTargetBitmap(new PixelSize(208, 208), new Vector(96, 96));
        double previousWeight = 1;
        foreach (var range in new[] { 3d, 60, 600 })
        {
            control.Map = ReliefMap(Observer, (_, column) => 1 + range * column / 15);
            rendered.Render(control);
            Assert.True(control.EffectiveLocalContrastBlend < previousWeight);
            previousWeight = control.EffectiveLocalContrastBlend;
            if (range != 3) continue;
            Assert.Equal(6, control.VisibleEffectiveElevationRangeMetres);
            Assert.Equal(5, control.EffectiveLocalContrastGain);
            using var pixels = ((WriteableBitmap)control.DisplayBitmap!).Lock();
            var low = Marshal.ReadByte(pixels.Address, 0);
            var high = Marshal.ReadByte(pixels.Address, 15 * 4);
            // Previous mapping: .35*(3/8) local blend, full local palette
            // at these endpoints, plus the global 3/20 elevation range.
            var previousSeparation = 174 * ((1 - .35 * 3 / 8) * 3 / 20 + .35 * 3 / 8);
            Assert.True(high - low > previousSeparation * 1.5);
            Assert.InRange(low, 39, 200);
            Assert.InRange(high, low + 1, 216);
        }
        Assert.Equal(.4, previousWeight, 8);

        control.Map = ReliefMap(Observer, (_, _) => 2,
            (_, _) => Noctaxis.Core.Environment.LandCoverClass.PermanentWater);
        rendered.Render(control);
        using var water = ((WriteableBitmap)control.DisplayBitmap!).Lock();
        Assert.Equal(0x77, Marshal.ReadByte(water.Address, 0));
        Assert.Equal(0x5f, Marshal.ReadByte(water.Address, 1));
        Assert.Equal(0x31, Marshal.ReadByte(water.Address, 2));
    }

    private static TerrainDebugMapSnapshot ReliefMap(GeoCoordinate observer,
        Func<int, int, double> elevation,
        Func<int, int, Noctaxis.Core.Environment.LandCoverClass>? classification = null)
    {
        const int width = 16;
        const int height = 16;
        var coordinates = Enumerable.Repeat(observer, width * height).ToArray();
        var elevations = Enumerable.Range(0, height)
            .SelectMany(row => Enumerable.Range(0, width).Select(column => (double?)elevation(row, column)))
            .ToArray();
        var classes = Enumerable.Range(0, height)
            .SelectMany(row => Enumerable.Range(0, width)
                .Select(column => (Noctaxis.Core.Environment.LandCoverClass?)((classification ?? ((_, _) => Noctaxis.Core.Environment.LandCoverClass.Grassland))(row, column))))
            .ToArray();
        return new TerrainDebugMapSnapshot(observer, 20_000, width, height, coordinates,
            elevations, elevations, classes,
            Enumerable.Repeat(false, width * height).ToArray(),
            Enumerable.Repeat(TerrainSampleStatus.Valid, width * height).ToArray(),
            Enumerable.Repeat("12/1/1", width * height).ToArray(),
            Noctaxis.Core.Environment.EnvironmentalDataState.Available,
            Instant.FromUtc(2026, 1, 1, 0, 0), "Synthetic");
    }
}

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task MainMapZoomWhileResolvingReadyAndDisabledNeverRequestsTerrain()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var maps = new ControllableTerrainDebugMapService();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), terrainDebugMaps: maps);
        await vm.InitializeAsync();
        var view = new NoctaxisMapView { Observer = vm.Observer };
        view.MapControlForTesting.Map!.Layers.Clear();
        var mini = new LocalTerrainMap { Width = 208, Height = 208 };
        using var scaleBinding = mini.Bind(LocalTerrainMap.MetresPerPixelProperty, new Binding(nameof(view.GroundMetresPerPixel)) { Source = view });
        using var heightBinding = mini.Bind(LocalTerrainMap.MainViewportHeightPixelsProperty, new Binding(nameof(view.ViewportHeightPixels)) { Source = view });
        using var mapBinding = mini.Bind(LocalTerrainMap.MapProperty, new Binding(nameof(vm.TerrainDebugMap)) { Source = vm });
        using var stateBinding = mini.Bind(LocalTerrainMap.LoadStateProperty, new Binding(nameof(vm.TerrainDebugMapLoadState)) { Source = vm });
        var window = new Window { Width = 800, Height = 600, Content = new Grid { Children = { view, mini } } };
        try
        {
            window.Show();
            vm.ShowPlannerCommand.Execute(null);
            Assert.True(await maps.Started.WaitAsync(TimeSpan.FromSeconds(3)));
            var generation = vm.TerrainDebugMapGeneration;
            Zoom(500);
            Zoom(50);
            Assert.Equal(TerrainDebugMapLoadState.Resolving, mini.LoadState);
            maps.Complete(0);
            await vm.WaitForTerrainDebugMapRefreshAsync();
            await vm.WaitForPlannerRefreshAsync();
            Dispatcher.UIThread.RunJobs();
            var snapshot = vm.TerrainDebugMap;
            Assert.NotNull(snapshot);
            Assert.Equal(Math.Clamp(view.GroundMetresPerPixel * view.ViewportHeightPixels / 2 * 2.5, 1_000, 20_000), mini.DisplayedRadiusMetres, 6);
            Zoom(1000);
            Assert.Equal(20_000, mini.DisplayedRadiusMetres);
            Zoom(2000);
            Assert.Equal(20_000, mini.DisplayedRadiusMetres);
            Zoom(20);
            Assert.Equal(Math.Clamp(view.GroundMetresPerPixel * view.ViewportHeightPixels / 2 * 2.5, 1_000, 20_000), mini.DisplayedRadiusMetres, 6);
            Assert.Same(snapshot, vm.TerrainDebugMap);
            Assert.Equal(generation, vm.TerrainDebugMapGeneration);
            Assert.Equal(1, maps.RequestCount);
            await vm.ApplySettingsAsync(vm.Settings with { EnableTerrainCalculations = false });
            Zoom(1000);
            Zoom(10);
            Assert.Null(vm.TerrainDebugMap);
            Assert.Equal(TerrainDebugMapLoadState.Disabled, mini.LoadState);
            Assert.Equal(1, maps.RequestCount);
        }
        finally { window.Close(); }

        void Zoom(double groundMetresPerPixel)
        {
            var centre = WebMercator.FromWgs84(vm.Observer);
            view.MapControlForTesting.Map!.Navigator.CenterOnAndZoomTo(new MPoint(centre.X, centre.Y),
                groundMetresPerPixel / Math.Cos(vm.Observer.Latitude * Angles.DegreesToRadians), 0);
            Dispatcher.UIThread.RunJobs();
        }
    }

}
