using System.Collections.Immutable;
using Avalonia.Media;

namespace Noctaxis.Desktop.Themes;

/// <summary>
/// Semantic data-colour roles for celestial objects and the timeline chart. Every palette supplies
/// them, and every colour-vision variant re-authors them for its deficiency, so the target list,
/// map rays, camera framing, environmental cone and chart all follow
/// <c>theme family + Light/Dark + colour-vision mode</c>. Drawing code never names a family.
/// </summary>
public static class CelestialThemeTokens
{
    public const string Sun = "CelestialSun";
    public const string Moon = "CelestialMoon";
    public const string DeepSky1 = "CelestialDeepSky1";
    public const string DeepSky2 = "CelestialDeepSky2";
    public const string DeepSky3 = "CelestialDeepSky3";
    public const string DeepSky4 = "CelestialDeepSky4";
    public const string DeepSky5 = "CelestialDeepSky5";

    /// <summary>Path segments below the horizon on the timeline chart (thinner and dashed as well).</summary>
    public const string ChartBelowHorizon = "ChartBelowHorizon";
    /// <summary>Terrain silhouette on the timeline chart.</summary>
    public const string ChartTerrainFill = "ChartTerrainFill";
    public const string ChartTerrainOutline = "ChartTerrainOutline";

    /// <summary>Resource key of the resolved <see cref="CelestialColourSet"/> published by ThemeService.</summary>
    public const string ColourSet = "CelestialColourSet";

    /// <summary>Object roles in <see cref="Controls.CelestialRole"/> order.</summary>
    public static IReadOnlyList<string> Objects { get; } = [Sun, Moon, DeepSky1, DeepSky2, DeepSky3, DeepSky4, DeepSky5];

    public static IReadOnlyList<string> Chart { get; } = [ChartBelowHorizon, ChartTerrainFill, ChartTerrainOutline];

    public static IReadOnlyList<string> All { get; } = [.. Objects, .. Chart];
}

/// <summary>The resolved celestial and chart colours of one theme palette.</summary>
public sealed record CelestialColourSet(ImmutableArray<Color> Objects, Color BelowHorizon, Color TerrainFill, Color TerrainOutline)
{
    /// <summary>Noctaxis Dark with no colour-vision mode; used before a theme is loaded.</summary>
    public static CelestialColourSet Default { get; } = FromPalette(BuiltInPalettes.Definitions[0].Dark.Palette);

    public Color this[Controls.CelestialRole role] => Objects[(int)role];

    public static CelestialColourSet FromPalette(IReadOnlyDictionary<string, string> palette) => new(
        [.. CelestialThemeTokens.Objects.Select(token => Color.Parse(palette[token]))],
        Color.Parse(palette[CelestialThemeTokens.ChartBelowHorizon]),
        Color.Parse(palette[CelestialThemeTokens.ChartTerrainFill]),
        Color.Parse(palette[CelestialThemeTokens.ChartTerrainOutline]));
}
