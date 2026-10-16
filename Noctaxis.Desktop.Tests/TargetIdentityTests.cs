using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// A target is told apart by its colour and its line: the focused target solid and heavier, every other one in its
/// role's dash pattern, heavier again under a colour-vision mode. No shape is drawn along a path. The map, the
/// timeline and the line samples in the Inspector and Settings all draw it with the one stroke.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly string Sources = Path.Combine(TestPaths.DesktopProject, "Controls");

    /// <summary>Nothing is left that draws a shape on a path: no marker type, no marker drawing, no marker distance.</summary>
    [Fact]
    public void NoTargetGlyphIsDrawnAlongAPath()
    {
        var assembly = typeof(CelestialPalette).Assembly;
        Assert.Null(assembly.GetType("Noctaxis.Desktop.Controls.CelestialMarker"));
        Assert.DoesNotContain(typeof(CelestialPalette).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            member => member.Name.Contains("Marker", StringComparison.Ordinal));
        Assert.Null(typeof(NoctaxisMapView).GetField("RayMarkerDistance", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public));
        foreach (var file in new[] { "NoctaxisMapView.cs", "HorizonGraph.cs", "CelestialLegendGlyph.cs" })
            Assert.DoesNotContain("DrawMarker", File.ReadAllText(Path.Combine(Sources, file)));
    }

    /// <summary>
    /// The one stroke: the map rays, the timeline and the line sample all take their pen from
    /// <see cref="CelestialPalette.Stroke"/>, and no other file holds a target dash pattern of its own.
    /// </summary>
    [Fact]
    public void EverySurfaceDrawsATargetWithTheOneStroke()
    {
        foreach (var file in new[] { "NoctaxisMapView.cs", "HorizonGraph.cs", "CelestialLegendGlyph.cs" })
            Assert.Contains("CelestialPalette.Stroke(", File.ReadAllText(Path.Combine(Sources, file)));
        foreach (var file in Directory.EnumerateFiles(TestPaths.DesktopProject, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.EndsWith("CelestialPalette.cs")))
            Assert.DoesNotContain("Dashes[(int)role]", File.ReadAllText(file));
    }

    /// <summary>
    /// Focused: solid and in its colour. Otherwise: its role's own pattern in its colour, at the surface's weight with
    /// normal colour vision and heavier with a colour-vision mode, with the same pattern.
    /// </summary>
    [Fact]
    public void TheStrokeIsColourAndPatternAndPatternsAreEmphasisedForColourVision()
    {
        var normal = CelestialColourSet.Default;
        var emphasised = normal with { EmphasisePatterns = true };
        foreach (var role in Enum.GetValues<CelestialRole>())
        {
            var focused = CelestialPalette.Stroke(normal, role, true, 2.6);
            Assert.Null(focused.DashStyle);
            Assert.Equal(2.6, focused.Thickness);
            Assert.Equal(normal[role], Assert.IsAssignableFrom<ISolidColorBrush>(focused.Brush).Color);
            var other = CelestialPalette.Stroke(normal, role, false, 1.5);
            Assert.Same(CelestialPalette.Dash(role), other.DashStyle);
            Assert.Equal(1.5, other.Thickness);
            var cvd = CelestialPalette.Stroke(emphasised, role, false, 1.5);
            Assert.Same(CelestialPalette.Dash(role), cvd.DashStyle);
            Assert.Equal(1.5 * CelestialPalette.PatternEmphasis, cvd.Thickness, 6);
            // The focused target is solid in every mode, so it stays the one solid path.
            Assert.Null(CelestialPalette.Stroke(emphasised, role, true, 2.6).DashStyle);
        }
    }

    /// <summary>
    /// Patterns that a viewer can tell apart, not merely different numbers: every two differ in how many dashes a
    /// repeat has or, with as many, in a dash's length by at least half again. None is solid, so none can be mistaken
    /// for the focused target.
    /// </summary>
    [Fact]
    public void EveryTwoTargetPatternsAreClearlyDistinct()
    {
        static double[] OnSegments(CelestialRole role) =>
            CelestialPalette.Dash(role).Dashes!.Where((_, index) => index % 2 == 0).Order().ToArray();
        var roles = Enum.GetValues<CelestialRole>();
        foreach (var a in roles)
        {
            Assert.True(CelestialPalette.Dash(a).Dashes!.Count >= 2, $"{a} is solid");
            foreach (var b in roles.Where(b => b > a))
            {
                var (x, y) = (OnSegments(a), OnSegments(b));
                if (x.Length != y.Length) continue;
                var ratio = x.Zip(y, (p, q) => Math.Max(p, q) / Math.Min(p, q)).Max();
                Assert.True(ratio >= 1.5, $"{a} and {b} are too alike ({ratio:0.00})");
            }
        }
    }

    /// <summary>
    /// The published colour set says whether patterns are emphasised: on with any colour-vision mode, off without,
    /// across every theme family and appearance, and a line sample follows it at once.
    /// </summary>
    [AvaloniaFact]
    public void ColourVisionModesEmphasiseTargetPatternsInEveryTheme()
    {
        var service = ((App)Application.Current!).Themes!;
        var glyph = new CelestialLegendGlyph { Role = CelestialRole.DeepSky3 };
        var window = new Window { Width = 200, Height = 80, Content = glyph };
        try
        {
            service.SetSupporterEntitlement(true);
            window.Show();
            foreach (var variant in AppearanceTests.Variants)
            {
                var (id, mode, appearance) = ((string)variant[0], (ColourVisionMode)variant[1], (AppearanceMode)variant[2]);
                service.Apply(new(id, mode, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                Assert.True(Application.Current!.TryFindResource(CelestialThemeTokens.ColourSet, out var published));
                var set = Assert.IsType<CelestialColourSet>(published);
                Assert.Equal(mode != ColourVisionMode.None, set.EmphasisePatterns);
                Assert.Equal(mode != ColourVisionMode.None, glyph.CelestialColours.EmphasisePatterns);
                Assert.Same(CelestialPalette.Dash(CelestialRole.DeepSky3), glyph.StrokeForTesting.DashStyle);
            }
        }
        finally
        {
            window.Close();
            service.SetSupporterEntitlement(false);
            service.Apply(new());
        }
    }

    private static byte[] Pixels(Control control, int scale, out PixelSize size)
    {
        size = new PixelSize((int)Math.Ceiling(control.Bounds.Width * scale), (int)Math.Ceiling(control.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        var pixels = new byte[size.Width * size.Height * 4];
        var buffer = Marshal.AllocHGlobal(pixels.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), buffer, pixels.Length, size.Width * 4);
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return pixels;
    }

    /// <summary>
    /// The line sample is a line and nothing else: only the line's own few rows are drawn, broken into its pattern
    /// (solid for the focused target), with no shape over its middle.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(CelestialRole.Sun, false)]
    [InlineData(CelestialRole.Moon, false)]
    [InlineData(CelestialRole.DeepSky3, false)]
    [InlineData(CelestialRole.DeepSky1, true)]
    public void TheLineSampleIsTheLineAlone(CelestialRole role, bool focused)
    {
        var glyph = new CelestialLegendGlyph { Role = role, IsPrimary = focused };
        var stage = new Border { Background = Brushes.Black, Child = glyph,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Width = 120, Height = 60, Content = stage };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            const int scale = 2;
            var pixels = Pixels(stage, scale, out var size);
            bool Lit(int x, int y) => pixels[(y * size.Width + x) * 4 + 3] > 0 &&
                Math.Max(pixels[(y * size.Width + x) * 4], Math.Max(pixels[(y * size.Width + x) * 4 + 1], pixels[(y * size.Width + x) * 4 + 2])) > 60;
            var litRows = Enumerable.Range(0, size.Height).Where(y => Enumerable.Range(0, size.Width).Any(x => Lit(x, y))).ToArray();
            Assert.NotEmpty(litRows);
            Assert.True(litRows.Length <= Math.Ceiling(glyph.StrokeForTesting.Thickness * scale) + 2,
                $"{role}: {litRows.Length} rows drawn for a {glyph.StrokeForTesting.Thickness} px line");
            var middle = litRows[litRows.Length / 2];
            var runs = Enumerable.Range(2 * scale, size.Width - 4 * scale).Select(x => Lit(x, middle))
                .Zip(Enumerable.Range(2 * scale + 1, size.Width - 4 * scale - 1).Select(x => Lit(x, middle)), (a, b) => a != b).Count(change => change);
            if (focused) Assert.Equal(0, runs);
            else Assert.True(runs >= 4, $"{role}: the pattern shows {runs / 2} breaks");
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The Inspector's rows and Settings' object list show each target's line sample, the one its map ray and
    /// timeline path are drawn with, named by its pattern for a screen reader.
    /// </summary>
    [AvaloniaFact]
    public async Task TheInspectorAndSettingsShowEachTargetsLineSample()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var rows = window.FindControl<ItemsControl>("PlannerTargetRows")!;
            foreach (var item in vm.CelestialObjects.Where(item => item.IsVisible))
            {
                var sample = rows.ContainerFromItem(item)!.GetVisualDescendants().OfType<CelestialLegendGlyph>().Single();
                Assert.Equal((item.Role, item.IsPrimary), (sample.Role, sample.IsPrimary));
                Assert.Equal(CelestialPalette.PatternName(item.Role, item.IsPrimary), Avalonia.Automation.AutomationProperties.GetName(sample));
                Assert.Equal(CelestialLegendGlyph.SampleWidth, sample.Bounds.Width);
            }
            var focused = vm.CelestialObjects.Single(item => item.IsPrimary);
            Assert.Equal("Solid line", CelestialPalette.PatternName(focused.Role, true));
            Assert.Equal(Enum.GetValues<CelestialRole>().Length,
                Enum.GetValues<CelestialRole>().Select(role => CelestialPalette.PatternName(role, false)).Distinct().Count());

            var settings = window.FindControl<ItemsControl>("CelestialObjectList")!;
            ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 2;
            window.FindControl<TabControl>("SettingsCategories")!.SelectedIndex = 5;
            Settle(window);
            Assert.Equal(vm.CelestialObjects.Count, settings.GetVisualDescendants().OfType<CelestialLegendGlyph>().Count());
            Assert.All(settings.GetVisualDescendants().OfType<CelestialLegendGlyph>(), sample =>
                Assert.Equal(((Noctaxis.Desktop.ViewModels.CelestialObjectItemViewModel)sample.DataContext!).Role, sample.Role));
        }
        finally { Close(window); }
    }

    /// <summary>At twice the text size, the line samples keep their full length and are not cut by their rows.</summary>
    [AvaloniaFact]
    public async Task LineSamplesStayWholeAtTwiceTheTextSize()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        var service = ((App)Application.Current!).Themes!;
        try
        {
            service.Apply(new(TextScale: 2));
            Settle(window);
            var rows = window.FindControl<ItemsControl>("PlannerTargetRows")!;
            foreach (var item in vm.CelestialObjects.Where(item => item.IsVisible))
            {
                var row = rows.ContainerFromItem(item)!;
                var sample = row.GetVisualDescendants().OfType<CelestialLegendGlyph>().Single();
                Assert.Equal(CelestialLegendGlyph.SampleWidth, sample.Bounds.Width);
                var at = sample.TranslatePoint(new Point(), row)!.Value;
                Assert.True(at.X >= 0 && at.X + sample.Bounds.Width <= row.Bounds.Width + .5, $"{item.DisplayName}: the sample is cut");
                Assert.True(at.Y >= 0 && at.Y + sample.Bounds.Height <= row.Bounds.Height + .5, $"{item.DisplayName}: the sample is cut");
            }
        }
        finally { service.Apply(new()); Close(window); }
    }

    /// <summary>A colour set given to a chart or line sample stays when the theme changes; the theme's is only the default.</summary>
    [AvaloniaFact]
    public void AnExplicitTargetColourSetSurvivesAThemeChange()
    {
        var service = ((App)Application.Current!).Themes!;
        var own = CelestialColourSet.Default with { Objects = [.. Enumerable.Repeat(Colors.HotPink, CelestialThemeTokens.Objects.Count)] };
        var glyph = new CelestialLegendGlyph { CelestialColours = own };
        var graph = new HorizonGraph { CelestialColours = own, Width = 100, Height = 50 };
        var plain = new CelestialLegendGlyph();
        var window = new Window { Width = 200, Height = 120, Content = new StackPanel { Children = { glyph, graph, plain } } };
        try
        {
            window.Show();
            service.Apply(new(ColourVisionMode: ColourVisionMode.Tritanopia));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(own, glyph.CelestialColours);
            Assert.Same(own, graph.CelestialColours);
            // ...while one given nothing follows the theme.
            Assert.True(plain.CelestialColours.EmphasisePatterns);
        }
        finally { window.Close(); service.Apply(new()); }
    }
}
