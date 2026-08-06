using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.Tests;

public sealed class BuildingFeatureDataTests
{
    [Fact]
    public void BuildingQuery_RequestsCentresWithoutFootprintGeometry()
    {
        var query = OverpassBuildingFeatureDataService.BuildQuery(Viewport().Bounds);

        Assert.Contains("way[\"building\"]", query);
        Assert.Contains("relation[\"building\"]", query);
        Assert.Contains("out tags center qt", query);
        Assert.DoesNotContain("geom", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("place=", query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildingParser_NormalisesWaysAndRelationsAndSkipsMissingCentres()
    {
        var json = Bytes("""
        {"elements":[
          {"type":"way","id":1,"center":{"lat":53.60,"lon":-0.45},"tags":{"building":"house","building:levels":"2","name":"One"}},
          {"type":"way","id":1,"center":{"lat":53.61,"lon":-0.44},"tags":{"building":"house"}},
          {"type":"relation","id":2,"center":{"lat":53.62,"lon":-0.43},"tags":{"building":"warehouse","building:levels":"not-a-number"}},
          {"type":"way","id":3,"tags":{"building":"shed"}},
          {"type":"node","id":4,"lat":53.6,"lon":-0.4,"tags":{"building":"yes"}}
        ]}
        """);

        var buildings = OverpassBuildingFeatureDataService.Parse(json, out var malformed);

        Assert.Equal(2, buildings.Length);
        Assert.Equal(2, malformed);
        Assert.Equal(2, buildings.Single(value => value.OsmId == 1).Levels);
        Assert.Null(buildings.Single(value => value.OsmId == 2).Levels);
        Assert.Contains(buildings, value => value.OsmType == "relation" && value.Building == "warehouse");
    }

    [Fact]
    public async Task FullRegionCacheHit_AvoidsNetwork()
    {
        using var files = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => Json(Buildings(3)));
        using var service = Service(files.Path, handler);

        var first = await service.Value.FetchAsync(Guid.NewGuid(), "First", Viewport(), false, default);
        var second = await service.Value.FetchAsync(Guid.NewGuid(), "Second", Viewport(), false, default);

        Assert.Equal(BuildingStarStatus.Complete, first.Outcome.Status);
        Assert.Equal(BuildingStarStatus.Cached, second.Outcome.Status);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(3, second.Outcome.BuildingCount);
        Assert.Single(Directory.GetFiles(Path.Combine(service.Value.SharedCacheDirectory, "regions"), "*.json.gz"));
    }

    [Fact]
    public async Task ExplicitRefresh_ReplacesRegionCacheButDeduplicatesAnImmediateBulkPeer()
    {
        using var files = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => Json(Buildings(2)));
        using var service = Service(files.Path, handler);
        await service.Value.FetchAsync(Guid.NewGuid(), "Initial", Viewport(), false, default);

        var refreshed = await service.Value.FetchAsync(Guid.NewGuid(), "Refresh", Viewport(), true, default);
        var bulkPeer = await service.Value.FetchAsync(Guid.NewGuid(), "Peer", Viewport(), true, default);

        Assert.Equal(BuildingStarStatus.Complete, refreshed.Outcome.Status);
        Assert.Equal(BuildingStarStatus.Cached, bulkPeer.Outcome.Status);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GatewayTimeout_SubdividesIntoBoundedTileRegions()
    {
        using var files = new TemporaryDirectory();
        var handler = new RecordingHandler(request => request == 1
            ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            : Json(Buildings(2, request * 100L)));
        using var service = Service(files.Path, handler);

        var result = await service.Value.FetchAsync(Guid.NewGuid(), "Dense", Viewport(), false, default);

        Assert.Equal(BuildingStarStatus.Complete, result.Outcome.Status);
        Assert.True(result.Outcome.SubdivisionUsed);
        Assert.InRange(result.Outcome.AttemptCount, 2, OverpassBuildingFeatureDataService.MaximumRequestsPerRefresh);
        Assert.True(result.Outcome.RegionCount >= 1);
        Assert.Equal(result.Outcome.RegionCount, result.Outcome.CompletedRegionCount);
        Assert.Equal(handler.RequestCount, result.Outcome.AttemptCount);
        Assert.All(result.Data!.Buildings, building =>
            Assert.True(Viewport().Bounds.Contains(building.Latitude, building.Longitude)));
    }

    [Fact]
    public async Task SuccessfulLargeFullQuery_DoesNotSubdivide()
    {
        using var files = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => Json(Buildings(5_864)));
        using var service = Service(files.Path, handler);

        var result = await service.Value.FetchAsync(Guid.NewGuid(), "Populated", Viewport(), false, default);

        Assert.Equal(BuildingStarStatus.Complete, result.Outcome.Status);
        Assert.Equal(5_864, result.Outcome.BuildingCount);
        Assert.False(result.Outcome.SubdivisionUsed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UnrelatedHttpFailure_DoesNotSubdivide()
    {
        using var files = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var service = Service(files.Path, handler);

        var result = await service.Value.FetchAsync(Guid.NewGuid(), "Forbidden", Viewport(), false, default);

        Assert.Equal(BuildingStarStatus.Unavailable, result.Outcome.Status);
        Assert.False(result.Outcome.SubdivisionUsed);
        Assert.Equal(403, result.Outcome.HttpStatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RepeatedDensityFailures_RespectRequestAndSubdivisionLimits()
    {
        using var files = new TemporaryDirectory();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        using var service = Service(files.Path, handler);

        var result = await service.Value.FetchAsync(Guid.NewGuid(), "Dense", Viewport(), false, default);

        Assert.Equal(BuildingStarStatus.Unavailable, result.Outcome.Status);
        Assert.True(result.Outcome.SubdivisionUsed);
        Assert.True(result.Outcome.SubdivisionDepth <= OverpassBuildingFeatureDataService.MaximumSubdivisionDepth);
        Assert.True(handler.RequestCount <= OverpassBuildingFeatureDataService.MaximumRequestsPerRefresh);
    }

    [Fact]
    public async Task SharedSubdivisionRegions_AreReusedByAnotherLocation()
    {
        using var files = new TemporaryDirectory();
        var bounds = Viewport().Bounds;
        var fullBbox = $"({bounds.South:0.#######},{bounds.West:0.#######}," +
                       $"{bounds.North:0.#######},{bounds.East:0.#######})";
        var handler = new RecordingHandler((_, body) =>
            Uri.UnescapeDataString(body).Contains(fullBbox, StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                : Json(Buildings(1)));
        using var service = Service(files.Path, handler);

        var first = await service.Value.FetchAsync(Guid.NewGuid(), "First", Viewport(), false, default);
        var firstRequests = handler.RequestCount;
        var second = await service.Value.FetchAsync(Guid.NewGuid(), "Second", Viewport(), false, default);

        Assert.True(first.Outcome.SubdivisionUsed);
        Assert.True(second.Outcome.SubdivisionUsed);
        Assert.Equal(firstRequests + 1, handler.RequestCount);
        Assert.True(second.Outcome.CacheHitCount > 0);
        Assert.Equal(0, second.Outcome.DownloadedRegionCount);
    }

    private static WebMercatorViewport Viewport() => new(13, 256, 896, 504, 53.5664, -0.5063);

    private static string Buildings(int count, long idOffset = 0)
    {
        var entries = Enumerable.Range(0, count).Select(index =>
            "{\"type\":\"way\",\"id\":" + (idOffset + index + 1) +
            ",\"center\":{\"lat\":53.56,\"lon\":-0.50}," +
            "\"tags\":{\"building\":\"house\",\"building:levels\":\"2\"}}" );
        return "{\"elements\":[" + string.Join(',', entries) + "]}";
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static OwnedService Service(string root, RecordingHandler handler)
    {
        var client = new HttpClient(handler);
        var service = new OverpassBuildingFeatureDataService(client,
            new OverpassMapFeatureOptions(new Uri("https://overpass.test/api/interpreter"), "test-overpass"),
            new TestPaths(root), NullLogger<OverpassBuildingFeatureDataService>.Instance);
        return new OwnedService(client, service);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<int, string, HttpResponseMessage> _response;
        public RecordingHandler(Func<int, HttpResponseMessage> response) : this((request, _) => response(request)) { }
        public RecordingHandler(Func<int, string, HttpResponseMessage> response) => _response = response;
        public int RequestCount { get; private set; }
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty :
                await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return _response(++RequestCount, body);
        }
    }

    private sealed class TestPaths(string root) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => root;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "Noctaxis.BuildingTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    private sealed class OwnedService(HttpClient client, OverpassBuildingFeatureDataService value) : IDisposable
    {
        public OverpassBuildingFeatureDataService Value { get; } = value;
        public void Dispose() => client.Dispose();
    }
}
