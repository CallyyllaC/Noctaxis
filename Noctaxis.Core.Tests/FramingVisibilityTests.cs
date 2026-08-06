using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed class FramingVisibilityTests
{
    private static readonly Instant Now = Instant.FromUtc(2025, 1, 1, 20, 0);
    private readonly FramingVisibilityCalculator _calculator = new();

    [Fact]
    public void MissingWeatherVisibility_DoesNotCreateRadialLimit()
    {
        var result = _calculator.Calculate(Weather(null), Terrain(0, false), 15, 90);

        Assert.Empty(result.RadialLimits);
        Assert.False(result.IsTargetTerrainObstructed);
        Assert.Equal("Visibility data unavailable", result.Status);
    }

    [Fact]
    public void ValidWeatherVisibility_IsConvertedFromKilometresToMetres()
    {
        var result = _calculator.Calculate(Weather(8.2), Terrain(0, false), 15, 90);

        var limit = Assert.Single(result.RadialLimits);
        Assert.Equal(FramingLimitReason.WeatherVisibility, limit.Reason);
        Assert.Equal(8_200, limit.DistanceMetres, 6);
        Assert.Equal("Weather visibility: 8.2 km", result.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(9999)]
    public void InvalidOrSentinelVisibility_DoesNotCreateLimit(double visibility)
    {
        var result = _calculator.Calculate(Weather(visibility), Terrain(0, false), 15, 90);
        Assert.Empty(result.RadialLimits);
    }

    [Fact]
    public void TargetBelowTerrainHorizon_IsMarkedAsAngularObstruction()
    {
        var result = _calculator.Calculate(Weather(null), Terrain(12, true), 9.6, 90);

        Assert.True(result.IsTargetTerrainObstructed);
        Assert.Equal(-2.4, result.TerrainClearanceDegrees!.Value, 6);
        Assert.Equal("Below terrain horizon by 2.4°", result.Status);
        Assert.Empty(result.RadialLimits);
    }

    [Fact]
    public void TargetAboveTerrainHorizon_IsNotObstructed()
    {
        var result = _calculator.Calculate(Weather(null), Terrain(4, true), 9.6, 90);

        Assert.False(result.IsTargetTerrainObstructed);
        Assert.Equal(5.6, result.TerrainClearanceDegrees!.Value, 6);
    }

    [Fact]
    public void TerrainObstructionTakesStatusPriorityButRetainsIndependentWeatherLimit()
    {
        var result = _calculator.Calculate(Weather(18), Terrain(8, true), 5, 90);

        Assert.True(result.IsTargetTerrainObstructed);
        Assert.Equal("Below terrain horizon by 3.0°", result.Status);
        Assert.Equal(18_000, Assert.Single(result.RadialLimits).DistanceMetres);
    }

    [Fact]
    public void VisibilityAssessmentDoesNotChangeOpticalFieldOfView()
    {
        var fieldOfView = new LensCalculator().Calculate(new LensConfiguration(FocalLengthMillimetres: 200));
        var guide = new CameraFramingGuideCalculator().Calculate(fieldOfView, 90, new CameraFramingSettings());
        _ = _calculator.Calculate(Weather(5), Terrain(20, true), 10, 90);

        Assert.Equal(fieldOfView.HorizontalDegrees, guide.HorizontalFieldOfViewDegrees, 10);
        Assert.Equal(fieldOfView.HorizontalDegrees,
            ShortestSeparation(guide.LeftEdgeBearingDegrees, guide.RightEdgeBearingDegrees), 10);
    }

    private static WeatherResult Weather(double? visibility, bool stale = false) => new(
        DataState.Ready,
        new WeatherConditions(Now, null, null, null, null, null, null, null, null, null, null,
            null, null, null, visibility, "Test", Now, stale),
        "Test");

    private static TerrainHorizonProfile Terrain(double altitude, bool hasCoverage) => new(
        new GeoCoordinate(0, 0),
        Enumerable.Range(0, 8).Select(index => new TerrainHorizonSample(index * 45, altitude)).ToArray(),
        hasCoverage,
        hasCoverage ? "Synthetic terrain" : "Flat fallback",
        Now);

    private static double ShortestSeparation(double first, double second)
    {
        var difference = Math.Abs(first - second);
        return Math.Min(difference, 360 - difference);
    }
}
