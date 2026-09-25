using System.Net;
using Noctaxis.Core.LightPollution;

namespace Noctaxis.Desktop.Tests;

public sealed class LightPollutionInstallationTests
{
    [Fact]
    public async Task FullInstallRetriesReportsBoundedProgressAndReplacesOnlyOnSuccess()
    {
        await using var paths = new AtlasTestData();
        var data = AtlasTestData.Gzip(AtlasTestData.Encode((_, _) => 0));
        var calls = 0; var active = 0; var peak = 0;
        var fail = false;
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            int observed; do { observed = peak; } while (count > observed && Interlocked.CompareExchange(ref peak, count, observed) != observed);
            try
            {
                await Task.Delay(1, ct);
                if (fail) return new(HttpStatusCode.NotFound);
                if (Interlocked.Increment(ref calls) == 1) return new(HttpStatusCode.ServiceUnavailable);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            }
            finally { Interlocked.Decrement(ref active); }
        }));
        var store = new LorenzInstallation(paths, http);
        await store.DetectAsync(); Assert.False(store.IsAvailable); Assert.Equal(0, calls);
        var progress = new Capture();
        await store.InstallAsync(progress);
        Assert.True(store.IsAvailable); Assert.InRange(peak, 1, 4);
        Assert.Equal(2017, calls); Assert.Equal(2016, progress.Last!.CompletedTiles);
        Assert.Equal(2016L * data.Length, progress.Last.DownloadedBytes);
        var original = store.CurrentDirectory;
        fail = true;
        await Assert.ThrowsAnyAsync<Exception>(() => store.InstallAsync());
        Assert.Equal(original, store.CurrentDirectory); Assert.True(store.IsAvailable);
        fail = false;
        await store.InstallAsync();
        Assert.NotEqual(original, store.CurrentDirectory); Assert.False(Directory.Exists(original));
        var beforeDetect = calls; await store.DetectAsync(); Assert.True(store.IsAvailable); Assert.Equal(beforeDetect, calls);
    }

    [Fact]
    public async Task CancellationAndCorruptionCannotPublishAnInstallation()
    {
        await using var paths = new AtlasTestData();
        using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, ct) => { cancel.Cancel(); await Task.Delay(10, ct); return new(HttpStatusCode.OK); }));
        var store = new LorenzInstallation(paths, http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(cancellationToken: cancel.Token));
        await store.DetectAsync(); Assert.False(store.IsAvailable);
        foreach (var payload in new[] { new byte[] { 3, 4, 5 }, AtlasTestData.Gzip(new byte[360000]), AtlasTestData.Gzip(new byte[360002]) })
        {
            using var corruptHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })));
            var corruptStore = new LorenzInstallation(paths, corruptHttp);
            await Assert.ThrowsAnyAsync<InvalidDataException>(() => corruptStore.InstallAsync());
            await corruptStore.DetectAsync(); Assert.False(corruptStore.IsAvailable);
        }
    }

    [Fact]
    public async Task StartupRequiresCompleteManifestAndRemoveClearsNumericalCaches()
    {
        await using var paths = new AtlasTestData();
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("No network allowed")));
        var store = new LorenzInstallation(paths, http);
        var directory = await paths.SeedAsync(store);
        await using var provider = new LightPollutionDataProvider(store);
        Assert.NotNull(await provider.SampleAsync(0, 0)); Assert.Equal(1, provider.DecodedTileCount);
        using (var read = await provider.OpenAsync()) await read!.SampleAsync(0, 0, overview: true);
        Assert.True(provider.HasOverview);
        await store.RemoveAsync();
        Assert.False(store.IsAvailable); Assert.False(Directory.Exists(directory));
        Assert.Equal(0, provider.DecodedTileCount); Assert.False(provider.HasOverview);
        Assert.Null(await provider.SampleAsync(0, 0));
        directory = await paths.SeedAsync(store);
        File.Delete(Path.Combine(directory, "binary", LorenzAtlas.FileName(72, 28)));
        await Assert.ThrowsAnyAsync<IOException>(() => provider.SampleAsync(74, 179));
        Assert.False(store.IsAvailable); Assert.NotNull(store.ReadError);
        await store.DetectAsync(); Assert.False(store.IsAvailable);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), "{}");
        await store.DetectAsync(); Assert.False(store.IsAvailable);
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Capture : IProgress<LorenzInstallProgress>
    { public LorenzInstallProgress? Last; public void Report(LorenzInstallProgress value) { Assert.True(Last is null || value.CompletedTiles > Last.CompletedTiles); Last = value; } }
}
