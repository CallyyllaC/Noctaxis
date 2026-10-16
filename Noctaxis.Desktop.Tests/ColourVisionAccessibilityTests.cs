using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mapsui;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Colour-vision accessibility across every theme family, Light/Dark and colour-vision mode (56
/// combinations). Each palette is judged as its intended viewer sees it: a colour-vision variant under
/// simulation of its own deficiency, and the None palette with normal vision. Only meaningful semantic
/// pairs are enforced; decorative colours are not.
/// <para>
/// Thresholds follow the audit that introduced these tests: CIEDE2000 below 3 means two colours read as
/// the same (major), 3–10 is hard to tell apart (minor), and 10 or more is clearly distinct. Contrast uses
/// WCAG 2: 4.5:1 for text and 3:1 for data lines, focus rings and other meaningful graphics.
/// </para>
/// </summary>
public sealed class ColourVisionAccessibilityTests
{
    public static IEnumerable<object[]> Variants => AppearanceTests.Variants;

    private static readonly string[] Status = ["Success", "Warning", "Error", "Information"];

    private static IReadOnlyDictionary<string, string> Resolve(string id, ColourVisionMode mode, AppearanceMode appearance) =>
        new ThemeCatalogue(isAvailable: _ => true).Resolve(new(id, mode, AppearanceMode: appearance)).Palette;

    private static Color C(IReadOnlyDictionary<string, string> palette, string token) => Color.Parse(palette[token]);

    private static (string A, string B, double DeltaE) WorstPair(IReadOnlyDictionary<string, string> palette,
        IReadOnlyList<string> tokens, ColourVisionMode mode) =>
        (from a in Enumerable.Range(0, tokens.Count)
         from b in Enumerable.Range(a + 1, tokens.Count - a - 1)
         select (tokens[a], tokens[b], ColourScience.DeltaE(C(palette, tokens[a]), C(palette, tokens[b]), mode)))
        .MinBy(pair => pair.Item3);

    [Fact]
    public void EveryInstanceOfTheCountedMatrixIsCovered()
    {
        Assert.Equal(56, Variants.Count());
        Assert.Equal(7, BuiltInPalettes.Definitions.Count);
    }

    [Theory, MemberData(nameof(Variants))]
    public void EverySemanticRoleIsDefinedAndAuthored(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var resolved = new ThemeCatalogue(isAvailable: _ => true).Resolve(new(id, mode, AppearanceMode: appearance));
        foreach (var token in Status.Concat(Status.Select(s => s + "Background")).Concat(CelestialThemeTokens.All).Concat(MapThemeTokens.All).Append("FocusBorder"))
        {
            Assert.Contains(token, BuiltInPalettes.RequiredTokens);
            Assert.True(Color.TryParse(resolved.Palette[token], out _), $"{id}/{appearance}/{mode}: {token}");
        }
        Assert.Empty(resolved.InheritedTokens);
        Assert.Equal(mode, resolved.Mode);
    }

    [Theory, MemberData(nameof(Variants))]
    public void StatusColoursStayDistinctForTheirViewer(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        var (a, b, deltaE) = WorstPair(p, Status, mode);
        Assert.True(deltaE >= 10, $"{id}/{appearance}/{mode}: {a}/{b} ΔE00 {deltaE:F1} under {mode}");
        foreach (var role in Status)
        {
            Assert.True(ColourScience.Contrast(p[role], p[role + "Background"]) >= 4.5, $"{id}/{appearance}/{mode}: {role} text on its background");
            Assert.True(ColourScience.Contrast(p[role], p["SurfaceBackground"]) >= 4.5, $"{id}/{appearance}/{mode}: {role} text on the surface");
        }
    }

    [Theory, MemberData(nameof(Variants))]
    public void CelestialIdentitiesStayDistinctForTheirViewer(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        var (a, b, deltaE) = WorstPair(p, CelestialThemeTokens.Objects, mode);
        // Objects also differ by dash pattern and label, so colour is not the only cue; it must still separate clearly.
        Assert.True(deltaE >= 10, $"{id}/{appearance}/{mode}: {a}/{b} ΔE00 {deltaE:F1} under {mode}");
    }

    [Theory, MemberData(nameof(Variants))]
    public void NormalVisionCelestialColoursDoNotCollapseForUnconfiguredColourVision(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        if (mode != ColourVisionMode.None) return;
        var p = Resolve(id, mode, appearance);
        // A user who has not chosen a colour-vision mode still sees separable paths. The audit measured 1.9
        // for the previous colours under deuteranopia; 7.5 keeps the improvement from regressing.
        foreach (var deficiency in ColourScience.Deficiencies)
        {
            var (a, b, deltaE) = WorstPair(p, CelestialThemeTokens.Objects, deficiency);
            Assert.True(deltaE >= 7.5, $"{id}/{appearance}: {a}/{b} ΔE00 {deltaE:F1} under {deficiency}");
        }
    }

    [Theory, MemberData(nameof(Variants))]
    public void DataLinesAreVisibleOnTheChartAndMap(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        // The audit found 1.3–1.7:1 on Light themes. Paths and rays are meaningful graphics: 3:1.
        foreach (var token in CelestialThemeTokens.Objects)
        {
            Assert.True(ColourScience.Contrast(p[token], p["SurfaceBackground"]) >= 3, $"{id}/{appearance}/{mode}: {token} on the chart");
            Assert.True(ColourScience.Contrast(p[token], p[MapThemeTokens.Land]) >= 3, $"{id}/{appearance}/{mode}: {token} on map land");
        }
        Assert.True(ColourScience.Contrast(p[CelestialThemeTokens.ChartBelowHorizon], p["SurfaceBackground"]) >= 3, $"{id}/{appearance}/{mode}: below-horizon path");
        Assert.True(ColourScience.Contrast(p[CelestialThemeTokens.ChartTerrainOutline], p["SurfaceBackground"]) >= 2, $"{id}/{appearance}/{mode}: chart terrain outline");
        // Light themes no longer draw the dark-theme terrain block.
        var fill = ColourScience.Luminance(C(p, CelestialThemeTokens.ChartTerrainFill));
        Assert.True(appearance == AppearanceMode.Light ? fill > .5 : fill < .1, $"{id}/{appearance}: chart terrain fill luminance {fill:F2}");
    }

    [Theory, MemberData(nameof(Variants))]
    public void MapAreasStaySeparableForTheirViewer(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        var normal = Resolve(id, ColourVisionMode.None, appearance);
        (string, string)[] pairs =
        [
            (MapThemeTokens.Land, MapThemeTokens.Water), (MapThemeTokens.Land, MapThemeTokens.Vegetation),
            (MapThemeTokens.Land, MapThemeTokens.BuiltUp), (MapThemeTokens.Vegetation, MapThemeTokens.BuiltUp),
            (MapThemeTokens.Water, MapThemeTokens.Vegetation), (MapThemeTokens.Water, MapThemeTokens.BuiltUp),
            (MapThemeTokens.RoadMajor, MapThemeTokens.RoadMinor)
        ];
        foreach (var (a, b) in pairs)
        {
            // Basemap areas are deliberately subtle. A deficiency must not make a pair less separable than the
            // family's own normal-vision design (15% tolerance), and never below 5 where that design is above it.
            var designed = ColourScience.DeltaE(C(normal, a), C(normal, b));
            var required = Math.Min(5, .85 * designed);
            var seen = ColourScience.DeltaE(C(p, a), C(p, b), mode);
            Assert.True(seen >= required, $"{id}/{appearance}/{mode}: {a}/{b} ΔE00 {seen:F1} < {required:F1} (normal-vision design {designed:F1})");
        }
    }

    [Theory, MemberData(nameof(Variants))]
    public void MapStateSymbolsStayDistinctForTheirViewer(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        foreach (var (a, b) in new[] { (MapThemeTokens.PinOutline, MapThemeTokens.PinActivity), (MapThemeTokens.ViewStroke, MapThemeTokens.Bearing) })
        {
            var deltaE = ColourScience.DeltaE(C(p, a), C(p, b), mode);
            Assert.True(deltaE >= 10, $"{id}/{appearance}/{mode}: {a}/{b} ΔE00 {deltaE:F1}");
        }
    }

    [Theory, MemberData(nameof(Variants))]
    public void FocusRingIsVisibleOnEverySurface(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = Resolve(id, mode, appearance);
        foreach (var surface in new[] { "WindowBackground", "ApplicationBackground", "SurfaceBackground", "ElevatedSurface", "InputBackground", "CardBackground", "ButtonBackground" })
            Assert.True(ColourScience.Contrast(p["FocusBorder"], p[surface]) >= 3, $"{id}/{appearance}/{mode}: focus ring on {surface}");
    }

    [Theory, MemberData(nameof(Variants))]
    public void ColourVisionVariantsKeepTheFamilyIdentity(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        if (mode == ColourVisionMode.None) return;
        var p = Resolve(id, mode, appearance);
        var normal = Resolve(id, ColourVisionMode.None, appearance);
        // Accent, selection, focus, surfaces, chrome and every map role outside the re-authored areas and
        // state symbols are the family's own: no generic accessible palette replaces the theme.
        string[] reauthored = [.. Status, .. Status.Select(s => s + "Background"), .. CelestialThemeTokens.Objects,
            MapThemeTokens.Water, MapThemeTokens.Vegetation, MapThemeTokens.BuiltUp, MapThemeTokens.PinActivity, MapThemeTokens.Bearing];
        foreach (var token in BuiltInPalettes.RequiredTokens.Except(reauthored))
            Assert.Equal(normal[token], p[token]);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void EachDeficiencyHasItsOwnVariant(string id)
    {
        foreach (var appearance in new[] { AppearanceMode.Dark, AppearanceMode.Light })
        {
            var sets = ColourScience.Deficiencies.ToDictionary(m => m, m =>
            {
                var p = Resolve(id, m, appearance);
                return string.Join(",", Status.Concat(CelestialThemeTokens.Objects).Select(t => p[t]));
            });
            Assert.Equal(3, sets.Values.Distinct().Count());
            var normal = Resolve(id, ColourVisionMode.None, appearance);
            foreach (var mode in ColourScience.Deficiencies)
            {
                var p = Resolve(id, mode, appearance);
                Assert.Contains(CelestialThemeTokens.Objects, t => !string.Equals(normal[t], p[t], StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public static IEnumerable<object[]> Families => BuiltInPalettes.Definitions.Select(theme => new object[] { theme.Id });

    [Fact]
    public void CelestialObjectsDifferByPatternAsWellAsColour()
    {
        var roles = Enum.GetValues<CelestialRole>();
        Assert.Equal(CelestialThemeTokens.Objects.Count, roles.Length);
        Assert.Equal(roles.Length, roles.Select(role => string.Join(",", CelestialPalette.Dash(role).Dashes!)).Distinct().Count());
        var sun = new AstralTarget("sun", "Sun", AstralTargetCategory.Solar, 0, 0, "J2000");
        var moon = new AstralTarget("moon", "Moon", AstralTargetCategory.Lunar, 0, 0, "J2000");
        var galaxy = new AstralTarget("m31", "Andromeda Galaxy", AstralTargetCategory.Galaxy, 0, 0, "J2000", PrimaryIdentifier: "M31");
        Assert.Equal(CelestialRole.Sun, CelestialPalette.Role(sun, 4));
        Assert.Equal(CelestialRole.Moon, CelestialPalette.Role(moon, 0));
        Assert.Equal(CelestialRole.DeepSky3, CelestialPalette.Role(galaxy, 2));
        Assert.Equal(CelestialRole.DeepSky1, CelestialPalette.Role(galaxy, 5));
        Assert.Equal(("Sun", "Moon", "M31"), (CelestialPalette.ShortLabel(sun), CelestialPalette.ShortLabel(moon), CelestialPalette.ShortLabel(galaxy)));
    }
}
