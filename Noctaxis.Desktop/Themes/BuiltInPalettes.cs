using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Themes;

public static class BuiltInPalettes
{
    private static IReadOnlyList<IReadOnlyDictionary<string, string>> Authored { get; } =
    [
        new Dictionary<string, string>
        {
            ["WindowBackground"] = "#0B0F17",
            ["ApplicationBackground"] = "#0D131C",
            ["SurfaceBackground"] = "#131A25",
            ["SurfaceBackgroundHover"] = "#202A38",
            ["ElevatedSurface"] = "#111925",
            ["InputBackground"] = "#0F1621",
            ["SurfaceBorder"] = "#344156",
            ["SubtleBorder"] = "#273142",
            ["StrongBorder"] = "#68778E",
            ["PrimaryText"] = "#E8ECF2",
            ["SecondaryText"] = "#AAB5C5",
            ["MutedText"] = "#95A2B4",
            ["DisabledText"] = "#8794A7",
            ["InverseText"] = "#0B0F17",
            ["Accent"] = "#B4A9FF",
            ["AccentHover"] = "#C0B4FF",
            ["AccentPressed"] = "#A99CFF",
            ["AccentForeground"] = "#0B0F17",
            ["SelectionBackground"] = "#493D73",
            ["SelectionForeground"] = "#FFFFFF",
            ["FocusBorder"] = "#B8AEFF",
            ["HoverOverlay"] = "#202A38",
            ["PressedOverlay"] = "#293546",
            ["Success"] = "#83D9B1",
            ["SuccessBackground"] = "#142B24",
            ["Warning"] = "#E8C96A",
            ["WarningBackground"] = "#302817",
            ["Error"] = "#FF9A9F",
            ["ErrorBackground"] = "#3B1820",
            ["Information"] = "#9ACBFF",
            ["InformationBackground"] = "#172A40",
            // Celestial data colours: one role per object slot, chosen for separation and 3:1 on every dark surface and map.
            ["CelestialSun"] = "#F7B733", ["CelestialMoon"] = "#79C1FF", ["CelestialDeepSky1"] = "#B186FF", ["CelestialDeepSky2"] = "#27E3D1",
            ["CelestialDeepSky3"] = "#F890AE", ["CelestialDeepSky4"] = "#ADDA76", ["CelestialDeepSky5"] = "#FF804C",
            ["ChartBelowHorizon"] = "#6E7788", ["ChartTerrainFill"] = "#2A342E", ["ChartTerrainOutline"] = "#657563",
            ["MapChromeBackground"] = "#E6111722",
            ["MapChromeBorder"] = "#68778E",
            ["MapChromeText"] = "#E8ECF2",
            ["MapOverlayText"] = "#FFFFFF",
            ["MapOverlayShadow"] = "#000000",
            ["MapSurface"] = "#0D131C",
            ["MapPinFill"] = "#0B0F17",
            ["MapPinOutline"] = "#FFFFFF",
            ["MapPinActivity"] = "#DCAED8FF",
            ["MapViewFill"] = "#245FCBEA",
            ["MapViewStroke"] = "#88DCF4",
            ["MapBearing"] = "#F5D070",
            ["MapLand"] = "#0D131C",
            ["MapBuiltUp"] = "#171D29",
            ["MapBuilding"] = "#1F2634",
            ["MapWater"] = "#0E2335",
            ["MapVegetation"] = "#122019",
            ["MapRoadMinor"] = "#2A3242",
            ["MapRoadMajor"] = "#5B5480",
            ["MapRoadCasing"] = "#0A0D13",
            ["MapRail"] = "#4A5263",
            ["MapBoundary"] = "#7A6FB0",
            ["MapLabel"] = "#C9CFDA",
            ["MapLabelHalo"] = "#0B0F17",
            ["MapWaterLabel"] = "#7F9CC0",
            ["MapPoi"] = "#9E93E0",
            ["ButtonBackground"] = "#202A38",
            ["ButtonHover"] = "#293546",
            ["ButtonPressed"] = "#344156",
            ["ButtonDisabled"] = "#131A25",
            ["ToggleTrack"] = "#68778E",
            ["ToggleThumb"] = "#E8ECF2",
            ["SliderTrack"] = "#68778E",
            ["SliderFill"] = "#B4A9FF",
            ["CardBackground"] = "#101827",
            ["CardHoverOverlay"] = "#10FFFFFF",
            ["CardPressedOverlay"] = "#20FFFFFF",
        },
        new Dictionary<string, string>
        {
            ["WindowBackground"] = "#F4F6FA",
            ["ApplicationBackground"] = "#F4F6FA",
            ["SurfaceBackground"] = "#FFFFFF",
            ["SurfaceBackgroundHover"] = "#E6EAF2",
            ["ElevatedSurface"] = "#FFFFFF",
            ["InputBackground"] = "#FFFFFF",
            ["SurfaceBorder"] = "#69778B",
            ["SubtleBorder"] = "#B6BECC",
            ["StrongBorder"] = "#536177",
            ["PrimaryText"] = "#172334",
            ["SecondaryText"] = "#425169",
            ["MutedText"] = "#526178",
            ["DisabledText"] = "#59677B",
            ["InverseText"] = "#FFFFFF",
            ["Accent"] = "#59409B",
            ["AccentHover"] = "#493285",
            ["AccentPressed"] = "#3C286E",
            ["AccentForeground"] = "#FFFFFF",
            ["SelectionBackground"] = "#DED5F7",
            ["SelectionForeground"] = "#24133E",
            ["FocusBorder"] = "#59409B",
            ["HoverOverlay"] = "#E6EAF2",
            ["PressedOverlay"] = "#D5DDEA",
            ["Success"] = "#17613F",
            ["SuccessBackground"] = "#E0F4E9",
            ["Warning"] = "#705000",
            ["WarningBackground"] = "#FFF2C9",
            ["Error"] = "#A21C37",
            ["ErrorBackground"] = "#FFE7EC",
            ["Information"] = "#19588F",
            ["InformationBackground"] = "#E4F1FF",
            // Darker data colours for pale surfaces: 3:1 on every light surface and map.
            ["CelestialSun"] = "#744A00", ["CelestialMoon"] = "#1F76C1", ["CelestialDeepSky1"] = "#6537C5", ["CelestialDeepSky2"] = "#007870",
            ["CelestialDeepSky3"] = "#B0306A", ["CelestialDeepSky4"] = "#518118", ["CelestialDeepSky5"] = "#D74412",
            ["ChartBelowHorizon"] = "#7C8594", ["ChartTerrainFill"] = "#DCE3D8", ["ChartTerrainOutline"] = "#7D8C7A",
            ["MapChromeBackground"] = "#F2FFFFFF",
            ["MapChromeBorder"] = "#536177",
            ["MapChromeText"] = "#172334",
            ["MapOverlayText"] = "#FFFFFF",
            ["MapOverlayShadow"] = "#000000",
            ["MapSurface"] = "#E6EAF2",
            // Map symbols are drawn on the basemap and relief, not on this palette's surfaces.
            ["MapPinFill"] = "#0B0F17",
            ["MapPinOutline"] = "#FFFFFF",
            ["MapPinActivity"] = "#DCAED8FF",
            ["MapViewFill"] = "#245FCBEA",
            ["MapViewStroke"] = "#88DCF4",
            ["MapBearing"] = "#F5D070",
            ["MapLand"] = "#E6EAF2",
            ["MapBuiltUp"] = "#DADFE9",
            ["MapBuilding"] = "#CDD3DF",
            ["MapWater"] = "#B7CCE4",
            ["MapVegetation"] = "#D3E3D3",
            ["MapRoadMinor"] = "#FFFFFF",
            ["MapRoadMajor"] = "#C9BCEB",
            ["MapRoadCasing"] = "#A6AEBD",
            ["MapRail"] = "#8E97A8",
            ["MapBoundary"] = "#7B64B8",
            ["MapLabel"] = "#1E2838",
            ["MapLabelHalo"] = "#F4F6FA",
            ["MapWaterLabel"] = "#2F5580",
            ["MapPoi"] = "#59409B",
            ["ButtonBackground"] = "#E6EAF2",
            ["ButtonHover"] = "#D5DDEA",
            ["ButtonPressed"] = "#C4CDDC",
            ["ButtonDisabled"] = "#EEF0F5",
            ["ToggleTrack"] = "#536177",
            ["ToggleThumb"] = "#FFFFFF",
            ["SliderTrack"] = "#69778B",
            ["SliderFill"] = "#59409B",
            ["CardBackground"] = "#FFFFFF",
            ["CardHoverOverlay"] = "#10000000",
            ["CardPressedOverlay"] = "#20000000",
        },
        new Dictionary<string, string>
        {
            ["WindowBackground"] = "#000000",
            ["ApplicationBackground"] = "#000000",
            ["SurfaceBackground"] = "#000000",
            ["SurfaceBackgroundHover"] = "#202020",
            ["ElevatedSurface"] = "#000000",
            ["InputBackground"] = "#000000",
            ["SurfaceBorder"] = "#FFFFFF",
            ["SubtleBorder"] = "#FFFFFF",
            ["StrongBorder"] = "#FFFFFF",
            ["PrimaryText"] = "#FFFFFF",
            ["SecondaryText"] = "#FFFFFF",
            ["MutedText"] = "#E6E6E6",
            ["DisabledText"] = "#CFCFCF",
            ["InverseText"] = "#000000",
            ["Accent"] = "#FFFF00",
            ["AccentHover"] = "#FFFF99",
            ["AccentPressed"] = "#FFFF00",
            ["AccentForeground"] = "#000000",
            ["SelectionBackground"] = "#FFFF00",
            ["SelectionForeground"] = "#000000",
            ["FocusBorder"] = "#00FFFF",
            ["HoverOverlay"] = "#202020",
            ["PressedOverlay"] = "#333333",
            ["Success"] = "#8CFFB1",
            ["SuccessBackground"] = "#000000",
            ["Warning"] = "#FFFF00",
            ["WarningBackground"] = "#000000",
            ["Error"] = "#FFA8BA",
            ["ErrorBackground"] = "#000000",
            ["Information"] = "#8DDFFF",
            ["InformationBackground"] = "#000000",
            ["CelestialSun"] = "#F7B733", ["CelestialMoon"] = "#79C1FF", ["CelestialDeepSky1"] = "#B186FF", ["CelestialDeepSky2"] = "#27E3D1",
            ["CelestialDeepSky3"] = "#F890AE", ["CelestialDeepSky4"] = "#ADDA76", ["CelestialDeepSky5"] = "#FF804C",
            ["ChartBelowHorizon"] = "#BFBFBF", ["ChartTerrainFill"] = "#1A1A1A", ["ChartTerrainOutline"] = "#FFFFFF",
            ["MapChromeBackground"] = "#FF000000",
            ["MapChromeBorder"] = "#FFFFFF",
            ["MapChromeText"] = "#FFFFFF",
            ["MapOverlayText"] = "#FFFFFF",
            ["MapOverlayShadow"] = "#000000",
            ["MapSurface"] = "#000000",
            ["MapPinFill"] = "#000000",
            ["MapPinOutline"] = "#FFFFFF",
            ["MapPinActivity"] = "#00FFFF",
            ["MapViewFill"] = "#3000FFFF",
            ["MapViewStroke"] = "#00FFFF",
            ["MapBearing"] = "#FFFF00",
            ["MapLand"] = "#000000",
            ["MapBuiltUp"] = "#141414",
            ["MapBuilding"] = "#262626",
            ["MapWater"] = "#003A5C",
            ["MapVegetation"] = "#0A2A0A",
            ["MapRoadMinor"] = "#BFBFBF",
            ["MapRoadMajor"] = "#FFD24D",
            ["MapRoadCasing"] = "#000000",
            ["MapRail"] = "#FFFFFF",
            ["MapBoundary"] = "#00FFFF",
            ["MapLabel"] = "#FFFFFF",
            ["MapLabelHalo"] = "#000000",
            ["MapWaterLabel"] = "#8DDFFF",
            ["MapPoi"] = "#00FFFF",
            ["ButtonBackground"] = "#000000",
            ["ButtonHover"] = "#202020",
            ["ButtonPressed"] = "#333333",
            ["ButtonDisabled"] = "#000000",
            ["ToggleTrack"] = "#FFFFFF",
            ["ToggleThumb"] = "#000000",
            ["SliderTrack"] = "#FFFFFF",
            ["SliderFill"] = "#FFFF00",
            ["CardBackground"] = "#000000",
            ["CardHoverOverlay"] = "#00FFFFFF",
            ["CardPressedOverlay"] = "#20FFFF00",
        },
    ];
    public static IReadOnlyList<string> RequiredTokens { get; } = [.. Authored[0].Keys, .. LocationCardTokens.All];
    private static ThemePaletteDefinition Mode(string family, IReadOnlyDictionary<string, string> authored, bool light)
    {
        // Location-card roles are derived from the palette's own roles unless the family authors them.
        var palette = new Dictionary<string, string>(authored);
        LocationCardTokens.Derive(palette, light, family == "builtin.highcontrast");
        return new(palette, ThemeCatalogue.AllModes.ToDictionary(m => m, m =>
        {
            var variant = new Dictionary<string, string>(palette);
            ApplySemanticVariant(variant, m, light, family);
            return (IReadOnlyDictionary<string, string>)variant;
        }));
    }

    // Colour-vision variants. Each deficiency has its own authored sets, measured by simulating that
    // deficiency (Machado et al. 2009, full severity) and CIEDE2000 separation; see
    // docs/colour-vision-accessibility.md. Sets start from the normal-vision colours and move only as
    // far as separation needs, so a variant still looks like its theme.
    private static readonly string[] StatusRoles = ["Success", "Warning", "Error", "Information"];
    private static string[]? StatusSet(ColourVisionMode mode, bool light, bool highContrast) => (mode, light, highContrast) switch
    {
        (ColourVisionMode.Protanopia, false, false) => ["#83D9B1", "#EAC75C", "#F691A0", "#9ACBFF"],
        (ColourVisionMode.Deuteranopia, false, false) => ["#6ADAC6", "#EFCD60", "#FC9A9F", "#9ACBFF"],
        (ColourVisionMode.Tritanopia, false, false) => ["#81D9B1", "#E8C96A", "#FF8A93", "#BABEFF"],
        (ColourVisionMode.Protanopia, false, true) => ["#8DFEB4", "#FFFF00", "#FDA6B6", "#8FDBFF"],
        (ColourVisionMode.Deuteranopia, false, true) => ["#8CFFB1", "#FFFF00", "#F4A4BE", "#8FDBFF"],
        (ColourVisionMode.Tritanopia, false, true) => ["#8CFFB1", "#FFFF00", "#FFA8BA", "#7EB4CF"],
        (ColourVisionMode.Protanopia, true, _) => ["#207657", "#705000", "#A21C37", "#19588F"],
        (ColourVisionMode.Deuteranopia, true, _) => ["#055839", "#705000", "#C81F5F", "#18588F"],
        (ColourVisionMode.Tritanopia, true, _) => ["#17613F", "#694F01", "#A21B37", "#0063D4"],
        _ => null
    };
    // CelestialThemeTokens.Objects order: Sun, Moon, then the five deep-sky slots.
    private static string[]? CelestialSet(ColourVisionMode mode, bool light) => (mode, light) switch
    {
        (ColourVisionMode.Protanopia, false) => ["#E39E1A", "#7FC5FF", "#AA81FF", "#4FD9CA", "#F48FB1", "#B7E37D", "#E76D47"],
        (ColourVisionMode.Deuteranopia, false) => ["#FDBE06", "#86C2FF", "#9C73F8", "#31C5B8", "#FF8BAA", "#CBEBA4", "#DF8A5D"],
        (ColourVisionMode.Tritanopia, false) => ["#FFE241", "#56A2E4", "#BD8DFF", "#63E6D3", "#FF6095", "#87B058", "#FFAB78"],
        (ColourVisionMode.Protanopia, true) => ["#5E4127", "#2774D0", "#612EB9", "#00796B", "#AF3168", "#439206", "#FC1200"],
        (ColourVisionMode.Deuteranopia, true) => ["#8D5B01", "#00579B", "#8D3DFF", "#008380", "#B32A63", "#2C5622", "#F53200"],
        (ColourVisionMode.Tritanopia, true) => ["#865801", "#0B75C6", "#6F3CCB", "#00574C", "#CF257D", "#577C1A", "#D81200"],
        _ => null
    };
    // Basemap areas that collapse under a deficiency are nudged apart; roles that stay distinguishable
    // keep their family values. Land/built-up is a lightness step and is unaffected by colour vision.
    private static readonly Dictionary<(string Family, bool Light, ColourVisionMode Mode), (string Key, string Value)[]> MapAreaOverrides = new()
    {
        [("builtin.noctaxis", false, ColourVisionMode.Protanopia)] = [(MapThemeTokens.Water, "#0A2439"), (MapThemeTokens.Vegetation, "#132019"), (MapThemeTokens.BuiltUp, "#1A1F2A")],
        [("builtin.noctaxis", false, ColourVisionMode.Deuteranopia)] = [(MapThemeTokens.Water, "#0D2335"), (MapThemeTokens.BuiltUp, "#1B2029")],
        [("builtin.noctaxis", false, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Water, "#11273A"), (MapThemeTokens.Vegetation, "#13221A"), (MapThemeTokens.BuiltUp, "#1C1E28")],
        [("builtin.noctaxis", true, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Vegetation, "#D0E2D2"), (MapThemeTokens.BuiltUp, "#D9DCE4")],
        [("supporter.noctaxis-neon", false, ColourVisionMode.Protanopia)] = [(MapThemeTokens.Water, "#082937"), (MapThemeTokens.Vegetation, "#101E1B"), (MapThemeTokens.BuiltUp, "#0F1621")],
        [("supporter.noctaxis-neon", false, ColourVisionMode.Deuteranopia)] = [(MapThemeTokens.Water, "#002737"), (MapThemeTokens.Vegetation, "#121F1A")],
        [("supporter.noctaxis-neon", false, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Water, "#062A38"), (MapThemeTokens.Vegetation, "#0A1D1A"), (MapThemeTokens.BuiltUp, "#131822")],
        [("supporter.noctaxis-neon", true, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Vegetation, "#CEE9DE"), (MapThemeTokens.BuiltUp, "#D7E2E5")],
        [("supporter.foxfire", false, ColourVisionMode.Protanopia)] = [(MapThemeTokens.Water, "#0C1D27"), (MapThemeTokens.Vegetation, "#161D14"), (MapThemeTokens.BuiltUp, "#1B191A")],
        [("supporter.foxfire", false, ColourVisionMode.Deuteranopia)] = [(MapThemeTokens.Vegetation, "#151D14"), (MapThemeTokens.BuiltUp, "#1C1A1D")],
        [("supporter.foxfire", false, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Vegetation, "#151D16"), (MapThemeTokens.BuiltUp, "#1B191E")],
        [("supporter.foxfire", true, ColourVisionMode.Protanopia)] = [(MapThemeTokens.Vegetation, "#DBE2C3"), (MapThemeTokens.BuiltUp, "#E5D5C9")],
        [("supporter.foxfire", true, ColourVisionMode.Deuteranopia)] = [(MapThemeTokens.Vegetation, "#DDE3BE"), (MapThemeTokens.BuiltUp, "#E4D5C9")],
        [("supporter.deep-space", false, ColourVisionMode.Protanopia)] = [(MapThemeTokens.Water, "#09193B"), (MapThemeTokens.Vegetation, "#152128"), (MapThemeTokens.BuiltUp, "#11172A")],
        [("supporter.deep-space", false, ColourVisionMode.Deuteranopia)] = [(MapThemeTokens.Vegetation, "#17232A"), (MapThemeTokens.BuiltUp, "#111629")],
        [("supporter.deep-space", false, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Water, "#102044"), (MapThemeTokens.Vegetation, "#0A181F"), (MapThemeTokens.BuiltUp, "#1C1D2F")],
        [("supporter.deep-space", true, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Vegetation, "#CEE3D9"), (MapThemeTokens.BuiltUp, "#D4D9E7")],
        [("supporter.moonlit", false, ColourVisionMode.Protanopia)] = [(MapThemeTokens.Water, "#15263A"), (MapThemeTokens.Vegetation, "#1E2D25"), (MapThemeTokens.BuiltUp, "#252A35")],
        [("supporter.moonlit", false, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Water, "#132538"), (MapThemeTokens.Vegetation, "#212F28"), (MapThemeTokens.BuiltUp, "#272933")],
        [("supporter.moonlit", true, ColourVisionMode.Tritanopia)] = [(MapThemeTokens.Vegetation, "#D3E5E0"), (MapThemeTokens.BuiltUp, "#D4D9DF")],
    };
    // Map symbols are shared by a family's Light and Dark palettes, so these apply to both.
    private static readonly Dictionary<(string Family, ColourVisionMode Mode), (string Key, string Value)[]> MapSymbolOverrides = new()
    {
        // A white ring and a cyan activity arc look alike without red cones; amber keeps the arc distinct.
        [("builtin.highcontrast", ColourVisionMode.Protanopia)] = [(MapThemeTokens.PinActivity, "#FFC400")],
        // A cyan field of view and a pink bearing look alike without green cones; the bearing turns amber.
        [("supporter.noctaxis-neon", ColourVisionMode.Deuteranopia)] = [(MapThemeTokens.Bearing, "#FFD36B")],
    };
    private static IReadOnlyDictionary<string, string> HighContrastLight()
    {
        // Deliberately authored white-dominant counterpart; no RGB inversion.
        var p = new Dictionary<string, string>(Authored[1]);
        foreach (var key in new[] { "WindowBackground", "ApplicationBackground", "SurfaceBackground", "ElevatedSurface", "InputBackground", "ButtonBackground", "ButtonDisabled", "CardBackground" }) p[key] = "#FFFFFF";
        foreach (var key in new[] { "SurfaceBorder", "SubtleBorder", "StrongBorder", "PrimaryText", "SecondaryText", "MapChromeBorder", "MapChromeText", "ToggleTrack", "SliderTrack" }) p[key] = "#000000";
        p["MutedText"] = "#333333"; p["DisabledText"] = "#595959";
        p["Accent"] = "#000000"; p["AccentHover"] = "#202020"; p["AccentPressed"] = "#000000";
        p["AccentForeground"] = "#FFFFFF"; p["SelectionBackground"] = "#000000"; p["SelectionForeground"] = "#FFFFFF";
        p["FocusBorder"] = "#005FCC"; p["SliderFill"] = "#000000";
        p["ButtonHover"] = "#E0E0E0"; p["ButtonPressed"] = "#C0C0C0";
        p["SurfaceBackgroundHover"] = "#E0E0E0"; p["HoverOverlay"] = "#E0E0E0"; p["PressedOverlay"] = "#C0C0C0";
        p["MapChromeBackground"] = "#FFFFFFFF";
        p["MapSurface"] = "#FFFFFF";
        p["ChartBelowHorizon"] = "#595959"; p["ChartTerrainFill"] = "#E6E6E6"; p["ChartTerrainOutline"] = "#000000";
        // The same structural symbols as High Contrast Dark: they sit on map content, not on white.
        foreach (var key in MapThemeTokens.Symbols) p[key] = Authored[2][key];
        // Legibility first: black text and dark roads on a white map, strong blue water and boundaries.
        foreach (var (key, value) in MapThemeTokens.Basemap.Zip(HighContrastLightBasemap)) p[key] = value;
        p["CardHoverOverlay"] = "#00000000"; p["CardPressedOverlay"] = "#20000000";
        foreach (var role in new[] { "Success", "Warning", "Error", "Information" }) p[role + "Background"] = "#FFFFFF";
        return p;
    }
    private static IReadOnlyDictionary<string, string> Palette(int source, params (string Key, string Value)[] overrides) =>
        Palette(source, [], overrides);
    private static IReadOnlyDictionary<string, string> Palette(int source, (string Key, string Value)[] map,
        params (string Key, string Value)[] overrides)
    {
        var palette = new Dictionary<string, string>(Authored[source]);
        foreach (var (key, value) in map.Concat(overrides)) palette[key] = value;
        return palette;
    }
    /// <summary>
    /// A family's map roles for one mode. The symbol set is shared by both modes of a family because
    /// it is drawn on map content; the wedge fill is the stroke at a fixed low opacity.
    /// </summary>
    private static (string Key, string Value)[] MapRoles(string chromeBackground,
        (string PinFill, string PinOutline, string PinActivity, string ViewStroke, string Bearing) symbols,
        string[] basemap) =>
    [
        // The canvas matches the land colour, so nothing flashes while tiles load.
        (MapThemeTokens.Surface, basemap[0]), (MapThemeTokens.ChromeBackground, chromeBackground),
        .. MapThemeTokens.Basemap.Zip(basemap),
        (MapThemeTokens.PinFill, symbols.PinFill), (MapThemeTokens.PinOutline, symbols.PinOutline),
        (MapThemeTokens.PinActivity, symbols.PinActivity), (MapThemeTokens.ViewStroke, symbols.ViewStroke),
        (MapThemeTokens.ViewFill, "#24" + symbols.ViewStroke.TrimStart('#')), (MapThemeTokens.Bearing, symbols.Bearing)
    ];
    // Location identity (pin ring) and activity/bearing roles use different hues, so no single accent
    // floods the map: Neon cyan/fuchsia, Foxfire ethereal cyan/copper, Deep Space starlight/gold.
    private static readonly (string, string, string, string, string) NeonMap = ("#080A0F", "#38E8FF", "#FF3DBD", "#38E8FF", "#FF9BDD");
    private static readonly (string, string, string, string, string) FoxfireMap = ("#0D0E12", "#9DE7F0", "#FF8A3D", "#FFB15C", "#9DE7F0");
    private static readonly (string, string, string, string, string) DeepSpaceMap = ("#050711", "#AFC8FF", "#F5D070", "#B8C4FF", "#F5D070");
    private static readonly (string, string, string, string, string) MoonlitMap = ("#171A20", "#EDF1F5", "#C5B9E5", "#BFCDE6", "#F0D8A0");
    // Basemap roles in MapThemeTokens.Basemap order: land, built-up, building, water, vegetation,
    // minor road, major road, road casing, rail, boundary, label, label halo, water label, POI.
    // Dark maps keep neutral land and let one restrained family hue mark roads, boundaries and POIs;
    // light maps use pale land with darker roads and labels. No role is simply the Accent.
    private static readonly string[] HighContrastLightBasemap =
        ["#FFFFFF", "#EBEBEB", "#D0D0D0", "#9FD3FF", "#CFEFCF", "#FFFFFF", "#333333", "#1A1A1A", "#262626", "#005FCC", "#000000", "#FFFFFF", "#003B80", "#005FCC"];
    private static readonly string[] NeonDarkBasemap =
        ["#0B0E14", "#121822", "#1A2230", "#062532", "#0E1D1A", "#26303D", "#1F6F80", "#05070B", "#5A4A5E", "#B8398F", "#D8E1EA", "#080A0F", "#4FB7CC", "#FF6FCB"];
    private static readonly string[] NeonLightBasemap =
        ["#E4F0F3", "#D8E6EA", "#C9D9DE", "#A8DCE8", "#D2E8DC", "#FFFFFF", "#8FD3E0", "#8FA7AF", "#9C7A94", "#B3167A", "#101820", "#F7FAFC", "#00566A", "#B3167A"];
    private static readonly string[] FoxfireDarkBasemap =
        ["#101014", "#18181D", "#212127", "#0D1D26", "#161D15", "#2C2A2E", "#6E4A36", "#09090C", "#55525A", "#6FA9B0", "#E6E1E8", "#0D0E12", "#7FB6C4", "#FF9A55"];
    private static readonly string[] FoxfireLightBasemap =
        ["#F2E4D8", "#EAD9CB", "#DCC8B7", "#B9D6DD", "#D9E2C8", "#FFFDF8", "#E3A77E", "#A58C79", "#8D7766", "#167C8B", "#261812", "#FAF5EE", "#1F5E6B", "#A64B20"];
    private static readonly string[] DeepSpaceDarkBasemap =
        ["#080B18", "#0F1528", "#172040", "#0B1A3A", "#0D1A22", "#1E2742", "#4A57A8", "#04060D", "#3F4A6E", "#8295FF", "#D7DEF5", "#050711", "#8FA8E8", "#F5D070"];
    private static readonly string[] DeepSpaceLightBasemap =
        ["#E2E8F4", "#D6DDEC", "#C7D0E3", "#B4C6EC", "#D2E3DA", "#FFFFFF", "#AEB7EA", "#8C97B4", "#7C86A3", "#4B55A7", "#101B33", "#F2F6FC", "#2B3F87", "#8A5A00"];
    private static readonly string[] MoonlitDarkBasemap =
        ["#1C2028", "#232833", "#2C323E", "#172638", "#1D2C24", "#363D4A", "#6C7B93", "#13161B", "#58606D", "#A99CCF", "#DDE3EA", "#171A20", "#8FA6C4", "#C5B9E5"];
    private static readonly string[] MoonlitLightBasemap =
        ["#E1E5EA", "#D6DBE1", "#C8CED6", "#BACADD", "#D3DDD6", "#FFFFFF", "#B3BFD1", "#8F99A6", "#7D8795", "#596E9A", "#202733", "#EEF0F3", "#3A5578", "#566E91"];
    public static IReadOnlyList<ThemeDefinition> Definitions { get; } =
    [
        new("builtin.noctaxis", "Noctaxis", false, Mode("builtin.noctaxis", Authored[0], false), Mode("builtin.noctaxis", Authored[1], true), "Noctaxis appearance"),
        new("builtin.highcontrast", "High Contrast", false, Mode("builtin.highcontrast", Authored[2], false), Mode("builtin.highcontrast", HighContrastLight(), true), "Strong contrast and structural states"),
        new("supporter.noctaxis-neon", "Noctaxis Neon", true,
            Mode("supporter.noctaxis-neon", Palette(0, MapRoles("#E611151D", NeonMap, NeonDarkBasemap),
                ("WindowBackground", "#080A0F"), ("ApplicationBackground", "#0B0E14"), ("SurfaceBackground", "#11151D"),
                ("SurfaceBackgroundHover", "#1B2530"), ("ElevatedSurface", "#171D27"), ("InputBackground", "#0D121A"),
                ("SurfaceBorder", "#3A4858"), ("SubtleBorder", "#252F3B"), ("StrongBorder", "#72879B"),
                ("PrimaryText", "#F5F7FA"), ("SecondaryText", "#B7C1CC"), ("MutedText", "#9CA8B5"), ("DisabledText", "#7E8A98"),
                ("Accent", "#38E8FF"), ("AccentHover", "#FF3DBD"), ("AccentPressed", "#D62998"), ("AccentForeground", "#00151A"),
                ("SelectionBackground", "#005A70"), ("SelectionForeground", "#FFFFFF"), ("FocusBorder", "#00D9FF"),
                ("HoverOverlay", "#1B2530"), ("PressedOverlay", "#29343F"), ("MapChromeBorder", "#72879B"), ("MapChromeText", "#F5F7FA"),
                ("ButtonBackground", "#1A1D2E"), ("ButtonHover", "#382036"), ("ButtonPressed", "#482640"), ("ButtonDisabled", "#11151D"), ("CardHoverOverlay", "#18FF3DBD"), ("CardPressedOverlay", "#28FF3DBD"),
                ("ToggleTrack", "#72879B"), ("SliderTrack", "#72879B"), ("SliderFill", "#FF3DBD"), ("CardBackground", "#10161E")), false),
            Mode("supporter.noctaxis-neon", Palette(1, MapRoles("#F2FFFFFF", NeonMap, NeonLightBasemap),
                ("WindowBackground", "#F7FAFC"), ("ApplicationBackground", "#F7FAFC"), ("SurfaceBackground", "#FFFFFF"),
                ("SurfaceBackgroundHover", "#E8F5F8"), ("ElevatedSurface", "#FFFFFF"), ("InputBackground", "#FFFFFF"),
                ("SurfaceBorder", "#58717B"), ("SubtleBorder", "#B6C8CE"), ("StrongBorder", "#35515B"),
                ("PrimaryText", "#101820"), ("SecondaryText", "#374C56"), ("MutedText", "#4A626D"), ("DisabledText", "#5A6E77"),
                ("Accent", "#007A91"), ("AccentHover", "#006476"), ("AccentPressed", "#005263"), ("AccentForeground", "#FFFFFF"),
                ("SelectionBackground", "#BDEAF1"), ("SelectionForeground", "#06242B"), ("FocusBorder", "#007A91"),
                ("HoverOverlay", "#E8F5F8"), ("PressedOverlay", "#D5E9ED"), ("MapChromeBorder", "#35515B"), ("MapChromeText", "#101820"),
                ("ButtonBackground", "#E4F0F3"), ("ButtonHover", "#F2DCEE"), ("ButtonPressed", "#EAC7E3"), ("ButtonDisabled", "#EEF4F5"), ("CardHoverOverlay", "#18B3167A"), ("CardPressedOverlay", "#28B3167A"),
                ("ToggleTrack", "#35515B"), ("SliderTrack", "#58717B"), ("SliderFill", "#B3167A"), ("CardBackground", "#FFFFFF")), true)),
        new("supporter.foxfire", "Foxfire", true,
            Mode("supporter.foxfire", Palette(0, MapRoles("#E617171D", FoxfireMap, FoxfireDarkBasemap),
                ("WindowBackground", "#0D0E12"), ("ApplicationBackground", "#101014"), ("SurfaceBackground", "#17171D"),
                ("SurfaceBackgroundHover", "#29292F"), ("ElevatedSurface", "#202027"), ("InputBackground", "#141419"),
                ("SurfaceBorder", "#50505A"), ("SubtleBorder", "#33333D"), ("StrongBorder", "#92929E"),
                ("PrimaryText", "#F1F0F3"), ("SecondaryText", "#C0BDC6"), ("MutedText", "#AAA6B2"), ("DisabledText", "#918D99"),
                ("Accent", "#FF8A3D"), ("AccentHover", "#FFB15C"), ("AccentPressed", "#DD7130"), ("AccentForeground", "#1B100A"),
                ("SelectionBackground", "#754123"), ("SelectionForeground", "#FFF7EF"), ("FocusBorder", "#9DE7F0"),
                ("HoverOverlay", "#29292F"), ("PressedOverlay", "#36333B"), ("MapChromeBackground", "#E617171D"), ("MapChromeBorder", "#92929E"), ("MapChromeText", "#F1F0F3"),
                ("ButtonBackground", "#24242C"), ("ButtonHover", "#383039"), ("ButtonPressed", "#483744"), ("ButtonDisabled", "#17171D"),
                ("ToggleTrack", "#92929E"), ("SliderTrack", "#92929E"), ("SliderFill", "#FF8A3D"), ("CardBackground", "#19191F")), false),
            Mode("supporter.foxfire", Palette(1, MapRoles("#F2FFFDF8", FoxfireMap, FoxfireLightBasemap),
                ("WindowBackground", "#FAF5EE"), ("ApplicationBackground", "#FAF5EE"), ("SurfaceBackground", "#FFFDF8"),
                ("SurfaceBackgroundHover", "#F5E8DC"), ("ElevatedSurface", "#FFFFFF"), ("InputBackground", "#FFFFFF"),
                ("SurfaceBorder", "#80614E"), ("SubtleBorder", "#CDB8A7"), ("StrongBorder", "#5C3D2D"),
                ("PrimaryText", "#261812"), ("SecondaryText", "#5A3E30"), ("MutedText", "#705444"), ("DisabledText", "#80685A"),
                ("Accent", "#A64B20"), ("AccentHover", "#853A18"), ("AccentPressed", "#6E2F12"), ("AccentForeground", "#FFFFFF"),
                ("SelectionBackground", "#F0C9AE"), ("SelectionForeground", "#2B160C"), ("FocusBorder", "#167C8B"),
                ("HoverOverlay", "#F5E8DC"), ("PressedOverlay", "#EBD5C5"), ("MapChromeBorder", "#5C3D2D"), ("MapChromeText", "#261812"),
                ("ButtonBackground", "#F2E4D8"), ("ButtonHover", "#EAD4C3"), ("ButtonPressed", "#DEC0A9"), ("ButtonDisabled", "#F3ECE5"),
                ("ToggleTrack", "#5C3D2D"), ("SliderTrack", "#80614E"), ("SliderFill", "#A64B20"), ("CardBackground", "#FFFDF8")), true)),
        new("supporter.deep-space", "Deep Space", true,
            Mode("supporter.deep-space", Palette(0, MapRoles("#E610162A", DeepSpaceMap, DeepSpaceDarkBasemap),
                ("WindowBackground", "#050711"), ("ApplicationBackground", "#080B18"), ("SurfaceBackground", "#10162A"),
                ("SurfaceBackgroundHover", "#1A2340"), ("ElevatedSurface", "#151D36"), ("InputBackground", "#0C1222"),
                ("SurfaceBorder", "#35466C"), ("SubtleBorder", "#222E4C"), ("StrongBorder", "#7185B4"),
                ("PrimaryText", "#EEF3FF"), ("SecondaryText", "#B3C0DE"), ("MutedText", "#96A7C9"), ("DisabledText", "#7788A9"),
                ("Accent", "#8295FF"), ("AccentHover", "#A2B0FF"), ("AccentPressed", "#6477E0"), ("AccentForeground", "#0A1025"),
                ("SelectionBackground", "#3B4384"), ("SelectionForeground", "#FFFFFF"), ("FocusBorder", "#AFC8FF"),
                ("HoverOverlay", "#1A2340"), ("PressedOverlay", "#26345A"), ("MapChromeBorder", "#7185B4"), ("MapChromeText", "#EEF3FF"),
                ("ButtonBackground", "#182342"), ("ButtonHover", "#22325A"), ("ButtonPressed", "#2E4270"), ("ButtonDisabled", "#10162A"),
                ("ToggleTrack", "#7185B4"), ("SliderTrack", "#7185B4"), ("SliderFill", "#8295FF"), ("CardBackground", "#0E1427")), false),
            Mode("supporter.deep-space", Palette(1, MapRoles("#F2FFFFFF", DeepSpaceMap, DeepSpaceLightBasemap),
                ("WindowBackground", "#F2F6FC"), ("ApplicationBackground", "#F2F6FC"), ("SurfaceBackground", "#FFFFFF"),
                ("SurfaceBackgroundHover", "#E4EBF8"), ("ElevatedSurface", "#FFFFFF"), ("InputBackground", "#FFFFFF"),
                ("SurfaceBorder", "#607397"), ("SubtleBorder", "#BAC6D9"), ("StrongBorder", "#3F5276"),
                ("PrimaryText", "#101B33"), ("SecondaryText", "#3D4D6C"), ("MutedText", "#506184"), ("DisabledText", "#637391"),
                ("Accent", "#4B55A7"), ("AccentHover", "#3C458A"), ("AccentPressed", "#303875"), ("AccentForeground", "#FFFFFF"),
                ("SelectionBackground", "#D8DDF8"), ("SelectionForeground", "#1B204A"), ("FocusBorder", "#4B55A7"),
                ("HoverOverlay", "#E4EBF8"), ("PressedOverlay", "#D2DCF0"), ("MapChromeBorder", "#3F5276"), ("MapChromeText", "#101B33"),
                ("ButtonBackground", "#E2E8F4"), ("ButtonHover", "#D3DDF0"), ("ButtonPressed", "#C2D0E7"), ("ButtonDisabled", "#EDF1F7"),
                ("ToggleTrack", "#3F5276"), ("SliderTrack", "#607397"), ("SliderFill", "#4B55A7"), ("CardBackground", "#FFFFFF")), true)),
        new("supporter.moonlit", "Moonlit", true,
            Mode("supporter.moonlit", Palette(0, MapRoles("#E6252B35", MoonlitMap, MoonlitDarkBasemap),
                ("WindowBackground", "#171A20"), ("ApplicationBackground", "#1C2028"), ("SurfaceBackground", "#252B35"),
                ("SurfaceBackgroundHover", "#343C49"), ("ElevatedSurface", "#2D333E"), ("InputBackground", "#20252D"),
                ("SurfaceBorder", "#526071"), ("SubtleBorder", "#3A4553"), ("StrongBorder", "#8997A7"),
                ("PrimaryText", "#EDF1F5"), ("SecondaryText", "#BCC5CE"), ("MutedText", "#A3AFBA"), ("DisabledText", "#87939F"),
                ("Accent", "#A7B9D7"), ("AccentHover", "#C1D0E6"), ("AccentPressed", "#8CA0BE"), ("AccentForeground", "#14181E"),
                ("SelectionBackground", "#4D5D75"), ("SelectionForeground", "#FFFFFF"), ("FocusBorder", "#C5B9E5"),
                ("HoverOverlay", "#343C49"), ("PressedOverlay", "#414C5B"), ("MapChromeBorder", "#8997A7"), ("MapChromeText", "#EDF1F5"),
                ("ButtonBackground", "#303844"), ("ButtonHover", "#3D4857"), ("ButtonPressed", "#4A5666"), ("ButtonDisabled", "#252B35"),
                ("ToggleTrack", "#8997A7"), ("SliderTrack", "#8997A7"), ("SliderFill", "#A7B9D7"), ("CardBackground", "#222832")), false),
            Mode("supporter.moonlit", Palette(1, MapRoles("#F2F8F9FA", MoonlitMap, MoonlitLightBasemap),
                ("WindowBackground", "#EEF0F3"), ("ApplicationBackground", "#EEF0F3"), ("SurfaceBackground", "#F8F9FA"),
                ("SurfaceBackgroundHover", "#E2E6EB"), ("ElevatedSurface", "#FFFFFF"), ("InputBackground", "#FFFFFF"),
                ("SurfaceBorder", "#687585"), ("SubtleBorder", "#BBC3CC"), ("StrongBorder", "#4D5968"),
                ("PrimaryText", "#202733"), ("SecondaryText", "#45515F"), ("MutedText", "#586573"), ("DisabledText", "#687481"),
                ("Accent", "#566E91"), ("AccentHover", "#465B79"), ("AccentPressed", "#394B65"), ("AccentForeground", "#FFFFFF"),
                ("SelectionBackground", "#D5DDE9"), ("SelectionForeground", "#1D2735"), ("FocusBorder", "#596E9A"),
                ("HoverOverlay", "#E2E6EB"), ("PressedOverlay", "#D3D9E1"), ("MapChromeBorder", "#4D5968"), ("MapChromeText", "#202733"),
                ("ButtonBackground", "#E1E5EA"), ("ButtonHover", "#D4DAE1"), ("ButtonPressed", "#C5CED8"), ("ButtonDisabled", "#ECEEF1"),
                ("ToggleTrack", "#4D5968"), ("SliderTrack", "#687585"), ("SliderFill", "#566E91"), ("CardBackground", "#F8F9FA")), true))
    ];

    /// <summary>Applies the authored colour-vision sets for <paramref name="mode"/>; never a transformation of the composed image.</summary>
    public static void ApplySemanticVariant(IDictionary<string, string> palette, ColourVisionMode mode, bool light, string? family = null)
    {
        if (mode == ColourVisionMode.None) return;
        var highContrast = family == "builtin.highcontrast";
        if (StatusSet(mode, light, highContrast) is { } status)
            for (var i = 0; i < StatusRoles.Length; i++)
            {
                palette[StatusRoles[i]] = status[i];
                // A light wash of the status colour keeps banners recognisable; High Contrast keeps its plain surfaces.
                if (!highContrast) palette[StatusRoles[i] + "Background"] = Mix(palette["SurfaceBackground"], status[i], light ? .08 : .14);
            }
        if (CelestialSet(mode, light) is { } celestial)
            foreach (var (key, value) in CelestialThemeTokens.Objects.Zip(celestial)) palette[key] = value;
        if (family is null) return;
        foreach (var (key, value) in MapAreaOverrides.GetValueOrDefault((family, light, mode), [])) palette[key] = value;
        foreach (var (key, value) in MapSymbolOverrides.GetValueOrDefault((family, mode), [])) palette[key] = value;
    }

    private static string Mix(string background, string foreground, double amount)
    {
        var a = Avalonia.Media.Color.Parse(background);
        var b = Avalonia.Media.Color.Parse(foreground);
        byte Channel(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return $"#{Channel(a.R, b.R):X2}{Channel(a.G, b.G):X2}{Channel(a.B, b.B):X2}";
    }
}
