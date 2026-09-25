using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Services;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

// The debounce is driven by ManualDelay, so no test depends on real elapsed time.
public sealed class StateSaveSchedulerTests
{
    [Fact]
    public async Task RapidRequestsCoalesceIntoOneWriteOfTheNewestState()
    {
        var harness = new Harness();
        for (var revision = 1; revision <= 5; revision++) harness.Request(revision);

        Assert.Empty(harness.Written);
        Assert.Equal(1, harness.Delay.Pending);
        harness.Delay.ReleaseAll();
        await harness.Scheduler.WhenIdleAsync();

        Assert.Equal([5], harness.Written);
    }

    [Fact]
    public async Task ChangeDuringAnInFlightWriteIsWrittenAfterItWithoutOverlap()
    {
        var harness = new Harness(immediate: true);
        harness.BlockWrites();
        harness.Request(1);
        harness.Request(2);
        harness.Request(3);
        Assert.Equal(1, harness.WritesStarted);
        Assert.Empty(harness.Written);

        harness.UnblockWrites();
        await harness.Scheduler.WhenIdleAsync();

        Assert.Equal([1, 3], harness.Written);
        Assert.Equal(1, harness.MaximumConcurrentWrites);
    }

    [Fact]
    public async Task TheWrittenStateIsTheSnapshotSuppliedWithTheRequest()
    {
        // The scheduler holds no reference to live application state: whatever the caller captured on
        // its own thread is exactly what reaches the store, however long the write is deferred.
        var harness = new Harness();
        var snapshot = Harness.State(7);
        harness.Scheduler.RequestSave(snapshot);

        harness.Delay.ReleaseAll();
        await harness.Scheduler.WhenIdleAsync();

        Assert.Same(snapshot, Assert.Single(harness.WrittenStates));
    }

    [Fact]
    public async Task FilesystemFailureIsReportedAndALaterRequestRetries()
    {
        var harness = new Harness(immediate: true);
        var results = new List<StateSaveResult>();
        harness.Scheduler.Completed += results.Add;
        harness.Failure = new IOException("disk full");

        Assert.False(await harness.FlushAsync(1));
        harness.Request(1);
        await harness.Scheduler.WhenIdleAsync();
        Assert.Empty(harness.Written);
        Assert.All(results, result => Assert.False(result.Succeeded));
        Assert.IsType<IOException>(results[0].Failure);

        harness.Failure = null;
        harness.Request(2);
        await harness.Scheduler.WhenIdleAsync();

        Assert.Equal([2], harness.Written);
        Assert.True(results[^1].Succeeded);
    }

    [Fact]
    public async Task FlushDuringDebounceWritesNowAndLeavesTheSchedulerUsable()
    {
        var harness = new Harness();
        harness.Request(1);

        Assert.True(await harness.FlushAsync(1));
        // The cancelled debounce completes without writing again or faulting.
        await harness.Scheduler.WhenIdleAsync();
        Assert.Equal([1], harness.Written);
        Assert.Equal(0, harness.Delay.Pending);

        harness.Request(2);
        harness.Delay.ReleaseAll();
        await harness.Scheduler.WhenIdleAsync();
        Assert.Equal([1, 2], harness.Written);
    }

    [Fact]
    public async Task FlushWaitsForAnInFlightWriteThenWritesTheNewestState()
    {
        var harness = new Harness(immediate: true);
        harness.BlockWrites();
        harness.Request(1);
        var flush = harness.FlushAsync(2);
        Assert.False(flush.IsCompleted);

        harness.UnblockWrites();

        Assert.True(await flush);
        Assert.Equal([1, 2], harness.Written);
        Assert.Equal(1, harness.MaximumConcurrentWrites);
    }

    [Fact]
    public async Task NothingIsWrittenBeforeTheSchedulerIsEnabled()
    {
        var harness = new Harness(immediate: true, enable: false);
        harness.Request(1);

        Assert.True(await harness.FlushAsync(1));
        await harness.Scheduler.WhenIdleAsync();
        Assert.Empty(harness.Written);
    }

    [Fact]
    public async Task UnexpectedFailuresAreNotSwallowed()
    {
        var harness = new Harness(immediate: true);
        Exception? reported = null;
        harness.Scheduler.UnexpectedBackgroundFailure = exception => reported = exception;
        harness.Failure = new InvalidOperationException("defect");

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.FlushAsync(1));
        harness.Request(1);
        await harness.Scheduler.WhenIdleAsync();

        Assert.IsType<InvalidOperationException>(reported);
    }

    private sealed class Harness
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _block;
        private int _active;

        public Harness(bool immediate = false, bool enable = true)
        {
            Scheduler = new StateSaveScheduler(WriteAsync, NullLogger.Instance,
                delay: immediate ? (_, _) => Task.CompletedTask : Delay.WaitAsync);
            if (enable) Scheduler.Enable();
        }

        public StateSaveScheduler Scheduler { get; }
        public ManualDelay Delay { get; } = new();
        public Exception? Failure { get; set; }
        public List<int> Written { get; } = [];
        public List<PersistedState> WrittenStates { get; } = [];
        public int MaximumConcurrentWrites { get; private set; }
        public int WritesStarted { get; private set; }

        public void BlockWrites() => _block = true;
        public void UnblockWrites() => _release.TrySetResult();
        public void Request(int revision) => Scheduler.RequestSave(State(revision));
        public Task<bool> FlushAsync(int revision) => Scheduler.FlushAsync(State(revision));

        // The revision is carried in the state so each write records which snapshot it was given.
        public static PersistedState State(int revision) => new(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null,
            new GeoCoordinate(revision, 0));

        private async Task WriteAsync(PersistedState state, CancellationToken cancellationToken)
        {
            WritesStarted++;
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, ++_active);
            try
            {
                if (_block) await _release.Task;
                if (Failure is not null) throw Failure;
                WrittenStates.Add(state);
                Written.Add((int)state.LastCustomCoordinate!.Value.Latitude);
            }
            finally { _active--; }
        }
    }

    private sealed class ManualDelay
    {
        private readonly List<TaskCompletionSource> _pending = [];
        public int Pending { get { lock (_pending) return _pending.Count(source => !source.Task.IsCompleted); } }

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            lock (_pending) _pending.Add(source);
            return source.Task;
        }

        public void ReleaseAll()
        {
            TaskCompletionSource[] pending;
            lock (_pending) pending = [.. _pending];
            foreach (var source in pending) source.TrySetResult();
        }
    }
}
