using System.Net;
using Noctaxis.Desktop.Mapping.Vector;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// R01 / S-A04: an OpenFreeMap tile whose body stops arriving after the headers ends as an unavailable tile (retried
/// later) within the client's timeout, and does not leave the tile's shared download pending for later requests.
/// </summary>
public sealed class OpenFreeMapBodyStallTests
{
    private const string TileJson = """{"tilejson":"3.0.0","tiles":["https://tiles.example/planet/20260913_164504_pt/{z}/{x}/{y}.pbf"],"maxzoom":14}""";

    [Fact]
    public async Task AStalledTileBodyIsUnavailableAndTheNextRequestTriesAgain()
    {
        var stall = true;
        var tileRequests = 0;
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/planet")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TileJson) };
            Interlocked.Increment(ref tileRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = stall ? new StreamContent(new SilentStream()) : new ByteArrayContent([0x1A, 0x00])
            };
        });
        var fetcher = new OpenFreeMapTileFetcher(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(300) },
            cacheDirectory: null, new Uri("https://tiles.example/planet"));

        Assert.Null(await fetcher.FetchAsync(14, 1, 1, default).WaitAsync(TimeSpan.FromSeconds(10)));

        stall = false;
        Assert.Equal([0x1A, 0x00], await fetcher.FetchAsync(14, 1, 1, default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, tileRequests);
    }

    [Fact]
    public void ATilePayloadThatInflatesBeyondTheLimitIsMalformedInput()
    {
        // R02: 1 MiB of zeros gzips to about 1 KiB; with a 256 KiB limit the expansion stops at the limit.
        var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(new byte[1024 * 1024]);
        var bytes = compressed.ToArray();
        Assert.True(bytes.Length < 16 * 1024);

        Assert.Throws<InvalidDataException>(() => OpenFreeMapTileFetcher.Decompress(bytes, maximumBytes: 256 * 1024));
        Assert.Equal(1024 * 1024, OpenFreeMapTileFetcher.Decompress(bytes).Length);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>A body that never sends anything until its read is cancelled, like a stalled connection.</summary>
    private sealed class SilentStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
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
}
