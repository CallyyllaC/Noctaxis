using System.Collections.Immutable;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Controls;

public sealed record PlanTerrainBand(double StartDistanceMetres, double EndDistanceMetres,
    double ApparentAltitudeDegrees, double Strength);

public sealed record PlanTerrainRay(double BearingDegrees, double LeftOffset, double RightOffset,
    ImmutableArray<PlanTerrainBand> Bands);

public sealed record PlanTerrainPatch(double LeftOffset, double RightOffset,
    double LeftStart, double RightStart, double LeftEnd, double RightEnd,
    double Strength, ImmutableArray<GeoCoordinate> Corners)
{
    // Colour metadata only. Strength remains the original topology key so changing
    // the display curve cannot merge/split or move any polygon.
    public double ApparentAltitudeDegrees { get; init; }

    public bool Contains(double offset, double distance)
    {
        if (offset < LeftOffset || offset > RightOffset) return false;
        var t = (offset - LeftOffset) / (RightOffset - LeftOffset);
        return distance >= LeftStart + (RightStart - LeftStart) * t &&
               distance < LeftEnd + (RightEnd - LeftEnd) * t;
    }
}

/// <summary>Cumulative skyline presentation using the frame's bearings/bounds and existing sightlines.</summary>
public sealed record PlanTerrainFan(ImmutableArray<PlanTerrainRay> Rays, bool GroundFacing,
    ImmutableArray<GeoCoordinate> GroundOutline)
{
    public ImmutableArray<PlanTerrainPatch> Patches { get; init; } = [];
    public int SamplesInspected { get; init; }

    // Preserve the original horizon-anchored coordinate for matching mesh levels.
    // Draw-time contrast is separate in TerrainFanTone and cannot alter topology.
    public static double ToneStrength(double angle, double verticalFov) =>
        Math.Clamp(angle / Math.Max(.001, verticalFov / 2), 0, 1);

    public static PlanTerrainFan Build(GeoSector sector, TerrainHorizonProfile terrain, CameraTerrainDepth depth)
    {
        var ground = depth.UpperAltitude <= 0;
        var rays = ImmutableArray.CreateBuilder<PlanTerrainRay>(depth.Width);
        var offsets = depth.Bearings.Select(b => Angles.NormaliseDegrees(b - sector.LeftBearingDegrees)).ToArray();
        var inspected = 0;
        for (var column = 0; column < depth.Width; column++)
        {
            var offset = offsets[column];
            if (offset > sector.HorizontalFovDegrees + 1e-6) continue;
            var left = column == 0 ? 0 : (offsets[column - 1] + offset) / 2;
            var right = column == depth.Width - 1 ? sector.HorizontalFovDegrees : (offset + offsets[column + 1]) / 2;
            var bands = ImmutableArray.CreateBuilder<PlanTerrainBand>();
            var highest = 0d;
            if (!ground)
            foreach (var point in terrain.SightlineAt(depth.Bearings[column]))
            {
                inspected++;
                if (!double.IsFinite(point.DistanceMetres) || point.DistanceMetres <= 0 ||
                    point.DistanceMetres >= sector.DistanceMetres ||
                    point.TerrainElevationAngleDegrees is not double angle || !double.IsFinite(angle) ||
                    angle <= highest) continue;
                // All foreground terrain occludes, including terrain above the frame.
                highest = angle;
                if (angle < depth.LowerAltitude) continue;
                if (angle > depth.UpperAltitude)
                {
                    // An off-image peak can still have a visible foreground surface.
                    // Only the existing first-intersection rows can establish that;
                    // retain the full peak in 'highest' to occlude farther lower terrain.
                    var visibleAngle = double.NaN;
                    for (var row = 0; row < CameraTerrainDepth.Rows; row++)
                        if (depth.AltitudeAtRow(row) > 0 && depth.DistanceAt(column, row) == point.DistanceMetres)
                        { visibleAngle = depth.AltitudeAtRow(row); break; }
                    if (!double.IsFinite(visibleAngle)) continue;
                    angle = visibleAngle;
                }
                if (bands.Count > 0)
                    bands[^1] = bands[^1] with { EndDistanceMetres = point.DistanceMetres };
                bands.Add(new(point.DistanceMetres, sector.DistanceMetres, angle,
                    ToneStrength(angle, depth.UpperAltitude - depth.LowerAltitude)));
            }
            rays.Add(new(depth.Bearings[column], left, right, bands.ToImmutable()));
        }
        var result = rays.ToImmutable();
        return new(result, ground, ground
            ? new GeographicOverlayGeometryBuilder().BuildCameraBaseFill(sector).ToImmutableArray() : [])
        { Patches = Connect(sector, result), SamplesInspected = inspected };
    }

    private static ImmutableArray<PlanTerrainPatch> Connect(GeoSector sector, ImmutableArray<PlanTerrainRay> rays)
    {
        var patches = ImmutableArray.CreateBuilder<PlanTerrainPatch>();
        for (var i = 1; i < rays.Length; i++)
        {
            var a = rays[i - 1]; var b = rays[i];
            var left = Angles.NormaliseDegrees(a.BearingDegrees - sector.LeftBearingDegrees);
            var right = Angles.NormaliseDegrees(b.BearingDegrees - sector.LeftBearingDegrees);
            if (right <= left) continue;
            // Match by cumulative strength, never by transition index. Missing levels
            // meet the cone edge, forming a taper within this single angular interval.
            var levels = a.Bands.Concat(b.Bands).Select(x => x.Strength).Distinct().Order().ToArray();
            var levelAngles = a.Bands.Concat(b.Bands).GroupBy(x => x.Strength)
                .ToDictionary(g => g.Key, g => g.Min(x => x.ApparentAltitudeDegrees));
            var aIndex = 0; var bIndex = 0;
            double Frontier(PlanTerrainRay ray, double level, ref int index)
            {
                while (index < ray.Bands.Length && ray.Bands[index].Strength < level) index++;
                return index < ray.Bands.Length ? ray.Bands[index].StartDistanceMetres : sector.DistanceMetres;
            }
            var starts = levels.Select(level => (A: Frontier(a, level, ref aIndex),
                B: Frontier(b, level, ref bIndex))).Append((A: sector.DistanceMetres, B: sector.DistanceMetres)).ToArray();
            for (var level = 0; level < levels.Length; level++)
            {
                var start = starts[level]; var end = starts[level + 1];
                if (start.A >= end.A && start.B >= end.B) continue;
                var corners = ImmutableArray.CreateBuilder<GeoCoordinate>();
                // Shared fixed radial subdivisions follow long geodesic sides without
                // doing any extra terrain sampling. Adjacent polygons share these edges.
                void Side(double offset, double from, double to)
                {
                    corners.Add(Angles.Destination(sector.Origin, sector.LeftBearingDegrees + offset, from));
                    const double step = MapOverlayGeometry.GeodesicSampleSpacingMetres;
                    if (to > from)
                        for (var d = (Math.Floor(from / step) + 1) * step; d < to; d += step)
                            corners.Add(Angles.Destination(sector.Origin, sector.LeftBearingDegrees + offset, d));
                    else
                        for (var d = (Math.Ceiling(from / step) - 1) * step; d > to; d -= step)
                            corners.Add(Angles.Destination(sector.Origin, sector.LeftBearingDegrees + offset, d));
                    corners.Add(Angles.Destination(sector.Origin, sector.LeftBearingDegrees + offset, to));
                }
                Side(left, end.A, start.A);
                Side(right, start.B, end.B);
                patches.Add(new(left, right, start.A, start.B, end.A, end.B, levels[level], corners.ToImmutable())
                    { ApparentAltitudeDegrees = levelAngles[levels[level]] });
            }
        }
        return patches.ToImmutable();
    }

    public bool ContainsTerrain(double offset, double distance) => Patches.Any(p => p.Contains(offset, distance));
}
