using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Theme-to-map token resolution. Map code names semantic roles; every theme family, mode and
/// colour-vision variant must supply them with values that stay legible on real map content.
/// </summary>
public sealed class MapThemeTokenTests
{
    // Reference map content the symbols and chrome are drawn over. The street basemap is imagery and
    // is never recoloured, so these do not vary with the theme.
    private static readonly Color StreetLand = Color.Parse("#F2EFE9");
    private static readonly Color ReliefDarkest = Color.FromRgb(42, 42, 42);
    private static readonly Color ReliefMiddle = Color.FromRgb(129, 129, 129);

    public static IEnumerable<object[]> Variants => AppearanceTests.Variants;

    private static IReadOnlyDictionary<string, string> Resolve(string id, ColourVisionMode mode, AppearanceMode appearance) =>
        new ThemeCatalogue(isAvailable: _ => true).Resolve(new(id, mode, AppearanceMode: appearance)).Palette;

    [Fact]
    public void EveryMapRoleIsARequiredToken()
    {
        Assert.All(MapThemeTokens.All, token => Assert.Contains(token, BuiltInPalettes.RequiredTokens));
        Assert.Equal(MapThemeTokens.All.Count, MapThemeTokens.All.Distinct().Count());
    }

    [Theory, MemberData(nameof(Variants))]
    public void MapRolesAreAuthoredNotInherited(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var resolved = new ThemeCatalogue(isAvailable: _ => true).Resolve(new(id, mode, AppearanceMode: appearance));
        Assert.DoesNotContain(resolved.InheritedTokens, MapThemeTokens.All.Contains);
        var source = appearance == AppearanceMode.Light ? resolved.Definition.Light! : resolved.Definition.Dark;
        // Colour-vision variants may re-author only the roles that carry hue-based distinctions (areas and
        // the pin/minimap state symbols); every other role keeps the family value.
        string[] accessible = [MapThemeTokens.Water, MapThemeTokens.Vegetation, MapThemeTokens.BuiltUp, MapThemeTokens.PinActivity, MapThemeTokens.Bearing];
        foreach (var token in MapThemeTokens.All.Where(token => mode == ColourVisionMode.None || !accessible.Contains(token)))
            Assert.Equal(source.Palette[token], resolved.Palette[token]);
    }

    [Theory, MemberData(nameof(Variants))]
    public void MapSurfaceFollowsTheAppearanceMode(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var surface = Luminance(Color.Parse(Resolve(id, mode, appearance)[MapThemeTokens.Surface]));
        if (appearance == AppearanceMode.Dark) Assert.True(surface < .05, $"{id}: dark map surface is {surface:F3}");
        else Assert.True(surface > .5, $"{id}: light map surface is {surface:F3}");
    }

    [Theory, MemberData(nameof(Variants))]
    public void MapChromeStaysReadableOverAnyBasemap(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        var chrome = Color.Parse(p[MapThemeTokens.ChromeBackground]);
        // The chrome is translucent: judge the text on what the chrome becomes over bright and dark imagery.
        foreach (var underlay in new[] { Colors.White, Colors.Black, StreetLand })
        {
            var composite = Over(chrome, underlay);
            Assert.True(Contrast(Color.Parse(p[MapThemeTokens.ChromeText]), composite) >= 4.5, $"{id}/{mode}: map chrome text over {underlay}");
            Assert.True(Contrast(Color.Parse(p["SecondaryText"]), composite) >= 4.5, $"{id}/{mode}: secondary text over {underlay}");
            Assert.True(Contrast(Color.Parse(p["Accent"]), composite) >= 3, $"{id}/{mode}: accent label over {underlay}");
        }
        Assert.True(Contrast(Color.Parse(p[MapThemeTokens.ChromeBorder]), Over(chrome, StreetLand)) >= 3, $"{id}/{mode}: chrome border");
    }

    [Theory, MemberData(nameof(Variants))]
    public void ObserverPinIsLegibleOnTheBasemap(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        var fill = Color.Parse(p[MapThemeTokens.PinFill]);
        var outline = Color.Parse(p[MapThemeTokens.PinOutline]);
        var activity = Color.Parse(p[MapThemeTokens.PinActivity]);
        Assert.Equal(255, fill.A);
        Assert.Equal(255, outline.A);
        Assert.True(Contrast(fill, StreetLand) >= 4.5, $"{id}: pin body on street land");
        Assert.True(Contrast(outline, fill) >= 4.5, $"{id}: pin ring against its body");
        // The activity arc is drawn over a pin-fill halo, so that is the background it must read on.
        Assert.True(Contrast(Over(activity, fill), fill) >= 3, $"{id}: activity arc on its halo");
    }

    [Theory, MemberData(nameof(Variants))]
    public void MinimapSymbolsAreLegibleOnTheRelief(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        foreach (var token in new[] { MapThemeTokens.ViewStroke, MapThemeTokens.Bearing, MapThemeTokens.PinOutline })
        {
            var colour = Color.Parse(p[token]);
            Assert.True(Contrast(colour, ReliefMiddle) >= 2, $"{id}: {token} on mid relief");
            Assert.True(Contrast(colour, ReliefDarkest) >= 4.5, $"{id}: {token} on dark relief");
        }
        var fill = Color.Parse(p[MapThemeTokens.ViewFill]);
        Assert.InRange(fill.A, 1, 96); // A wash over the relief, never an opaque cover.
    }

    [Theory, MemberData(nameof(Variants))]
    public void MapSymbolsUseDistinctRolesInsteadOfOneAccentWash(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        var stroke = Color.Parse(p[MapThemeTokens.ViewStroke]);
        var bearing = Color.Parse(p[MapThemeTokens.Bearing]);
        Assert.True(HueDistance(stroke, bearing) >= 45, $"{id}: field of view and bearing share a hue");
        var outline = Color.Parse(p[MapThemeTokens.PinOutline]);
        var activity = Color.Parse(p[MapThemeTokens.PinActivity]);
        if (Saturation(outline) >= .25 && Saturation(activity) >= .25)
            Assert.True(HueDistance(outline, activity) >= 45, $"{id}: pin ring and activity share a hue");
        else Assert.NotEqual((outline.R, outline.G, outline.B), (activity.R, activity.G, activity.B));
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void MapSymbolsDoNotChangeBetweenLightAndDark(string id)
    {
        var definition = BuiltInPalettes.Definitions.Single(theme => theme.Id == id);
        foreach (var token in MapThemeTokens.Symbols)
            Assert.Equal(definition.Dark.Palette[token], definition.Light!.Palette[token]);
    }

    public static IEnumerable<object[]> Families => BuiltInPalettes.Definitions.Select(theme => new object[] { theme.Id });

    [Theory, MemberData(nameof(Families))]
    public void EveryBasemapRoleDiffersBetweenLightAndDark(string id)
    {
        var definition = BuiltInPalettes.Definitions.Single(theme => theme.Id == id);
        foreach (var token in MapThemeTokens.Basemap)
            Assert.NotEqual(definition.Dark.Palette[token], definition.Light!.Palette[token]);
        Assert.True(Luminance(Color.Parse(definition.Dark.Palette[MapThemeTokens.Land])) < .05, $"{id}: dark land");
        Assert.True(Luminance(Color.Parse(definition.Light!.Palette[MapThemeTokens.Land])) > .5, $"{id}: light land");
    }

    [Theory, MemberData(nameof(Variants))]
    public void MapCanvasMatchesTheBasemapLand(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        Assert.Equal(p[MapThemeTokens.Land], p[MapThemeTokens.Surface]);
    }

    [Theory, MemberData(nameof(Variants))]
    public void BasemapLabelsAndPointsAreReadable(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        Color C(string token) => Color.Parse(p[token]);
        var land = C(MapThemeTokens.Land);
        Assert.True(Contrast(C(MapThemeTokens.Label), C(MapThemeTokens.LabelHalo)) >= 4.5, $"{id}: label on halo");
        Assert.True(Contrast(C(MapThemeTokens.Label), land) >= 4.5, $"{id}: label on land");
        Assert.True(Contrast(C(MapThemeTokens.WaterLabel), C(MapThemeTokens.LabelHalo)) >= 4.5, $"{id}: water label on halo");
        Assert.True(Contrast(C(MapThemeTokens.Poi), land) >= 3, $"{id}: POI on land");
        Assert.True(Contrast(C(MapThemeTokens.Boundary), land) >= 2, $"{id}: boundary on land");
        Assert.True(Contrast(C(MapThemeTokens.Rail), land) >= 1.8, $"{id}: rail on land");
    }

    [Theory, MemberData(nameof(Variants))]
    public void BasemapGeographyKeepsItsHierarchy(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        Color C(string token) => Color.Parse(p[token]);
        var land = C(MapThemeTokens.Land);
        // Areas are subtle but always distinguishable from open land and from each other.
        foreach (var area in new[] { MapThemeTokens.Water, MapThemeTokens.Vegetation, MapThemeTokens.BuiltUp, MapThemeTokens.Building })
            Assert.True(Distance(C(area), land) >= 12, $"{id}: {area} is lost in the land");
        Assert.True(Distance(C(MapThemeTokens.Building), C(MapThemeTokens.BuiltUp)) >= 12, $"{id}: buildings vanish into built-up areas");
        // A road stands out by its fill or by its casing, whichever the theme relies on.
        double Salience(string fill) => Math.Max(Contrast(C(fill), land), Contrast(C(MapThemeTokens.RoadCasing), land));
        Assert.True(Contrast(C(MapThemeTokens.RoadMajor), land) >= 1.3, $"{id}: major road fill");
        Assert.True(Salience(MapThemeTokens.RoadMajor) >= 1.8, $"{id}: major roads");
        Assert.True(Salience(MapThemeTokens.RoadMinor) >= 1.2, $"{id}: minor roads");
        Assert.True(Salience(MapThemeTokens.RoadMajor) >= Salience(MapThemeTokens.RoadMinor), $"{id}: road hierarchy inverted");
        if (id == "builtin.highcontrast")
        {
            Assert.True(Contrast(C(MapThemeTokens.Label), land) >= 7, "High contrast labels");
            Assert.True(Contrast(C(MapThemeTokens.RoadMajor), land) >= 4.5, "High contrast major roads");
        }
    }

    [Theory, MemberData(nameof(Variants))]
    public void BasemapIsNotAnAccentWash(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        var accent = Color.Parse(p["Accent"]);
        Assert.True(MapThemeTokens.Basemap.Count(token => Color.Parse(p[token]) == accent) <= 1, $"{id}: several basemap roles are the accent");
        Assert.NotEqual(accent, Color.Parse(p[MapThemeTokens.RoadMajor]));
        // Land stays neutral: restrained theme hues belong to roads, boundaries and points.
        var land = Color.Parse(p[MapThemeTokens.Land]);
        Assert.True(new[] { land.R, land.G, land.B }.Max() - new[] { land.R, land.G, land.B }.Min() <= 28, $"{id}: tinted land");
        Assert.True(MapThemeTokens.Basemap.Select(token => p[token]).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 10,
            $"{id}: too few distinct basemap colours");
    }

    [Fact]
    public void NeonKeepsCyanLocationAndFuchsiaActivity()
    {
        var p = Resolve("supporter.noctaxis-neon", ColourVisionMode.None, AppearanceMode.Dark);
        Assert.InRange(Hue(Color.Parse(p[MapThemeTokens.PinOutline])), 170, 200);
        Assert.InRange(Hue(Color.Parse(p[MapThemeTokens.ViewStroke])), 170, 200);
        Assert.InRange(Hue(Color.Parse(p[MapThemeTokens.PinActivity])), 300, 340);
        Assert.InRange(Hue(Color.Parse(p[MapThemeTokens.Bearing])), 300, 340);
    }

    [Fact]
    public void FoxfireKeepsCopperViewAndEtherealCyanLocation()
    {
        var p = Resolve("supporter.foxfire", ColourVisionMode.None, AppearanceMode.Dark);
        Assert.InRange(Hue(Color.Parse(p[MapThemeTokens.ViewStroke])), 15, 45);
        Assert.InRange(Hue(Color.Parse(p[MapThemeTokens.PinActivity])), 15, 45);
        Assert.InRange(Hue(Color.Parse(p[MapThemeTokens.PinOutline])), 170, 200);
        Assert.InRange(Hue(Color.Parse(p[MapThemeTokens.Bearing])), 170, 200);
        // Foxfire's dark room stays neutral: the map canvas is not tinted copper.
        var surface = Color.Parse(p[MapThemeTokens.Surface]);
        Assert.True(new[] { surface.R, surface.G, surface.B }.Max() - new[] { surface.R, surface.G, surface.B }.Min() <= 8);
    }

    [Fact]
    public void NoctaxisDarkKeepsTheEstablishedMapSymbols()
    {
        var p = Resolve(ThemeCatalogue.DefaultId, ColourVisionMode.None, AppearanceMode.Dark);
        Assert.Equal("#0B0F17", p[MapThemeTokens.PinFill]);
        Assert.Equal("#FFFFFF", p[MapThemeTokens.PinOutline]);
        Assert.Equal("#DCAED8FF", p[MapThemeTokens.PinActivity]);
        Assert.Equal("#88DCF4", p[MapThemeTokens.ViewStroke]);
        Assert.Equal("#F5D070", p[MapThemeTokens.Bearing]);
    }

    [AvaloniaFact]
    public void MapElementsFollowTheSelectedThemeWithoutTouchingBasemapImagery()
    {
        var service = ((App)Application.Current!).Themes!;
        // Vector data from memory: theming must work, and be tested, without the network.
        var basemapTile = new VectorTileTestBuilder();
        basemapTile.Layer("water").Rectangle(0, 0, 2048, 4096);
        var vector = new Mapping.Vector.OpenFreeMapBasemap(new StaticFetcher(basemapTile.Build()));
        var view = new NoctaxisMapView(new Mapping.PlannerMapComposition(vector)) { Observer = new GeoCoordinate(53, -1) };
        var map = view.MapControlForTesting.Map!;
        var basemap = map.Layers.ToArray();
        var panel = new Border { Classes = { "mapChrome" }, Child = new TextBlock { Text = "Chrome" } };
        var button = new Button { Classes = { "mapChrome" }, Content = "Layers" };
        var window = new Window { Width = 800, Height = 600, Content = new Grid { Children = { view, panel, button } } };
        try
        {
            service.SetSupporterEntitlement(true);
            window.Show();
            foreach (var (id, appearance) in new[]
                     {
                         (ThemeCatalogue.DefaultId, AppearanceMode.Dark), ("supporter.noctaxis-neon", AppearanceMode.Dark),
                         ("supporter.foxfire", AppearanceMode.Light), ("builtin.highcontrast", AppearanceMode.Light)
                     })
            {
                service.Apply(new(id, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                var p = service.EffectiveTheme.Palette;
                Color Token(string token) => Color.Parse(p[token]);
                Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

                Assert.Equal(id, service.EffectiveTheme.Definition.Id);
                Assert.Equal(Token(MapThemeTokens.Surface), Brush(view.Background));
                Assert.Equal((Token(MapThemeTokens.PinFill), Token(MapThemeTokens.PinOutline), Token(MapThemeTokens.PinActivity)),
                    view.PinColoursForTesting);
                Assert.Equal(Token(MapThemeTokens.ChromeText), Brush(view.AttributionControlForTesting.Foreground));
                Assert.Equal(Token(MapThemeTokens.ChromeBackground), Brush(view.AttributionStripForTesting.Background));
                Assert.Equal(Token(MapThemeTokens.ChromeBackground), Brush(panel.Background));
                Assert.Equal(Token(MapThemeTokens.ChromeBorder), Brush(panel.BorderBrush));
                Assert.Equal(Token(MapThemeTokens.ChromeBackground), Brush(button.Background));
                Assert.Equal(Token(MapThemeTokens.ChromeText), Brush(button.Foreground));

                // The vector basemap itself takes the theme, drawn from the basemap roles...
                Assert.Equal(Mapping.Vector.BasemapStyle.FromPalette(p), vector.Style);
                // ...without rebuilding it: same native layers. Mapsui paints its own opaque background, which
                // takes the map surface so nothing white shows before tiles arrive.
                Assert.Equal(basemap, map.Layers.ToArray());
                var surface = Token(MapThemeTokens.Surface);
                Assert.Equal(new Mapsui.Styles.Color(surface.R, surface.G, surface.B, surface.A), map.BackColor);
            }
        }
        finally
        {
            window.Close();
            service.SetSupporterEntitlement(false);
            service.Apply(new());
        }
    }

    private sealed class StaticFetcher(byte[] tile) : Mapping.Vector.IVectorTileFetcher
    {
        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(tile);
    }

    private static double Distance(Color left, Color right) =>
        Math.Sqrt(Math.Pow(left.R - right.R, 2) + Math.Pow(left.G - right.G, 2) + Math.Pow(left.B - right.B, 2));

    private static Color Over(Color foreground, Color background)
    {
        var a = foreground.A / 255d;
        byte Mix(byte top, byte bottom) => (byte)Math.Round(top * a + bottom * (1 - a));
        return Color.FromRgb(Mix(foreground.R, background.R), Mix(foreground.G, background.G), Mix(foreground.B, background.B));
    }

    private static double Luminance(Color c)
    {
        static double Linear(byte b) { var v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
    }

    private static double Contrast(Color foreground, Color background)
    {
        var a = Luminance(foreground); var b = Luminance(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static double Saturation(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255d;
        var min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255d;
        return max == 0 ? 0 : (max - min) / max;
    }

    private static double Hue(Color c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        if (delta == 0) return 0;
        var hue = max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return hue < 0 ? hue + 360 : hue;
    }

    private static double HueDistance(Color left, Color right)
    {
        var distance = Math.Abs(Hue(left) - Hue(right)) % 360;
        return Math.Min(distance, 360 - distance);
    }
}
