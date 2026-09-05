using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

public sealed class TerrainTests
{
    [Fact]
    public async Task SyntheticRidgeCreatesPositiveDirectionalHorizon()
    {
        var origin = new GeoCoordinate(51, 0);
        var profile = await new HorizonService(new FunctionTerrain(origin,
                (distance, bearing) => bearing < 20 && distance is > 1_800 and < 2_200 ? 1_000 : 0),
            NullLogger<HorizonService>.Instance).GetProfileAsync(origin,
            new TerrainProfileRequest(8, 5_000, 500, false, 0), default);

        Assert.True(profile.HasTerrainCoverage);
        Assert.InRange(profile.Samples[0].AltitudeDegrees, 20, 30);
        Assert.Equal(0, profile.Samples[4].AltitudeDegrees);
    }

    [Fact]
    public async Task UnavailableTerrainRemainsUnavailable()
    {
        var profile = await new HorizonService(new MissingTerrain(),
            NullLogger<HorizonService>.Instance).GetProfileAsync(new GeoCoordinate(51, 0),
            new TerrainProfileRequest(8, 2_000, 500), default);

        Assert.False(profile.HasTerrainCoverage);
        Assert.Null(profile.ChosenObserverGroundElevationMetres);
        Assert.Null(profile.ObserverAbsoluteElevationMetres);
        Assert.Equal(EnvironmentalDataState.Unavailable, profile.GroundHorizonState);
        Assert.Contains("unavailable", profile.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ObserverAndCameraHeightUseOneCanonicalElevationExactlyOnce()
    {
        var service = new HorizonService(new ConstantTerrain(100),
            NullLogger<HorizonService>.Instance);
        var automatic = await service.GetProfileAsync(new GeoCoordinate(51, 0, 900),
            new TerrainProfileRequest(8, 500, 100, false, 1.7), default);
        var manual = await service.GetProfileAsync(new GeoCoordinate(51, 0, 900),
            new TerrainProfileRequest(8, 500, 100, false, 1.7,
                ManualGroundElevationOverrideMetres: 250), default);

        Assert.Equal(100, automatic.ChosenObserverGroundElevationMetres);
        Assert.Equal(101.7, automatic.ObserverAbsoluteElevationMetres);
        Assert.Equal(TerrariumTerrainProvider.SourceId, automatic.GroundElevationAtObserver!.SourceId);
        Assert.Equal(250, manual.ChosenObserverGroundElevationMetres);
        Assert.Equal(251.7, manual.ObserverAbsoluteElevationMetres);
        Assert.Contains("Manual", manual.ObserverDatumMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CacheReusesSameProfileAndRebuildsForNewCoordinate()
    {
        var terrain = new CountingTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance);
        var request = new TerrainProfileRequest(8, 1_000, 500, false, 0);
        var first = await service.GetProfileAsync(new GeoCoordinate(51, 0), request, default);
        var requests = terrain.BatchRequests;
        var second = await service.GetProfileAsync(new GeoCoordinate(51, 0), request, default);
        var moved = await service.GetProfileAsync(new GeoCoordinate(51.000001, 0), request, default);

        Assert.Same(first, second);
        Assert.Equal(requests + 1, terrain.BatchRequests);
        Assert.Equal(51.000001, moved.Observer.Latitude, 10);
    }

    [Fact]
    public async Task DistanceSequencePreservesAdaptiveAndUniformModes()
    {
        var service = new HorizonService(new ConstantTerrain(0), NullLogger<HorizonService>.Instance);
        var adaptiveProfile = await service.GetProfileAsync(new GeoCoordinate(0, 0),
            new TerrainProfileRequest(AzimuthSampleCount: 8, MaximumDistanceMetres: 6_000), default);
        var uniformProfile = await service.GetProfileAsync(new GeoCoordinate(0, 0),
            new TerrainProfileRequest(8, 1_000, 250), default);
        var adaptive = adaptiveProfile.Samples[0].Sightline!.Select(point => point.DistanceMetres).ToArray();
        var uniform = uniformProfile.Samples[0].Sightline!.Select(point => point.DistanceMetres).ToArray();

        Assert.Equal(15, adaptive[0]);
        Assert.Equal(6_000, adaptive[^1]);
        Assert.Equal([250d, 500, 750, 1_000], uniform);
    }

    [Fact]
    public async Task EarthCurvatureDropsAFlatDistantHorizon()
    {
        var service = new HorizonService(new ConstantTerrain(0), NullLogger<HorizonService>.Instance);
        var flat = await service.GetProfileAsync(new GeoCoordinate(0, 0),
            new TerrainProfileRequest(8, 10_000, 10_000, false, 0), default);
        var curved = await service.GetProfileAsync(new GeoCoordinate(0, 0),
            new TerrainProfileRequest(8, 10_000, 10_000, true, 0), default);

        Assert.Equal(0, flat.GroundAltitudeAt(0));
        Assert.True(curved.GroundAltitudeAt(0) < -0.03);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1.7)]
    [InlineData(-20, 0)]
    [InlineData(-20, 2.5)]
    [InlineData(120, 2.5)]
    public async Task ObserverGroundAndCameraHeight_ResolveExactlyOnce(double ground, double camera)
    {
        var profile = await new HorizonService(new ConstantTerrain(ground), NullLogger<HorizonService>.Instance)
            .GetProfileAsync(new GeoCoordinate(51, 0, 999), new TerrainProfileRequest(8, 500, 500,
                ObserverHeightAboveGroundMetres: camera), default);
        Assert.Equal(ground, profile.ChosenObserverGroundElevationMetres);
        Assert.Equal(ground + camera, profile.ObserverAbsoluteElevationMetres);
    }

    [Fact]
    public async Task ManualObserverOverride_RemainsResolvedWhenTerrainUnavailable()
    {
        var profile = await new HorizonService(new MissingTerrain(), NullLogger<HorizonService>.Instance)
            .GetProfileAsync(new GeoCoordinate(51, 0), new TerrainProfileRequest(8, 500, 500,
                ObserverHeightAboveGroundMetres: 2, ManualGroundElevationOverrideMetres: -20), default);
        Assert.Equal(-20, profile.ChosenObserverGroundElevationMetres);
        Assert.Equal(-18, profile.ObserverAbsoluteElevationMetres);
        Assert.False(profile.HasTerrainCoverage);
    }

    [Fact]
    public async Task ProfileCacheIdentity_AllSamplingInputsInvalidateAndSubMetreCoordinatesRemainDistinct()
    {
        var terrain = new CountingTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance);
        var observer = new GeoCoordinate(51, -1);
        var request = new TerrainProfileRequest(8, 1000, 500, false, 0);
        var baseline = await service.GetProfileAsync(observer, request, default);
        var variants = new[] { request with { AzimuthSampleCount = 16 },
            request with { MaximumDistanceMetres = 1500 }, request with { DistanceStepMetres = 250 },
            request with { AccountForEarthCurvature = true }, request with { ObserverHeightAboveGroundMetres = 2 },
            request with { ManualGroundElevationOverrideMetres = 25 },
            request with { DistanceStepMetres = null, AdaptiveSampling = new(MinimumDistanceMetres: 10) } };
        foreach (var variant in variants)
        {
            var before = terrain.BatchRequests;
            var changed = await service.GetProfileAsync(observer, variant, default);
            Assert.NotSame(baseline, changed);
            Assert.Equal(before + 1, terrain.BatchRequests);
            Assert.Same(changed, await service.GetProfileAsync(observer, variant, default));
        }
        foreach (var offset in new[] { 0.000001, 0.000002 })
        {
            var moved = observer with { Longitude = observer.Longitude + offset };
            Assert.InRange(Angles.GreatCircleDistanceMetres(observer, moved), 0.01, 1);
            var changed = await service.GetProfileAsync(moved, request, default);
            Assert.NotSame(baseline, changed);
            Assert.Equal(moved, changed.Observer);
        }
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(50000)]
    [InlineData(250000)]
    [InlineData(500000)]
    public async Task DistantFlatHorizon_UsesDocumentedCurvatureAndStandardRefraction(double distance)
    {
        var profile = await new HorizonService(new ConstantTerrain(0), NullLogger<HorizonService>.Instance)
            .GetProfileAsync(new GeoCoordinate(53, -1), new TerrainProfileRequest(8, distance, distance,
                ObserverHeightAboveGroundMetres: 0), default);
        var drop = distance * distance / (2 * 6371008.8 * (7d / 6));
        var expected = Math.Atan(-drop / distance) * 180 / Math.PI;
        Assert.Equal(expected, profile.GroundAltitudeAt(0)!.Value, 9);
        Assert.Equal(distance, profile.Samples[0].TerrainHorizonFeatureDistanceMetres);
        Assert.Null(profile.TerrainObstructionAt(0).EffectiveFirstObstructionDistanceMetres);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    [InlineData(2000)]
    public void HorizontalFirstHit_IsIndependentOfHighestAndFinalHorizonSample(double firstHit)
    {
        var line = new[] { new TerrainSightlineSample(firstHit, 10, 10, 1),
            new TerrainSightlineSample(4000, 1000, 1000, 20),
            new TerrainSightlineSample(12000, 2000, 2000, 10) };
        var profile = new TerrainHorizonProfile(new GeoCoordinate(51, 0),
            [new TerrainHorizonSample(0, 20, 4000, Sightline: line)], true, "Fixture",
            NodaTime.Instant.FromUtc(2026, 1, 1, 0, 0));
        Assert.Equal(firstHit, profile.TerrainObstructionAt(0).EffectiveFirstObstructionDistanceMetres);
        Assert.Equal(20, profile.GroundAltitudeAt(0));
    }

    [Theory]
    [InlineData(0, 0, 0, 1000, 0)]
    [InlineData(10, 40, 90, 3000, .03)]
    [InlineData(-10, -40, -90, 1000, -.01)]
    [InlineData(100, 20, 30, 1000, .1)]
    [InlineData(10, 20, 600, 3000, .2)]
    [InlineData(100, 400, 300, 2000, .2)]
    [InlineData(-100, -100, -100, 3000, -1d / 30)]
    public async Task SyntheticTerrain_HorizonSelectsWinningAngularSample(double near, double middle,
        double far, double winningDistance, double expectedSlope)
    {
        var origin = new GeoCoordinate(53, -1);
        var heights = new[] { near, middle, far };
        var terrain = new FunctionTerrain(origin, (distance, _) => heights[(int)Math.Round(distance / 1000) - 1]);
        var profile = await new HorizonService(terrain, NullLogger<HorizonService>.Instance)
            .GetProfileAsync(origin, new TerrainProfileRequest(8, 3000, 1000, false, 0), default);
        Assert.All(profile.Samples, sample =>
        {
            Assert.Equal(winningDistance, sample.TerrainHorizonFeatureDistanceMetres);
            Assert.Equal(Math.Atan(expectedSlope) * 180 / Math.PI, sample.AltitudeDegrees, 9);
        });
    }

    private sealed class ConstantTerrain(double elevation) : ITerrainElevationProvider
    {
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available, elevation,
                TerrariumTerrainProvider.SourceId, TerrariumTerrainProvider.SourceVersion, "Synthetic"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken token) => Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(_ => (double?)elevation).ToArray(), TerrariumTerrainProvider.SourceId,
                TerrariumTerrainProvider.SourceVersion, "Synthetic"));
    }

    private sealed class CountingTerrain : ITerrainElevationProvider
    {
        public int BatchRequests { get; private set; }
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available, 0,
                TerrariumTerrainProvider.SourceId, TerrariumTerrainProvider.SourceVersion, "Synthetic"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken token)
        {
            BatchRequests++;
            return Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(_ => (double?)0).ToArray(), TerrariumTerrainProvider.SourceId,
                TerrariumTerrainProvider.SourceVersion, "Synthetic"));
        }
    }

    private sealed class MissingTerrain : ITerrainElevationProvider
    {
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(EnvironmentalValue<double>.Unavailable(TerrariumTerrainProvider.SourceId,
                TerrariumTerrainProvider.SourceVersion, "Missing"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken token) => Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Unavailable,
                coordinates.Select(_ => (double?)null).ToArray(), TerrariumTerrainProvider.SourceId,
                TerrariumTerrainProvider.SourceVersion, "Missing"));
    }

    private sealed class FunctionTerrain(GeoCoordinate origin,
        Func<double, double, double> value) : ITerrainElevationProvider
    {
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available, 0,
                TerrariumTerrainProvider.SourceId, TerrariumTerrainProvider.SourceVersion, "Synthetic"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken token) => Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(point => (double?)value(Angles.GreatCircleDistanceMetres(origin, point),
                    Angles.InitialBearing(origin, point))).ToArray(), TerrariumTerrainProvider.SourceId,
                TerrariumTerrainProvider.SourceVersion, "Synthetic"));
    }
}
