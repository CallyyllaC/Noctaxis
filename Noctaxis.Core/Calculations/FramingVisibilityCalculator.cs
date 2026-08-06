using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Calculations;

public interface IFramingVisibilityCalculator
{
    FramingVisibilityAssessment Calculate(
        WeatherResult weather,
        TerrainHorizonProfile terrain,
        double targetAltitudeDegrees,
        double cameraBearingDegrees);
}

/// <summary>
/// Assesses practical framing visibility without modifying optical field-of-view geometry.
/// Terrain is angular; weather visibility is the only radial limit currently available.
/// </summary>
public sealed class FramingVisibilityCalculator : IFramingVisibilityCalculator
{
    public const double MaximumValidVisibilityKilometres = 200;

    public FramingVisibilityAssessment Calculate(
        WeatherResult weather,
        TerrainHorizonProfile terrain,
        double targetAltitudeDegrees,
        double cameraBearingDegrees)
    {
        var terrainAvailable = terrain.HasDemCoverage && terrain.Samples.Count > 0;
        double? terrainHorizon = terrainAvailable ? terrain.AltitudeAt(cameraBearingDegrees) : null;
        double? clearance = terrainHorizon.HasValue ? targetAltitudeDegrees - terrainHorizon.Value : null;
        var terrainObstructed = clearance is < 0;

        var radialLimits = new List<FramingRadialLimit>();
        var visibility = weather.State == DataState.Ready && weather.Conditions is { IsStale: false }
            ? weather.Conditions.VisibilityKilometres
            : null;
        if (IsValidVisibility(visibility))
        {
            radialLimits.Add(new FramingRadialLimit(
                FramingLimitReason.WeatherVisibility,
                visibility!.Value * 1_000,
                $"Visibility {visibility.Value:F1} km"));
        }

        var status = terrainObstructed
            ? $"Below terrain horizon by {Math.Abs(clearance!.Value):F1}°"
            : radialLimits.Count > 0
                ? $"Weather visibility: {visibility!.Value:F1} km"
                : "Visibility data unavailable";

        return new FramingVisibilityAssessment(
            terrainObstructed,
            clearance,
            terrainHorizon,
            radialLimits,
            status);
    }

    public static bool IsValidVisibility(double? visibilityKilometres) =>
        visibilityKilometres is double value &&
        double.IsFinite(value) &&
        value > 0 &&
        value <= MaximumValidVisibilityKilometres;
}
