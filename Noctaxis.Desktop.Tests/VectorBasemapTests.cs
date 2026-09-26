using System.IO.Compression;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BruTile;
using BruTile.Predefined;
using Mapsui.Tiling.Layers;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Mapping.Vector;
using Noctaxis.Desktop.Themes;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class VectorBasemapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-basemap-" + Guid.NewGuid().ToString("N"));
    private static BasemapStyle Style(string id, AppearanceMode mode) =>
        BasemapStyle.FromPalette(new ThemeCatalogue(isAvailable: _ => true).Resolve(new(id, AppearanceMode: mode)).Palette);
    private static readonly BasemapStyle NoctaxisDark = Style(ThemeCatalogue.DefaultId, AppearanceMode.Dark);
    private static readonly BasemapStyle FoxfireLight = Style("supporter.foxfire", AppearanceMode.Light);

    // ---- Decoding -------------------------------------------------------------------------------

    [Fact]
    public void DecoderReadsLayersGeometryAndEveryValueType()
    {
        var builder = new VectorTileTestBuilder();
        builder.Layer("place", 512).Point(10, 20, ("name", "Ridge"), ("rank", 3L), ("population", 12UL),
            ("offset", new SignedValue(-7)), ("capital", true), ("ratio", .5f), ("elevation", 812.25));
        builder.Layer("water").Polygon([[(0, 0), (100, 0), (100, 100), (0, 100)], [(20, 20), (20, 40), (40, 40)]],
            ("class", "lake"));
        builder.Layer("transportation").Line([(0, 5), (50, 5), (50, 60)], ("class", "motorway"));

        var tile = VectorTile.Decode(builder.Build());

        Assert.Equal(["place", "water", "transportation"], tile.Layers.Select(layer => layer.Name));
        var place = Assert.Single(tile["place"]!.Features);
        Assert.Equal(512, tile["place"]!.Extent);
        Assert.Equal(512, place.Extent);
        Assert.Equal(VectorTileGeometryType.Point, place.Type);
        Assert.Equal(new VectorTilePoint(10, 20), Assert.Single(Assert.Single(place.Parts)));
        Assert.Equal("Ridge", place.Text("name"));
        Assert.Equal(3, place.Number("rank"));
        Assert.Equal(12, place.Number("population"));
        Assert.Equal(-7, place.Number("offset"));
        Assert.Equal(1, place.Number("capital"));
        Assert.Equal(.5, place.Number("ratio"));
        Assert.Equal(812.25, place.Number("elevation"));
        var water = Assert.Single(tile["water"]!.Features);
        Assert.Equal(VectorTileGeometryType.Polygon, water.Type);
        Assert.Equal(2, water.Parts.Count);
        Assert.Equal([new(20, 20), new(20, 40), new(40, 40)], water.Parts[1]);
        var road = Assert.Single(tile["transportation"]!.Features);
        Assert.Equal([new(0, 5), new(50, 5), new(50, 60)], Assert.Single(road.Parts));
        Assert.Null(tile["missing"]);
    }

    [Fact]
    public void DecoderSkipsUnknownFieldsAndRejectsTruncatedData()
    {
        var layer = new VectorTileTestBuilder();
        layer.Layer("water").Rectangle(0, 0, 10, 10);
        var extra = new Protobuf();
        extra.Varint(9, 42);
        extra.String(10, "unknown");
        var bytes = extra.ToArray().Concat(layer.Build()).ToArray();

        Assert.Single(VectorTile.Decode(bytes).Layers);
        Assert.Throws<InvalidDataException>(() => VectorTile.Decode(bytes.AsSpan(0, bytes.Length - 3)));
        Assert.Empty(VectorTile.Decode([]).Layers);
    }

    // ---- Rendering ------------------------------------------------------------------------------

    private static VectorTile WaterAndRoadTile()
    {
        var builder = new VectorTileTestBuilder();
        builder.Layer("water").Rectangle(-64, -64, 2048, 4160, ("class", "lake"));
        builder.Layer("transportation")
            .Line([(-64, 1024), (4160, 1024)], ("class", "motorway"))
            .Line([(-64, 3072), (4160, 3072)], ("class", "minor"))
            .Line([(-64, 2048), (4160, 2048)], ("class", "primary"));
        return VectorTile.Decode(builder.Build());
    }

    [Fact]
    public void RendererDrawsEachFeatureInItsSemanticRole()
    {
        var renderer = new BasemapTileRenderer(1);
        using var bitmap = renderer.RenderBitmap(WaterAndRoadTile(), new BasemapTileRequest(14, 0, 0), NoctaxisDark);

        Assert.Equal(256, bitmap.Width);
        Assert.Equal(NoctaxisDark.Water, bitmap.GetPixel(40, 100));
        Assert.Equal(NoctaxisDark.Land, bitmap.GetPixel(200, 100));
        Assert.Equal(NoctaxisDark.RoadMajor, bitmap.GetPixel(200, 64));
        Assert.Equal(NoctaxisDark.RoadMinor, bitmap.GetPixel(200, 192));
        // Primary roads sit between the minor and major colours, leaning to minor.
        var primary = bitmap.GetPixel(200, 128);
        Assert.NotEqual(NoctaxisDark.RoadMajor, primary);
        Assert.NotEqual(NoctaxisDark.RoadMinor, primary);
        Assert.InRange(primary.Red, Math.Min(NoctaxisDark.RoadMinor.Red, NoctaxisDark.RoadMajor.Red),
            Math.Max(NoctaxisDark.RoadMinor.Red, NoctaxisDark.RoadMajor.Red));
        // Major roads carry a casing in the casing role.
        Assert.Contains(Enumerable.Range(66, 3).Select(y => bitmap.GetPixel(200, y)), pixel => Near(pixel, NoctaxisDark.RoadCasing, 40));
    }

    [Fact]
    public void TheSameVectorDataTakesAnotherThemeWithoutChange()
    {
        var tile = WaterAndRoadTile();
        var renderer = new BasemapTileRenderer(1);
        using var dark = renderer.RenderBitmap(tile, new BasemapTileRequest(14, 0, 0), NoctaxisDark);
        using var light = renderer.RenderBitmap(tile, new BasemapTileRequest(14, 0, 0), FoxfireLight);

        Assert.Equal(FoxfireLight.Water, light.GetPixel(40, 100));
        Assert.Equal(FoxfireLight.Land, light.GetPixel(200, 100));
        Assert.Equal(FoxfireLight.RoadMajor, light.GetPixel(200, 64));
        Assert.NotEqual(dark.GetPixel(200, 100), light.GetPixel(200, 100));
    }

    [Fact]
    public void MinorRoadsAppearOnlyAtLocalZoomsWhileMajorRoadsPersist()
    {
        var renderer = new BasemapTileRenderer(1);
        using var regional = renderer.RenderBitmap(WaterAndRoadTile(), new BasemapTileRequest(9, 0, 0), NoctaxisDark);
        Assert.Equal(NoctaxisDark.Land, regional.GetPixel(200, 192));
        Assert.True(Near(regional.GetPixel(200, 64), NoctaxisDark.RoadMajor, 60));
    }

    [Fact]
    public void OverzoomDrawsTheMatchingQuarterOfTheDeepestVectorTile()
    {
        var builder = new VectorTileTestBuilder();
        builder.Layer("water").Rectangle(2048, -64, 4160, 4160);
        var tile = VectorTile.Decode(builder.Build());
        var renderer = new BasemapTileRenderer(1);
        var east = new BasemapTileRequest(15, 1, 0);
        var west = new BasemapTileRequest(15, 0, 0);
        Assert.Equal((14, 0, 0, 2), (east.SourceZoom, east.SourceColumn, east.SourceRow, east.SubScale));

        using var eastBitmap = renderer.RenderBitmap(tile, east, NoctaxisDark);
        using var westBitmap = renderer.RenderBitmap(tile, west, NoctaxisDark);
        Assert.Equal(NoctaxisDark.Water, eastBitmap.GetPixel(10, 10));
        Assert.Equal(NoctaxisDark.Water, eastBitmap.GetPixel(245, 245));
        Assert.Equal(NoctaxisDark.Land, westBitmap.GetPixel(10, 10));
    }

    [Fact]
    public void LabelsUseTheLabelRolesAndAreNeverCutAtTheTileEdge()
    {
        var builder = new VectorTileTestBuilder();
        builder.Layer("place").Point(2048, 2048, ("class", "city"), ("name", "Centreville"), ("rank", 1L))
            .Point(30, 2048, ("class", "city"), ("name", "Edgeton"), ("rank", 2L));
        var renderer = new BasemapTileRenderer(1);
        using var bitmap = renderer.RenderBitmap(VectorTile.Decode(builder.Build()), new BasemapTileRequest(10, 0, 0), NoctaxisDark);

        var pixels = Enumerable.Range(0, 256).SelectMany(x => Enumerable.Range(96, 64).Select(y => (x, y, bitmap.GetPixel(x, y)))).ToArray();
        Assert.Contains(pixels, p => p.Item3 == NoctaxisDark.Label);
        Assert.Contains(pixels, p => p.Item3 == NoctaxisDark.LabelHalo);
        // The edge label would cross x = 0, so it is not drawn at all.
        Assert.All(pixels.Where(p => p.x < 8), p => Assert.Equal(NoctaxisDark.Land, p.Item3));
    }

    // ---- Source: caching and restyling -----------------------------------------------------------

    [Fact]
    public async Task ThemeChangesRerenderFromDecodedDataWithoutFetchingAgain()
    {
        var fetcher = new FakeFetcher(WaterAndRoadBytes());
        var source = new ThemedVectorTileSource(fetcher, NoctaxisDark, new BasemapTileRenderer(1));
        var info = Tile(14, 0, 0);

        using var first = SKBitmap.Decode(await source.GetTileAsync(info));
        Assert.Equal(NoctaxisDark.Land, first.GetPixel(200, 100));
        var version = source.StyleVersion;

        Assert.True(source.SetStyle(FoxfireLight));
        Assert.False(source.SetStyle(FoxfireLight));
        using var second = SKBitmap.Decode(await source.GetTileAsync(info));

        Assert.Equal(FoxfireLight.Land, second.GetPixel(200, 100));
        Assert.Equal(version + 1, source.StyleVersion);
        Assert.Equal(1, fetcher.Calls);
        Assert.Equal(1, source.DecodeCount);
        Assert.Same(FoxfireLight, source.Style);
    }

    [Fact]
    public async Task DeeperZoomsShareOneVectorTileDownload()
    {
        var fetcher = new FakeFetcher(WaterAndRoadBytes());
        var source = new ThemedVectorTileSource(fetcher, NoctaxisDark, new BasemapTileRenderer(1));
        for (var column = 0; column < 4; column++)
            Assert.NotNull(await source.GetTileAsync(Tile(16, column, 0)));
        Assert.Equal(1, fetcher.Calls);
        Assert.Equal((14, 0, 0), Assert.Single(fetcher.Requested));
    }

    [Fact]
    public async Task UnavailableTilesAreRetriedAndAbsentTilesDrawBareLand()
    {
        var fetcher = new FakeFetcher(null);
        var source = new ThemedVectorTileSource(fetcher, NoctaxisDark, new BasemapTileRenderer(1));
        Assert.Null(await source.GetTileAsync(Tile(12, 1, 1)));
        Assert.Null(await source.GetTileAsync(Tile(12, 1, 1)));
        Assert.Equal(2, fetcher.Calls);

        fetcher.Bytes = [];
        using var empty = SKBitmap.Decode(await source.GetTileAsync(Tile(12, 1, 1)));
        Assert.Equal(NoctaxisDark.Land, empty.GetPixel(128, 128));

        fetcher.Bytes = [0x0A, 0xFF, 0xFF];
        Assert.Null(await source.GetTileAsync(Tile(12, 2, 2)));
        Assert.Equal((12, 2, 2), Assert.Single(fetcher.Discarded));
    }

    // ---- OpenFreeMap fetcher ---------------------------------------------------------------------

    private const string TileJson = """{"tilejson":"3.0.0","tiles":["https://tiles.example/planet/20260913_164504_pt/{z}/{x}/{y}.pbf"],"maxzoom":14}""";

    [Fact]
    public async Task FetcherResolvesTheVersionedTemplateAndCachesTilesForOfflineUse()
    {
        var payload = WaterAndRoadBytes();
        var online = new FakeHttp(request => request.RequestUri!.AbsolutePath == "/planet"
            ? Text(TileJson) : Binary(Gzip(payload)));
        var fetcher = new OpenFreeMapTileFetcher(new HttpClient(online), _directory, new Uri("https://tiles.example/planet"));

        Assert.Equal(payload, await fetcher.FetchAsync(14, 8171, 5446, default));
        Assert.Contains("https://tiles.example/planet/20260913_164504_pt/14/8171/5446.pbf", online.Requested);
        Assert.True(File.Exists(Path.Combine(_directory, "20260913_164504_pt", "14", "8171", "5446.pbf")));

        // A later session with no network uses the stored TileJSON and tile.
        var offline = new FakeHttp(_ => throw new HttpRequestException("offline"));
        var restarted = new OpenFreeMapTileFetcher(new HttpClient(offline), _directory, new Uri("https://tiles.example/planet"),
            () => DateTimeOffset.UtcNow.AddDays(3));
        Assert.Equal(payload, await restarted.FetchAsync(14, 8171, 5446, default));
        Assert.Null(await restarted.FetchAsync(14, 1, 1, default));
    }

    [Fact]
    public async Task FetcherMapsAbsentTilesToEmptyAndFailuresToRetry()
    {
        var status = HttpStatusCode.NotFound;
        var http = new FakeHttp(request => request.RequestUri!.AbsolutePath == "/planet"
            ? Text(TileJson) : new HttpResponseMessage(status));
        var fetcher = new OpenFreeMapTileFetcher(new HttpClient(http), cacheDirectory: null, new Uri("https://tiles.example/planet"));

        var absent = await fetcher.FetchAsync(3, 1, 1, default);
        Assert.NotNull(absent);
        Assert.Empty(absent);
        status = HttpStatusCode.InternalServerError;
        Assert.Null(await fetcher.FetchAsync(3, 2, 2, default));
    }

    [Theory]
    [InlineData("""{"tiles":["http://insecure/{z}/{x}/{y}.pbf"]}""")]
    [InlineData("""{"tiles":["https://missing/{z}/{x}.pbf"]}""")]
    [InlineData("""{"vector_layers":[]}""")]
    public void TileJsonWithoutAUsableHttpsTemplateIsRejected(string json) =>
        Assert.Null(OpenFreeMapTileFetcher.ParseTemplate(json));

    [Fact]
    public void TilesetVersionIsTheSegmentBeforeTheZoom() =>
        Assert.Equal("20260913_164504_pt",
            OpenFreeMapTileFetcher.VersionOf("https://tiles.openfreemap.org/planet/20260913_164504_pt/{z}/{x}/{y}.pbf"));

    // ---- Composition, live theming and satellite -------------------------------------------------

    [AvaloniaFact]
    public void ProductionStreetBasemapIsTheThemedVectorBasemap()
    {
        var composition = new PlannerMapComposition();
        var basemap = Assert.IsType<OpenFreeMapBasemap>(composition.ActiveBasemap);
        Assert.Equal(BasemapKind.Street, composition.Composition.Basemap.Kind);
        Assert.Equal(OpenFreeMapBasemap.AttributionText, composition.Composition.AttributionText);
        var layer = Assert.IsAssignableFrom<TileLayer>(Assert.Single(composition.Map.Layers));
        Assert.Same(basemap.Source, layer.TileSource);
        composition.Map.Dispose();
    }

    [AvaloniaFact]
    public void ThemeSwitchesRestyleTheVectorBasemapLiveAndKeepItsStructure()
    {
        var service = ((App)Application.Current!).Themes!;
        var fetcher = new FakeFetcher(WaterAndRoadBytes());
        var basemap = new OpenFreeMapBasemap(fetcher);
        var composition = new PlannerMapComposition(basemap);
        var view = new NoctaxisMapView(composition) { Observer = new GeoCoordinate(53, -1) };
        var map = composition.Map;
        var layers = map.Layers.ToArray();
        var source = basemap.Source;
        var window = new Window { Width = 400, Height = 300, Content = view };
        try
        {
            service.SetSupporterEntitlement(true);
            window.Show();
            var versions = new List<long>();
            foreach (var (id, appearance) in new[]
                     {
                         ("supporter.noctaxis-neon", AppearanceMode.Dark), ("supporter.foxfire", AppearanceMode.Dark),
                         ("supporter.foxfire", AppearanceMode.Light), ("builtin.highcontrast", AppearanceMode.Light),
                         (ThemeCatalogue.DefaultId, AppearanceMode.Dark)
                     })
            {
                service.Apply(new(id, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                var expected = BasemapStyle.FromPalette(service.EffectiveTheme.Palette);
                Assert.Equal(expected, basemap.Style);
                Assert.Equal(expected, view.ResolveBasemapStyle());
                versions.Add(source.StyleVersion);
                // Same native layers in the same order, same source: only the drawing changed.
                Assert.Equal(layers, map.Layers.ToArray());
                Assert.Same(source, ((TileLayer)map.Layers.First()).TileSource);
                Assert.Same(basemap, composition.ActiveBasemap);
            }
            Assert.Equal(versions.Count, versions.Distinct().Count());
            // Every restyle re-rendered from decoded data: no vector tile was ever downloaded twice.
            lock (fetcher.Requested) Assert.Equal(fetcher.Requested.Distinct().Count(), fetcher.Requested.Count);
        }
        finally
        {
            window.Close();
            service.SetSupporterEntitlement(false);
            service.Apply(new());
        }
    }

    [AvaloniaFact]
    public async Task SatelliteImageryIsNeverRestyled()
    {
        var service = ((App)Application.Current!).Themes!;
        var imagery = new FakeImagerySource([1, 2, 3, 4, 5]);
        var satellite = new RasterImageryBasemap(new BasemapDefinition(BasemapKind.Satellite,
            new MapProviderMetadata("Test imagery", "Imagery credit")), () => imagery);
        var vector = new OpenFreeMapBasemap(new FakeFetcher(WaterAndRoadBytes()));
        var composition = new PlannerMapComposition(vector);
        var view = new NoctaxisMapView(composition);
        var window = new Window { Width = 400, Height = 300, Content = view };
        try
        {
            window.Show();
            composition.ReplaceBasemap(satellite);
            Assert.IsNotAssignableFrom<IThemeableBasemap>(satellite);
            var layer = Assert.IsAssignableFrom<TileLayer>(Assert.Single(composition.Map.Layers));
            Assert.Same(imagery, layer.TileSource);

            service.Apply(new(ThemeCatalogue.DefaultId, AppearanceMode: AppearanceMode.Light));
            Dispatcher.UIThread.RunJobs();

            Assert.False(composition.ApplyBasemapStyle(Style("builtin.highcontrast", AppearanceMode.Dark)));
            Assert.Same(layer, composition.Map.Layers.Single());
            Assert.Same(imagery, layer.TileSource);
            var served = await imagery.GetTileAsync(Tile(3, 1, 1));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, served!);
            Assert.Equal(0, imagery.Modifications);

            // Returning to the vector basemap adopts the theme that was applied while imagery showed.
            composition.ReplaceBasemap(vector);
            Assert.Equal(Style("builtin.highcontrast", AppearanceMode.Dark), vector.Style);
        }
        finally
        {
            window.Close();
            service.Apply(new());
        }
    }

    [Fact]
    public void ImageryBasemapsMustBeSatelliteKind() =>
        Assert.Throws<ArgumentException>(() => new RasterImageryBasemap(
            new BasemapDefinition(BasemapKind.Street, new MapProviderMetadata("Street")), () => new FakeImagerySource([])));

    // ---- Helpers ---------------------------------------------------------------------------------

    private static byte[] WaterAndRoadBytes()
    {
        var builder = new VectorTileTestBuilder();
        builder.Layer("water").Rectangle(-64, -64, 2048, 4160, ("class", "lake"));
        builder.Layer("transportation").Line([(-64, 1024), (4160, 1024)], ("class", "motorway"));
        return builder.Build();
    }

    // The source reads only the XYZ index; the extent matters to Mapsui, not here.
    private static TileInfo Tile(int level, int column, int row) =>
        new() { Index = new TileIndex(column, row, level), Extent = new Extent(0, 0, 1, 1) };

    private static bool Near(SKColor actual, SKColor expected, int tolerance) =>
        Math.Abs(actual.Red - expected.Red) + Math.Abs(actual.Green - expected.Green) + Math.Abs(actual.Blue - expected.Blue) <= tolerance;

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Binary(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested) Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeFetcher(byte[]? bytes) : IVectorTileFetcher
    {
        private int _calls;
        public byte[]? Bytes { get; set; } = bytes;
        public int Calls => Volatile.Read(ref _calls);
        public List<(int, int, int)> Requested { get; } = [];
        public List<(int, int, int)> Discarded { get; } = [];

        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            lock (Requested) Requested.Add((zoom, column, row));
            return Task.FromResult(Bytes);
        }

        public void Discard(int zoom, int column, int row) { lock (Discarded) Discarded.Add((zoom, column, row)); }
    }

    private sealed class FakeImagerySource(byte[] bytes) : ILocalTileSource
    {
        public int Modifications { get; private set; }
        public ITileSchema Schema { get; } = new GlobalSphericalMercator(0, 5);
        public string Name => "Test imagery";
        public Attribution Attribution { get; } = new("Imagery credit", "");
        public Task<byte[]?> GetTileAsync(TileInfo tileInfo) => Task.FromResult<byte[]?>(bytes.ToArray());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
