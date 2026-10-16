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
        // Each chunk arrives within the window, while the whole body takes several windows.
        using var client = new HttpClient(new TricklingHandler(chunks: 8, delay: TimeSpan.FromMilliseconds(120)))
            { Timeout = TimeSpan.FromMilliseconds(300) };
        using var destination = new MemoryStream();

        var result = await EnvironmentalHttpDownloader.DownloadToStreamAsync(client, new Uri("https://source.test/tile"),
            1024 * 1024, destination, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EnvironmentalDataState.Available, result.State);
        Assert.Equal(8 * 16, destination.Length);
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

    private sealed class TricklingHandler(int chunks, TimeSpan delay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TricklingStream(chunks, delay)) });
    }

    private sealed class TricklingStream(int chunks, TimeSpan delay) : Stream
    {
        private int _remaining = chunks;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return 0;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _remaining--;
            buffer.Span[..16].Fill(0x2A);
            return 16;
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
