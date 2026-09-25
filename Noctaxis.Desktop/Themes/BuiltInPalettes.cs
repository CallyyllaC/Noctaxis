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
            ["MapChromeBackground"] = "#E6111722",
            ["MapChromeBorder"] = "#68778E",
            ["MapChromeText"] = "#E8ECF2",
            ["MapOverlayText"] = "#FFFFFF",
            ["MapOverlayShadow"] = "#000000",
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
            ["MapChromeBackground"] = "#F2FFFFFF",
            ["MapChromeBorder"] = "#536177",
            ["MapChromeText"] = "#172334",
            ["MapOverlayText"] = "#FFFFFF",
            ["MapOverlayShadow"] = "#000000",
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
            ["MapChromeBackground"] = "#FF000000",
            ["MapChromeBorder"] = "#FFFFFF",
            ["MapChromeText"] = "#FFFFFF",
            ["MapOverlayText"] = "#FFFFFF",
            ["MapOverlayShadow"] = "#000000",
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
    public static IReadOnlyList<string> RequiredTokens { get; } = Authored[0].Keys.ToArray();
    private static ThemePaletteDefinition Mode(IReadOnlyDictionary<string, string> palette, bool light) => new(palette,
        ThemeCatalogue.AllModes.ToDictionary(m => m, m =>
        {
            var variant = new Dictionary<string, string>(palette);
            ApplySemanticVariant(variant, m, light);
            return (IReadOnlyDictionary<string, string>)variant;
        }));
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
        p["CardHoverOverlay"] = "#00000000"; p["CardPressedOverlay"] = "#20000000";
        foreach (var role in new[] { "Success", "Warning", "Error", "Information" }) p[role + "Background"] = "#FFFFFF";
        return p;
    }
    private static IReadOnlyDictionary<string, string> Palette(int source, params (string Key, string Value)[] overrides)
    {
        var palette = new Dictionary<string, string>(Authored[source]);
        foreach (var (key, value) in overrides) palette[key] = value;
        return palette;
    }
    public static IReadOnlyList<ThemeDefinition> Definitions { get; } =
    [
        new("builtin.noctaxis", "Noctaxis", false, Mode(Authored[0], false), Mode(Authored[1], true), "Noctaxis appearance"),
        new("builtin.highcontrast", "High Contrast", false, Mode(Authored[2], false), Mode(HighContrastLight(), true), "Strong contrast and structural states"),
        new("supporter.noctaxis-neon", "Noctaxis Neon", true,
            Mode(Palette(0,
                ("WindowBackground", "#080A0F"), ("ApplicationBackground", "#0B0E14"), ("SurfaceBackground", "#11151D"),
                ("SurfaceBackgroundHover", "#1B2530"), ("ElevatedSurface", "#171D27"), ("InputBackground", "#0D121A"),
                ("SurfaceBorder", "#3A4858"), ("SubtleBorder", "#252F3B"), ("StrongBorder", "#72879B"),
                ("PrimaryText", "#F5F7FA"), ("SecondaryText", "#B7C1CC"), ("MutedText", "#9CA8B5"), ("DisabledText", "#7E8A98"),
                ("Accent", "#38E8FF"), ("AccentHover", "#FF3DBD"), ("AccentPressed", "#D62998"), ("AccentForeground", "#00151A"),
                ("SelectionBackground", "#005A70"), ("SelectionForeground", "#FFFFFF"), ("FocusBorder", "#00D9FF"),
                ("HoverOverlay", "#1B2530"), ("PressedOverlay", "#29343F"), ("MapChromeBorder", "#72879B"), ("MapChromeText", "#F5F7FA"),
                ("ButtonBackground", "#1A1D2E"), ("ButtonHover", "#382036"), ("ButtonPressed", "#482640"), ("ButtonDisabled", "#11151D"), ("CardHoverOverlay", "#18FF3DBD"), ("CardPressedOverlay", "#28FF3DBD"),
                ("ToggleTrack", "#72879B"), ("SliderTrack", "#72879B"), ("SliderFill", "#FF3DBD"), ("CardBackground", "#10161E")), false),
            Mode(Palette(1,
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
            Mode(Palette(0,
                ("WindowBackground", "#0D0E12"), ("ApplicationBackground", "#101014"), ("SurfaceBackground", "#17171D"),
                ("SurfaceBackgroundHover", "#29292F"), ("ElevatedSurface", "#202027"), ("InputBackground", "#141419"),
                ("SurfaceBorder", "#50505A"), ("SubtleBorder", "#33333D"), ("StrongBorder", "#92929E"),
                ("PrimaryText", "#F1F0F3"), ("SecondaryText", "#C0BDC6"), ("MutedText", "#AAA6B2"), ("DisabledText", "#918D99"),
                ("Accent", "#FF8A3D"), ("AccentHover", "#FFB15C"), ("AccentPressed", "#DD7130"), ("AccentForeground", "#1B100A"),
                ("SelectionBackground", "#754123"), ("SelectionForeground", "#FFF7EF"), ("FocusBorder", "#9DE7F0"),
                ("HoverOverlay", "#29292F"), ("PressedOverlay", "#36333B"), ("MapChromeBackground", "#E617171D"), ("MapChromeBorder", "#92929E"), ("MapChromeText", "#F1F0F3"),
                ("ButtonBackground", "#24242C"), ("ButtonHover", "#383039"), ("ButtonPressed", "#483744"), ("ButtonDisabled", "#17171D"),
                ("ToggleTrack", "#92929E"), ("SliderTrack", "#92929E"), ("SliderFill", "#FF8A3D"), ("CardBackground", "#19191F")), false),
            Mode(Palette(1,
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
            Mode(Palette(0,
                ("WindowBackground", "#050711"), ("ApplicationBackground", "#080B18"), ("SurfaceBackground", "#10162A"),
                ("SurfaceBackgroundHover", "#1A2340"), ("ElevatedSurface", "#151D36"), ("InputBackground", "#0C1222"),
                ("SurfaceBorder", "#35466C"), ("SubtleBorder", "#222E4C"), ("StrongBorder", "#7185B4"),
                ("PrimaryText", "#EEF3FF"), ("SecondaryText", "#B3C0DE"), ("MutedText", "#96A7C9"), ("DisabledText", "#7788A9"),
                ("Accent", "#8295FF"), ("AccentHover", "#A2B0FF"), ("AccentPressed", "#6477E0"), ("AccentForeground", "#0A1025"),
                ("SelectionBackground", "#3B4384"), ("SelectionForeground", "#FFFFFF"), ("FocusBorder", "#AFC8FF"),
                ("HoverOverlay", "#1A2340"), ("PressedOverlay", "#26345A"), ("MapChromeBorder", "#7185B4"), ("MapChromeText", "#EEF3FF"),
                ("ButtonBackground", "#182342"), ("ButtonHover", "#22325A"), ("ButtonPressed", "#2E4270"), ("ButtonDisabled", "#10162A"),
                ("ToggleTrack", "#7185B4"), ("SliderTrack", "#7185B4"), ("SliderFill", "#8295FF"), ("CardBackground", "#0E1427")), false),
            Mode(Palette(1,
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
            Mode(Palette(0,
                ("WindowBackground", "#171A20"), ("ApplicationBackground", "#1C2028"), ("SurfaceBackground", "#252B35"),
                ("SurfaceBackgroundHover", "#343C49"), ("ElevatedSurface", "#2D333E"), ("InputBackground", "#20252D"),
                ("SurfaceBorder", "#526071"), ("SubtleBorder", "#3A4553"), ("StrongBorder", "#8997A7"),
                ("PrimaryText", "#EDF1F5"), ("SecondaryText", "#BCC5CE"), ("MutedText", "#A3AFBA"), ("DisabledText", "#87939F"),
                ("Accent", "#A7B9D7"), ("AccentHover", "#C1D0E6"), ("AccentPressed", "#8CA0BE"), ("AccentForeground", "#14181E"),
                ("SelectionBackground", "#4D5D75"), ("SelectionForeground", "#FFFFFF"), ("FocusBorder", "#C5B9E5"),
                ("HoverOverlay", "#343C49"), ("PressedOverlay", "#414C5B"), ("MapChromeBorder", "#8997A7"), ("MapChromeText", "#EDF1F5"),
                ("ButtonBackground", "#303844"), ("ButtonHover", "#3D4857"), ("ButtonPressed", "#4A5666"), ("ButtonDisabled", "#252B35"),
                ("ToggleTrack", "#8997A7"), ("SliderTrack", "#8997A7"), ("SliderFill", "#A7B9D7"), ("CardBackground", "#222832")), false),
            Mode(Palette(1,
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

    // Authored semantic sets, never a transformation of the composed image.
    public static void ApplySemanticVariant(IDictionary<string, string> palette, ColourVisionMode mode, bool light)
    {
        string[]? colours = mode switch
        {
            ColourVisionMode.Protanopia => light ? ["#00628B", "#705000", "#914B00", "#664499"] : ["#79D3FF", "#FFE080", "#FFB66D", "#D7B7FF"],
            ColourVisionMode.Deuteranopia => light ? ["#00628B", "#705000", "#914B00", "#664499"] : ["#79D3FF", "#FFE080", "#FFB66D", "#D7B7FF"],
            ColourVisionMode.Tritanopia => light ? ["#00665B", "#705000", "#A21C47", "#354D9F"] : ["#7DE0CA", "#FFE5B4", "#FF9CBF", "#BDCFFF"],
            _ => null
        };
        if (colours is null) return;
        string[] roles = ["Success", "Warning", "Error", "Information"];
        for (var i = 0; i < roles.Length; i++)
        {
            palette[roles[i]] = colours[i];
            palette[roles[i] + "Background"] = palette["SurfaceBackground"];
        }
        palette["FocusBorder"] = colours[0];
    }
}
