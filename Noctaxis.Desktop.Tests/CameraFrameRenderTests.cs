using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Camera frame as finally drawn, after the theme has been applied to a window that is already open, as the
/// app does at start-up and on every appearance change. The direction pad lies over the picture, transparent;
/// its theme default once came back over the markup's Transparent when the theme's resources were replaced,
/// and filled the whole picture with the surface colour, leaving only the scales on a plain navy rectangle.
/// </summary>
public sealed partial class MainViewModelTests
{
    /// <summary>A profile whose skyline is <paramref name="horizon"/>° at every bearing, ground from 20 m out.</summary>
    private static TerrainHorizonProfile LevelSkyline(PlanningSnapshot snapshot, double horizon) => snapshot.Terrain with
    {
        HasDemCoverage = true,
        Samples = Enumerable.Range(0, 360).Select(bearing => new TerrainHorizonSample(bearing, horizon, 2000,
            Sightline: [new TerrainSightlineSample(20, 0, 0, -4), new TerrainSightlineSample(200, 10, 0, 2),
                new TerrainSightlineSample(2000, 200, 0, horizon), new TerrainSightlineSample(5000, 300, 0, horizon - 1)])).ToArray()
    };

    /// <summary>
    /// Terrain below the skyline, sky above it and the scales over both, in the final pixels: looking just above
    /// a +6.2° skyline (pitch +7.2°, the skyline about 1° below the centre) and looking down at a +7.0° skyline
    /// (pitch −2.8°, mostly terrain).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(7.2, 6.2)]
    [InlineData(-2.8, 7.0)]
    public async Task TheCameraFrameShowsTerrainBelowItsSkylineAndSkyAbove(double pitch, double horizon)
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            vm.CameraBearingDegrees = 293.3;
            vm.CameraPitchDegrees = pitch;
            vm.Snapshot = vm.Snapshot! with { Terrain = LevelSkyline(vm.Snapshot, horizon) };
            Settle(window);
            using var themes = new ThemeService(Application.Current!);
            Settle(window);

            var card = window.FindControl<Border>("PlannerTerrainFrame")!;
            var view = window.FindControl<TerrainFrameView>("PlannerCameraFrameView")!;
            var pad = window.FindControl<CameraDirectionPad>("CameraDirection")!;
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(pad.ChromeBackground).Color.A);
            Assert.Same(CameraDirectionPad.OverlayForeground, pad.ChromeForeground);
            Assert.NotNull(view.Depth);
            var frame = view.FrameRectangle;
            var skyline = view.AltitudeY(horizon)!.Value;
            // The skyline is where the pitch scale puts its altitude: below the centre looking above it, above
            // the centre looking below it.
            Assert.True(Math.Abs(pad.TranslatePoint(new Point(0, pad.AltitudeY(horizon)), view)!.Value.Y - skyline) < .5);
            Assert.True(pitch > horizon ? skyline > frame.Center.Y : skyline < frame.Center.Y);

            const int scale = 2;
            var size = new PixelSize((int)Math.Ceiling(card.Bounds.Width * scale), (int)Math.Ceiling(card.Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(card);
            var pixels = new byte[size.Width * size.Height * 4];
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixels.Length);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), buffer, pixels.Length, size.Width * 4);
                System.Runtime.InteropServices.Marshal.Copy(buffer, pixels, 0, pixels.Length);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            Color At(Point point)
            {
                var at = view.TranslatePoint(point, card)!.Value;
                var index = ((int)(at.Y * scale) * size.Width + (int)(at.X * scale)) * 4;
                return Color.FromRgb(pixels[index + 2], pixels[index + 1], pixels[index]);
            }
            static bool IsTerrain(Color c) => c.R > 80 && Math.Abs(c.R - c.B) < 12 && Math.Abs(c.G - c.B) < 12;
            static bool IsSky(Color c) => c.B > c.R + 12 && c.R < 60;

            // Across the open middle of the picture, clear of the pitch scale on the left and the bearing tape below.
            foreach (var across in new[] { .45, .6, .75, .9 })
            {
                var x = frame.Left + frame.Width * across;
                var below = At(new Point(x, Math.Min(skyline + 6, frame.Bottom - CameraDirectionPad.EdgeBand - 4)));
                var above = At(new Point(x, skyline - 6));
                Assert.True(IsTerrain(below), $"{below} below the skyline at {across:P0} is not terrain");
                Assert.True(IsSky(above), $"{above} above the skyline at {across:P0} is not sky");
            }
            // The scales are drawn over the picture: the camera's pitch mark, in the accent, is neither terrain
            // grey nor sky blue.
            var mark = pad.TranslatePoint(new Point(0, pad.AltitudeY(pitch)), view)!.Value.Y;
            Assert.Contains(Enumerable.Range(2, 16), x => At(new Point(x, mark)) is var c && c.B > 150 && c.B - c.G > 25);
        }
        finally { Close(window); }
    }

    /// <summary>A drawn control that sets none of its chrome still follows the theme when it changes.</summary>
    [AvaloniaFact]
    public void ChromeDefaultsStillFollowTheTheme()
    {
        var view = new TerrainFrameView();
        var window = new Window { Width = 240, Height = 180, Content = view };
        window.Show();
        try
        {
            using var themes = new ThemeService(Application.Current!);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(window.TryFindResource("SurfaceBackground", out var surface));
            Assert.Equal(surface, view.ChromeBackground);
            Assert.True(window.TryFindResource("SecondaryText", out var text));
            Assert.Equal(text, view.ChromeForeground);
        }
        finally { window.Close(); }
    }
}
