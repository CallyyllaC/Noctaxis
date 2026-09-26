using Noctaxis.Desktop.Themes;
using SkiaSharp;

namespace Noctaxis.Desktop.Mapping.Vector;

/// <summary>
/// The semantic colours a themed vector basemap is drawn with, resolved from the selected theme's
/// <see cref="MapThemeTokens.Basemap"/> roles. Immutable, so a render in progress never observes a
/// half-applied theme.
/// </summary>
public sealed record BasemapStyle(
    SKColor Land, SKColor BuiltUp, SKColor Building, SKColor Water, SKColor Vegetation,
    SKColor RoadMinor, SKColor RoadMajor, SKColor RoadCasing, SKColor Rail, SKColor Boundary,
    SKColor Label, SKColor LabelHalo, SKColor WaterLabel, SKColor Poi)
{
    /// <summary>The Noctaxis Dark roles, used before any theme has been resolved.</summary>
    public static BasemapStyle Default { get; } =
        FromPalette(BuiltInPalettes.Definitions[0].Dark.Palette);

    public static BasemapStyle FromPalette(IReadOnlyDictionary<string, string> palette) =>
        FromTokens(token => palette.TryGetValue(token, out var value) && SKColor.TryParse(value, out var colour)
            ? colour : null);

    /// <summary>Builds a style from a token lookup; unresolved roles keep the Noctaxis Dark value.</summary>
    public static BasemapStyle FromTokens(Func<string, SKColor?> lookup)
    {
        // Default is only consulted for a missing role; its own palette is complete.
        SKColor Role(string token, Func<BasemapStyle, SKColor> current) => lookup(token) ?? current(Default);
        return new(
            Role(MapThemeTokens.Land, s => s.Land), Role(MapThemeTokens.BuiltUp, s => s.BuiltUp),
            Role(MapThemeTokens.Building, s => s.Building), Role(MapThemeTokens.Water, s => s.Water),
            Role(MapThemeTokens.Vegetation, s => s.Vegetation), Role(MapThemeTokens.RoadMinor, s => s.RoadMinor),
            Role(MapThemeTokens.RoadMajor, s => s.RoadMajor), Role(MapThemeTokens.RoadCasing, s => s.RoadCasing),
            Role(MapThemeTokens.Rail, s => s.Rail), Role(MapThemeTokens.Boundary, s => s.Boundary),
            Role(MapThemeTokens.Label, s => s.Label), Role(MapThemeTokens.LabelHalo, s => s.LabelHalo),
            Role(MapThemeTokens.WaterLabel, s => s.WaterLabel), Role(MapThemeTokens.Poi, s => s.Poi));
    }
}
