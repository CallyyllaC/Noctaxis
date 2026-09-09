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
        double verticalFovDegrees = 0, CameraTerrainFrame? cameraFrame = null);
}

/// <summary>
/// Assesses practical framing visibility without modifying optical field-of-view geometry.
/// Terrain and surface sightlines provide radial obstruction boundaries; weather remains an
/// independent visibility effect and neither changes the geographic extent of the camera cone.
/// </summary>
public sealed class FramingVisibilityCalculator : IFramingVisibilityCalculator
{
    public const double MaximumValidVisibilityKilometres = 200;
    private const double TransitionProbeStepDegrees = 1;
    private const double TransitionRefinementDegrees = 0.125;

    public FramingVisibilityAssessment Calculate(
        WeatherResult weather,
        TerrainHorizonProfile terrain,
        double targetAltitudeDegrees,
        double cameraBearingDegrees,
        double horizontalFovDegrees = 0,
        double terrainCastAngularDetailDegrees = CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees,
        double verticalFovDegrees = 0, CameraTerrainFrame? cameraFrame = null)
    {
        // Legacy callers without a camera frame retain the horizontal diagnostic path.
        // Production supplies independent pitch and vertical FoV through cameraFrame.
        _ = verticalFovDegrees;
        // Legacy callers may still supply this argument; production always consumes 1° detail.
        _ = terrainCastAngularDetailDegrees;
        var terrainAvailable = terrain.TerrainCalculationsEnabled && terrain.HasTerrainCoverage && terrain.Samples.Count > 0;
        double? terrainHorizon = terrainAvailable ? terrain.EffectiveAltitudeAt(cameraBearingDegrees) : null;
        double? clearance = terrainHorizon.HasValue ? targetAltitudeDegrees - terrainHorizon.Value : null;
        // Equality is treated as occulted: a sightline tangent to the resolved terrain surface
        // has no positive angular clearance.
        var terrainObstructed = clearance is <= 0;
        CameraTerrainDepth? depth = null;
        var terrainObstructions = terrainAvailable
            ? SampleTerrainObstructions(terrain, cameraBearingDegrees, horizontalFovDegrees,
                CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees, cameraFrame, out depth)
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
            terrainObstructions, depth);
    }

    private IReadOnlyList<FramingTerrainObstructionSample> SampleTerrainObstructions(
        TerrainHorizonProfile terrain,
        double centreBearingDegrees,
        double horizontalFovDegrees,
        double angularDetailDegrees, CameraTerrainFrame? cameraFrame, out CameraTerrainDepth? depth)
    {
        var fov = double.IsFinite(horizontalFovDegrees) ? Math.Clamp(horizontalFovDegrees, 0, 179) : 0;
        var segments = fov > 0 ? Math.Max(1, (int)Math.Ceiling(fov / angularDetailDegrees)) : 0;
        var spacing = segments > 0 ? fov / segments : 0;
        var left = centreBearingDegrees - fov / 2;
        var coarseSamples = new FramingTerrainObstructionSample[segments + 1];
        var pixels = cameraFrame is null ? null : new double[coarseSamples.Length * CameraTerrainDepth.Rows];
        for (var index = 0; index < coarseSamples.Length; index++)
            coarseSamples[index] = TerrainConeSampleAt(
                terrain, left + spacing * index, cameraFrame, pixels, index, coarseSamples.Length);
        depth = pixels is null || !coarseSamples.Any(s => s.FrameTerrainHorizonDegrees.HasValue) ? null
            : new CameraTerrainDepth(System.Collections.Immutable.ImmutableArray.CreateRange(coarseSamples.Select(s => s.BearingDegrees)),
                cameraFrame!.Lower, cameraFrame.Upper, System.Collections.Immutable.ImmutableArray.CreateRange(pixels));
        if (coarseSamples.Length < 2) return coarseSamples;

        var samples = new List<FramingTerrainObstructionSample>(coarseSamples.Length + 8)
        {
            coarseSamples[0]
        };
        for (var index = 0; index < coarseSamples.Length - 1; index++)
        {
            AddRefinedTransitions(terrain,
                coarseSamples[index], coarseSamples[index + 1], samples, cameraFrame);
            AddIfDistinct(samples, coarseSamples[index + 1]);
        }
        return samples;
    }

    private static FramingTerrainObstructionSample TerrainConeSampleAt(
        TerrainHorizonProfile terrain,
        double bearingDegrees, CameraTerrainFrame? cameraFrame = null,
        double[]? depth = null, int column = 0, int width = 1)
    {
        var bearing = Angles.NormaliseDegrees(bearingDegrees);
        if (cameraFrame is not null) return cameraFrame.Scan(terrain, bearing, depth, column, width);
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
        ICollection<FramingTerrainObstructionSample> destination, CameraTerrainFrame? cameraFrame)
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
                : TerrainConeSampleAt(terrain, currentBearing, cameraFrame);
            if (previous.IsObstructed != current.IsObstructed)
            {
                var lowBearing = previousBearing;
                var highBearing = currentBearing;
                var low = previous;
                var high = current;
                while (highBearing - lowBearing > TransitionRefinementDegrees)
                {
                    var middleBearing = (lowBearing + highBearing) / 2;
                    var middle = TerrainConeSampleAt(terrain, middleBearing, cameraFrame);
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
