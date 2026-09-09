using System.Collections.Immutable;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Calculations;

public sealed record CameraTerrainFrame(double PitchDegrees, double VerticalFovDegrees, double MinimumCoveragePercent)
{
    public double Lower => PitchDegrees - VerticalFovDegrees / 2;
    public double Upper => PitchDegrees + VerticalFovDegrees / 2;
    public double Threshold => Math.Clamp(MinimumCoveragePercent / 100, 0, .5);
    public double ThresholdAltitude => Lower + VerticalFovDegrees * Threshold;

    public FramingTerrainObstructionSample Scan(TerrainHorizonProfile terrain, double bearing,
        double[]? depth = null, int column = 0, int width = 1)
    {
        var sightline = terrain.SightlineAt(bearing);
        double? horizon = null, first = null;
        var row = CameraTerrainDepth.Rows - 1;
        foreach (var point in sightline)
        {
            if (point.DistanceMetres <= 0 || point.DistanceMetres > LocalHorizonCalculator.MaximumTerrainCastDistanceMetres ||
                point.TerrainElevationAngleDegrees is not double angle || !double.IsFinite(angle)) continue;
            horizon = horizon.HasValue ? Math.Max(horizon.Value, angle) : angle;
            if (first is null && angle >= ThresholdAltitude) first = point.DistanceMetres;
            // Fill each vertical bin once, at its first intersection. Rows are top to bottom.
            if (depth is not null)
                while (row >= 0 && Upper - (row + .5) * VerticalFovDegrees / CameraTerrainDepth.Rows <= angle)
                    depth[row-- * width + column] = point.DistanceMetres;
        }
        var raw = horizon.HasValue && VerticalFovDegrees > 0
            ? Math.Clamp((horizon.Value - Lower) / VerticalFovDegrees, 0, 1) : 0;
        var effective = raw <= Threshold ? 0 : (raw - Threshold) / (1 - Threshold);
        var hit = effective > 0 && first is > 0 and < LocalHorizonCalculator.MaximumTerrainCastDistanceMetres;
        return new(Angles.NormaliseDegrees(bearing), hit, hit ? first : null,
            EffectiveCoverage: effective, RawCoverage: raw, FrameTerrainHorizonDegrees: horizon,
            ThresholdAltitudeDegrees: ThresholdAltitude);
    }
}
