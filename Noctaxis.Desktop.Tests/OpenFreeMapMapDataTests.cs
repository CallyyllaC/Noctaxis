using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Mapping.Vector;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Saved-location map data from OpenFreeMap vector tiles: roads, waterways and the landscape replace the public
/// Overpass API and OpenStreetMap raster capture, read through the repository the Planner basemap uses.
/// </summary>
public sealed class OpenFreeMapMapDataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-ofm-mapdata-" + Guid.NewGuid().ToString("N"));
    private static readonly WebMercatorViewport Frame = WebMercatorViewport.Create(new GeoCoordinate(53.61, -0.43),
        LocationMapThumbnailService.TileZoom, LocationMapThumbnailService.TileSize, LocationMapThumbnailService.SourceWidth,
        LocationMapThumbnailService.SourceHeight);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void RoadsAndWaterwaysAreDerivedFromTheVectorTilesWithThePreviewsClasses()
    {
        var tile = new VectorTileTestBuilder();
        tile.Layer("transportation")
            .Line([(0, 100), (4096, 100)], ("class", "motorway"))
            .Line([(0, 200), (4096, 200)], ("class", "trunk"))
            .Line([(0, 300), (4096, 300)], ("class", "primary"))
            .Line([(0, 400), (4096, 400)], ("class", "secondary"))
            .Line([(0, 500), (4096, 500)], ("class", "tertiary"))
            .Line([(0, 600), (4096, 600)], ("class", "minor"))
            .Line([(0, 700), (4096, 700)], ("class", "primary"), ("ramp", 1L))
            .Line([(0, 800), (4096, 800)], ("class", "secondary"), ("brunnel", "bridge"));
        tile.Layer("waterway")
            .Line([(100, 0), (100, 4096)], ("class", "river"), ("name", "River Ancholme"))
            .Line([(200, 0), (200, 4096)], ("class", "canal"), ("name", "Old Canal"))
            .Line([(300, 0), (300, 4096)], ("class", "stream"), ("intermittent", 1L))
            .Line([(400, 0), (400, 4096)], ("class", "drain"), ("name", "Barrow Drain"));
        var placed = new PlacedVectorTile(13, 4086, 2659, Decode(tile.Build()));

        var document = OpenFreeMapMapFeatureDataService.Derive(Guid.NewGuid(), Frame, [placed], DateTimeOffset.UnixEpoch);

        Assert.Equal(OpenFreeMapMapFeatureDataService.FeatureSchemaVersion, document.SchemaVersion);
        Assert.Equal(OpenFreeMapMapFeatureDataService.ProviderId, document.Source.ProviderId);
        Assert.Contains("OpenStreetMap", document.Source.AttributionText);
        Assert.Equal(Frame.Bounds, document.Source.Bounds);
        Assert.Equal(
            [MapRoadClassification.Motorway, MapRoadClassification.ARoad, MapRoadClassification.ARoad,
                MapRoadClassification.BRoad, MapRoadClassification.BRoad],
            document.Roads.Select(road => road.Classification).Order());
        Assert.Single(document.Roads, road => road.Bridge == "yes");
        Assert.Equal([MapWaterwayClassification.River, MapWaterwayClassification.Canal, MapWaterwayClassification.Stream],
            document.Waterways.Select(waterway => waterway.Classification).Order());
        Assert.Equal("River Ancholme", document.Waterways.Single(w => w.Classification == MapWaterwayClassification.River).Name);
        Assert.Equal("yes", document.Waterways.Single(w => w.Classification == MapWaterwayClassification.Stream).Intermittent);
        Assert.All(document.Roads, road => Assert.True(road.Geometry.Length >= 2));
    }

    [Fact]
    public void ALineCrossingATileEdgeIsHeldOnceAndJoined()
    {
        // Each tile carries the river into its 64-unit buffer beyond the shared edge; the result is one line,
        // from the first tile's start to the second tile's end, with no doubled segment in the buffer.
        var west = new VectorTileTestBuilder();
        west.Layer("waterway").Line([(2000, 2048), (4160, 2048)], ("class", "river"), ("name", "River Ancholme"));
        var east = new VectorTileTestBuilder();
        east.Layer("waterway").Line([(-64, 2048), (2000, 2048)], ("class", "river"), ("name", "River Ancholme"));
        var tiles = new[]
        {
            new PlacedVectorTile(13, 4086, 2659, Decode(west.Build())),
            new PlacedVectorTile(13, 4087, 2659, Decode(east.Build()))
        };

        var river = Assert.Single(OpenFreeMapMapFeatureDataService.Derive(Guid.NewGuid(), Frame, tiles, DateTimeOffset.UnixEpoch).Waterways);

        var start = MapFrameVectorTiles.Unproject((4086 + 2000 / 4096d) * 256, (2659 + .5) * 256, 13);
        var end = MapFrameVectorTiles.Unproject((4087 + 2000 / 4096d) * 256, (2659 + .5) * 256, 13);
        Assert.Equal(start.Longitude, river.Geometry[0].Longitude, 9);
        Assert.Equal(end.Longitude, river.Geometry[^1].Longitude, 9);
        // Monotonic eastwards: no part runs back over the buffer.
        Assert.True(river.Geometry.Zip(river.Geometry.Skip(1)).All(pair => pair.Second.Longitude >= pair.First.Longitude));
    }

    [Fact]
    public void ClippingKeepsOnlyTheTilesOwnSquare()
    {
        var runs = MapFrameVectorTiles.ClipToTile([new(-100, 100), new(100, 100), new(100, -100), new(200, -100), new(200, 300)], 4096);

        Assert.Equal(2, runs.Count);
        Assert.Equal((0d, 100d), runs[0][0]);
        Assert.Equal((100d, 0d), runs[0][^1]);
        Assert.Equal((200d, 0d), runs[1][0]);
        Assert.Equal((200d, 300d), runs[1][^1]);
    }

    [Fact]
    public void MissingLayersAndEmptyTilesGiveNoFeaturesRatherThanFailing()
    {
        var noLayers = new PlacedVectorTile(13, 4086, 2659, Decode(new VectorTileTestBuilder().Build()));

        var document = OpenFreeMapMapFeatureDataService.Derive(Guid.NewGuid(), Frame, [noLayers], DateTimeOffset.UnixEpoch);
        var landscape = OpenFreeMapLandscapeSource.Rasterise(Frame, 2, [noLayers]);

        Assert.Empty(document.Roads);
        Assert.Empty(document.Waterways);
        Assert.True(landscape.HasNoData);
        Assert.All(landscape.Water.Concat(landscape.Vegetation).Concat(landscape.BuiltUp), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task AFrameWithAnUnavailableTileIsNotUsed()
    {
        var available = 0;
        var fetcher = new DelegateFetcher((_, _, _) => Interlocked.Increment(ref available) == 3 ? null : Tile());
        var repository = new VectorTileRepository(fetcher);

        var features = await new OpenFreeMapMapFeatureDataService(repository, NullLogger<OpenFreeMapMapFeatureDataService>.Instance)
            .FetchAsync(Guid.NewGuid(), Frame, CancellationToken.None);
        Assert.Null(features.Data);
        Assert.Equal("tile_unavailable", features.Outcome.FailureCode);
        await Assert.ThrowsAsync<MapFeatureRequestException>(() =>
            new OpenFreeMapLandscapeSource(new VectorTileRepository(new DelegateFetcher((_, _, _) => (byte[]?)null)))
                .AcquireAsync(Frame, 2, CancellationToken.None));
    }

    [Fact]
    public async Task RequestsAreBoundedToTheFrameAndAtMostFourAtATime()
    {
        var active = 0;
        var peak = 0;
        var requested = new System.Collections.Concurrent.ConcurrentBag<(int, int, int)>();
        var fetcher = new DelegateFetcher(async (z, x, y) =>
        {
            requested.Add((z, x, y));
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref peak, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
            return Tile();
        });

        var landscape = await new OpenFreeMapLandscapeSource(new VectorTileRepository(fetcher)).AcquireAsync(Frame, 2, CancellationToken.None);

        Assert.InRange(requested.Count, 8, 15); // 896 × 504 at zoom 13: 4–5 × 2–3 tiles.
        Assert.All(requested, key => Assert.Equal(13, key.Item1));
        Assert.Equal(requested.Count, requested.Distinct().Count());
        Assert.InRange(peak, 1, MapFrameVectorTiles.MaximumParallelRequests);
        Assert.Equal(requested.Count, landscape.TileCount);
        // A frame needing more tiles than any saved-location frame does is refused before a single request.
        var wide = new WebMercatorViewport(12, 256, 1800, 1000, 53.61, -0.43);
        requested.Clear();
        var refused = await Assert.ThrowsAsync<MapFeatureRequestException>(() =>
            MapFrameVectorTiles.LoadAsync(new VectorTileRepository(fetcher), wide, CancellationToken.None));
        Assert.Equal("area_too_large", refused.Code);
        Assert.Empty(requested);
    }

    [Fact]
    public async Task TheLandscapeAndTheFeaturesShareOneReadOfEachTile()
    {
        var requests = 0;
        var repository = new VectorTileRepository(new DelegateFetcher((_, _, _) => { Interlocked.Increment(ref requests); return Tile(); }));

        var landscape = await new OpenFreeMapLandscapeSource(repository).AcquireAsync(Frame, 2, CancellationToken.None);
        var features = await new OpenFreeMapMapFeatureDataService(repository, NullLogger<OpenFreeMapMapFeatureDataService>.Instance)
            .FetchAsync(Guid.NewGuid(), Frame, CancellationToken.None);

        Assert.NotNull(features.Data);
        Assert.Equal(landscape.TileCount, requests);
        Assert.Equal(landscape.TileCount, repository.DecodeCount);
    }

    [Fact]
    public void TheLandscapeRepresentsWaterVegetationAndBuiltUpLandWhereThePolygonsAre()
    {
        // One tile filling the frame's centre: water in its west quarter, wood in the east, houses between.
        var tile = new VectorTileTestBuilder();
        tile.Layer("water").Rectangle(-64, -64, 1024, 4160, ("class", "lake"));
        tile.Layer("landcover").Rectangle(3072, -64, 4160, 4160, ("class", "wood"))
            .Rectangle(-64, -64, 4160, 4160, ("class", "farmland"));
        tile.Layer("landuse").Rectangle(1024, -64, 3072, 4160, ("class", "residential"));
        tile.Layer("building").Rectangle(3200, 1000, 3600, 1400);
        var centre = Frame.WorldPixelCentreX / 256;
        var row = Frame.WorldPixelCentreY / 256;
        var placed = new PlacedVectorTile(13, (int)Math.Floor(centre), (int)Math.Floor(row), Decode(tile.Build()));

        var landscape = OpenFreeMapLandscapeSource.Rasterise(Frame, 2, [placed]);

        byte At(byte[] mask, double tileX, double tileY)
        {
            var worldX = (placed.RawColumn + tileX / 4096) * 256 - Frame.WorldPixelLeft;
            var worldY = (placed.Row + tileY / 4096) * 256 - Frame.WorldPixelTop;
            return mask[(int)(worldY / 2) * landscape.Width + (int)(worldX / 2)];
        }
        Assert.Equal(255, At(landscape.Water, 500, 2048));
        Assert.Equal(0, At(landscape.Vegetation, 500, 2048));
        Assert.Equal(255, At(landscape.BuiltUp, 2048, 2048));
        Assert.Equal(255, At(landscape.Vegetation, 3800, 2048));
        // A building in the wood is built-up, not vegetation; farmland is neither.
        Assert.Equal(255, At(landscape.BuiltUp, 3400, 1200));
        Assert.Equal(0, At(landscape.Vegetation, 3400, 1200));
        Assert.False(landscape.HasNoData);

        var restored = SavedLocationLandscapeCodec.TryDecode(SavedLocationLandscapeCodec.Encode(landscape));
        Assert.NotNull(restored);
        Assert.Equal(landscape.Water, restored.Water);
        Assert.Equal(landscape.Bounds, restored.Bounds);
        Assert.Null(SavedLocationLandscapeCodec.TryDecode([1, 2, 3]));
    }

    [Fact]
    public async Task PreviouslyDownloadedTilesServeTheFrameOffline()
    {
        const string tileJson = "{\"tilejson\":\"3.0.0\",\"tiles\":[\"https://tiles.example/planet/20260913_164504_pt/{z}/{x}/{y}.pbf\"]}";
        var online = 0;
        HttpResponseMessage Online(HttpRequestMessage request)
        {
            if (request.RequestUri!.AbsolutePath == "/planet") return new(HttpStatusCode.OK) { Content = new StringContent(tileJson) };
            Interlocked.Increment(ref online);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Tile()) };
        }
        var first = new OpenFreeMapTileFetcher(new HttpClient(new Handler(Online)), _directory, new Uri("https://tiles.example/planet"));
        var acquired = await new OpenFreeMapLandscapeSource(new VectorTileRepository(first)).AcquireAsync(Frame, 2, CancellationToken.None);

        var offline = new OpenFreeMapTileFetcher(new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))),
            _directory, new Uri("https://tiles.example/planet"));
        var again = await new OpenFreeMapLandscapeSource(new VectorTileRepository(offline)).AcquireAsync(Frame, 2, CancellationToken.None);

        Assert.Equal(acquired.TileCount, online);
        Assert.Equal(acquired.Water, again.Water);
        Assert.Equal(acquired.BuiltUp, again.BuiltUp);
    }

    [Fact]
    public void NoRuntimeCodeAddressesPublicNominatimOverpassOrOpenStreetMapRasterEndpoints()
    {
        var root = Path.GetDirectoryName(TestPaths.DesktopProject)!;
        var offenders = new List<string>();
        foreach (var project in new[] { "Noctaxis.Core", "Noctaxis.Desktop" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            var text = File.ReadAllText(file);
            foreach (var host in new[] { "nominatim.openstreetmap.org", "overpass-api.de", "tile.openstreetmap.org", "/api/interpreter" })
                if (text.Contains(host, StringComparison.OrdinalIgnoreCase)) offenders.Add($"{Path.GetFileName(file)}: {host}");
        }
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    /// <summary>A tile with some of everything the saved-location data reads.</summary>
    private static byte[] Tile()
    {
        var tile = new VectorTileTestBuilder();
        tile.Layer("water").Rectangle(-64, -64, 1200, 1200, ("class", "lake"));
        tile.Layer("landcover").Rectangle(2600, 2600, 4160, 4160, ("class", "wood"));
        tile.Layer("landuse").Rectangle(1500, 200, 2700, 1500, ("class", "residential"));
        tile.Layer("transportation").Line([(-64, 3600), (4160, 700)], ("class", "primary"));
        tile.Layer("waterway").Line([(1200, -64), (1600, 4160)], ("class", "river"), ("name", "River Test"));
        return tile.Build();
    }

    private static VectorTile Decode(byte[] bytes) => VectorTile.Decode(bytes);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private sealed class DelegateFetcher : IVectorTileFetcher
    {
        private readonly Func<int, int, int, Task<byte[]?>> _fetch;
        public DelegateFetcher(Func<int, int, int, byte[]?> fetch) => _fetch = (z, x, y) => Task.FromResult(fetch(z, x, y));
        public DelegateFetcher(Func<int, int, int, Task<byte[]?>> fetch) => _fetch = fetch;
        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken) => _fetch(zoom, column, row);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
