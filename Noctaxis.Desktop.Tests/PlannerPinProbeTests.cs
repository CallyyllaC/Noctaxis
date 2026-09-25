using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Rendering.Skia;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Diagnostics;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class PlannerPinProbeTests
{
    [AvaloniaFact]
    public void AllVariantsRetainExistingDragPreviewCommitAndLocationSwitching()
    {
        foreach (var mode in new[] { PlannerPinMode.Floating, PlannerPinMode.Layer, PlannerPinMode.Debounced })
        {
            var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
            var map = view.MapControlForTesting.Map!;
            map.Layers.Clear();
            view.PinProbe = new PlannerPinProbe(mode);
            if (mode == PlannerPinMode.Layer) map.Layers.Add(view.PinProbe.CreateLayer());
            var window = new Window { Width = 800, Height = 600, Content = view };
            GeoCoordinate? preview = null, committed = null;
            view.PreviewCoordinateChanged += (_, coordinate) => preview = coordinate;
            view.CoordinateCommitted += (_, coordinate) => committed = coordinate;
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var original = view.Observer;
                var point = view.TranslatePoint(view.PinScreenPointForTesting!.Value, window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseMove(point + new Vector(30, 20), RawInputModifiers.LeftMouseButton);
                Assert.NotNull(preview);
                Assert.Null(committed);
                Assert.Equal(original, view.Observer);
                window.MouseUp(point + new Vector(30, 20), MouseButton.Left);
                Assert.Equal(preview, committed);
                Assert.NotEqual(original, committed);
                view.Observer = new GeoCoordinate(-35, 149);
                var projected = PlannerPinProbe.Project(view.Observer, map.Navigator.Viewport);
                Assert.Equal(view.PinScreenPointForTesting!.Value.X, projected.X, 8);
                Assert.Equal(view.PinScreenPointForTesting!.Value.Y, projected.Y, 8);
            }
            finally { view.PinProbe = null; window.Close(); }
        }
    }

    [Fact]
    public void DebounceRestoresAt200MillisecondsAndNeverHidesAnActivePinDrag()
    {
        var probe = new PlannerPinProbe(PlannerPinMode.Debounced);
        var coordinate = new GeoCoordinate(53, -1);
        Assert.False(probe.Poll(false, false, coordinate, PlannerPinActivity.None, 0));
        Assert.True(probe.Poll(true, false, coordinate, PlannerPinActivity.None, 10));
        Assert.True(probe.HideFloatingPin);
        Assert.False(probe.Poll(false, false, coordinate, PlannerPinActivity.None, 209));
        Assert.True(probe.Poll(false, false, coordinate, PlannerPinActivity.None, 210));
        Assert.False(probe.HideFloatingPin);
        probe.Poll(true, false, coordinate, PlannerPinActivity.None, 220);
        probe.Poll(true, true, coordinate, PlannerPinActivity.None, 221);
        Assert.False(probe.HideFloatingPin);
    }

    [AvaloniaFact]
    public void ProjectionMatchesProductionThroughWrappedWorldsZoomRotationResizeAndLocationChanges()
    {
        var view = new NoctaxisMapView();
        var map = view.MapControlForTesting.Map!;
        map.Layers.Clear();
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            window.Show();
            foreach (var coordinate in new[] { new GeoCoordinate(53, -1), new GeoCoordinate(-35, 179.9), new GeoCoordinate(80, -179.9) })
            foreach (var resolution in new[] { 10d, 500, 2000 })
            foreach (var rotation in new[] { 0d, 45 })
            {
                view.Observer = coordinate;
                var world = WebMercator.FromWgs84(coordinate);
                map.Navigator.CenterOnAndZoomTo(new MPoint(world.X + 2 * Math.PI * 6378137 + 1250, world.Y), resolution, 0);
                map.Navigator.RotateTo(rotation, 0);
                window.Width = resolution == 10 ? 700 : 800;
                Dispatcher.UIThread.RunJobs();
                var viewport = map.Navigator.Viewport;
                var expected = view.PinScreenPointForTesting!.Value;
                var actual = PlannerPinProbe.Project(coordinate, viewport);
                Assert.Equal(expected.X, actual.X, 8);
                Assert.Equal(expected.Y, actual.Y, 8);
                Assert.Equal(coordinate, view.Observer);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RepeatedOverlayRedrawDoesNotMeasureArrangeOrChangeObserverAndHiddenPinStillProjects()
    {
        var view = new NoctaxisMapView();
        view.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var probe = new PlannerPinProbe(PlannerPinMode.Floating);
            view.PinProbe = probe;
            using var bitmap = new RenderTargetBitmap(new PixelSize(800, 600));
            bitmap.Render(view.OverlayForTesting);
            var before = JsonSerializer.SerializeToElement(probe.Snapshot());
            var observer = view.Observer;
            for (var i = 0; i < 20; i++) bitmap.Render(view.OverlayForTesting);
            var after = JsonSerializer.SerializeToElement(probe.Snapshot());
            Assert.Equal(before.GetProperty("measures").GetInt64(), after.GetProperty("measures").GetInt64());
            Assert.Equal(before.GetProperty("arranges").GetInt64(), after.GetProperty("arranges").GetInt64());
            Assert.Equal(20, Calls(after, "pin") - Calls(before, "pin"));
            Assert.Equal(observer, view.Observer);
            var hidden = new PlannerPinProbe(PlannerPinMode.Debounced);
            view.PinProbe = hidden;
            hidden.Poll(true, false, observer, PlannerPinActivity.None, 0);
            bitmap.Render(view.OverlayForTesting);
            var result = JsonSerializer.SerializeToElement(hidden.Snapshot());
            Assert.Equal(0, Calls(result, "pin"));
            Assert.Equal(1, Calls(result, "projection"));
        }
        finally { view.PinProbe = null; window.Close(); }
    }

    [Fact]
    public void MapsuiLayerActuallyRendersMarkerAtCoordinateAtBothPixelDensities()
    {
        foreach (var density in new[] { 1f, 2f })
        {
            using var map = new Map();
            map.Navigator.SetSize(800, 600);
            var coordinate = new GeoCoordinate(53, -1);
            var world = WebMercator.FromWgs84(coordinate);
            map.Navigator.CenterOnAndZoomTo(new MPoint(world.X, world.Y), 100, 0);
            var probe = new PlannerPinProbe(PlannerPinMode.Layer);
            probe.Poll(false, false, coordinate, PlannerPinActivity.None);
            map.Layers.Add(probe.CreateLayer());
            using var stream = new MapRenderer().RenderToBitmapStream(map, density);
            stream.Position = 0;
            using var bitmap = SKBitmap.Decode(stream);
            Assert.NotNull(bitmap);
            var center = bitmap.GetPixel((int)(400 * density), (int)(302 * density));
            Assert.Equal(new SKColor(11, 15, 23), center);
            // Check screen-space size and outline, not just a nonempty renderer callback.
            Assert.Equal(SKColors.White, bitmap.GetPixel((int)(412 * density), (int)(302 * density)));
            Assert.NotEqual(center, bitmap.GetPixel((int)(420 * density), (int)(302 * density)));
            var captureDirectory = System.Environment.GetEnvironmentVariable("NOCTAXIS_PIN_CAPTURES");
            if (!string.IsNullOrEmpty(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(Path.Combine(captureDirectory, $"layer-{density}x.png"));
                encoded.SaveTo(file);
            }
            Assert.True(Calls(JsonSerializer.SerializeToElement(probe.Snapshot()), "pin") > 0);
        }
    }
    private static long Calls(JsonElement result, string metric) => result.GetProperty(metric).GetProperty("calls").GetInt64();
}
