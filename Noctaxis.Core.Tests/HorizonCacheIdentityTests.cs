using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

/// <summary>
/// Cache identity covers the inputs that can change a horizon, not the observer elevation a build
/// resolves and the session then adopts. The fallback datum carried by the request is what stands in
/// for the observer's ground, and it is the only part of that elevation identity keeps.
/// </summary>
public sealed class HorizonCacheIdentityTests
{
    private static readonly TerrainProfileRequest Request = new(8, 100, 50);
    private static readonly GeoCoordinate Observer = new(51.5, -.1, 0);

    [Fact]
    public async Task ResolvedObserverElevationDoesNotChangeHorizonCacheIdentity()
    {
        var terrain = new FlatTerrain(observerElevation: 120, groundElevation: 100);
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 1);
        var request = Request with { FallbackObserverGroundElevationMetres = 0 };

        var supplied = await service.GetProfileAsync(Observer, request, default);
        // What the view model does after a build: the resolved ground replaces the observer elevation.
        var afterResolution = await service.GetProfileAsync(Observer with { ElevationMetres = 120 },
            request, default);

        Assert.Same(supplied, afterResolution);
        Assert.Equal(1, terrain.Builds);
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
        // Resolved terrain, not the fallback, chose the datum.
        Assert.Equal(120, supplied.ChosenObserverGroundElevationMetres);
    }

    [Fact]
    public async Task ResolvedObserverElevationDoesNotChangeEnvironmentCacheIdentity()
    {
        var terrain = new FlatTerrain(observerElevation: 120, groundElevation: 100);
        var horizons = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 1);
        var environment = new PlannerEnvironmentService(horizons, new Settlement(),
            NullLogger<PlannerEnvironmentService>.Instance);
        var request = Request with { FallbackObserverGroundElevationMetres = 0 };

        var supplied = await environment.GetSnapshotAsync(Observer, request, default);
        var afterResolution = await environment.GetSnapshotAsync(Observer with { ElevationMetres = 120 },
            request, default);

        Assert.Same(supplied, afterResolution);
        Assert.Equal(1, terrain.Builds);
        Assert.Equal(1, environment.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task DifferentSuppliedFallbacksStillDifferWhereObserverTerrainIsUnavailable()
    {
        // Terrain answers along the bearings but not at the observer, which is the only case where
        // the supplied datum reaches the mathematics.
        var terrain = new ObserverlessTerrain(groundElevation: 100);
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 1);

        var low = await service.GetProfileAsync(Observer,
            Request with { FallbackObserverGroundElevationMetres = 0 }, default);
        var high = await service.GetProfileAsync(Observer,
            Request with { FallbackObserverGroundElevationMetres = 500 }, default);

        Assert.NotSame(low, high);
        Assert.Equal(2, terrain.Builds);
        Assert.Equal(2, service.CompletedCacheDiagnostics.Count);
        // Ground at 100 m rises above an observer standing at 0 m and falls below one at 500 m.
        Assert.True(low.Samples[0].TerrainHorizonElevationDegrees > 0);
        Assert.True(high.Samples[0].TerrainHorizonElevationDegrees < 0);
    }

    [Fact]
    public async Task ManualOverrideRemainsAuthoritativeOverTerrainAndFallback()
    {
        var terrain = new FlatTerrain(observerElevation: 120, groundElevation: 100);
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 1);
        var overridden = Request with
        {
            ManualGroundElevationOverrideMetres = 300,
            FallbackObserverGroundElevationMetres = 0
        };

        var first = await service.GetProfileAsync(Observer, overridden, default);
        var otherFallback = await service.GetProfileAsync(Observer,
            overridden with { FallbackObserverGroundElevationMetres = 500 }, default);

        Assert.Equal(300, first.ChosenObserverGroundElevationMetres);
        Assert.Equal(300, otherFallback.ChosenObserverGroundElevationMetres);
        Assert.Equal(first.Samples[0].TerrainHorizonElevationDegrees,
            otherFallback.Samples[0].TerrainHorizonElevationDegrees);
    }

    [Fact]
    public async Task CompletedHorizonCacheStillRetainsFourProfiles()
    {
        var terrain = new FlatTerrain(observerElevation: 120, groundElevation: 100);
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 1);
        for (var index = 0; index <= HorizonService.CompletedProfileCapacity; index++)
            await service.GetProfileAsync(new GeoCoordinate(40 + index, 0), Request, default);

        Assert.Equal(HorizonService.CompletedProfileCapacity, service.CompletedCacheDiagnostics.Count);
        Assert.Equal(HorizonService.CompletedProfileCapacity + 1, terrain.Builds);

        var builds = terrain.Builds;
        await service.GetProfileAsync(new GeoCoordinate(44, 0), Request, default);
        Assert.Equal(builds, terrain.Builds);
        // The oldest of the five was evicted and has to be rebuilt.
        await service.GetProfileAsync(new GeoCoordinate(40, 0), Request, default);
        Assert.Equal(builds + 1, terrain.Builds);
    }

    private class FlatTerrain(double observerElevation, double groundElevation) : ITerrainElevationProvider
    {
        private int _builds;
        public int Builds => Volatile.Read(ref _builds);

        public Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        {
            Interlocked.Increment(ref _builds);
            return Task.CompletedTask;
        }

        public virtual Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate,
            CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available,
                observerElevation, "fixture", "1", "Observer ground"));

        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken token) =>
            Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(_ => (double?)groundElevation).ToArray(), "fixture", "1", "Flat"));
    }

    private sealed class ObserverlessTerrain(double groundElevation) : FlatTerrain(0, groundElevation)
    {
        public override Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate,
            CancellationToken token) =>
            Task.FromResult(EnvironmentalValue<double>.Unavailable("fixture", "1",
                "No terrain sample at the observer"));
    }

    private sealed class Settlement : ISettlementDataProvider
    {
        public Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request,
            CancellationToken token) =>
            Task.FromResult(EnvironmentalValue<SettlementRaster>.Unavailable("fixture", "1", "Unavailable"));
    }
}
