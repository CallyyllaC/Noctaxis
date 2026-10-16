using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Camera frame's pitch scale: ticks and figures placed by the one projection the skyline uses, figures on
/// the majors where they fit, the level (0°) tick where the true horizontal is, and the camera's own pitch mark
/// never competing with a figure.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static (Window Window, CameraDirectionPad Pad) PitchPad(double horizontal, double vertical, double pitch, double height = 119, double width = 178)
    {
        var pad = new CameraDirectionPad
        {
            Width = width, Height = height, Pitch = pitch, Bearing = 180,
            HorizontalFieldOfView = horizontal, VerticalFieldOfView = vertical
        };
        var window = new Window { Width = width + 40, Height = height + 40, Content = pad };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, pad);
    }

    /// <summary>
    /// For a wide, a normal and a long lens, Landscape and Portrait: every tick is where its altitude is drawn
    /// (<see cref="CameraDirectionPad.AltitudeY"/>, the skyline's projection), each figure is centred on its tick
    /// and says its tick's altitude, the level tick is at the true horizontal, and every figure is inside the
    /// picture, between the tilt arrows, clear of the pitch mark and of the other figures.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(104.3, 81.2, 5)]     // 14 mm
    [InlineData(73.7, 53.1, 0)]      // 24 mm, level
    [InlineData(73.7, 53.1, 20)]     // looking up
    [InlineData(73.7, 53.1, -20)]    // looking down
    [InlineData(39.6, 27.0, 5)]      // 50 mm
    [InlineData(27.0, 39.6, 5)]      // 50 mm portrait
    [InlineData(10.3, 6.9, 1)]       // 200 mm
    [InlineData(6.9, 10.3, 1)]       // 200 mm portrait
    public void PitchFiguresStandWhereTheirAnglesAreDrawn(double horizontal, double vertical, double pitch)
    {
        var (window, pad) = PitchPad(horizontal, vertical, pitch);
        try
        {
            var ticks = pad.PitchScale();
            Assert.NotEmpty(ticks);
            Assert.Contains(ticks, tick => tick.Label is not null);
            var ladder = pad.LadderBounds;
            var marker = pad.AltitudeY(pitch);
            Assert.Equal(pad.Bounds.Height / 2, marker, 6);
            var arrows = new[] { new Rect(0, 0, CameraDirectionPad.EdgeBand, CameraDirectionPad.EdgeBand),
                new Rect(0, pad.Bounds.Height - CameraDirectionPad.EdgeBand, CameraDirectionPad.EdgeBand, CameraDirectionPad.EdgeBand) };
            var placed = new List<Rect>();
            foreach (var tick in ticks)
            {
                Assert.Equal(pad.AltitudeY(tick.Altitude), tick.Y, 6);
                Assert.InRange(tick.Y, ladder.Top, ladder.Bottom);
                if (tick.Altitude == 0) Assert.Equal(pad.HorizonY, tick.Y, 6);
                if (tick.Label is not { } label) continue;
                var box = tick.LabelBounds!.Value;
                Assert.Equal(CameraDirectionPad.PitchLabel(tick.Altitude), label);
                Assert.True(tick.IsMajor);
                Assert.Equal(tick.Y, box.Center.Y, 6);
                Assert.True(box.Top >= ladder.Top && box.Bottom <= ladder.Bottom, $"{label} {box} is outside the ladder");
                Assert.True(box.Right <= pad.Bounds.Width && box.Left >= 0);
                Assert.DoesNotContain(arrows, arrow => arrow.Intersects(box));
                Assert.True(Math.Abs(box.Center.Y - marker) >= box.Height / 2 + 2, $"{label} competes with the pitch mark");
                Assert.DoesNotContain(placed, other => other.Intersects(box));
                placed.Add(box);
            }
            // Minor ticks are at least 7 px apart, and majors are a whole number of minors.
            var steps = ticks.Zip(ticks.Skip(1), (a, b) => Math.Abs(b.Y - a.Y)).ToArray();
            Assert.All(steps, step => Assert.True(step >= 6.9, $"ticks {step:0.0} px apart"));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Every figure and tick is drawn where it is laid out, together with the bearing scale's figures. A figure
    /// drawn after another's clip had closed once came out at the bitmap's scale twice over, taking every later
    /// tick, figure and the pitch mark out of the picture.
    /// </summary>
    [AvaloniaFact]
    public void EveryPitchFigureAndTickIsDrawnWhereItIsLaidOut()
    {
        var pad = new CameraDirectionPad { Width = 178, Height = 119, Pitch = 0, Bearing = 180, HorizontalFieldOfView = 73.7, VerticalFieldOfView = 53.1 };
        var stage = new Border { Background = Avalonia.Media.Brushes.Black, Child = pad,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Width = 240, Height = 180, Content = stage };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        try
        {
            var size = new PixelSize(178 * 2, 119 * 2);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(192, 192));
            bitmap.Render(stage);
            var pixels = new byte[size.Width * size.Height * 4];
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixels.Length);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), buffer, pixels.Length, size.Width * 4);
                System.Runtime.InteropServices.Marshal.Copy(buffer, pixels, 0, pixels.Length);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            bool Lit(Rect area)
            {
                for (var y = (int)(area.Top * 2); y < (int)(area.Bottom * 2); y++)
                    for (var x = (int)(area.Left * 2); x < (int)(area.Right * 2); x++)
                        if (pixels[(y * size.Width + x) * 4 + 1] > 120) return true;
                return false;
            }
            var ticks = pad.PitchScale();
            Assert.Contains(ticks, tick => tick.Label is not null);
            foreach (var tick in ticks)
            {
                Assert.True(Lit(new Rect(4, tick.Y - 1.5, tick.Length - 2, 3)), $"the {tick.Altitude}° tick is not drawn");
                if (tick.LabelBounds is { } box) Assert.True(Lit(box), $"the {tick.Label} figure is not drawn");
            }
        }
        finally { window.Close(); }
    }

    /// <summary>The level figure and tick stand out: 0° is the longest tick and keeps its figure where there is room.</summary>
    [AvaloniaFact]
    public void TheLevelTickIsTheLongestAndLabelled()
    {
        var (window, pad) = PitchPad(39.6, 27.0, 5);
        try
        {
            var ticks = pad.PitchScale();
            var level = Assert.Single(ticks, tick => tick.Altitude == 0);
            Assert.Equal("0°", level.Label);
            Assert.All(ticks.Where(tick => tick.Altitude != 0), tick => Assert.True(tick.Length < level.Length));
            Assert.All(ticks.Where(tick => tick.Altitude != 0 && tick.IsMajor), tick => Assert.True(tick.Length > ticks.Where(t => !t.IsMajor).Max(t => t.Length)));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The same lens drawn larger shows its figures more densely: a small frame keeps fewer figures, never
    /// cramped ones, and a tall one figures every major.
    /// </summary>
    [AvaloniaFact]
    public void FigureDensityFollowsTheFramesHeight()
    {
        var (small, smallPad) = PitchPad(104.3, 81.2, 0, height: 119);
        var (tall, tallPad) = PitchPad(104.3, 81.2, 0, height: 400, width: 520);
        try
        {
            static double Spacing(IReadOnlyList<CameraDirectionPad.PitchTick> ticks)
            {
                var labelled = ticks.Where(tick => tick.Label is not null && tick.Altitude != 0).Select(tick => tick.Altitude).Order().ToArray();
                return labelled.Length < 2 ? double.PositiveInfinity : labelled.Zip(labelled.Skip(1), (a, b) => b - a).Min();
            }
            var smallTicks = smallPad.PitchScale();
            var tallTicks = tallPad.PitchScale();
            Assert.True(Spacing(tallTicks) <= Spacing(smallTicks), $"tall {Spacing(tallTicks)}°, small {Spacing(smallTicks)}°");
            Assert.True(tallTicks.Count(tick => tick.Label is not null) > smallTicks.Count(tick => tick.Label is not null));
            var tallSteps = tallTicks.Where(tick => !tick.IsMajor).Select(tick => tick.Altitude).ToArray();
            var smallSteps = smallTicks.Where(tick => !tick.IsMajor).Select(tick => tick.Altitude).ToArray();
            Assert.True(tallSteps.Length >= smallSteps.Length);
        }
        finally { small.Close(); tall.Close(); }
    }
}
