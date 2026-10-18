using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Locations.Descriptions;

/// <summary>A displacement in metres on the local tangent plane: east and north of an origin.</summary>
public readonly record struct LocalOffset(double East, double North)
{
    public double Length => Math.Sqrt(East * East + North * North);
    public static LocalOffset operator -(LocalOffset a, LocalOffset b) => new(a.East - b.East, a.North - b.North);
    public static LocalOffset operator +(LocalOffset a, LocalOffset b) => new(a.East + b.East, a.North + b.North);
    public static LocalOffset operator *(LocalOffset a, double s) => new(a.East * s, a.North * s);
    public double Dot(LocalOffset other) => East * other.East + North * other.North;
}

/// <summary>Where a feature's geometry comes closest to the observer.</summary>
/// <param name="Nearest">The closest point, relative to the observer.</param>
/// <param name="Inside">The observer is inside an area feature (distance 0).</param>
/// <param name="AlongPartMetres">For lines: distance along the closest part to the closest point.</param>
/// <param name="PartLengthMetres">For lines: the closest part's length.</param>
public readonly record struct GeometryProximity(
    double DistanceMetres,
    LocalOffset Nearest,
    bool Inside,
    int PartIndex,
    double AlongPartMetres,
    double PartLengthMetres);

/// <summary>
/// Spatial reasoning for descriptions: distance, bearing, containment and bank side, computed on a local tangent plane
/// around the observer. The plane is accurate to well under a metre over the few kilometres a description spans.
/// </summary>
public static class GeographicSpatialAnalysis
{
    private const double EarthRadiusMetres = 6_371_008.8;
    private const double DegreesToRadians = Math.PI / 180;

    public static LocalOffset Project(GeoCoordinate origin, GeographicPoint point)
    {
        var east = Angles.NormaliseSignedDegrees(point.Longitude - origin.Longitude) * DegreesToRadians *
                   Math.Cos(origin.Latitude * DegreesToRadians) * EarthRadiusMetres;
        var north = (point.Latitude - origin.Latitude) * DegreesToRadians * EarthRadiusMetres;
        return new LocalOffset(east, north);
    }

    /// <summary>Compass bearing (0° north, clockwise) of an offset.</summary>
    public static double Bearing(LocalOffset offset) =>
        Angles.NormaliseDegrees(Math.Atan2(offset.East, offset.North) / DegreesToRadians);

    /// <summary>The 8-point compass point nearest a bearing.</summary>
    public static CompassPoint ToCompass(double bearingDegrees) =>
        (CompassPoint)((int)Math.Round(Angles.NormaliseDegrees(bearingDegrees) / 45) % 8);

    /// <summary>The cardinal point within <paramref name="toleranceDegrees"/> of a bearing, or null between them.</summary>
    public static CompassPoint? ToCardinal(double bearingDegrees, double toleranceDegrees)
    {
        var bearing = Angles.NormaliseDegrees(bearingDegrees);
        var nearest = Math.Round(bearing / 90) * 90;
        if (Math.Abs(bearing - nearest) > toleranceDegrees) return null;
        return (CompassPoint)((int)(nearest / 90) % 4 * 2);
    }

    public static GeometryProximity Measure(GeographicFeature feature, GeoCoordinate observer)
    {
        var parts = feature.Parts.Select(part => part.Select(point => Project(observer, point)).ToArray()).ToArray();
        return feature.Geometry switch
        {
            GeographicGeometryKind.Point => MeasurePoints(parts),
            GeographicGeometryKind.Line => MeasureLines(parts, closed: false),
            _ => Contains(parts, default)
                ? new GeometryProximity(0, default, true, -1, 0, 0)
                : MeasureLines(parts, closed: true)
        };
    }

    /// <summary>Even-odd containment of <paramref name="point"/> in rings given on the local plane.</summary>
    public static bool Contains(IReadOnlyList<LocalOffset[]> rings, LocalOffset point)
    {
        var inside = false;
        foreach (var ring in rings)
        {
            if (ring.Length < 3) continue;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.North > point.North) != (b.North > point.North) &&
                    point.East < (b.East - a.East) * (point.North - a.North) / (b.North - a.North) + a.East)
                    inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// The bank of a watercourse the observer is on, or null when the geometry cannot support the claim: the closest
    /// point must lie well inside a part (a part's ends may be clipping, not the river's end), the course must run
    /// nearly straight over <paramref name="windowMetres"/> either side, and the side must be close to a cardinal
    /// direction. A generalised centreline gives the side of the course, never the water's edge.
    /// </summary>
    public static CompassPoint? Bank(GeographicFeature feature, GeoCoordinate observer, GeometryProximity proximity,
        double windowMetres = 120)
    {
        if (feature.Geometry != GeographicGeometryKind.Line || proximity.PartIndex < 0 || proximity.Inside) return null;
        if (proximity.DistanceMetres < Math.Max(20, 3 * feature.DetailMetres)) return null;
        if (proximity.AlongPartMetres < windowMetres || proximity.PartLengthMetres - proximity.AlongPartMetres < windowMetres)
            return null;
        var part = feature.Parts[proximity.PartIndex].Select(point => Project(observer, point)).ToArray();
        var before = PointAlong(part, proximity.AlongPartMetres - windowMetres);
        var after = PointAlong(part, proximity.AlongPartMetres + windowMetres);
        var chord = after - before;
        if (chord.Length < 2 * windowMetres * .85) return null; // Meandering here: no side is clear.
        // The observer is at the origin; the offset from the course to the observer gives the side.
        var toObserver = proximity.Nearest * -1;
        var normal = new LocalOffset(-chord.North, chord.East);
        if (normal.Dot(toObserver) < 0) normal *= -1;
        return ToCardinal(Bearing(normal), 30);
    }

    private static LocalOffset PointAlong(LocalOffset[] line, double distance)
    {
        var travelled = 0d;
        for (var i = 1; i < line.Length; i++)
        {
            var segment = (line[i] - line[i - 1]).Length;
            if (travelled + segment >= distance && segment > 0)
                return line[i - 1] + (line[i] - line[i - 1]) * ((distance - travelled) / segment);
            travelled += segment;
        }
        return line[^1];
    }

    private static GeometryProximity MeasurePoints(LocalOffset[][] parts)
    {
        var best = new GeometryProximity(double.PositiveInfinity, default, false, -1, 0, 0);
        for (var p = 0; p < parts.Length; p++)
            foreach (var point in parts[p])
                if (point.Length < best.DistanceMetres)
                    best = new GeometryProximity(point.Length, point, false, p, 0, 0);
        return best;
    }

    private static GeometryProximity MeasureLines(LocalOffset[][] parts, bool closed)
    {
        var best = new GeometryProximity(double.PositiveInfinity, default, false, -1, 0, 0);
        for (var p = 0; p < parts.Length; p++)
        {
            var part = parts[p];
            if (part.Length == 0) continue;
            var length = 0d;
            var candidate = best;
            var segments = closed ? part.Length : part.Length - 1;
            if (part.Length == 1)
            {
                if (part[0].Length < best.DistanceMetres) best = new GeometryProximity(part[0].Length, part[0], false, p, 0, 0);
                continue;
            }
            var found = false;
            for (var i = 0; i < segments; i++)
            {
                var a = part[i];
                var b = part[(i + 1) % part.Length];
                var ab = b - a;
                var segment = ab.Length;
                var t = segment == 0 ? 0 : Math.Clamp(-a.Dot(ab) / (segment * segment), 0, 1);
                var nearest = a + ab * t;
                if (nearest.Length < candidate.DistanceMetres)
                {
                    candidate = new GeometryProximity(nearest.Length, nearest, false, p, length + segment * t, 0);
                    found = true;
                }
                length += segment;
            }
            if (found) best = candidate with { PartLengthMetres = length };
        }
        return best;
    }
}
