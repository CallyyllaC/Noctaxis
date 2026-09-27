using Noctaxis.Desktop.Themes;
using SkiaSharp;

namespace Noctaxis.Desktop.Mapping.Preview;

/// <summary>How the preview carries atmosphere: luminous on dark grounds, airy on pale ones, crisp in High Contrast.</summary>
public enum LocationPreviewTone { Dark, Light, HighContrastDark, HighContrastLight }

/// <summary>
/// Colours for one themed preview render, taken from the resolved theme palette: the Planner basemap
/// roles (<see cref="MapThemeTokens"/>, already tuned per family, Light/Dark and colour-vision mode)
/// and the preview roles in <see cref="LocationCardTokens"/>. No family is named here.
/// </summary>
public sealed record LocationPreviewStyle(
    LocationPreviewTone Tone,
    SKColor Land, SKColor Water, SKColor WaterLine, SKColor Vegetation, SKColor BuiltUp,
    SKColor RoadMajor, SKColor RoadMinor, SKColor RoadCasing,
    SKColor Glow, SKColor Haze, SKColor Reticle, SKColor Instrument)
{
    public bool IsHighContrast => Tone is LocationPreviewTone.HighContrastDark or LocationPreviewTone.HighContrastLight;
    public bool IsLight => Tone is LocationPreviewTone.Light or LocationPreviewTone.HighContrastLight;

    /// <summary>Stable identity for render caches.</summary>
    public string Key => string.Join(",", new object[] { Tone, Land, Water, WaterLine, Vegetation, BuiltUp, RoadMajor, RoadMinor,
        RoadCasing, Glow, Haze, Reticle, Instrument });

    public static LocationPreviewStyle FromPalette(IReadOnlyDictionary<string, string> palette, bool light, bool highContrast)
    {
        SKColor C(string token) => SKColor.Parse(palette[token]);
        var water = C(MapThemeTokens.Water);
        var waterLabel = C(MapThemeTokens.WaterLabel);
        return new(
            highContrast ? light ? LocationPreviewTone.HighContrastLight : LocationPreviewTone.HighContrastDark
                : light ? LocationPreviewTone.Light : LocationPreviewTone.Dark,
            C(MapThemeTokens.Land), water, Mix(water, waterLabel, light ? .55 : .6), C(MapThemeTokens.Vegetation), C(MapThemeTokens.BuiltUp),
            C(MapThemeTokens.RoadMajor), C(MapThemeTokens.RoadMinor), C(MapThemeTokens.RoadCasing),
            C(LocationCardTokens.PreviewGlow), C(LocationCardTokens.PreviewHaze), C(MapThemeTokens.Label),
            C(LocationCardTokens.PreviewInstrument));
    }

    internal static SKColor Mix(SKColor a, SKColor b, double amount)
    {
        byte Channel(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return new SKColor(Channel(a.Red, b.Red), Channel(a.Green, b.Green), Channel(a.Blue, b.Blue), Channel(a.Alpha, b.Alpha));
    }
}
