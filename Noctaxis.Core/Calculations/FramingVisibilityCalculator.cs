using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Calculations;

public interface IFramingVisibilityCalculator
{
    FramingVisibilityAssessment Calculate(
        WeatherResult weather,
        TerrainHorizonProfile terrain,
        double targetAltitudeDegrees,
        double cameraBearingDegrees,
        double horizontalFovDegrees = 0,
        double terrainCastAngularDetailDegrees = CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees,
        double verticalFovDegrees = 0);
}

/// <summary>
/// Assesses practical framing visibility without modifying optical field-of-view geometry.
/// Terrain and surface sightlines provide radial obstruction boundaries; weather remains an
/// independent visibility effect and neither changes the geographic extent of the camera cone.
/// </summary>
public sealed class FramingVisibilityCalculator(ILocalHorizonCalculator? localHorizon = null) : IFramingVisibilityCalculator
{
    public const double MaximumValidVisibilityKilometres = 200;
    private const double TransitionProbeStepDegrees = 1;
    private const double TransitionRefinementDegrees = 0.125;
    private readonly ILocalHorizonCalculator _localHorizon = localHorizon ?? new LocalHorizonCalculator();

    public FramingVisibilityAssessment Calculate(
        WeatherResult weather,
        TerrainHorizonProfile terrain,
        double targetAltitudeDegrees,
        double cameraBearingDegrees,
        double horizontalFovDegrees = 0,
        double terrainCastAngularDetailDegrees = CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees,
        double verticalFovDegrees = 0)
    {
        // Retained in the public calculation contract for callers that report optical framing,
        // but plan-view terrain geometry is deliberately independent of camera pitch/FOV height.
        _ = verticalFovDegrees;
        var terrainAvailable = terrain.HasTerrainCoverage && terrain.Samples.Count > 0;
        double? terrainHorizon = terrainAvailable ? terrain.EffectiveAltitudeAt(cameraBearingDegrees) : null;
        double? clearance = terrainHorizon.HasValue ? targetAltitudeDegrees - terrainHorizon.Value : null;
        // Equality is treated as occulted: a sightline tangent to the resolved terrain surface
        // has no positive angular clearance.
        var terrainObstructed = clearance is <= 0;
        var terrainObstructions = terrainAvailable
            ? SampleTerrainObstructions(terrain, cameraBearingDegrees, horizontalFovDegrees,
                terrainCastAngularDetailDegrees)
            : [];

        var visibility = weather.State == DataState.Ready && weather.Conditions is { IsStale: false }
            ? weather.Conditions.VisibilityKilometres
            : null;
        double? weatherVisibilityDistanceMetres = IsValidVisibility(visibility)
            ? visibility!.Value * 1_000
            : null;

        var status = terrainObstructed
            ? Math.Abs(clearance!.Value) <= 1e-12
                ? "At terrain horizon"
                : $"Below terrain horizon by {Math.Abs(clearance.Value):F1}°"
            : weatherVisibilityDistanceMetres.HasValue
                ? $"Weather visibility: {visibility!.Value:F1} km"
                : "Visibility data unavailable";

        return new FramingVisibilityAssessment(
            terrainObstructed,
            clearance,
            terrainHorizon,
            weatherVisibilityDistanceMetres,
            status,
            terrainObstructions);
    }

    private IReadOnlyList<FramingTerrainObstructionSample> SampleTerrainObstructions(
        TerrainHorizonProfile terrain,
        double centreBearingDegrees,
        double horizontalFovDegrees,
        double angularDetailDegrees)
    {
        var fov = double.IsFinite(horizontalFovDegrees) ? Math.Clamp(horizontalFovDegrees, 0, 179) : 0;
        var profiles = fov > 0
            ? _localHorizon.GetConeProfiles(terrain, centreBearingDegrees, fov, angularDetailDegrees)
            : [_localHorizon.GetRayProfile(terrain, centreBearingDegrees)];
        var coarseSamples = new FramingTerrainObstructionSample[profiles.Count];
        for (var index = 0; index < profiles.Count; index++)
            coarseSamples[index] = TerrainConeSampleAt(
                terrain, profiles[index].BearingDegrees);
        if (coarseSamples.Length < 2) return coarseSamples;

        var samples = new List<FramingTerrainObstructionSample>(coarseSamples.Length + 8)
        {
            coarseSamples[0]
        };
        for (var index = 0; index < coarseSamples.Length - 1; index++)
        {
            AddRefinedTransitions(terrain,
                coarseSamples[index], coarseSamples[index + 1], samples);
            AddIfDistinct(samples, coarseSamples[index + 1]);
        }
        return samples;
    }

    private static FramingTerrainObstructionSample TerrainConeSampleAt(
        TerrainHorizonProfile terrain,
        double bearingDegrees)
    {
        var bearing = Angles.NormaliseDegrees(bearingDegrees);
        var obstruction = terrain.TerrainObstructionAt(bearing);
        var effective = obstruction.EffectiveFirstObstructionDistanceMetres;
        var obstructed = effective is double distance && double.IsFinite(distance) && distance > 0 &&
                         distance < LocalHorizonCalculator.MaximumTerrainCastDistanceMetres;
        return new FramingTerrainObstructionSample(
            bearing,
            obstructed,
            obstructed ? effective : null);
    }

    private static void AddRefinedTransitions(
        TerrainHorizonProfile terrain,
        FramingTerrainObstructionSample left,
        FramingTerrainObstructionSample right,
        ICollection<FramingTerrainObstructionSample> destination)
    {
        var sweep = Angles.NormaliseDegrees(right.BearingDegrees - left.BearingDegrees);
        if (sweep <= 1e-9) return;
        var probeCount = Math.Max(1, (int)Math.Ceiling(sweep / TransitionProbeStepDegrees));
        var previousBearing = left.BearingDegrees;
        var previous = left;
        for (var probeIndex = 1; probeIndex <= probeCount; probeIndex++)
        {
            var currentBearing = left.BearingDegrees + sweep * probeIndex / probeCount;
            var current = probeIndex == probeCount
                ? right
                : TerrainConeSampleAt(terrain, currentBearing);
            if (previous.IsObstructed != current.IsObstructed)
            {
                var lowBearing = previousBearing;
                var highBearing = currentBearing;
                var low = previous;
                var high = current;
                while (highBearing - lowBearing > TransitionRefinementDegrees)
                {
                    var middleBearing = (lowBearing + highBearing) / 2;
                    var middle = TerrainConeSampleAt(terrain, middleBearing);
                    if (middle.IsObstructed == low.IsObstructed)
                    {
                        lowBearing = middleBearing;
                        low = middle;
                    }
                    else
                    {
                        highBearing = middleBearing;
                        high = middle;
                    }
                }
                AddIfDistinct(destination, low);
                AddIfDistinct(destination, high);
            }
            previousBearing = currentBearing;
            previous = current;
        }
    }

    private static void AddIfDistinct(
        ICollection<FramingTerrainObstructionSample> destination,
        FramingTerrainObstructionSample sample)
    {
        if (destination.LastOrDefault() is { } last &&
            Math.Abs(Angles.NormaliseDegrees(sample.BearingDegrees - last.BearingDegrees)) < 1e-7)
            return;
        destination.Add(sample);
    }

    public static bool IsValidVisibility(double? visibilityKilometres) =>
        visibilityKilometres is double value &&
        double.IsFinite(value) &&
        value > 0 &&
        value <= MaximumValidVisibilityKilometres;
}
