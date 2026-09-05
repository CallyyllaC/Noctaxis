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

        Assert.Null(result.WeatherVisibilityDistanceMetres);
        Assert.False(result.IsTargetTerrainObstructed);
        Assert.Equal("Visibility data unavailable", result.Status);
    }

    [Fact]
    public void ValidWeatherVisibility_IsConvertedFromKilometresToMetres()
    {
        var result = _calculator.Calculate(Weather(8.2), Terrain(0, false), 15, 90);

        Assert.Equal(8_200, result.WeatherVisibilityDistanceMetres!.Value, 6);
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
        Assert.Null(result.WeatherVisibilityDistanceMetres);
    }

    [Fact]
    public void TargetBelowTerrainHorizon_IsMarkedAsAngularObstruction()
    {
        var result = _calculator.Calculate(Weather(null), Terrain(12, true), 9.6, 90);

        Assert.True(result.IsTargetTerrainObstructed);
        Assert.Equal(-2.4, result.TerrainClearanceDegrees!.Value, 6);
        Assert.Equal("Below terrain horizon by 2.4°", result.Status);
        Assert.Null(result.WeatherVisibilityDistanceMetres);
    }

    [Fact]
    public void TargetAboveTerrainHorizon_IsNotObstructed()
    {
        var result = _calculator.Calculate(Weather(null), Terrain(4, true), 9.6, 90);

        Assert.False(result.IsTargetTerrainObstructed);
        Assert.Equal(5.6, result.TerrainClearanceDegrees!.Value, 6);
    }

    [Theory]
    [InlineData(-0.1, 5, false)]
    [InlineData(3, 1, true)]
    [InlineData(1, 3, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 1.999999999, true)]
    [InlineData(2, 2.000000001, false)]
    public void AstronomicalOccultationComparesTargetAltitudeWithTerrainHorizon(
        double terrainHorizon, double targetAltitude, bool expectedObstructed)
    {
        var result = _calculator.Calculate(Weather(null), Terrain(terrainHorizon, true),
            targetAltitude, 90);

        Assert.Equal(expectedObstructed, result.IsTargetTerrainObstructed);
        Assert.Equal(targetAltitude - terrainHorizon, result.TerrainClearanceDegrees!.Value, 10);
        if (targetAltitude == terrainHorizon) Assert.Equal("At terrain horizon", result.Status);
    }

    [Fact]
    public void TerrainObstructionTakesStatusPriorityButRetainsIndependentWeatherLimit()
    {
        var result = _calculator.Calculate(Weather(18), Terrain(8, true), 5, 90);

        Assert.True(result.IsTargetTerrainObstructed);
        Assert.Equal("Below terrain horizon by 3.0°", result.Status);
        Assert.Equal(18_000, result.WeatherVisibilityDistanceMetres);
    }

    [Fact]
    public void AngleOnlyProfileDoesNotInventBaseConeIntersectionDistances()
    {
        var result = _calculator.Calculate(Weather(null), Terrain(12, true), 9.6, 90, 60);

        Assert.Equal(7, result.EffectiveTerrainObstructions.Count);
        Assert.Equal(60, result.EffectiveTerrainObstructions[0].BearingDegrees, 10);
        Assert.Equal(120, result.EffectiveTerrainObstructions[^1].BearingDegrees, 10);
        Assert.All(result.EffectiveTerrainObstructions, sample =>
        {
            Assert.False(sample.IsObstructed);
            Assert.Null(sample.FirstObstructionDistanceMetres);
        });
    }

    [Fact]
    public void SightlineTerrainSamplesMultipleBearingsAndPreservesAsymmetry()
    {
        var result = _calculator.Calculate(Weather(null), AsymmetricSightlineTerrain(), 3, 90, 90);

        Assert.True(result.EffectiveTerrainObstructions.Count >= 10);
        var left = result.EffectiveTerrainObstructions[0];
        var centre = result.EffectiveTerrainObstructions.MinBy(sample =>
            ShortestSeparation(sample.BearingDegrees, 90))!;
        var right = result.EffectiveTerrainObstructions[^1];
        Assert.True(left.FirstObstructionDistanceMetres < centre.FirstObstructionDistanceMetres);
        Assert.True(left.IsObstructed);
        Assert.True(centre.IsObstructed);
        Assert.False(right.IsObstructed);
        Assert.Null(right.FirstObstructionDistanceMetres);
    }

    [Fact]
    public void BlockedClearTransitionsAreRefinedFromCachedProfileWithoutSyntheticFarDistance()
    {
        var result = _calculator.Calculate(Weather(null), AsymmetricSightlineTerrain(), 3, 90, 90, 10);
        var samples = result.EffectiveTerrainObstructions;
        var transitions = new List<(FramingTerrainObstructionSample Left, FramingTerrainObstructionSample Right)>();
        for (var index = 0; index < samples.Count - 1; index++)
            if (samples[index].IsObstructed != samples[index + 1].IsObstructed)
                transitions.Add((samples[index], samples[index + 1]));

        Assert.NotEmpty(transitions);
        Assert.All(transitions, transition => Assert.InRange(
            ShortestSeparation(transition.Left.BearingDegrees, transition.Right.BearingDegrees), 0, .126));
        Assert.All(samples.Where(sample => !sample.IsObstructed), sample =>
            Assert.Null(sample.FirstObstructionDistanceMetres));
        Assert.DoesNotContain(samples, sample =>
            sample.FirstObstructionDistanceMetres == LocalHorizonCalculator.MaximumTerrainCastDistanceMetres);
    }

    [Fact]
    public void ConeSamplesUsePitchIndependentPlanViewTerrainGeometry()
    {
        var terrain = AsymmetricSightlineTerrain();
        const double targetAltitude = 3;
        var result = _calculator.Calculate(Weather(null), terrain, targetAltitude, 90, 20, 10);

        Assert.All(result.EffectiveTerrainObstructions, sample =>
        {
            var expected = terrain.TerrainObstructionAt(sample.BearingDegrees)
                .EffectiveFirstObstructionDistanceMetres;
            Assert.Equal(expected, sample.FirstObstructionDistanceMetres);
            Assert.Empty(sample.EffectiveVisibilitySegments);
        });
    }

    [Fact]
    public void PlanViewHatchingDoesNotDependOnTargetOrCameraPitch()
    {
        var terrain = QuarrySightlineTerrain();
        var horizontal = _calculator.Calculate(Weather(null), terrain, 0, 90, 40, 10);
        var aboveWalls = _calculator.Calculate(Weather(null), terrain, 20, 90, 40, 10,
            verticalFovDegrees: 10);

        Assert.All(horizontal.EffectiveTerrainObstructions, sample => Assert.True(sample.IsObstructed));
        Assert.All(aboveWalls.EffectiveTerrainObstructions, sample => Assert.True(sample.IsObstructed));
        Assert.Equal(horizontal.EffectiveTerrainObstructions, aboveWalls.EffectiveTerrainObstructions);
        Assert.False(aboveWalls.IsTargetTerrainObstructed);
    }

    [Fact]
    public void NegativeHorizonDoesNotCreatePlanViewHatchingOrTargetOccultation()
    {
        var distances = new[] { 1_000d, 9_300d };
        var line = distances.Select(distance =>
            new TerrainSightlineSample(distance, 0, 0, -0.1)).ToArray();
        var terrain = new TerrainHorizonProfile(new GeoCoordinate(53.615275, .140637),
            Enumerable.Range(0, 8).Select(index => new TerrainHorizonSample(
                index * 45d, -0.1, 9_300, Sightline: line)).ToArray(),
            true, "Synthetic coastal water", Now);

        var result = _calculator.Calculate(Weather(null), terrain, 5, 90, 40, 10,
            verticalFovDegrees: 40);

        Assert.All(result.EffectiveTerrainObstructions, sample =>
        {
            Assert.False(sample.IsObstructed);
            Assert.Null(sample.FirstObstructionDistanceMetres);
        });
        Assert.False(result.IsTargetTerrainObstructed);
        Assert.Null(terrain.TerrainObstructionAt(90).EffectiveFirstObstructionDistanceMetres);
        Assert.Null(terrain.OccultationAt(90, 5).EffectiveFirstObstructionDistanceMetres);
    }

    [Fact]
    public void AngularDetailChangesDerivedConeSamplingWithoutChangingTerrainProfile()
    {
        var terrain = AsymmetricSightlineTerrain();
        var cachedSamples = terrain.Samples;

        var tenDegrees = _calculator.Calculate(Weather(null), terrain, 10, 90, 43, 10);
        var fiveDegrees = _calculator.Calculate(Weather(null), terrain, 10, 90, 43, 5);

        Assert.Equal(6, tenDegrees.EffectiveTerrainObstructions.Count);
        Assert.Equal(10, fiveDegrees.EffectiveTerrainObstructions.Count);
        Assert.Same(cachedSamples, terrain.Samples);
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

    private static TerrainHorizonProfile AsymmetricSightlineTerrain()
    {
        var distances = new[] { 1_000d, 2_000d, 3_000d };
        var samples = Enumerable.Range(0, 8).Select(index =>
        {
            var bearing = index * 45d;
            var angles = bearing switch
            {
                45 => new[] { 5d, 4d, 3d },
                90 => new[] { -1d, 5d, 4d },
                _ => new[] { -1d, -1d, -1d }
            };
            var line = distances.Select((distance, point) =>
                new TerrainSightlineSample(distance, null, 0, angles[point])).ToArray();
            var horizon = angles.Max();
            return new TerrainHorizonSample(bearing, horizon, distances[Array.IndexOf(angles, horizon)],
                Sightline: line);
        }).ToArray();
        return new TerrainHorizonProfile(new GeoCoordinate(0, 0), samples, true, "Synthetic", Now);
    }

    private static TerrainHorizonProfile QuarrySightlineTerrain()
    {
        var distances = new[] { 500d, 1_000d, 1_500d };
        var angles = new[] { -2d, 12d, 8d };
        var line = distances.Select((distance, index) =>
            new TerrainSightlineSample(distance, null, 0, angles[index])).ToArray();
        var samples = Enumerable.Range(0, 8).Select(index => new TerrainHorizonSample(
            index * 45d, 12, 1_000, Sightline: line)).ToArray();
        return new TerrainHorizonProfile(new GeoCoordinate(0, 0), samples, true,
            "Synthetic quarry", Now);
    }

    private static double ShortestSeparation(double first, double second)
    {
        var difference = Math.Abs(first - second);
        return Math.Min(difference, 360 - difference);
    }
}
