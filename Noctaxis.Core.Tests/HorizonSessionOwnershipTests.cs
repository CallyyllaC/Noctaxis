using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

/// <summary>
/// A shared horizon session is owned by the session, not by whichever caller created it. Every
/// caller — a tracked wait or work taken straight from StartProfile — is a counted consumer that
/// keeps its own cancellation, and the producer stops only once every consumer has gone.
/// </summary>
public sealed class HorizonSessionOwnershipTests
{
    private static readonly TerrainProfileRequest Request = new(360, 1000, 1000);
    private static readonly GeoCoordinate Observer = new(53.55, -.48);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CancellingOneConsumerLeavesAnotherConsumerWithTheCompletedProfile()
    {
        var terrain = new GatedTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        using var first = new CancellationTokenSource();
        var a = service.GetProfileAsync(Observer, Request, first.Token);
        await terrain.Started.Task.WaitAsync(Patience);
        var b = service.GetProfileAsync(Observer, Request, CancellationToken.None);

        first.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        terrain.Release.TrySetResult(true);
        var profile = await b.WaitAsync(Patience);
        Assert.True(profile.IsComplete);
        Assert.Equal(360, profile.EffectiveCompletedBearingCount);
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task CancellingATrackedConsumerLeavesDirectWorkRunning()
    {
        var terrain = new GatedTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        using var direct = new CancellationTokenSource();
        using var tracked = new CancellationTokenSource();
        var work = service.StartProfile(Observer, Request, direct.Token);
        var consumer = service.GetProfileAsync(Observer, Request, tracked.Token);
        await terrain.Started.Task.WaitAsync(Patience);

        tracked.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        // The direct caller is still interested, so its build must survive the tracked consumer.
        Assert.False(work.CompleteProfile.IsCompleted);
        terrain.Release.TrySetResult(true);
        var profile = await work.CompleteProfile.WaitAsync(Patience);
        Assert.True(profile.IsComplete);
        Assert.False(await terrain.PreloadCancelled.WaitAsync(Patience));
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task CancellingADirectCallerLeavesASurvivingConsumerWithTheProfile()
    {
        var terrain = new GatedTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        using var direct = new CancellationTokenSource();
        var work = service.StartProfile(Observer, Request, direct.Token);
        var surviving = service.GetProfileAsync(Observer, Request, CancellationToken.None);
        await terrain.Started.Task.WaitAsync(Patience);

        direct.Cancel();

        // The direct caller's own view is cancelled; the shared producer is not.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.CompleteProfile.WaitAsync(Patience));
        Assert.Equal(0, service.CompletedCacheDiagnostics.Count);
        terrain.Release.TrySetResult(true);
        var profile = await surviving.WaitAsync(Patience);
        Assert.True(profile.IsComplete);
        Assert.False(await terrain.PreloadCancelled.WaitAsync(Patience));
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task ProducerStopsAndCachesNothingOnceEveryConsumerHasLeft()
    {
        var terrain = new GatedTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        using var direct = new CancellationTokenSource();
        using var tracked = new CancellationTokenSource();
        var work = service.StartProfile(Observer, Request, direct.Token);
        var consumer = service.GetProfileAsync(Observer, Request, tracked.Token);
        await terrain.Started.Task.WaitAsync(Patience);

        tracked.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        Assert.False(work.CompleteProfile.IsCompleted);

        direct.Cancel();

        // Nobody is left, so the build stops where it stands instead of finishing for no one.
        Assert.True(await terrain.PreloadCancelled.WaitAsync(Patience));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.CompleteProfile.WaitAsync(Patience));
        Assert.Equal(0, service.CompletedCacheDiagnostics.Count);
        Assert.False(terrain.Release.Task.IsCompleted);
    }

    [Fact]
    public async Task CallerArrivingWhileASessionCancelsReceivesAFreshLiveSession()
    {
        // This terrain ignores the producer token, so the abandoned session is still mapped when the
        // next caller arrives: exactly the window in which a new consumer must not inherit it.
        var terrain = new StuckTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        using var abandoning = new CancellationTokenSource();
        var doomed = service.StartProfile(Observer, Request, abandoning.Token);
        await terrain.Started.Task.WaitAsync(Patience);

        abandoning.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed.CompleteProfile.WaitAsync(Patience));

        var rebuilt = service.GetProfileAsync(Observer, Request, CancellationToken.None);
        terrain.Release.TrySetResult(true);

        // A session that had inherited the cancellation could never produce a complete profile.
        var profile = await rebuilt.WaitAsync(Patience);
        Assert.True(profile.IsComplete);
        Assert.Equal(2, terrain.PreloadCalls);
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task CancelledPriorityCallerDoesNotFailAnUnrelatedEnvironmentSnapshot()
    {
        var terrain = new GatedTerrain();
        var horizons = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        var environment = new PlannerEnvironmentService(horizons, new Settlement(),
            NullLogger<PlannerEnvironmentService>.Instance);
        var snapshot = environment.GetSnapshotAsync(Observer, Request, CancellationToken.None);
        await terrain.Started.Task.WaitAsync(Patience);
        using var camera = new CancellationTokenSource();
        var priority = environment.GetPriorityHorizonAsync(Observer, Request, [90d], camera.Token);

        camera.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => priority);
        terrain.Release.TrySetResult(true);
        // The refresh that never cancelled still receives its terrain, so Planner reports no error.
        var result = await snapshot.WaitAsync(Patience);
        Assert.True(result.HorizonProfile.IsComplete);
        Assert.True(result.HorizonProfile.HasTerrainCoverage);
        Assert.Equal(1, horizons.CompletedCacheDiagnostics.Count);
    }

    [Fact]
    public async Task GenerationInvalidationStillCancelsAndRejectsAnActiveSession()
    {
        var terrain = new GatedTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        var consumer = service.GetProfileAsync(Observer, Request, CancellationToken.None);
        await terrain.Started.Task.WaitAsync(Patience);

        service.InvalidateCache();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.WaitAsync(Patience));
        Assert.Equal(0, service.CompletedCacheDiagnostics.Count);
        terrain.Release.TrySetResult(true);
        var rebuilt = await service.GetProfileAsync(Observer, Request, CancellationToken.None).WaitAsync(Patience);
        Assert.True(rebuilt.IsComplete);
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
    }

    private sealed class GatedTerrain : ITerrainElevationProvider
    {
        private readonly TaskCompletionSource<bool> _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>True once the producer token stopped a preload, false once one ran to completion.</summary>
        public Task<bool> PreloadCancelled => _cancelled.Task;

        public async Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        {
            Started.TrySetResult(true);
            try { await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { _cancelled.TrySetResult(true); throw; }
            _cancelled.TrySetResult(false);
        }

        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available, 10, "fixture", "1", "Flat"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token) =>
            Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(_ => (double?)10).ToArray(), "fixture", "1", "Flat"));
    }

    private sealed class StuckTerrain : ITerrainElevationProvider
    {
        private int _preloadCalls;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PreloadCalls => Volatile.Read(ref _preloadCalls);

        // Deliberately ignores cancellation, so a cancelled producer stays inside this call.
        public async Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        {
            Interlocked.Increment(ref _preloadCalls);
            Started.TrySetResult(true);
            await Release.Task;
        }

        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available, 10, "fixture", "1", "Flat"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token) =>
            Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(_ => (double?)10).ToArray(), "fixture", "1", "Flat"));
    }

    private sealed class Settlement : ISettlementDataProvider
    {
        public Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request,
            CancellationToken token) =>
            Task.FromResult(EnvironmentalValue<SettlementRaster>.Unavailable("fixture", "1", "Unavailable"));
    }
}
