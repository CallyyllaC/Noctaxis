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
