using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

public sealed class TerrainResultCacheTests
{
    private static readonly TerrainProfileRequest Request = new(8, 100, 50);
    private static GeoCoordinate Observer(int index) => new(index, 0);
    private static HorizonService Horizon(Ground ground) => new(ground, NullLogger<HorizonService>.Instance, 1);

    [Fact]
    public async Task HorizonLruEvictsBRetainsAAndRecalculatesEvictedProfile()
    {
        var ground = new Ground();
        var service = Horizon(ground);
        var capacity = HorizonService.CompletedProfileCapacity;
        var profiles = new List<TerrainHorizonProfile>();
        for (var i = 0; i < capacity; i++) profiles.Add(await service.GetProfileAsync(Observer(i), Request, default));
        var samples = ground.Samples;
        Assert.Same(profiles[0], await service.GetProfileAsync(Observer(0), Request, default));
        Assert.Equal(samples, ground.Samples);
        await service.GetProfileAsync(Observer(capacity), Request, default);
        Assert.Equal(capacity, service.CompletedCacheDiagnostics.Count);
        Assert.Equal(1, service.CompletedCacheDiagnostics.Evictions);
        samples = ground.Samples;
        Assert.Same(profiles[0], await service.GetProfileAsync(Observer(0), Request, default));
        Assert.Equal(samples, ground.Samples);
        Assert.NotSame(profiles[1], await service.GetProfileAsync(Observer(1), Request, default));
        Assert.True(ground.Samples > samples);
        samples = ground.Samples;
        await service.GetProfileAsync(Observer(1), Request, default);
        Assert.Equal(samples, ground.Samples);
        Assert.True(service.CompletedCacheDiagnostics.Hits >= 3);
        Assert.True(service.CompletedCacheDiagnostics.ApproximateRetainedBytes > 0);
        Assert.Equal(10, profiles[1].Samples[0].Sightline![0].TerrainElevationMetres);
    }

    [Fact]
    public async Task LongScoutingBoundsBothCachesAndReleasesCompletedBuildEntries()
    {
        var ground = new Ground();
        var horizon = Horizon(ground);
        var environment = new PlannerEnvironmentService(horizon, new Settlement(), NullLogger<PlannerEnvironmentService>.Instance);
        long profileBytes = 0;
        for (var index = 0; index < 40; index++)
        {
            await environment.GetSnapshotAsync(Observer(index), Request, default);
            if (index == 0) profileBytes = horizon.CompletedCacheDiagnostics.ApproximateRetainedBytes;
            Assert.InRange(horizon.CompletedCacheDiagnostics.Count, 1, HorizonService.CompletedProfileCapacity);
            Assert.InRange(environment.CompletedCacheDiagnostics.Count, 1, PlannerEnvironmentService.CompletedSnapshotCapacity);
            Assert.Equal(0, environment.ActiveBuildCount);
            Assert.True(horizon.CompletedCacheDiagnostics.ApproximateRetainedBytes <= profileBytes * HorizonService.CompletedProfileCapacity);
        }
        var samples = ground.Samples;
        await environment.GetSnapshotAsync(Observer(39), Request, default);
        Assert.Equal(samples, ground.Samples);
        Assert.True(environment.CompletedCacheDiagnostics.Evictions > 0);
    }

    [Fact]
    public async Task EnvironmentLruRetainsReusedSnapshotAndRebuildsEvictedSnapshot()
    {
        var horizon = Horizon(new Ground());
        var settlement = new Settlement();
        var service = new PlannerEnvironmentService(horizon, settlement, NullLogger<PlannerEnvironmentService>.Instance);
        var snapshots = new List<PlannerEnvironmentSnapshot>();
        for (var i = 0; i < PlannerEnvironmentService.CompletedSnapshotCapacity; i++)
            snapshots.Add(await service.GetSnapshotAsync(Observer(i), Request, default));
        Assert.Same(snapshots[0], await service.GetSnapshotAsync(Observer(0), Request, default));
        await service.GetSnapshotAsync(Observer(PlannerEnvironmentService.CompletedSnapshotCapacity), Request, default);
        Assert.Same(snapshots[0], await service.GetSnapshotAsync(Observer(0), Request, default));
        var calls = settlement.Calls;
        Assert.NotSame(snapshots[1], await service.GetSnapshotAsync(Observer(1), Request, default));
        Assert.Equal(calls + 1, settlement.Calls);
    }

    [Fact]
    public async Task CompletedEvictionDoesNotCancelActiveCoalescedBuild()
    {
        var ground = new Ground();
        var horizon = Horizon(ground);
        var service = new PlannerEnvironmentService(horizon, new Settlement(), NullLogger<PlannerEnvironmentService>.Instance);
        var first = service.GetSnapshotAsync(Observer(80), Request, default);
        await ground.Started.Task;
        var second = service.GetSnapshotAsync(Observer(80), Request, default);
        for (var i = 0; i < HorizonService.CompletedProfileCapacity + 2; i++)
            await service.GetSnapshotAsync(Observer(i), Request, default);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, service.ActiveBuildCount);
        ground.Release.TrySetResult(true);
        Assert.Same(await first, await second);
        Assert.Equal(1, ground.BlockedBatches);
        Assert.Equal(0, service.ActiveBuildCount);
        Assert.Equal(HorizonService.CompletedProfileCapacity, horizon.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task GenerationClearCannotBeRepopulatedByOldHorizonCompletion()
    {
        var ground = new Ground();
        var horizon = Horizon(ground);
        await horizon.GetProfileAsync(Observer(0), Request, default);
        var obsolete = horizon.StartProfile(Observer(80), Request, default).CompleteProfile;
        await ground.Started.Task;
        horizon.InvalidateCache();
        Assert.Equal(0, horizon.CompletedCacheDiagnostics.Count);
        Assert.Equal(0, horizon.CompletedCacheDiagnostics.ApproximateRetainedBytes);
        ground.Release.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
        Assert.Equal(0, horizon.CompletedCacheDiagnostics.Count);
        await horizon.GetProfileAsync(Observer(0), Request, default);
        Assert.Equal(1, horizon.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task GenerationClearCannotBeRepopulatedByOldEnvironmentCompletion()
    {
        var ground = new Ground();
        var horizon = Horizon(ground);
        var cache = new CacheSignal();
        var service = new PlannerEnvironmentService(horizon, new Settlement(), NullLogger<PlannerEnvironmentService>.Instance, cache);
        await service.GetSnapshotAsync(Observer(0), Request, default);
        var old = service.GetSnapshotAsync(Observer(80), Request, default);
        await ground.Started.Task;
        cache.Clear();
        Assert.Equal(0, service.CompletedCacheDiagnostics.Count);
        ground.Release.TrySetResult(true);
        await old; // Existing callers retain their result; it must not become a reusable new-generation result.
        Assert.Equal(0, service.CompletedCacheDiagnostics.Count);
        Assert.Equal(0, service.ActiveBuildCount);
        await service.GetSnapshotAsync(Observer(0), Request, default);
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task CancellingOnlyWaiterDoesNotLeaveCompletedBuildInActiveDictionary()
    {
        var ground = new Ground();
        var service = new PlannerEnvironmentService(Horizon(ground), new Settlement(), NullLogger<PlannerEnvironmentService>.Instance);
        using var cancellation = new CancellationTokenSource();
        var waiting = service.GetSnapshotAsync(Observer(80), Request, cancellation.Token);
        await ground.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var remainingWaiter = service.GetSnapshotAsync(Observer(80), Request, default);
        ground.Release.TrySetResult(true);
        var completed = await remainingWaiter;
        Assert.Equal(0, service.ActiveBuildCount);
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
        Assert.Same(completed, await service.GetSnapshotAsync(Observer(80), Request, default));
        Assert.Equal(1, ground.BlockedBatches);
    }

    [Fact]
    public async Task IndependentCachesMayRetainDifferentFourProfileWorkingSets()
    {
        var ground = new Ground();
        var horizon = Horizon(ground);
        var environment = new PlannerEnvironmentService(horizon, new Settlement(), NullLogger<PlannerEnvironmentService>.Instance);
        var profiles = new List<TerrainHorizonProfile>();
        for (var i = 0; i < PlannerEnvironmentService.CompletedSnapshotCapacity; i++)
            profiles.Add((await environment.GetSnapshotAsync(Observer(i), Request, default)).HorizonProfile);
        for (var i = 0; i < HorizonService.CompletedProfileCapacity; i++)
            profiles.Add(await horizon.GetProfileAsync(Observer(10 + i), Request, default));
        Assert.Equal(8, profiles.Distinct(ReferenceEqualityComparer.Instance).Count());
        var samples = ground.Samples;
        Assert.Same(profiles[0], (await environment.GetSnapshotAsync(Observer(0), Request, default)).HorizonProfile);
        Assert.Equal(samples, ground.Samples);
        Assert.Equal(4, environment.CompletedCacheDiagnostics.Count);
        Assert.Equal(4, horizon.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task OfflineHorizonIsReusedOnlyBrieflyAndRebuiltAfterRecovery()
    {
        // V-03: a horizon built while terrain was unreachable must not be served for the whole session.
        var time = new ManualTime();
        var ground = new FlakyGround { Online = false };
        var service = new HorizonService(ground, NullLogger<HorizonService>.Instance, 1, time);

        var offline = await service.GetProfileAsync(Observer(0), Request, default);
        Assert.False(offline.HasTerrainCoverage);
        Assert.True(HorizonService.IsDegraded(offline));
        // Within its short lifetime the degraded profile is still shared, so refreshes do not rebuild it.
        Assert.Same(offline, await service.GetProfileAsync(Observer(0), Request, default));

        ground.Online = true;
        time.Advance(HorizonService.DegradedProfileLifetime);
        var recovered = await service.GetProfileAsync(Observer(0), Request, default);
        Assert.NotSame(offline, recovered);
        Assert.True(recovered.HasTerrainCoverage);
        Assert.False(HorizonService.IsDegraded(recovered));

        // A complete profile keeps the permanent behaviour: it is reused regardless of age.
        time.Advance(TimeSpan.FromHours(6));
        Assert.Same(recovered, await service.GetProfileAsync(Observer(0), Request, default));
    }

    [Fact]
    public async Task OfflineSnapshotIsRebuiltAfterRecoveryButPermanentAbsenceIsKept()
    {
        var time = new ManualTime();
        var ground = new FlakyGround { Online = false };
        var horizon = new HorizonService(ground, NullLogger<HorizonService>.Instance, 1, time);
        var settlement = new StatedSettlement(EnvironmentalDataState.TileAbsent);
        var service = new PlannerEnvironmentService(horizon, settlement,
            NullLogger<PlannerEnvironmentService>.Instance, time: time);

        var offline = await service.GetSnapshotAsync(Observer(0), Request, default);
        Assert.False(offline.HorizonProfile.HasTerrainCoverage);
        Assert.Same(offline, await service.GetSnapshotAsync(Observer(0), Request, default));

        ground.Online = true;
        time.Advance(HorizonService.DegradedProfileLifetime);
        var recovered = await service.GetSnapshotAsync(Observer(0), Request, default);
        Assert.NotSame(offline, recovered);
        Assert.True(recovered.HorizonProfile.HasTerrainCoverage);

        // Settlement the source reports as absent is permanent, so this snapshot is not degraded.
        var calls = settlement.Calls;
        time.Advance(TimeSpan.FromHours(6));
        Assert.Same(recovered, await service.GetSnapshotAsync(Observer(0), Request, default));
        Assert.Equal(calls, settlement.Calls);

        // Settlement that could not be reached is transient, even with complete terrain.
        settlement.State = EnvironmentalDataState.SourceUnavailable;
        var unreachable = await service.GetSnapshotAsync(Observer(1), Request, default);
        Assert.Same(unreachable, await service.GetSnapshotAsync(Observer(1), Request, default));
        settlement.State = EnvironmentalDataState.TileAbsent;
        time.Advance(HorizonService.DegradedProfileLifetime);
        Assert.NotSame(unreachable, await service.GetSnapshotAsync(Observer(1), Request, default));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FlakyGround : ITerrainElevationProvider
    {
        public volatile bool Online;
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(Online
                ? new EnvironmentalValue<double>(EnvironmentalDataState.Available, 10, "fixture", "1", "fixture")
                : new EnvironmentalValue<double>(EnvironmentalDataState.Unavailable, default, "fixture", "1", "offline"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token) =>
            Task.FromResult(Online
                ? new ElevationBatchResult(EnvironmentalDataState.Available,
                    Enumerable.Repeat<double?>(10, coordinates.Count).ToArray(), "fixture", "1", "fixture")
                : new ElevationBatchResult(EnvironmentalDataState.Unavailable,
                    new double?[coordinates.Count], "fixture", "1", "offline"));
    }

    private sealed class StatedSettlement(EnvironmentalDataState state) : ISettlementDataProvider
    {
        public int Calls;
        public EnvironmentalDataState State { get; set; } = state;
        public Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new EnvironmentalValue<SettlementRaster>(State, default, "fixture", "1", State.ToString()));
        }
    }

    private sealed class Ground : ITerrainElevationProvider
    {
        public long Samples;
        public int BlockedBatches;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token)
        {
            Interlocked.Increment(ref Samples);
            return Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available, 10, "fixture", "1", "fixture"));
        }
        public async Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        {
            if (coordinates[0].Latitude > 70)
            {
                Interlocked.Increment(ref BlockedBatches);
                Started.TrySetResult(true);
                await Release.Task; // Deliberately ignores cancellation to exercise the generation fence.
            }
            Interlocked.Add(ref Samples, coordinates.Count);
            return new(EnvironmentalDataState.Available, Enumerable.Repeat<double?>(10, coordinates.Count).ToArray(), "fixture", "1", "fixture");
        }
    }

    private sealed class Settlement : ISettlementDataProvider
    {
        public int Calls;
        public Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(EnvironmentalValue<SettlementRaster>.Unavailable("fixture", "1", "Unavailable"));
        }
    }

    private sealed class CacheSignal : IEnvironmentalTileCache
    {
        private Action? _clear;
        public void RegisterTerrainInvalidation(Action callback) => _clear += callback;
        public void Clear() => _clear?.Invoke();
        public string RootDirectory => "unused";
        public Task<EnvironmentalCacheResult> GetOrCreateAsync(EnvironmentalTileDescriptor descriptor, Func<CancellationToken, Task<byte[]?>> acquire,
            Func<string, bool> validate, CancellationToken token) => throw new NotSupportedException();
        public Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire, Func<string, bool> validate, CancellationToken token) => throw new NotSupportedException();
    }
}
