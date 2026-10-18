using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Terrain;
using SkiaSharp;

namespace Noctaxis.Core.Tests;

// PF-02: preloading skips tiles already stored, which the calculation loads (and validates) itself, so a stored
// tile is no longer decoded once by the preload and again after the decoded cache evicted it.
public sealed class TerrariumStoredTilePreloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Noctaxis-preload-" + Guid.NewGuid());
    private static readonly GeoCoordinate Observer = new(53.4, -1.5);
    private static readonly TerrainProfileRequest Request = new(72, 6_000);

    public TerrariumStoredTilePreloadTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task TheCacheReportsAStoredTileOnlyOnceItIsStored()
    {
        var cache = new EnvironmentalTileCache(new Paths(_directory), NullLogger<EnvironmentalTileCache>.Instance);
        using var server = new TileServer();
        var provider = Provider(cache, server);
        var descriptor = new EnvironmentalTileDescriptor(TerrariumTerrainProvider.SourceId, TerrariumTerrainProvider.SourceVersion,
            "z12", $"{TerrariumTerrainProvider.LocatePixel(Observer, 12).Tile.X}-{TerrariumTerrainProvider.LocatePixel(Observer, 12).Tile.Y}", "png");
        Assert.False(cache.Contains(descriptor));
        await provider.GetElevationAsync(Observer, CancellationToken.None);
        Assert.True(cache.Contains(descriptor));
    }

    [Fact]
    public async Task PreloadingStoredTilesLoadsNothingAndAMissingTileIsStillFetched()
    {
        var cache = new EnvironmentalTileCache(new Paths(_directory), NullLogger<EnvironmentalTileCache>.Instance);
        using var server = new TileServer();
        var coordinates = Ring(Observer, 4_000);
        var first = Provider(cache, server);
        await first.PreloadAsync(coordinates, CancellationToken.None);
        var stored = server.Requests;
        Assert.True(stored > 0);
        Assert.Equal(stored, first.Metrics.TileLoads);

        var later = Provider(cache, server);
        await later.PreloadAsync(coordinates, CancellationToken.None);
        Assert.Equal(0, later.Metrics.TileLoads);
        Assert.Equal(0, later.Metrics.TileDecodes);
        Assert.Equal(stored, server.Requests);

        // A stored tile removed behind the cache's back is not stored any more, so it is fetched again.
        var removed = Directory.EnumerateFiles(_directory, "*.png", SearchOption.AllDirectories).First();
        File.Delete(removed);
        await Provider(cache, server).PreloadAsync(coordinates, CancellationToken.None);
        Assert.Equal(stored + 1, server.Requests);
    }

    [Fact]
    public async Task AProfileFromStoredTilesIsIdenticalToTheFirstAndDownloadsNothing()
    {
        var cache = new EnvironmentalTileCache(new Paths(_directory), NullLogger<EnvironmentalTileCache>.Instance);
        using var server = new TileServer();
        var first = await Horizon(Provider(cache, server)).GetProfileAsync(Observer, Request, CancellationToken.None);
        var downloads = server.Requests;
        var provider = Provider(cache, server);
        var second = await Horizon(provider).GetProfileAsync(Observer, Request, CancellationToken.None);
        Assert.Equal(downloads, server.Requests);
        Assert.Equal(first.Samples.Count, second.Samples.Count);
        for (var index = 0; index < first.Samples.Count; index++)
        {
            Assert.Equal(first.Samples[index].TerrainHorizonElevationDegrees, second.Samples[index].TerrainHorizonElevationDegrees);
            Assert.Equal(first.Samples[index].TerrainHorizonFeatureDistanceMetres, second.Samples[index].TerrainHorizonFeatureDistanceMetres);
            Assert.Equal(first.Samples[index].Sightline, second.Samples[index].Sightline);
        }
        Assert.Equal(first.ChosenObserverGroundElevationMetres, second.ChosenObserverGroundElevationMetres);
        // Each stored tile is decoded once: by the calculation that uses it, not also by the preload.
        Assert.InRange(provider.Metrics.TileDecodes, 1, downloads);
    }

    private static TerrariumTerrainProvider Provider(IEnvironmentalTileCache cache, TileServer server) =>
        new(new HttpClient(server, disposeHandler: false), cache, NullLogger<TerrariumTerrainProvider>.Instance);

    private static HorizonService Horizon(TerrariumTerrainProvider terrain) =>
        new(new TerrainSurfaceResolver(terrain, new NoCover(), NullLogger<TerrainSurfaceResolver>.Instance),
            NullLogger<HorizonService>.Instance);

    private static GeoCoordinate[] Ring(GeoCoordinate centre, double metres) =>
        Enumerable.Range(0, 36).Select(index =>
        {
            var bearing = index * 10 * Math.PI / 180;
            return new GeoCoordinate(centre.Latitude + metres * Math.Cos(bearing) / 111_320,
                centre.Longitude + metres * Math.Sin(bearing) / (111_320 * Math.Cos(centre.Latitude * Math.PI / 180)));
        }).Append(centre).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class Paths(string directory) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => directory;
    }

    private sealed class NoCover : ILandCoverProvider
    {
        public Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(EnvironmentalValue<LandCoverClass>.Unavailable("fixture", "1", "No classification."));

        public Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token) =>
            Task.FromResult(new LandCoverBatchResult(EnvironmentalDataState.Unavailable, new LandCoverClass?[coordinates.Count],
                "fixture", "1", "No classification."));
    }

    // Serves one deterministic Terrarium PNG for every tile and counts requests.
    private sealed class TileServer : HttpMessageHandler
    {
        private static readonly byte[] Png = CreatePng();
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        }

        private static byte[] CreatePng()
        {
            using var bitmap = new SKBitmap(256, 256);
            for (var y = 0; y < 256; y++)
            for (var x = 0; x < 256; x++)
            {
                var encoded = 100 + (x * 17 + y * 13) % 900 + 32768;
                bitmap.SetPixel(x, y, new SKColor((byte)(encoded / 256), (byte)encoded, 0));
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
