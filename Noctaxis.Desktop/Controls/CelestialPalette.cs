using Noctaxis.Core.Localisation;
using Avalonia;
using Avalonia.Media;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

/// <summary>The identity slot of a celestial object; colours come from the theme (<see cref="CelestialThemeTokens"/>).</summary>
public enum CelestialRole { Sun, Moon, DeepSky1, DeepSky2, DeepSky3, DeepSky4, DeepSky5 }

/// <summary>
/// How a celestial object is told apart: its colour, and its line. Every role has its own dash pattern; the
/// focused target is drawn solid and heavier instead, so it is the one solid path. Colour leads with normal
/// colour vision and the pattern backs it; under a colour-vision mode (<see cref="CelestialColourSet.EmphasisePatterns"/>)
/// the dashed paths are drawn heavier, so the pattern itself reads as the identity. There are no shapes along
/// the paths: the map rays, the timeline and every line sample in the UI (<see cref="CelestialLegendGlyph"/>) draw
/// a target with <see cref="Stroke"/>, and nothing else.
/// </summary>
internal static class CelestialPalette
{
    private const int DeepSkyRoles = 5;

    public static CelestialRole Role(AstralTarget target, int order) =>
        target.IsSun ? CelestialRole.Sun : target.IsMoon ? CelestialRole.Moon
            : CelestialRole.DeepSky1 + Math.Abs(order) % DeepSkyRoles;

    public static CelestialRole Role(PlanningSnapshot snapshot, AstralTarget target) => Role(target, OrderFor(snapshot, target));

    public static int OrderFor(PlanningSnapshot snapshot, AstralTarget target) =>
        snapshot.Session.EffectiveVisibleObjects.FirstOrDefault(item => item.TargetId.Equals(target.Id, StringComparison.OrdinalIgnoreCase))?.Order ?? 0;

    // Dash lengths are in multiples of the pen thickness (Avalonia DashStyle semantics). Each differs from every
    // other in how many dashes a repeat has or, with as many, in their lengths by at least half again.
    private static readonly IDashStyle[] Dashes =
    [
        new Avalonia.Media.Immutable.ImmutableDashStyle([4, 2], 0),             // Sun: long dash
        new Avalonia.Media.Immutable.ImmutableDashStyle([1, 2], 0),             // Moon: dots
        new Avalonia.Media.Immutable.ImmutableDashStyle([6, 2, 1, 2], 0),       // dash-dot
        new Avalonia.Media.Immutable.ImmutableDashStyle([2, 2], 0),             // short dash
        new Avalonia.Media.Immutable.ImmutableDashStyle([6, 2, 1, 2, 1, 2], 0), // dash-dot-dot
        new Avalonia.Media.Immutable.ImmutableDashStyle([9, 3], 0),             // extra-long dash
        new Avalonia.Media.Immutable.ImmutableDashStyle([3, 2, 3, 5], 0)        // paired dashes
    ];

    public static IDashStyle Dash(CelestialRole role) => Dashes[(int)role];

    /// <summary>How much heavier a dashed path is drawn under a colour-vision mode, so its pattern carries the identity.</summary>
    public const double PatternEmphasis = 1.35;

    /// <summary>
    /// The one pen a target is drawn with, on the map, on the timeline and in every line sample: its colour, and
    /// solid when it is focused, otherwise its role's dash pattern, heavier when patterns are emphasised.
    /// <paramref name="thickness"/> is the surface's own weight for that line.
    /// </summary>
    public static Pen Stroke(CelestialColourSet colours, CelestialRole role, bool focused, double thickness)
    {
        var brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(colours[role]);
        return focused
            ? new Pen(brush, thickness)
            : new Pen(brush, colours.EmphasisePatterns ? thickness * PatternEmphasis : thickness, dashStyle: Dash(role));
    }

    private static readonly string[] PatternKeys =
    [
        "Planner_Targets_Pattern_LongDash", "Planner_Targets_Pattern_Dotted", "Planner_Targets_Pattern_DashDot",
        "Planner_Targets_Pattern_ShortDash", "Planner_Targets_Pattern_DashDotDot", "Planner_Targets_Pattern_ExtraLongDash",
        "Planner_Targets_Pattern_PairedDash"
    ];

    /// <summary>The line's pattern in words, for a screen reader: "Solid" for the focused target.</summary>
    public static string PatternName(CelestialRole role, bool focused) =>
        Strings.Get(focused ? "Planner_Targets_Pattern_Solid" : PatternKeys[(int)role]);

    /// <summary>Compact line-end label: "Sun", "Moon" or the catalogue identifier.</summary>
    public static string ShortLabel(AstralTarget target)
    {
        if (target.IsSun) return Strings.Get("Planner_Targets_Sun_ShortLabel");
        if (target.IsMoon) return Strings.Get("Planner_Targets_Moon_ShortLabel");
        var label = string.IsNullOrWhiteSpace(target.PrimaryIdentifier) ? target.DisplayName : target.PrimaryIdentifier;
        return label.Length <= 10 ? label : label[..9] + "…";
    }
}
