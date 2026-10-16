using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping.Preview;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// A theme resource is only a custom control's default (<see cref="ThemeDefault"/>). A value set on the control,
/// or by a style, survives every later theme change; a control that sets nothing follows each change live. The
/// theme is applied after the controls are shown, as the app does at start-up and on every appearance change:
/// that is how the Camera frame's explicitly transparent direction pad once came to be painted over.
/// </summary>
public sealed class ThemeDefaultPrecedenceTests
{
    public sealed record Case(string Name, Func<Control> Create, AvaloniaProperty Property, object Explicit, object Styled, string Key)
    {
        public override string ToString() => Name;
    }

    private static NoctaxisMapView OfflineMap()
    {
        var map = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        map.MapControlForTesting.Map!.Layers.Clear();
        return map;
    }

    private static CelestialColourSet ColourSet(Color colour) =>
        CelestialColourSet.Default with { Objects = [.. Enumerable.Repeat(colour, CelestialThemeTokens.Objects.Count)] };

    private static readonly LocationPreviewStyle OwnPreview =
        LocationPreviewStyle.FromPalette(BuiltInPalettes.Definitions[0].Light!.Palette, light: true, highContrast: false);
    private static readonly LocationPreviewStyle StyledPreview =
        LocationPreviewStyle.FromPalette(BuiltInPalettes.Definitions[0].Dark!.Palette, light: false, highContrast: false);

    /// <summary>Every public property a custom control takes from the theme as a default.</summary>
    public static TheoryData<Case> Cases => new()
    {
        new Case("ChromeBackground", () => new TerrainFrameView(), ChromeDrawingControl.ChromeBackgroundProperty, Brushes.Transparent, Brushes.Teal, "SurfaceBackground"),
        new Case("ChromeForeground", () => new HorizonGraph(), ChromeDrawingControl.ChromeForegroundProperty, Brushes.HotPink, Brushes.Teal, "SecondaryText"),
        new Case("ChromeFontSize", () => new CameraDirectionPad(), ChromeDrawingControl.ChromeFontSizeProperty, 17.0, 23.0, "TextCaption"),
        new Case("TerrainFrameOutline", () => new TerrainFrameView(), TerrainFrameView.OutlineProperty, Brushes.HotPink, Brushes.Teal, "SurfaceBorder"),
        new Case("TimelineCursor", () => new HorizonGraph(), HorizonGraph.CursorBrushProperty, Brushes.HotPink, Brushes.Teal, "Accent"),
        new Case("TimelineDarkness", () => new HorizonGraph(), HorizonGraph.DarknessBrushProperty, Brushes.HotPink, Brushes.Teal, "Accent"),
        new Case("TimelineColours", () => new HorizonGraph(), HorizonGraph.CelestialColoursProperty, ColourSet(Colors.HotPink), ColourSet(Colors.Teal), CelestialThemeTokens.ColourSet),
        new Case("LineSampleColours", () => new CelestialLegendGlyph(), CelestialLegendGlyph.CelestialColoursProperty, ColourSet(Colors.HotPink), ColourSet(Colors.Teal), CelestialThemeTokens.ColourSet),
        new Case("CameraPadAccent", () => new CameraDirectionPad(), CameraDirectionPad.AccentProperty, Brushes.HotPink, Brushes.Teal, "Accent"),
        new Case("LogoFrame", () => new NoctaxisLogoMark(), NoctaxisLogoMark.FrameBrushProperty, Brushes.HotPink, Brushes.Teal, NoctaxisLogoMark.FrameResourceKey),
        new Case("LogoStar", () => new NoctaxisLogoMark(), NoctaxisLogoMark.StarBrushProperty, Brushes.HotPink, Brushes.Teal, NoctaxisLogoMark.StarResourceKey),
        new Case("LocationPreview", () => new LocationPreviewImage(), LocationPreviewImage.PreviewStyleProperty, OwnPreview, StyledPreview, ThemeService.LocationPreviewStyleKey),
        new Case("MapBackground", OfflineMap, Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, Brushes.HotPink, Brushes.Teal, MapThemeTokens.Surface),
    };

    /// <summary>Dark to light, light to dark, one family to another, a colour-vision mode, and a text size.</summary>
    private static readonly AppearancePreferences[] Transitions =
    [
        new(ThemeCatalogue.DefaultId, AppearanceMode: AppearanceMode.Light),
        new(ThemeCatalogue.DefaultId, AppearanceMode: AppearanceMode.Dark),
        new("builtin.highcontrast", AppearanceMode: AppearanceMode.Dark),
        new("builtin.highcontrast", ColourVisionMode.Deuteranopia, AppearanceMode: AppearanceMode.Light),
        new(ThemeCatalogue.DefaultId, ColourVisionMode.Tritanopia, AppearanceMode: AppearanceMode.Dark, TextScale: 1.5),
    ];

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void AnExplicitValueOrStyleWinsAndADefaultFollowsTheTheme(Case @case)
    {
        var service = ((App)Application.Current!).Themes!;
        service.Apply(new(ThemeCatalogue.DefaultId, AppearanceMode: AppearanceMode.Dark));
        // Set on the control, as markup or code would; left alone; and set by a style that names only the type.
        var local = @case.Create();
        local.SetValue(@case.Property, @case.Explicit);
        var plain = @case.Create();
        var styled = @case.Create();
        var withLocal = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { local, plain } } };
        var withStyle = new Window { Width = 400, Height = 300, Content = styled };
        withStyle.Styles.Add(new Style(x => x.OfType(styled.GetType())) { Setters = { new Setter(@case.Property, @case.Styled) } });
        try
        {
            withLocal.Show();
            withStyle.Show();
            Dispatcher.UIThread.RunJobs();
            void Check(string when)
            {
                Assert.True(Equals(@case.Explicit, local.GetValue(@case.Property)), $"{@case.Name} {when}: the explicit value was replaced");
                Assert.True(Equals(@case.Styled, styled.GetValue(@case.Property)), $"{@case.Name} {when}: the style was replaced");
                Assert.True(Application.Current!.TryFindResource(@case.Key, out var themed), $"{@case.Key} is not published");
                Assert.True(Equals(themed, plain.GetValue(@case.Property)), $"{@case.Name} {when}: the default did not follow the theme");
            }
            Check("once shown");
            foreach (var preferences in Transitions)
            {
                service.Apply(preferences);
                Dispatcher.UIThread.RunJobs();
                Check($"after {preferences.ThemeFamilyId}/{preferences.AppearanceMode}/{preferences.ColourVisionMode}/{preferences.TextScale}");
            }
            // Cleared, the explicit value gives way to the theme's again.
            local.ClearValue(@case.Property);
            Assert.True(Application.Current!.TryFindResource(@case.Key, out var current));
            Assert.True(Equals(current, local.GetValue(@case.Property)), $"{@case.Name}: a cleared value did not return to the theme");
        }
        finally
        {
            withLocal.Close();
            withStyle.Close();
            service.Apply(new());
        }
    }

    /// <summary>Each control above really is themed this way, from its constructor: none is left on a local DynamicResource.</summary>
    [Fact]
    public void NoCustomControlBindsAThemeDefaultAtLocalPriority()
    {
        var controls = Path.Combine(TestPaths.DesktopProject, "Controls");
        foreach (var file in Directory.EnumerateFiles(controls, "*.cs"))
        {
            var source = File.ReadAllText(file);
            foreach (var line in source.Split('\n').Where(line => line.Contains("DynamicResourceExtension(")))
                // Only a control's internal parts (the map's credit strip, a status line's icon and text) bind this way.
                Assert.Matches(@"^\s*(line|glyph|content|var colour)\b", line);
            Assert.DoesNotContain("BindingPriority.Template", source);
        }
    }
}
