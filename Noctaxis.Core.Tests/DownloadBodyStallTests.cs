using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;

namespace Noctaxis.Core.Tests;

/// <summary>
/// R01 / R-01 (phase-0 audit, Opus base): HttpClient.Timeout ends at the response headers when the body is read as
/// a stream. A source that sends headers and then stops sending must still end within a bounded time, as a
/// transient source failure, and release the shared tile load, the per-tile lock and the disk lease it holds.
/// </summary>
public sealed class DownloadBodyStallTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-body-stall-" + Guid.NewGuid().ToString("N"));
    private sealed record Paths(string Root) : Noctaxis.Core.Persistence.IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Root;
    }

    [Fact]
    public async Task ABodyThatStopsArrivingEndsAsATimeoutNotAHang()
    {
        using var client = new HttpClient(new StallingHandler(stallAfterBytes: 10)) { Timeout = TimeSpan.FromMilliseconds(300) };
        using var destination = new MemoryStream();

        var download = EnvironmentalHttpDownloader.DownloadToStreamAsync(client, new Uri("https://source.test/tile"),
            1024 * 1024, destination, CancellationToken.None);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<TimeoutException>(failure.InnerException);
    }

    [Fact]
    public async Task ASlowButSteadyBodyIsNotCutOff()
    {
        // Each chunk arrives within the window, while the whole body takes several windows. The silence is measured
        // on time that passes only as the source sends, so a busy machine cannot make a chunk seem late.
        var time = new ManualTime();
        using var client = new HttpClient(new TricklingHandler(time, chunks: 8, delay: TimeSpan.FromMilliseconds(120)))
            { Timeout = TimeSpan.FromMilliseconds(300) };
        using var destination = new MemoryStream();

        var result = await EnvironmentalHttpDownloader.DownloadToStreamAsync(client, new Uri("https://source.test/tile"),
            1024 * 1024, destination, CancellationToken.None, 3, time).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EnvironmentalDataState.Available, result.State);
        Assert.Equal(8 * 16, destination.Length);
        Assert.True(time.Elapsed > 3 * client.Timeout, $"the body took only {time.Elapsed}");

        // The same source with each chunk later than the window is cut off at the first of them.
        using var late = new HttpClient(new TricklingHandler(time, chunks: 8, delay: TimeSpan.FromMilliseconds(301)))
            { Timeout = TimeSpan.FromMilliseconds(300) };
        using var partial = new MemoryStream();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EnvironmentalHttpDownloader
            .DownloadToStreamAsync(late, new Uri("https://source.test/tile"), 1024 * 1024, partial, CancellationToken.None, 3, time)
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.Equal(0, partial.Length);
    }

    [Fact]
    public async Task CallerCancellationDuringAStallIsStillCancellation()
    {
        using var client = new HttpClient(new StallingHandler(stallAfterBytes: 0)) { Timeout = TimeSpan.FromSeconds(30) };
        using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EnvironmentalHttpDownloader
            .DownloadToStreamAsync(client, new Uri("https://source.test/tile"), 1024, destination, cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task AStalledTileBecomesUnavailableAndReleasesItsLockAndLease()
    {
        var cache = new EnvironmentalTileCache(new Paths(_directory), NullLogger<EnvironmentalTileCache>.Instance);
        using var client = new HttpClient(new StallingHandler(stallAfterBytes: 4)) { Timeout = TimeSpan.FromMilliseconds(300) };
        var descriptor = new EnvironmentalTileDescriptor("fixture", "1", "layer", "tile", ".bin");

        Task<EnvironmentalCacheResult> Load() => cache.GetOrCreateStreamedAsync(descriptor,
            (destination, token) => EnvironmentalHttpDownloader.DownloadToStreamAsync(client, new Uri("https://source.test/tile"),
                1024, destination, token),
            _ => true, CancellationToken.None);

        var first = await Load().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EnvironmentalDataState.SourceUnavailable, first.State);
        // The per-tile lock was released: a second load gets as far as the source again, and also ends.
        var second = await Load().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EnvironmentalDataState.SourceUnavailable, second.State);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>Time that passes only when <see cref="Advance"/> is called, firing the timers it passes.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public TimeSpan Elapsed { get; private set; }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            Timer[] due;
            lock (_gate)
            {
                _now += by;
                Elapsed += by;
                due = _timers.Where(timer => timer.Due <= _now).ToArray();
                foreach (var timer in due) timer.Due = null;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class Timer(ManualTime time, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due;
            // One-shot, as a CancellationTokenSource uses it; an infinite due time disarms it.
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (time._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : time._now + dueTime;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (time._gate) { Due = null; time._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    /// <summary>Headers at once, then <paramref name="stallAfterBytes"/> bytes, then nothing until cancelled.</summary>
    private sealed class StallingHandler(int stallAfterBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(stallAfterBytes)) });
    }

    private sealed class StallingStream(int bytesBeforeStall) : Stream
    {
        private int _remaining = bytesBeforeStall;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining > 0)
            {
                var count = Math.Min(_remaining, buffer.Length);
                buffer.Span[..count].Fill(0x2A);
                _remaining -= count;
                return count;
            }
            // Like a network stream with no data: waits until the read is cancelled.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TricklingHandler(ManualTime time, int chunks, TimeSpan delay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TricklingStream(time, chunks, delay)) });
    }

    /// <summary>Sends <paramref name="chunks"/> chunks of 16 bytes, each after <paramref name="delay"/> of silence.</summary>
    private sealed class TricklingStream(ManualTime time, int chunks, TimeSpan delay) : Stream
    {
        private int _remaining = chunks;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return ValueTask.FromResult(0);
            // The silence passes before the chunk arrives; a read cancelled meanwhile gets nothing.
            time.Advance(delay);
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
            _remaining--;
            buffer.Span[..16].Fill(0x2A);
            return ValueTask.FromResult(16);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
