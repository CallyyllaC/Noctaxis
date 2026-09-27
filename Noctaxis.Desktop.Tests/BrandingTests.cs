using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Branding;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>The Noctaxis mark: one geometry for the committed icon, the SVG and the in-app mark.</summary>
public sealed class BrandingTests
{
    private static string Assets => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.MainWindowMarkup))!, "Assets");

    [Fact]
    public void CommittedAssetsAreGeneratedFromTheCanonicalLogo()
    {
        var ico = File.ReadAllBytes(Path.Combine(Assets, NoctaxisIcon.IcoFileName));
        using var reader = new BinaryReader(new MemoryStream(ico));
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        int count = reader.ReadUInt16();
        Assert.Equal(NoctaxisIcon.Sizes.Count, count);
        var entries = Enumerable.Range(0, count).Select(_ =>
        {
            int width = reader.ReadByte(), height = reader.ReadByte();
            reader.ReadBytes(6);
            return (Width: width == 0 ? 256 : width, Height: height == 0 ? 256 : height, Length: reader.ReadInt32(), Offset: reader.ReadInt32());
        }).ToArray();
        foreach (var (entry, size) in entries.Zip(NoctaxisIcon.Sizes))
        {
            Assert.Equal((size, size), (entry.Width, entry.Height));
            using var committed = SKBitmap.Decode(ico.AsSpan(entry.Offset, entry.Length));
            using var expected = NoctaxisIcon.Render(size);
            Assert.Equal((size, size), (committed.Width, committed.Height));
            var drift = 0;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                SKColor a = committed.GetPixel(x, y), b = expected.GetPixel(x, y);
                // Premultiplied: PNG round trips may change the colour of nearly transparent edge pixels.
                int P(byte channel, byte alpha) => channel * alpha / 255;
                drift = Math.Max(drift, new[] { P(a.Red, a.Alpha) - P(b.Red, b.Alpha), P(a.Green, a.Alpha) - P(b.Green, b.Alpha),
                    P(a.Blue, a.Alpha) - P(b.Blue, b.Alpha), a.Alpha - b.Alpha }.Max(Math.Abs));
            }
            // Regenerate with NoctaxisIcon.WriteAssets when the logo changes (docs/branding.md).
            Assert.True(drift <= 3, $"{size}px icon differs from the canonical logo by {drift}");
        }
        Assert.Equal(NoctaxisLogo.ToSvg(NoctaxisLogo.Regular).ReplaceLineEndings(),
            File.ReadAllText(Path.Combine(Assets, NoctaxisIcon.SvgFileName)).ReplaceLineEndings());
    }

    [Fact]
    public void ExecutableAndWindowsUseTheApplicationIcon()
    {
        var project = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assets)!, "Noctaxis.Desktop.csproj"));
        Assert.Contains(@"<ApplicationIcon>Assets\noctaxis.ico</ApplicationIcon>", project);
        Assert.Contains("Icon=\"/Assets/noctaxis.ico\"", File.ReadAllText(TestPaths.MainWindowMarkup));
    }

    [Theory]
    [InlineData(16)] [InlineData(20)] [InlineData(24)] [InlineData(32)] [InlineData(48)]
    public void SmallIconsStayCrispWithBothShapesAndTheGapBetweenThem(int size)
    {
        var weight = NoctaxisLogo.ForPixelSize(size);
        var scale = size / NoctaxisLogo.GridSize;
        Assert.Equal(size <= 24 ? NoctaxisLogo.Small.Name : NoctaxisLogo.Regular.Name, weight.Name);
        // Bracket edges on whole pixels, and never thinner than two pixels at taskbar sizes.
        Assert.Equal(Math.Round(weight.BracketStroke * scale), weight.BracketStroke * scale, 6);
        Assert.Equal(Math.Round(weight.FrameInset * scale), weight.FrameInset * scale, 6);
        Assert.True(weight.BracketStroke * scale >= 2, $"{size}px bracket stroke");

        using var icon = NoctaxisIcon.Render(size);
        var frame = SKColor.Parse(NoctaxisLogo.BrandFrame);
        var star = SKColor.Parse(NoctaxisLogo.BrandStar);
        var inset = (int)Math.Round(weight.FrameInset * scale);
        // On each bracket's straight vertical arm, just past its rounded corner.
        var arm = (int)Math.Ceiling((weight.FrameInset + weight.BracketStroke + weight.BracketRadius) * scale);
        // Solid bracket at each corner, solid star at the centre, clear space between them.
        foreach (var (x, y) in new[] { (inset, arm), (size - 1 - inset, arm), (inset, size - 1 - arm), (size - 1 - inset, size - 1 - arm) })
            Assert.Equal(frame, icon.GetPixel(x, y));
        Assert.Equal(star, icon.GetPixel(size / 2, size / 2));
        foreach (var (x, y) in new[] { (size / 4, size / 4), (size - 1 - size / 4, size / 4), (size / 4, size - 1 - size / 4), (size - 1 - size / 4, size - 1 - size / 4) })
            Assert.True(icon.GetPixel(x, y).Alpha < 40, $"{size}px gap at ({x},{y})");
    }

    [Fact]
    public void BrandColoursHoldTheirShapeOnLightAndDarkTaskbars()
    {
        Color frame = Color.Parse(NoctaxisLogo.BrandFrame), star = Color.Parse(NoctaxisLogo.BrandStar);
        foreach (var taskbar in new[] { "#F3F3F3", "#202020" })
        {
            Assert.True(ColourScience.Contrast(frame, Color.Parse(taskbar)) >= 3, $"frame on {taskbar}");
            Assert.True(ColourScience.Contrast(star, Color.Parse(taskbar)) >= 2.5, $"star on {taskbar}");
        }
        // Two tones of one hue: the frame is the stronger, the star the lighter.
        Assert.True(ColourScience.Luminance(star) > ColourScience.Luminance(frame));
    }

    [Fact]
    public void ThemeLogoColoursStandOutFromTheTitlebarInEveryTheme()
    {
        var catalogue = new ThemeCatalogue(isAvailable: _ => true);
        foreach (var theme in catalogue.Themes)
        foreach (var appearance in new[] { AppearanceMode.Dark, AppearanceMode.Light })
        foreach (var vision in Enum.GetValues<ColourVisionMode>())
        {
            var palette = catalogue.Resolve(new AppearancePreferences(theme.Id, vision, AppearanceMode: appearance)).Palette;
            var surface = Color.Parse(palette["SurfaceBackground"]);
            foreach (var role in new[] { NoctaxisLogoMark.FrameResourceKey, NoctaxisLogoMark.StarResourceKey })
                Assert.True(ColourScience.Contrast(Color.Parse(palette[role]), surface) >= 3,
                    $"{theme.Id}/{appearance}/{vision}: {role} on the titlebar");
        }
    }

    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)] [InlineData("builtin.noctaxis", AppearanceMode.Light)]
    [InlineData("builtin.highcontrast", AppearanceMode.Dark)] [InlineData("supporter.noctaxis-neon", AppearanceMode.Dark)]
    [InlineData("supporter.foxfire", AppearanceMode.Light)]
    public void TitlebarMarkIsDrawnInExactlyTheTwoThemeColours(string id, AppearanceMode appearance)
    {
        var service = ((App)Application.Current!).Themes!;
        service.SetSupporterEntitlement(true);
        try
        {
            service.Apply(new(id, AppearanceMode: appearance));
            var window = new MainWindow { Width = 900, Height = 600 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.NotNull(window.Icon);
                var mark = Assert.Single(window.GetVisualDescendants().OfType<NoctaxisLogoMark>());
                var palette = service.EffectiveTheme.Palette;
                Color frame = Color.Parse(palette["Accent"]), star = Color.Parse(palette["PrimaryText"]);
                Assert.Equal(frame, Assert.IsAssignableFrom<ISolidColorBrush>(mark.FrameBrush).Color);
                Assert.Equal(star, Assert.IsAssignableFrom<ISolidColorBrush>(mark.StarBrush).Color);
                Assert.Equal(NoctaxisLogo.Small.Name, mark.CurrentWeight.Name);

                // Drawn: every fully covered pixel is one of the two colours, and both appear.
                var large = new NoctaxisLogoMark { Width = 96, Height = 96 };
                var host = new Border { Background = Brushes.Transparent, Child = large };
                window.Content = host;
                Dispatcher.UIThread.RunJobs();
                using var bitmap = new RenderTargetBitmap(new PixelSize(96, 96));
                bitmap.Render(large);
                using var copy = new WriteableBitmap(new PixelSize(96, 96), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var buffer = copy.Lock()) bitmap.CopyPixels(buffer);
                using var pixels = copy.Lock();
                int frameCount = 0, starCount = 0;
                for (var y = 0; y < 96; y++)
                for (var x = 0; x < 96; x++)
                {
                    var offset = y * pixels.RowBytes + x * 4;
                    byte At(int channel) => Marshal.ReadByte(pixels.Address, offset + channel);
                    if (At(3) != 255) continue;
                    var colour = pixels.Format == PixelFormats.Rgba8888 ? Color.FromRgb(At(0), At(1), At(2)) : Color.FromRgb(At(2), At(1), At(0));
                    Assert.True(colour == frame || colour == star, $"{id}/{appearance}: unexpected {colour} at ({x},{y})");
                    if (colour == frame) frameCount++;
                    if (colour == star) starCount++;
                }
                Assert.True(frameCount > 150 && starCount > 150, $"{id}/{appearance}: frame {frameCount}, star {starCount}");
            }
            finally { window.Close(); }
        }
        finally
        {
            service.SetSupporterEntitlement(false);
            service.Apply(new());
        }
    }
}
