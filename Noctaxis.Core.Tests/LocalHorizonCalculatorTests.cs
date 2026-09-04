using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed class LocalHorizonCalculatorTests
{
    private readonly LocalHorizonCalculator _calculator = new();

    [Theory]
    [InlineData(40, 10, 5)]
    [InlineData(43, 10, 6)]
    [InlineData(5, 10, 2)]
    [InlineData(170, 45, 5)]
    public void ConeSampling_IncludesBothEdgesWithBoundedEvenSpacing(double fov, double detail,
        int expectedRayCount)
    {
        var rays = _calculator.GetConeProfiles(Profile([1, 2]), 5, fov, detail);

        Assert.Equal(expectedRayCount, rays.Count);
        Assert.Equal(Angles.NormaliseDegrees(5 - fov / 2), rays[0].BearingDegrees, 10);
        Assert.Equal(Angles.NormaliseDegrees(5 + fov / 2), rays[^1].BearingDegrees, 10);
        var spacings = rays.Zip(rays.Skip(1), (left, right) =>
            Angles.NormaliseDegrees(right.BearingDegrees - left.BearingDegrees)).ToArray();
        Assert.All(spacings, spacing => Assert.True(spacing <= detail + 1e-9));
        Assert.All(spacings, spacing => Assert.Equal(spacings[0], spacing, 10));
    }

    [Fact]
    public void RunningHorizon_AllowsMultipleOccludedAndVisibleIntervals()
    {
        var ray = _calculator.GetRayProfile(Profile([1, 5, 3, 2, 7, 6]), 0, 6_000);

        Assert.True(ray.HasTerrainData);
        Assert.Equal(7, ray.HorizonAltitudeDegrees);
        Assert.True(ray.Segments.Count >= 4);
        Assert.Contains(ray.Segments, segment => segment.State == HorizonVisibilityState.TerrainOccluded &&
                                                segment.StartDistanceMetres < 3_000);
        Assert.Contains(ray.Segments, segment => segment.State == HorizonVisibilityState.Visible &&
                                                segment.StartDistanceMetres > 3_000);
        Assert.Equal(HorizonVisibilityState.TerrainOccluded, ray.Segments[^1].State);
    }

    [Fact]
    public void NearbyHillOccludesLowerTerrainButFartherMountainCanReappear()
    {
        var ray = _calculator.GetRayProfile(Profile([0, 8, 4, 3, 10]), 0, 5_000);
        var stateBehindHill = StateAt(ray, 3_000);
        var stateAtMountain = StateAt(ray, 5_000 - 1);

        Assert.Equal(HorizonVisibilityState.TerrainOccluded, stateBehindHill);
        Assert.Equal(HorizonVisibilityState.Visible, stateAtMountain);
        Assert.Equal(10, ray.HorizonAltitudeDegrees);
    }

    [Fact]
    public void TerrainCastNeverExceedsHardCeiling()
    {
        var ray = _calculator.GetRayProfile(Profile([1, 0]), 0, 900_000);

        Assert.True(ray.MaximumDistanceMetres <= LocalHorizonCalculator.MaximumTerrainCastDistanceMetres);
        Assert.True(ray.Segments[^1].EndDistanceMetres <= LocalHorizonCalculator.MaximumTerrainCastDistanceMetres);
    }

    [Theory]
    [InlineData(-3, TargetLocalVisibilityState.BelowAstronomicalHorizon, null)]
    [InlineData(4, TargetLocalVisibilityState.TerrainBlocked, -2d)]
    [InlineData(6.4, TargetLocalVisibilityState.Marginal, .4)]
    [InlineData(12, TargetLocalVisibilityState.Clear, 6d)]
    public void TargetVisibility_DistinguishesAstronomicalAndLocalHorizon(double altitude,
        TargetLocalVisibilityState expected, double? expectedClearance)
    {
        var result = _calculator.AssessTarget(Profile([2, 6, 4]), 0, altitude);

        Assert.Equal(expected, result.State);
        if (expectedClearance.HasValue) Assert.Equal(expectedClearance.Value, result.ClearanceDegrees!.Value, 8);
        else Assert.Null(result.ClearanceDegrees);
    }

    [Fact]
    public void MissingTerrainFallsBackWithoutInventingLocalHorizon()
    {
        var unavailable = new TerrainHorizonProfile(new GeoCoordinate(0, 0), [], false, "Unavailable",
            Instant.FromUtc(2025, 1, 1, 0, 0));

        var result = _calculator.AssessTarget(unavailable, 90, 20);

        Assert.Equal(TargetLocalVisibilityState.TerrainUnavailable, result.State);
        Assert.Null(result.LocalHorizonAltitudeDegrees);
    }

    [Fact]
    public void TargetBearingCanReuseSameConeRayProfile()
    {
        var terrain = Profile([1, 4, 2]);
        var cone = _calculator.GetConeProfiles(terrain, 0, 20, 10);
        var exact = _calculator.GetRayProfile(terrain, 0);

        var coneRay = Assert.Single(cone, ray => Math.Abs(Angles.NormaliseDegrees(ray.BearingDegrees) - 0) < 1e-9);
        Assert.Equal(exact.HorizonAltitudeDegrees, coneRay.HorizonAltitudeDegrees);
        Assert.Equal(exact.Segments, coneRay.Segments);
    }

    private static HorizonVisibilityState StateAt(HorizonRayProfile profile, double distance) =>
        profile.Segments.Single(segment => distance >= segment.StartDistanceMetres &&
                                           distance <= segment.EndDistanceMetres).State;

    private static TerrainHorizonProfile Profile(double[] angles)
    {
        var line = angles.Select((angle, index) =>
            new TerrainSightlineSample((index + 1) * 1_000, null, 0, angle)).ToArray();
        var maximum = angles.Max();
        var samples = Enumerable.Range(0, 4).Select(index => new TerrainHorizonSample(index * 90,
            maximum, (Array.IndexOf(angles, maximum) + 1) * 1_000, Sightline: line)).ToArray();
        return new TerrainHorizonProfile(new GeoCoordinate(0, 0), samples, true, "Synthetic",
            Instant.FromUtc(2025, 1, 1, 0, 0), MaximumAnalysisDistanceMetres: angles.Length * 1_000);
    }
}
