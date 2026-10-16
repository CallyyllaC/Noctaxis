using System.Net;
using Noctaxis.Desktop.Mapping.Vector;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// NX-39 (Opus S-A02): offline basemap coverage survives an OpenFreeMap tileset change. Earlier versions are retained
/// within bounds and serve tiles the current version cannot (offline, server failure); retention is bounded by version
/// count and bytes, and the current version is never retired.
/// </summary>
public sealed class BasemapRetentionTests : IDisposable
{
    private const string Older = "20260801_000000_pt";
    private const string Newer = "20260913_164504_pt";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-basemap-retention-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri TileJsonUri = new("https://tiles.example/planet");

    private static string TileJson(string version) =>
        "{\"tilejson\":\"3.0.0\",\"tiles\":[\"https://tiles.example/planet/" + version + "/{z}/{x}/{y}.pbf\"],\"maxzoom\":14}";

    private void Store(string version, int z, int x, int y, byte[] bytes)
    {
        var path = Path.Combine(_directory, version, z.ToString(), x.ToString(), y + ".pbf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private OpenFreeMapTileFetcher Fetcher(Func<HttpRequestMessage, HttpResponseMessage> respond, Func<DateTimeOffset>? now = null) =>
        new(new HttpClient(new Handler(respond)) { Timeout = TimeSpan.FromSeconds(5) }, _directory, TileJsonUri, now);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task AfterAManifestUpdateAnUnreachableTileIsServedFromTheRetainedVersion()
    {
        Store(Older, 14, 1, 1, [1, 2, 3]);
        var tileRequests = 0;
        var fetcher = Fetcher(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/planet") return Json(TileJson(Newer));
            tileRequests++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        Assert.Equal([1, 2, 3], await fetcher.FetchAsync(14, 1, 1, default));
        Assert.Equal(1, tileRequests); // The current version was asked first; the retained tile only stood in.
        fetcher.RetireVersions(Newer);
        Assert.True(Directory.Exists(Path.Combine(_directory, Older)));
    }

    [Fact]
    public async Task OfflineStartupAfterAVersionChangeStillDrawsPreviouslyViewedAreas()
    {
        Store(Older, 14, 2, 2, [4, 5]);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "tilejson.json"), TileJson(Newer));
        var fetcher = Fetcher(_ => throw new HttpRequestException("offline"), () => DateTimeOffset.UtcNow.AddDays(3));

        Assert.Equal([4, 5], await fetcher.FetchAsync(14, 2, 2, default));
        Assert.Null(await fetcher.FetchAsync(14, 9, 9, default)); // Never viewed: nothing to show, and no failure.
    }

    [Fact]
    public async Task OfflineWithNoStoredManifestUsesAnyRetainedVersion()
    {
        Store(Older, 12, 3, 3, [6]);
        var fetcher = Fetcher(_ => throw new HttpRequestException("offline"));
        Assert.Equal([6], await fetcher.FetchAsync(12, 3, 3, default));
    }

    [Fact]
    public async Task OnlineTheCurrentVersionReplacesTheRetainedTile()
    {
        Store(Older, 14, 1, 1, [1]);
        var fetcher = Fetcher(request => request.RequestUri!.AbsolutePath == "/planet"
            ? Json(TileJson(Newer))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9]) });

        Assert.Equal([9, 9], await fetcher.FetchAsync(14, 1, 1, default));
        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(_directory, Newer, "14", "1", "1.pbf")));
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(_directory, Older, "14", "1", "1.pbf")));
    }

    [Fact]
    public async Task ARollbackToARetainedVersionUsesItsTilesWithoutDownloading()
    {
        Store(Older, 14, 1, 1, [7]);
        Store(Newer, 14, 1, 1, [8]);
        var tileRequests = 0;
        var fetcher = Fetcher(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/planet") return Json(TileJson(Older));
            tileRequests++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        Assert.Equal([7], await fetcher.FetchAsync(14, 1, 1, default));
        Assert.Equal(0, tileRequests);
    }

    [Fact]
    public void RetentionKeepsTheCurrentAndTheNewestEarlierVersionsWithinTheirBudget()
    {
        string[] versions = ["20260101_000000_pt", "20260201_000000_pt", "20260301_000000_pt", "20260401_000000_pt", "20260501_000000_pt"];
        foreach (var version in versions) Store(version, 1, 0, 0, new byte[100]);
        Store(Newer, 1, 0, 0, new byte[100]);
        var fetcher = new OpenFreeMapTileFetcher(new HttpClient(), _directory, TileJsonUri);

        fetcher.RetireVersions(Newer);

        var kept = Directory.GetDirectories(_directory).Select(path => Path.GetFileName(path)!).OrderBy(name => name).ToArray();
        Assert.Equal(["20260401_000000_pt", "20260501_000000_pt", Newer], kept);

        // A byte budget smaller than both earlier versions together retires the oldest of them; never the current one.
        var tight = new OpenFreeMapTileFetcher(new HttpClient(), _directory, TileJsonUri) { RetainedBytesBudget = 150 };
        tight.RetireVersions(Newer);
        kept = Directory.GetDirectories(_directory).Select(path => Path.GetFileName(path)!).OrderBy(name => name).ToArray();
        Assert.Equal(["20260501_000000_pt", Newer], kept);
        var none = new OpenFreeMapTileFetcher(new HttpClient(), _directory, TileJsonUri) { RetainedBytesBudget = 0 };
        none.RetireVersions(Newer);
        Assert.Equal([Newer], Directory.GetDirectories(_directory).Select(path => Path.GetFileName(path)!).ToArray());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
