using Avalonia;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Controls;

public enum FramingRaySegmentState { Visible, Limited }

public sealed record FramingRaySegment(
    Point Start,
    Point End,
    FramingRaySegmentState State,
    FramingLimitReason? LimitingReason = null,
    double? LimitDistanceMetres = null,
    string? Label = null);

public sealed record FramingRayGeometry(
    double BearingDegrees,
    IReadOnlyList<FramingRaySegment> Segments)
{
    public Point ClippedStart => Segments[0].Start;
    public Point ClippedEnd => Segments[^1].End;
}

public sealed record FramingTransitionGeometry(
    FramingLimitReason Reason,
    double DistanceMetres,
    string Label,
    Point? Left,
    Point? Centre,
    Point? Right);

public sealed record FramingShadeRegion(
    IReadOnlyList<Point> Points,
    FramingRaySegmentState State,
    FramingLimitReason? LimitingReason = null);

public sealed record FramingWedgeLayout(
    Point Apex,
    FramingRayGeometry? LeftBoundary,
    FramingRayGeometry? RightBoundary,
    FramingRayGeometry? CentreBearing,
    IReadOnlyList<FramingShadeRegion> ShadeRegions,
    FramingTransitionGeometry? Transition);

/// <summary>
/// Projects true angular bearing rays from the observer, clips them to the map viewport, and splits
/// their styling at independently calculated geographic visibility limits. Optical geometry is never
/// changed by visibility state.
/// </summary>
public sealed class FramingWedgeLayoutCalculator
{
    public const double ProjectionProbeDistanceMetres = 1_000;
    private const double DirectionEpsilon = 1e-9;

    public FramingWedgeLayout Calculate(
        GeoCoordinate observer,
        Rect viewport,
        CameraFramingGuide guide,
        Func<GeoCoordinate, Point?> project,
        FramingVisibilityAssessment? visibility = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (viewport.Width <= 0 || viewport.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewport), "The map viewport must have a positive size.");

        var apex = project(observer) ?? throw new InvalidOperationException("The planning pin could not be projected.");
        var radialLimit = visibility?.NearestRadialLimit;
        var terrainLimited = visibility?.IsTargetTerrainObstructed == true;
        var left = CalculateRay(observer, apex, viewport, guide.LeftEdgeBearingDegrees, project, radialLimit, terrainLimited, visibility?.Status);
        var right = CalculateRay(observer, apex, viewport, guide.RightEdgeBearingDegrees, project, radialLimit, terrainLimited, visibility?.Status);
        var centre = CalculateRay(observer, apex, viewport, guide.CentreBearingDegrees, project, radialLimit, terrainLimited, visibility?.Status);
        var transition = radialLimit is null ? null : new FramingTransitionGeometry(
            radialLimit.Reason,
            radialLimit.DistanceMetres,
            radialLimit.Label,
            TransitionPoint(left),
            TransitionPoint(centre),
            TransitionPoint(right));
        var shadeRegions = CalculateShadeRegions(apex, viewport, left, right, terrainLimited, transition);

        return new FramingWedgeLayout(apex, left, right, centre, shadeRegions, transition);
    }

    private static FramingRayGeometry? CalculateRay(
        GeoCoordinate observer,
        Point apex,
        Rect viewport,
        double bearingDegrees,
        Func<GeoCoordinate, Point?> project,
        FramingRadialLimit? radialLimit,
        bool terrainLimited,
        string? terrainLabel)
    {
        var probeCoordinate = Angles.Destination(observer, bearingDegrees, ProjectionProbeDistanceMetres);
        var probe = project(probeCoordinate);
        if (probe is null) return null;

        var dx = probe.Value.X - apex.X;
        var dy = probe.Value.Y - apex.Y;
        var magnitude = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(magnitude) || magnitude < DirectionEpsilon) return null;

        var direction = new Vector(dx / magnitude, dy / magnitude);
        if (!TryClipRay(apex, direction, viewport, out var clipped)) return null;

        if (terrainLimited)
        {
            return new FramingRayGeometry(bearingDegrees,
            [
                new FramingRaySegment(clipped.Start, clipped.End, FramingRaySegmentState.Limited,
                    FramingLimitReason.TerrainHorizon, Label: terrainLabel)
            ]);
        }

        if (radialLimit is null)
            return VisibleRay(bearingDegrees, clipped);

        var limitCoordinate = Angles.Destination(observer, bearingDegrees, radialLimit.DistanceMetres);
        var projectedLimit = project(limitCoordinate);
        if (projectedLimit is null)
            return VisibleRay(bearingDegrees, clipped);

        var limitParameter = (projectedLimit.Value.X - apex.X) * direction.X +
                             (projectedLimit.Value.Y - apex.Y) * direction.Y;
        if (!double.IsFinite(limitParameter) || limitParameter >= clipped.Exit)
            return VisibleRay(bearingDegrees, clipped);

        if (limitParameter <= clipped.Enter)
        {
            return new FramingRayGeometry(bearingDegrees,
            [
                new FramingRaySegment(clipped.Start, clipped.End, FramingRaySegmentState.Limited,
                    radialLimit.Reason, radialLimit.DistanceMetres, radialLimit.Label)
            ]);
        }

        var transition = apex + direction * limitParameter;
        return new FramingRayGeometry(bearingDegrees,
        [
            new FramingRaySegment(clipped.Start, transition, FramingRaySegmentState.Visible),
            new FramingRaySegment(transition, clipped.End, FramingRaySegmentState.Limited,
                radialLimit.Reason, radialLimit.DistanceMetres, radialLimit.Label)
        ]);
    }

    private static FramingRayGeometry VisibleRay(double bearingDegrees, ClippedRay clipped) =>
        new(bearingDegrees, [new FramingRaySegment(clipped.Start, clipped.End, FramingRaySegmentState.Visible)]);

    private static IReadOnlyList<FramingShadeRegion> CalculateShadeRegions(
        Point apex,
        Rect viewport,
        FramingRayGeometry? left,
        FramingRayGeometry? right,
        bool terrainLimited,
        FramingTransitionGeometry? transition)
    {
        // The planning pin may be outside the visible map while its field-of-view sector still
        // crosses the viewport. Keep the off-screen apex in the source polygon and let the
        // viewport clipper produce the visible portion of the shade.
        if (left is null || right is null) return [];

        var diagonal = Math.Sqrt(viewport.Width * viewport.Width + viewport.Height * viewport.Height);
        // Extend the closing edge beyond the viewport even when the apex is a long way off-screen.
        // A viewport-relative distance alone can stop before reaching the map in that case.
        var leftFar = ExtendFromApex(apex, left.ClippedEnd,
            Math.Max(diagonal * 3, Distance(apex, left.ClippedEnd) + diagonal));
        var rightFar = ExtendFromApex(apex, right.ClippedEnd,
            Math.Max(diagonal * 3, Distance(apex, right.ClippedEnd) + diagonal));
        if (terrainLimited)
        {
            return
            [
                new FramingShadeRegion(
                    ClipPolygon([apex, leftFar, rightFar], viewport),
                    FramingRaySegmentState.Limited,
                    FramingLimitReason.TerrainHorizon)
            ];
        }

        if (transition?.Left is Point leftTransition && transition.Right is Point rightTransition)
        {
            return
            [
                new FramingShadeRegion(
                    ClipPolygon([apex, leftTransition, rightTransition], viewport),
                    FramingRaySegmentState.Visible),
                new FramingShadeRegion(
                    ClipPolygon([leftTransition, leftFar, rightFar, rightTransition], viewport),
                    FramingRaySegmentState.Limited,
                    transition.Reason)
            ];
        }

        return
        [
            new FramingShadeRegion(
                ClipPolygon([apex, leftFar, rightFar], viewport),
                FramingRaySegmentState.Visible)
        ];
    }

    private static Point ExtendFromApex(Point apex, Point rayPoint, double distance)
    {
        var dx = rayPoint.X - apex.X;
        var dy = rayPoint.Y - apex.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < DirectionEpsilon
            ? apex
            : new Point(apex.X + dx / length * distance, apex.Y + dy / length * distance);
    }

    private static double Distance(Point first, Point second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static IReadOnlyList<Point> ClipPolygon(IReadOnlyList<Point> polygon, Rect viewport)
    {
        var points = polygon.ToList();
        points = ClipEdge(points, point => point.X >= viewport.Left,
            (first, second) => IntersectVertical(first, second, viewport.Left));
        points = ClipEdge(points, point => point.X <= viewport.Right,
            (first, second) => IntersectVertical(first, second, viewport.Right));
        points = ClipEdge(points, point => point.Y >= viewport.Top,
            (first, second) => IntersectHorizontal(first, second, viewport.Top));
        points = ClipEdge(points, point => point.Y <= viewport.Bottom,
            (first, second) => IntersectHorizontal(first, second, viewport.Bottom));
        return points;
    }

    private static List<Point> ClipEdge(
        IReadOnlyList<Point> input,
        Func<Point, bool> inside,
        Func<Point, Point, Point> intersect)
    {
        var output = new List<Point>();
        if (input.Count == 0) return output;
        var previous = input[^1];
        var previousInside = inside(previous);
        foreach (var current in input)
        {
            var currentInside = inside(current);
            if (currentInside)
            {
                if (!previousInside) output.Add(intersect(previous, current));
                output.Add(current);
            }
            else if (previousInside)
            {
                output.Add(intersect(previous, current));
            }
            previous = current;
            previousInside = currentInside;
        }
        return output;
    }

    private static Point IntersectVertical(Point first, Point second, double x)
    {
        var delta = second.X - first.X;
        if (Math.Abs(delta) < DirectionEpsilon) return new Point(x, first.Y);
        var fraction = (x - first.X) / delta;
        return new Point(x, first.Y + (second.Y - first.Y) * fraction);
    }

    private static Point IntersectHorizontal(Point first, Point second, double y)
    {
        var delta = second.Y - first.Y;
        if (Math.Abs(delta) < DirectionEpsilon) return new Point(first.X, y);
        var fraction = (y - first.Y) / delta;
        return new Point(first.X + (second.X - first.X) * fraction, y);
    }

    private static Point? TransitionPoint(FramingRayGeometry? ray)
    {
        if (ray?.Segments.Count != 2) return null;
        return ray.Segments[0].End;
    }

    private static bool TryClipRay(Point origin, Vector direction, Rect viewport, out ClippedRay clipped)
    {
        var enter = 0d;
        var exit = double.PositiveInfinity;
        if (!ClipAxis(origin.X, direction.X, viewport.Left, viewport.Right, ref enter, ref exit) ||
            !ClipAxis(origin.Y, direction.Y, viewport.Top, viewport.Bottom, ref enter, ref exit) ||
            exit < Math.Max(enter, 0))
        {
            clipped = default;
            return false;
        }

        enter = Math.Max(enter, 0);
        if (!double.IsFinite(exit))
        {
            clipped = default;
            return false;
        }

        clipped = new ClippedRay(
            origin + direction * enter,
            origin + direction * exit,
            enter,
            exit);
        return true;
    }

    private static bool ClipAxis(double origin, double direction, double minimum, double maximum,
        ref double enter, ref double exit)
    {
        if (Math.Abs(direction) < DirectionEpsilon)
            return origin >= minimum && origin <= maximum;

        var first = (minimum - origin) / direction;
        var second = (maximum - origin) / direction;
        if (first > second) (first, second) = (second, first);
        enter = Math.Max(enter, first);
        exit = Math.Min(exit, second);
        return enter <= exit;
    }

    private readonly record struct ClippedRay(Point Start, Point End, double Enter, double Exit);
}
