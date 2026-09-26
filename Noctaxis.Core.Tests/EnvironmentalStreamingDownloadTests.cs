using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Persistence;

namespace Noctaxis.Core.Tests;

// Large sources (WorldCover tiles are tens of megabytes) stream straight into the staging file.
// These tests pin the install, cleanup and validation behaviour that streaming must not change.
public sealed class EnvironmentalStreamingDownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "Noctaxis-Streaming-" + Guid.NewGuid().ToString("N"));
    private static readonly EnvironmentalTileDescriptor Descriptor =
        new("test-source", "v1", "land-cover", "N51W001", "tif");

    [Fact]
    public async Task StreamedInstall_CommitsTheWholePayloadWithoutBufferingIt()
    {
        var payload = Payload(512 * 1024);
        var handler = new StreamingHandler(payload);
        using var client = new HttpClient(handler);
        var cache = CreateCache();
        long staged = 0;

        var result = await cache.GetOrCreateStreamedAsync(Descriptor,
            (destination, token) => EnvironmentalHttpDownloader.DownloadToStreamAsync(client,
                new Uri("https://example.invalid/tile.tif"), payload.Length, destination, token),
            path => { staged = new FileInfo(path).Length; return true; }, CancellationToken.None);

        Assert.Equal(EnvironmentalDataState.Available, result.State);
        Assert.False(result.CacheHit);
        // Validation saw the complete payload, and it was committed under the cache's own name.
        Assert.Equal(payload.Length, staged);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path!));
        Assert.Empty(Directory.GetFiles(cache.RootDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StreamedDownload_ReportsSuccessWithoutMaterialisingTheBytes()
    {
        var payload = Payload(256 * 1024);
        using var client = new HttpClient(new StreamingHandler(payload));
        using var destination = new MemoryStream();

        var result = await EnvironmentalHttpDownloader.DownloadToStreamAsync(client,
            new Uri("https://example.invalid/tile.tif"), payload.Length, destination, CancellationToken.None);

        Assert.Equal(EnvironmentalDataState.Available, result.State);
        Assert.Null(result.Bytes);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task StreamedDownload_StopsAtTheSizeLimitInsteadOfReadingEverything()
    {
        var payload = Payload(512 * 1024);
        using var client = new HttpClient(new StreamingHandler(payload, declareContentLength: false));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => EnvironmentalHttpDownloader.DownloadToStreamAsync(
            client, new Uri("https://example.invalid/tile.tif"), 64 * 1024, destination, CancellationToken.None));

        Assert.True(destination.Length <= 64 * 1024);
    }

    [Fact]
    public async Task CancellationDuringDownload_LeavesNeitherAPartialFileNorAStagingFile()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StreamingHandler(Payload(512 * 1024), onChunk: () => cancellation.Cancel());
        using var client = new HttpClient(handler);
        var cache = CreateCache();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateStreamedAsync(Descriptor,
            (destination, token) => EnvironmentalHttpDownloader.DownloadToStreamAsync(client,
                new Uri("https://example.invalid/tile.tif"), 8 * 1024 * 1024, destination, token),
            _ => true, cancellation.Token));

        Assert.Empty(Directory.GetFiles(cache.RootDirectory, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(cache.RootDirectory, "N51W001.tif", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidDownload_IsRejectedAndTheExistingInstallationSurvives()
    {
        var installed = Payload(4 * 1024);
        var cache = CreateCache();
        using var client = new HttpClient(new StreamingHandler(Payload(8 * 1024)));
        // Install a valid file first, then make every later payload fail validation.
        var accept = true;
        var first = await cache.GetOrCreateStreamedAsync(Descriptor,
            async (destination, token) => { await destination.WriteAsync(installed, token);
                return new EnvironmentalAcquisitionResult(EnvironmentalDataState.Available, null, "seeded"); },
            _ => accept, CancellationToken.None);
        Assert.Equal(installed, await File.ReadAllBytesAsync(first.Path!));
        accept = false;

        var rejected = await cache.GetOrCreateStreamedAsync(Descriptor,
            (destination, token) => EnvironmentalHttpDownloader.DownloadToStreamAsync(client,
                new Uri("https://example.invalid/tile.tif"), 8 * 1024 * 1024, destination, token),
            _ => accept, CancellationToken.None);

        Assert.Equal(EnvironmentalDataState.InvalidData, rejected.State);
        Assert.Null(rejected.Path);
        Assert.Equal(installed, await File.ReadAllBytesAsync(first.Path!));
        Assert.Empty(Directory.GetFiles(cache.RootDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ValidInstallation_IsServedFromDiskWithoutDownloading()
    {
        var installed = Payload(4 * 1024);
        var cache = CreateCache();
        var handler = new StreamingHandler(Payload(8 * 1024));
        using var client = new HttpClient(handler);
        await cache.GetOrCreateStreamedAsync(Descriptor,
            async (destination, token) => { await destination.WriteAsync(installed, token);
                return new EnvironmentalAcquisitionResult(EnvironmentalDataState.Available, null, "seeded"); },
            _ => true, CancellationToken.None);

        var reused = await cache.GetOrCreateStreamedAsync(Descriptor,
            (destination, token) => EnvironmentalHttpDownloader.DownloadToStreamAsync(client,
                new Uri("https://example.invalid/tile.tif"), 8 * 1024 * 1024, destination, token),
            _ => true, CancellationToken.None);

        Assert.True(reused.CacheHit);
        Assert.Equal(EnvironmentalDataState.Cached, reused.State);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(installed, await File.ReadAllBytesAsync(reused.Path!));
    }

    [Fact]
    public async Task AbsentTile_IsReportedAndLeavesNothingBehind()
    {
        var cache = CreateCache();
        using var client = new HttpClient(new StreamingHandler([], status: HttpStatusCode.NotFound));

        var result = await cache.GetOrCreateStreamedAsync(Descriptor,
            (destination, token) => EnvironmentalHttpDownloader.DownloadToStreamAsync(client,
                new Uri("https://example.invalid/tile.tif"), 8 * 1024 * 1024, destination, token),
            _ => true, CancellationToken.None);

        Assert.Equal(EnvironmentalDataState.TileAbsent, result.State);
        Assert.Null(result.Path);
        Assert.Equal(404, result.HttpStatusCode);
        Assert.Empty(Directory.GetFiles(cache.RootDirectory, "*", SearchOption.AllDirectories));
    }

    private EnvironmentalTileCache CreateCache() =>
        new(new TestPaths(_directory), NullLogger<EnvironmentalTileCache>.Instance);

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++) bytes[index] = (byte)(index % 251);
        return bytes;
    }

    private sealed class TestPaths(string path) : IUserDataPathProvider
    { public string GetApplicationDataDirectory() => path; }

    // Writes the payload in chunks so tests can act part way through the body.
    private sealed class StreamingHandler(byte[] payload, HttpStatusCode status = HttpStatusCode.OK,
        Action? onChunk = null, bool declareContentLength = true) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status);
            if (status != HttpStatusCode.OK) return Task.FromResult(response);
            response.Content = new StreamContent(new ChunkedStream(payload, onChunk));
            if (declareContentLength) response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class ChunkedStream(byte[] payload, Action? onChunk) : Stream
    {
        private const int ChunkSize = 16 * 1024;
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= payload.Length) return 0;
            onChunk?.Invoke();
            var read = Math.Min(Math.Min(count, ChunkSize), payload.Length - _position);
            Array.Copy(payload, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
