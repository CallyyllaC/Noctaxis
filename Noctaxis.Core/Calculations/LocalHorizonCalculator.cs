using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Calculations;

public interface ILocalHorizonCalculator
{
    HorizonRayProfile GetRayProfile(TerrainHorizonProfile terrain, double bearingDegrees,
        double maximumDistanceMetres = LocalHorizonCalculator.MaximumTerrainCastDistanceMetres);
    IReadOnlyList<HorizonRayProfile> GetConeProfiles(TerrainHorizonProfile terrain,
        double centreBearingDegrees, double horizontalFovDegrees, double angularDetailDegrees,
        double maximumDistanceMetres = LocalHorizonCalculator.MaximumTerrainCastDistanceMetres);
    TargetLocalVisibility AssessTarget(TerrainHorizonProfile terrain, double targetAzimuthDegrees,
        double targetAltitudeDegrees);
}

/// <summary>
/// Converts the cached radial elevation sightlines into a reusable running-horizon envelope.
/// Data acquisition remains in the terrain providers; both the camera cone and celestial target
/// presentation consume these same immutable results.
/// </summary>
public sealed class LocalHorizonCalculator : ILocalHorizonCalculator
{
    public const double MaximumTerrainCastDistanceKilometres = 500;
    public const double MaximumTerrainCastDistanceMetres = MaximumTerrainCastDistanceKilometres * 1_000;
    public const double MarginalClearanceDegrees = 1;
    private const double AngleEpsilonDegrees = 1e-9;

    public HorizonRayProfile GetRayProfile(TerrainHorizonProfile terrain, double bearingDegrees,
        double maximumDistanceMetres = MaximumTerrainCastDistanceMetres)
    {
        var requestedMaximum = ValidateMaximumDistance(maximumDistanceMetres);
        var bearing = Angles.NormaliseDegrees(bearingDegrees);
        var sightline = terrain.SightlineAt(bearing);
        if (sightline.Count == 0)
            return new HorizonRayProfile(bearing, null, [], false, requestedMaximum);
        var sampledMaximum = terrain.MaximumAnalysisDistanceMetres > 0
            ? terrain.MaximumAnalysisDistanceMetres
            : sightline.Max(sample => sample.DistanceMetres);
        var maximum = Math.Min(requestedMaximum, sampledMaximum);

        var samples = new List<(double Distance, bool Visible)>(sightline.Count);
        double? runningHorizonSlope = null;
        foreach (var point in sightline)
        {
            if (point.DistanceMetres <= 0 || point.DistanceMetres > maximum) continue;
            var slope = EffectiveSlope(point);
            if (!slope.HasValue) continue;
            var epsilonSlope = AngleEpsilonDegrees * Angles.DegreesToRadians * (1 + slope.Value * slope.Value);
            var visible = !runningHorizonSlope.HasValue || slope.Value >= runningHorizonSlope.Value - epsilonSlope;
            if (!runningHorizonSlope.HasValue || slope.Value > runningHorizonSlope.Value)
                runningHorizonSlope = slope.Value;
            samples.Add((point.DistanceMetres, visible));
        }
        if (samples.Count == 0)
            return new HorizonRayProfile(bearing, null, [], false, maximum);

        var segments = BuildSegments(samples, maximum);
        return new HorizonRayProfile(bearing,
            runningHorizonSlope is double horizon ? Math.Atan(horizon) * Angles.RadiansToDegrees : null,
            segments, true, maximum);
    }

    public IReadOnlyList<HorizonRayProfile> GetConeProfiles(TerrainHorizonProfile terrain,
        double centreBearingDegrees, double horizontalFovDegrees, double angularDetailDegrees,
        double maximumDistanceMetres = MaximumTerrainCastDistanceMetres)
    {
        if (!double.IsFinite(horizontalFovDegrees) || horizontalFovDegrees <= 0 || horizontalFovDegrees >= 180)
            throw new ArgumentOutOfRangeException(nameof(horizontalFovDegrees));
        var detail = Math.Clamp(double.IsFinite(angularDetailDegrees)
                ? angularDetailDegrees
                : CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees,
            CameraFramingSettings.MinimumTerrainCastAngularDetailDegrees,
            CameraFramingSettings.MaximumTerrainCastAngularDetailDegrees);
        var segmentCount = Math.Max(1, (int)Math.Ceiling(horizontalFovDegrees / detail));
        var actualSpacing = horizontalFovDegrees / segmentCount;
        var left = centreBearingDegrees - horizontalFovDegrees / 2;
        var result = new HorizonRayProfile[segmentCount + 1];
        for (var index = 0; index <= segmentCount; index++)
            result[index] = GetRayProfile(terrain, left + actualSpacing * index, maximumDistanceMetres);
        return result;
    }

    public TargetLocalVisibility AssessTarget(TerrainHorizonProfile terrain, double targetAzimuthDegrees,
        double targetAltitudeDegrees)
    {
        if (targetAltitudeDegrees < 0)
            return new TargetLocalVisibility(TargetLocalVisibilityState.BelowAstronomicalHorizon,
                targetAltitudeDegrees, terrain.EffectiveAltitudeAt(targetAzimuthDegrees), null);
        var horizon = terrain.EffectiveAltitudeAt(targetAzimuthDegrees);
        if (!horizon.HasValue)
            return new TargetLocalVisibility(TargetLocalVisibilityState.TerrainUnavailable,
                targetAltitudeDegrees, null, null);
        var clearance = targetAltitudeDegrees - horizon.Value;
        var state = clearance < 0
            ? TargetLocalVisibilityState.TerrainBlocked
            : clearance <= MarginalClearanceDegrees
                ? TargetLocalVisibilityState.Marginal
                : TargetLocalVisibilityState.Clear;
        return new TargetLocalVisibility(state, targetAltitudeDegrees, horizon, clearance);
    }

    private static IReadOnlyList<HorizonVisibilitySegment> BuildSegments(
        IReadOnlyList<(double Distance, bool Visible)> samples, double maximumDistanceMetres)
    {
        var segments = new List<HorizonVisibilitySegment>();
        var state = HorizonVisibilityState.Visible;
        var start = 0d;
        for (var index = 0; index < samples.Count; index++)
        {
            var next = samples[index].Visible
                ? HorizonVisibilityState.Visible
                : HorizonVisibilityState.TerrainOccluded;
            if (next == state) continue;
            var previousDistance = index == 0 ? 0 : samples[index - 1].Distance;
            var boundary = Math.Clamp((previousDistance + samples[index].Distance) / 2,
                start, maximumDistanceMetres);
            if (boundary > start)
                segments.Add(new HorizonVisibilitySegment(start, boundary, state));
            start = boundary;
            state = next;
        }
        if (start < maximumDistanceMetres)
            segments.Add(new HorizonVisibilitySegment(start, maximumDistanceMetres, state));
        return segments;
    }

    private static double? EffectiveSlope(TerrainSightlineSample sample) =>
        sample.TerrainElevationSlope;

    private static double ValidateMaximumDistance(double maximumDistanceMetres)
    {
        if (!double.IsFinite(maximumDistanceMetres) || maximumDistanceMetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDistanceMetres));
        return Math.Min(maximumDistanceMetres, MaximumTerrainCastDistanceMetres);
    }
}
