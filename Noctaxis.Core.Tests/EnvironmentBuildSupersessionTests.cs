using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;
using NodaTime;

namespace Noctaxis.Core.Tests;

// PF-01 / NX-20: an environment build nobody waits for is cancelled once a build for another observer starts,
// and never while a caller still waits for it or could rejoin it for the same observer (NX-09).
public sealed class EnvironmentBuildSupersessionTests
{
    private static readonly GeoCoordinate A = new(53.4, -1.5);
    private static readonly GeoCoordinate B = new(53.5, -1.5);
    private static readonly GeoCoordinate C = new(53.6, -1.5);

    [Fact]
    public async Task AnAbandonedBuildIsCancelledWhenAnotherObserversBuildStarts()
    {
        var horizons = new ControlledHorizons();
        var service = Service(horizons);
        using var first = new CancellationTokenSource();
        var abandoned = service.GetSnapshotAsync(A, first.Token);
        await horizons.Started(A);
        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.False(horizons.WasCancelled(A));

        var current = service.GetSnapshotAsync(B, CancellationToken.None);
        await horizons.Started(B);
        Assert.True(await horizons.Cancelled(A));
        Assert.Equal(1, service.ActiveBuildCount);
        horizons.Complete(B);
        Assert.Equal(B.Latitude, (await current).HorizonProfile.Observer.Latitude);
    }

    [Fact]
    public async Task TwentyRapidMovesLeaveOnlyTheCurrentBuildRunning()
    {
        var horizons = new ControlledHorizons();
        var service = Service(horizons);
        CancellationTokenSource? previous = null;
        Task<PlannerEnvironmentSnapshot>? last = null;
        for (var move = 0; move < 20; move++)
        {
            if (previous is not null)
            {
                // The Planner's refresh debounce means the superseded refresh has stopped waiting by now.
                previous.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => last!);
                previous.Dispose();
            }
            previous = new CancellationTokenSource();
            var observer = new GeoCoordinate(50 + move * 0.05, 0);
            last = service.GetSnapshotAsync(observer, previous.Token);
            await horizons.Started(observer);
            Assert.True(service.ActiveBuildCount <= 1, $"{service.ActiveBuildCount} builds after move {move}");
        }
        for (var move = 0; move < 19; move++)
            Assert.True(await horizons.Cancelled(new GeoCoordinate(50 + move * 0.05, 0)), $"move {move} still building");
        horizons.Complete(new GeoCoordinate(50 + 19 * 0.05, 0));
        await last!;
        previous?.Dispose();
    }

    [Fact]
    public async Task ABuildSomeoneStillWaitsForIsNeverCancelled()
    {
        var horizons = new ControlledHorizons();
        var service = Service(horizons);
        using var leaving = new CancellationTokenSource();
        var staying = service.GetSnapshotAsync(A, CancellationToken.None);
        var leaver = service.GetSnapshotAsync(A, leaving.Token);
        await horizons.Started(A);
        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaver);

        var other = service.GetSnapshotAsync(B, CancellationToken.None);
        await horizons.Started(B);
        Assert.False(horizons.WasCancelled(A));
        horizons.Complete(A);
        horizons.Complete(B);
        Assert.Equal(A.Latitude, (await staying).HorizonProfile.Observer.Latitude);
        await other;
        Assert.Equal(1, horizons.Requests(A));
    }

    [Fact]
    public async Task AnAbandonedBuildIsRejoinedForTheSameObserverWithoutASecondBuild()
    {
        // NX-09: an astronomy refresh supersedes the observer refresh and carries its terrain work on.
        var horizons = new ControlledHorizons();
        var service = Service(horizons);
        using var observerRefresh = new CancellationTokenSource();
        var first = service.GetSnapshotAsync(A, observerRefresh.Token);
        await horizons.Started(A);
        observerRefresh.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var carried = service.GetSnapshotAsync(A, CancellationToken.None);
        horizons.Complete(A);
        Assert.Equal(A.Latitude, (await carried).HorizonProfile.Observer.Latitude);
        Assert.Equal(1, horizons.Requests(A));
        Assert.False(horizons.WasCancelled(A));
    }

    [Fact]
    public async Task ASupersededBuildIsNotCachedAndIsBuiltAgainWhenRequested()
    {
        var horizons = new ControlledHorizons();
        var service = Service(horizons);
        using (var first = new CancellationTokenSource())
        {
            var abandoned = service.GetSnapshotAsync(A, first.Token);
            await horizons.Started(A);
            first.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        }
        var b = service.GetSnapshotAsync(B, CancellationToken.None);
        await horizons.Cancelled(A);
        horizons.Complete(B);
        await b;

        var again = service.GetSnapshotAsync(A, CancellationToken.None);
        await horizons.StartedAgain(A);
        horizons.Complete(A);
        Assert.Equal(A.Latitude, (await again).HorizonProfile.Observer.Latitude);
        Assert.Equal(2, horizons.Requests(A));
        Assert.Equal(0, service.ActiveBuildCount);
    }

    [Fact]
    public async Task OnlyAbandonedBuildsAreSuperseded()
    {
        var horizons = new ControlledHorizons();
        var service = Service(horizons);
        using var leaving = new CancellationTokenSource();
        var waited = service.GetSnapshotAsync(A, CancellationToken.None);
        var abandoned = service.GetSnapshotAsync(B, leaving.Token);
        await horizons.Started(A);
        await horizons.Started(B);
        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        var current = service.GetSnapshotAsync(C, CancellationToken.None);
        await horizons.Started(C);
        Assert.True(await horizons.Cancelled(B));
        Assert.False(horizons.WasCancelled(A));
        horizons.Complete(A);
        horizons.Complete(C);
        await waited;
        await current;
    }

    private static PlannerEnvironmentService Service(IHorizonService horizons) =>
        new(horizons, new UnavailableSettlementDataProvider(), NullLogger<PlannerEnvironmentService>.Instance);

    // Each horizon request blocks until the test completes it or its token is cancelled.
    private sealed class ControlledHorizons : IHorizonService
    {
        private readonly ConcurrentDictionary<double, Build> _builds = new();

        private sealed class Build
        {
            public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource StartedAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Requests;
        }

        private Build For(GeoCoordinate observer) => _builds.GetOrAdd(observer.Latitude, _ => new Build());

        public async Task<TerrainHorizonProfile> GetProfileAsync(GeoCoordinate observer, TerrainProfileRequest request,
            CancellationToken cancellationToken)
        {
            var build = For(observer);
            TaskCompletionSource<bool> release;
            lock (build)
            {
                if (++build.Requests > 1)
                {
                    build.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    build.StartedAgain.TrySetResult();
                }
                release = build.Release;
            }
            build.Started.TrySetResult();
            try { await release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                build.Cancelled.TrySetResult(true);
                throw;
            }
            return new TerrainHorizonProfile(observer, [], true, "fixture", Instant.FromUtc(2026, 10, 17, 0, 0));
        }

        public Task Started(GeoCoordinate observer) => For(observer).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public Task StartedAgain(GeoCoordinate observer) => For(observer).StartedAgain.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public void Complete(GeoCoordinate observer) { lock (For(observer)) For(observer).Release.TrySetResult(true); }
        public int Requests(GeoCoordinate observer) => For(observer).Requests;
        public bool WasCancelled(GeoCoordinate observer) => For(observer).Cancelled.Task.IsCompleted;

        public async Task<bool> Cancelled(GeoCoordinate observer)
        {
            try { return await For(observer).Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { return false; }
        }
    }
}
