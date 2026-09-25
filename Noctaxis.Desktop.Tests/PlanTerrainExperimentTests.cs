using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed class PlanTerrainExperimentTests
{
    private static TerrainHorizonProfile Profile(Func<int, double> angle) => new(
        new(53, -1), Enumerable.Range(0, 360).Select(b => new TerrainHorizonSample(b, angle(b), null,
            Sightline: [new TerrainSightlineSample(500, 10, 0, angle(b)),
                        new TerrainSightlineSample(5000, 100, 0, angle(b) + .1)])).ToArray(),
        true, "Synthetic", Instant.FromUtc(2026, 1, 1, 0, 0));

    [Theory]
    [InlineData(-1, false)] [InlineData(0, false)] [InlineData(.001, true)]
    [InlineData(1, true)] [InlineData(30, true)]
    public void OnlyPositiveHorizonContributes(double angle, bool expected)
    {
        var terrain = Profile(_ => angle);
        var samples = EnvironmentalOverlayStateFactory.PlanSamples(new(terrain.Observer, 0, 24, 500000), terrain);
        Assert.Equal(25, samples.Length);
        Assert.All(samples, s =>
        {
            Assert.Equal(expected, s.IsObstructed);
            Assert.Equal(expected ? 500d : (double?)null, s.ObstructionDistanceMetres);
            Assert.InRange(s.EffectiveCoverage, 0, 1);
            if (expected) Assert.True(TerrainTintPresentation.DisplayStrength(s.EffectiveCoverage) >= .45);
        });
    }

    [Fact]
    public void PitchSelectsVisibleDepthWhileTargetAltitudeStillReusesPlanState()
    {
        var terrain = Profile(_ => 3);
        var sector = new GeoSector(terrain.Observer, .35, 24, 500000);
        var key = EnvironmentalOverlayStateFactory.CreateProfileKey(terrain, 1);
        var coordinator = new EnvironmentalOverlayStateCoordinator();
        var calculator = new FramingVisibilityCalculator();
        foreach (var pitch in new[] { -30d, 0, 30 })
        {
        EnvironmentalOverlayState? first = null;
        foreach (var altitude in new[] { -10d, 40 })
        {
            var visibility = calculator.Calculate(new(DataState.Loading, null, "Offline"), terrain,
                altitude, .35, 24, cameraFrame: new(pitch, 20, 5));
            Assert.Equal(altitude < 3, visibility.IsTargetTerrainObstructed);
            Assert.Equal(pitch < 30, visibility.EffectiveTerrainObstructions.Any(s => s.IsObstructed));
            var state = coordinator.Update(terrain.Observer, sector, visibility, key, terrain);
            first ??= state;
            Assert.Same(first, state);
            Assert.Equal(pitch == -30, state.TerrainFan!.GroundFacing);
            Assert.Equal(pitch == 0, state.TerrainFan.Rays.Any(ray => !ray.Bands.IsEmpty));
        }
        }
        Assert.Equal(75, coordinator.PlanSampleEvaluations);
        Assert.Equal(3, coordinator.Diagnostics.OverlayStateRebuilds);
        Assert.Equal(1, coordinator.Diagnostics.ProfileStateChanges);
        Assert.Equal(500, terrain.TerrainObstructionAt(0).EffectiveFirstObstructionDistanceMetres);
    }

    [Fact]
    public void BearingAndFovSelectNativeSamplesAndKeepProfileRevision()
    {
        var terrain = Profile(b => b is >= 80 and <= 100 ? 5 : -1);
        var coordinator = new EnvironmentalOverlayStateCoordinator();
        var key = EnvironmentalOverlayStateFactory.CreateProfileKey(terrain, 1);
        var sector = new GeoSector(terrain.Observer, 0, 24, 500000);
        FramingVisibilityAssessment Assessment(GeoSector s) => new FramingVisibilityCalculator().Calculate(
            new(DataState.Loading, null, "Offline"), terrain, 20, s.CentreBearingDegrees,
            s.HorizontalFovDegrees, cameraFrame: new(0, 20, 5));
        var clear = coordinator.Update(terrain.Observer, sector, Assessment(sector), key, terrain);
        Assert.All(clear.SourceSamples, s => Assert.False(s.IsObstructed));
        var blockedSector = sector with { CentreBearingDegrees = 90 };
        var blocked = coordinator.Update(terrain.Observer, blockedSector, Assessment(blockedSector), key, terrain);
        Assert.Contains(blocked.SourceSamples, s => s.IsObstructed && s.BearingDegrees == 90);
        var narrowSector = sector with { CentreBearingDegrees = 90, HorizontalFovDegrees = 10 };
        var narrow = coordinator.Update(terrain.Observer, narrowSector, Assessment(narrowSector), key, terrain);
        Assert.Equal(11, narrow.SourceSamples.Length);
        Assert.All(narrow.SourceSamples, s => Assert.InRange(s.BearingDegrees, 85, 95));
        Assert.Equal(clear.ProfileRevision, narrow.ProfileRevision);
        Assert.Equal(61, coordinator.PlanSampleEvaluations);
        Assert.Equal(3, coordinator.Diagnostics.OverlayStateRebuilds);
    }
}
