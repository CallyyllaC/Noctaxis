using Noctaxis.Core.Localisation;
using Avalonia;
using Avalonia.Media;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

/// <summary>The identity slot of a celestial object; colours come from the theme (<see cref="CelestialThemeTokens"/>).</summary>
public enum CelestialRole { Sun, Moon, DeepSky1, DeepSky2, DeepSky3, DeepSky4, DeepSky5 }

/// <summary>Marker shape drawn at a path's culmination on the chart, along its map ray, and in the target list.</summary>
public enum CelestialMarker { Disc, Ring, Square, TriangleUp, Diamond, TriangleDown, Cross }

/// <summary>
/// How a celestial object is told apart without relying on colour: every role has its own dash
/// pattern (secondary paths; the primary target is always solid and thicker) and marker shape. The
/// same identity is drawn on the chart, on the map and as the target-list swatch.
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

    public static CelestialMarker Marker(CelestialRole role) => (CelestialMarker)(int)role;

    // Dash lengths are in multiples of the pen thickness (Avalonia DashStyle semantics).
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

    /// <summary>Compact line-end label: "Sun", "Moon" or the catalogue identifier.</summary>
    public static string ShortLabel(AstralTarget target)
    {
        if (target.IsSun) return Strings.Get("Planner_Targets_Sun_ShortLabel");
        if (target.IsMoon) return Strings.Get("Planner_Targets_Moon_ShortLabel");
        var label = string.IsNullOrWhiteSpace(target.PrimaryIdentifier) ? target.DisplayName : target.PrimaryIdentifier;
        return label.Length <= 10 ? label : label[..9] + "…";
    }

    /// <summary>Draws a role's marker centred on <paramref name="centre"/>, outlined by <paramref name="halo"/>.</summary>
    public static void DrawMarker(DrawingContext context, CelestialRole role, Point centre, double size, IBrush fill, IBrush? halo)
    {
        var geometry = MarkerGeometry(Marker(role), centre, size);
        var haloPen = halo is null ? null : new Pen(halo, 1.5);
        if (Marker(role) is CelestialMarker.Ring or CelestialMarker.Cross)
        {
            if (haloPen is not null) context.DrawGeometry(null, new Pen(halo, 4.2), geometry);
            context.DrawGeometry(null, new Pen(fill, 2), geometry);
        }
        else context.DrawGeometry(fill, haloPen, geometry);
    }

    private static Geometry MarkerGeometry(CelestialMarker marker, Point c, double size)
    {
        var r = size / 2;
        Geometry Polygon(params Point[] points)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            g.BeginFigure(points[0], true);
            foreach (var point in points.Skip(1)) g.LineTo(point);
            g.EndFigure(true);
            return geometry;
        }
        return marker switch
        {
            CelestialMarker.Disc => new EllipseGeometry(new Rect(c.X - r, c.Y - r, size, size)),
            CelestialMarker.Ring => new EllipseGeometry(new Rect(c.X - r * .8, c.Y - r * .8, size * .8, size * .8)),
            CelestialMarker.Square => new RectangleGeometry(new Rect(c.X - r * .85, c.Y - r * .85, size * .85, size * .85)),
            CelestialMarker.TriangleUp => Polygon(new(c.X, c.Y - r), new(c.X + r, c.Y + r * .8), new(c.X - r, c.Y + r * .8)),
            CelestialMarker.Diamond => Polygon(new(c.X, c.Y - r), new(c.X + r, c.Y), new(c.X, c.Y + r), new(c.X - r, c.Y)),
            CelestialMarker.TriangleDown => Polygon(new(c.X - r, c.Y - r * .8), new(c.X + r, c.Y - r * .8), new(c.X, c.Y + r)),
            _ => Cross(c, r * .85)
        };
    }

    private static Geometry Cross(Point c, double r)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new(c.X - r, c.Y - r), false); g.LineTo(new(c.X + r, c.Y + r)); g.EndFigure(false);
        g.BeginFigure(new(c.X + r, c.Y - r), false); g.LineTo(new(c.X - r, c.Y + r)); g.EndFigure(false);
        return geometry;
    }
}
