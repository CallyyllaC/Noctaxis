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
    public void PlannerMapTakesWheelTrackpadAndPanInput()
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
            using var frame = window.CaptureRenderedFrame();
            // Away from the centre, where the planning pin sits, and clear of the Picture-in-Picture map
            // and the instruments laid over the map: dragging the pin moves the pin, and this is about the
            // map taking the gesture.
            var point = view.TranslatePoint(new Point(view.Bounds.Width * .7, view.Bounds.Height * .3), window)!.Value;
            window.MouseWheel(point, new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(50, navigator.Viewport.Resolution);
            // Four fractional trackpad deltas must reach Mapsui's existing accumulation path.
            for (var i = 0; i < 4; i++) window.MouseWheel(point, new Vector(0, -.25));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(100, navigator.Viewport.Resolution);
            var beforePan = navigator.Viewport;
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(35, 20), Avalonia.Input.RawInputModifiers.LeftMouseButton);
            window.MouseUp(point + new Vector(35, 20), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(beforePan.CenterX, navigator.Viewport.CenterX);
            Assert.Equal(100, navigator.Viewport.Resolution);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ViewportObserverAcceptsRenderThreadUpdatesWithoutInterruptingNavigation()
    {
        var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        view.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            window.Show();
            var navigator = view.MapControlForTesting.Map.Navigator;
            foreach (var resolution in new[] { 100d, 20, 2000, 50 })
            {
                // Mapsui also raises ViewportChanged from its render/animation thread.
                await Task.Run(() => navigator.CenterOnAndZoomTo(new MPoint(1200, 3400), resolution, 0));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(resolution, navigator.Viewport.Resolution);
                Assert.Equal(1200, navigator.Viewport.CenterX);
                Assert.Equal(3400, navigator.Viewport.CenterY);
                Assert.Equal(resolution * Math.Cos(53 * Angles.DegreesToRadians), view.GroundMetresPerPixel, 8);
            }
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
}
