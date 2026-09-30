using System.Globalization;
using System.Net;
using Avalonia.Headless.XUnit;
using BruTile;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Mapping.Vector;
using Noctaxis.Desktop.Themes;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The map's place names follow the application language. The vector tiles carry every language;
/// the choice is made when a tile is drawn, and a name is never lost for want of a translation.
/// </summary>
public sealed class BasemapLabelLanguageTests
{
    private static readonly BasemapStyle Style =
        BasemapStyle.FromPalette(new ThemeCatalogue(isAvailable: _ => true).Resolve(new(ThemeCatalogue.DefaultId, AppearanceMode: AppearanceMode.Dark)).Palette);

    private static VectorTileFeature Feature(params (string Key, object Value)[] properties) =>
        new(VectorTileGeometryType.Point, [[new VectorTilePoint(0, 0)]], properties.ToDictionary(p => p.Key, p => p.Value));

    // ---- The name chosen -------------------------------------------------------------------------

    [Fact]
    public void BritishEnglishTakesTheEnglishNameWhereTheDataHasOne()
    {
        var cardiff = Feature(("name", "Caerdydd"), ("name:en", "Cardiff"), ("name:cy", "Caerdydd"), ("name:latin", "Caerdydd"));
        Assert.Equal("Cardiff", BasemapLabelNames.Resolve(cardiff, "en-GB"));
        // The most specific name wins where the data distinguishes regions; the older key is understood too.
        Assert.Equal("Colour Bay", BasemapLabelNames.Resolve(Feature(("name", "X"), ("name:en", "Color Bay"), ("name:en-GB", "Colour Bay")), "en-GB"));
        Assert.Equal("Munich", BasemapLabelNames.Resolve(Feature(("name", "München"), ("name_en", "Munich")), "en-GB"));
        Assert.Equal(["name:en-GB", "name:en", "name_en"], BasemapLabelNames.LanguageKeys("en-GB"));
        // The same rule serves any later language, including script variants.
        Assert.Equal("Caerdydd", BasemapLabelNames.Resolve(cardiff, "cy"));
        Assert.Equal(["name:zh-Hans-CN", "name:zh-Hans", "name:zh", "name_zh"], BasemapLabelNames.LanguageKeys("zh-Hans-CN"));
    }

    [Fact]
    public void WithoutAnEnglishNameTheLocalNameIsUsed()
    {
        Assert.Equal("Llanfairpwll", BasemapLabelNames.Resolve(Feature(("name", "Llanfairpwll"), ("name:cy", "Llanfairpwllgwyngyll")), "en-GB"));
        Assert.Equal("Кострома", BasemapLabelNames.Resolve(Feature(("name", "Кострома"), ("name:latin", "Kostroma"), ("name:nonlatin", "Кострома")), "en-GB"));
        // A blank translation is no translation: it never blanks the label.
        Assert.Equal("Keld", BasemapLabelNames.Resolve(Feature(("name", "Keld"), ("name:en", "  ")), "en-GB"));
        Assert.Equal("Keld", BasemapLabelNames.Resolve(Feature(("name", "Keld"), ("name:en", "")), "en-GB"));
    }

    [Fact]
    public void WithNeitherAnyOtherUsableNameIsUsed()
    {
        Assert.Equal("Kostroma", BasemapLabelNames.Resolve(Feature(("name:latin", "Kostroma"), ("name:nonlatin", "Кострома")), "en-GB"));
        Assert.Equal("Mount Example", BasemapLabelNames.Resolve(Feature(("name", " "), ("name_int", "Mount Example"), ("name:de", "Beispielberg")), "en-GB"));
        Assert.Equal("Кострома", BasemapLabelNames.Resolve(Feature(("name:nonlatin", "Кострома")), "en-GB"));
        // Only names in other languages: the first in key order, the same every time.
        Assert.Equal("Beispielberg", BasemapLabelNames.Resolve(Feature(("name:fr", "Mont Exemple"), ("name:de", "Beispielberg"), ("class", "peak")), "en-GB"));
        Assert.Equal("Beispielberg", BasemapLabelNames.Resolve(Feature(("name:de", "Beispielberg")), null));
        // No name of any kind: nothing to draw. Non-text properties are never mistaken for names.
        Assert.Null(BasemapLabelNames.Resolve(Feature(("class", "city"), ("rank", 1L), ("name:en", 5L)), "en-GB"));
    }

    // ---- Drawing ---------------------------------------------------------------------------------

    private static byte[] TownBytes(params (string Key, object Value)[] names)
    {
        var builder = new VectorTileTestBuilder();
        builder.Layer("water").Rectangle(-64, -64, 1024, 4160, ("class", "lake"));
        builder.Layer("transportation").Line([(-64, 1024), (4160, 1024)], ("class", "motorway"));
        builder.Layer("place").Point(2048, 2048, [("class", "city"), ("rank", 1L), .. names]);
        return builder.Build();
    }

    private static int LabelPixels(SKBitmap bitmap) => bitmap.Pixels.Count(pixel => pixel == Style.Label);

    [Fact]
    public void ALabelIsDrawnWhicheverNameTheFeatureHas()
    {
        var renderer = new BasemapTileRenderer(1);
        var request = new BasemapTileRequest(10, 0, 0);
        foreach (var names in new (string, object)[][]
                 {
                     [("name", "Caerdydd"), ("name:en", "Cardiff")], [("name", "Caerdydd")], [("name:latin", "Caerdydd")],
                     [("name:de", "Kardiff")], [("name", "Caerdydd"), ("name:en", "")]
                 })
        {
            using var bitmap = renderer.RenderBitmap(VectorTile.Decode(TownBytes(names)), request, Style, BasemapPass.Reference, "en-GB");
            Assert.True(LabelPixels(bitmap) > 20, string.Join(", ", names.Select(n => n.Item1)));
        }
        using var unnamed = renderer.RenderBitmap(VectorTile.Decode(TownBytes()), request, Style, BasemapPass.Reference, "en-GB");
        Assert.Equal(0, LabelPixels(unnamed));
    }

    [Fact]
    public void TheLanguageChangesTheNamesAndNothingElse()
    {
        var tile = VectorTile.Decode(TownBytes(("name", "Caerdydd"), ("name:en", "Cardiff"), ("name:cy", "Caerdydd")));
        var renderer = new BasemapTileRenderer(1);
        var request = new BasemapTileRequest(10, 0, 0);
        byte[] Png(BasemapPass pass, string? language) => renderer.RenderPng(tile, request, Style, pass, language);

        Assert.NotEqual(Png(BasemapPass.Reference, "en-GB"), Png(BasemapPass.Reference, "cy"));
        Assert.NotEqual(Png(BasemapPass.Full, "en-GB"), Png(BasemapPass.Full, "cy"));
        // Land, water and buildings hold no names: the same in every language.
        Assert.Equal(Png(BasemapPass.Geography, "en-GB"), Png(BasemapPass.Geography, "cy"));
        // Not naming a language is British English, the application default.
        Assert.Equal(Png(BasemapPass.Reference, "en-GB"), Png(BasemapPass.Reference, null));
    }

    // ---- Changing the language -------------------------------------------------------------------

    [Fact]
    public async Task ALanguageChangeRedrawsNamesFromTheDataAlreadyHeld()
    {
        var fetcher = new CountingFetcher(TownBytes(("name", "Caerdydd"), ("name:en", "Cardiff"), ("name:cy", "Caerdydd")));
        var source = new ThemedVectorTileSource(fetcher, Style, new BasemapTileRenderer(1));
        var info = Tile(10, 0, 0);
        Assert.Equal("en-GB", source.LabelLanguage);

        var english = await source.RenderAsync(info, BasemapPass.Reference);
        var geography = await source.RenderAsync(info, BasemapPass.Geography);
        var (style, labels) = (source.StyleVersion, source.LabelVersion);

        Assert.False(source.SetLabelLanguage("en-GB"));
        Assert.True(source.SetLabelLanguage("cy"));
        var welsh = await source.RenderAsync(info, BasemapPass.Reference);
        var geographyAfter = await source.RenderAsync(info, BasemapPass.Geography);

        Assert.NotEqual(english.Bytes!, welsh.Bytes!);
        Assert.Equal(labels + 1, welsh.LabelVersion);
        Assert.Equal(geography.Bytes!, geographyAfter.Bytes!);
        // The theme is untouched, and the vector tile was downloaded and decoded once for all of it.
        Assert.Equal(style, source.StyleVersion);
        Assert.Equal(1, fetcher.Calls);
        Assert.Equal(1, source.DecodeCount);
    }

    [AvaloniaFact]
    public void OnlyTheNameBearingBasemapTilesAreInvalidated()
    {
        var fetcher = new CountingFetcher(TownBytes(("name", "Caerdydd"), ("name:en", "Cardiff")));
        var basemap = new OpenFreeMapBasemap(fetcher);
        var environmental = new MemoryLayer("environmental-test");
        var composition = new PlannerMapComposition(basemap,
            [new MapLayerDefinition("environmental-test", MapLayerGroup.Environmental, 0, new MapProviderMetadata("Test"))]);
        composition.InstallNativeOverlay("environmental-test", [environmental]);
        var layers = composition.Map.Layers.ToArray();
        var geography = layers.OfType<ThemedBasemapTileLayer>().Single(layer => layer.Pass == BasemapPass.Geography);
        var reference = layers.OfType<ThemedBasemapTileLayer>().Single(layer => layer.Pass == BasemapPass.Reference);
        var environmentalChanges = 0;
        environmental.DataChanged += (_, _) => environmentalChanges++;
        var styleVersion = basemap.Source.StyleVersion;

        // The language the map already uses: nothing happens.
        Assert.False(composition.ApplyLabelLanguage("en-GB"));
        Assert.Equal((0, 0), (geography.Invalidations, reference.Invalidations));

        Assert.True(composition.ApplyLabelLanguage("cy"));

        Assert.Equal("cy", basemap.LabelLanguage);
        Assert.Equal(1, reference.Invalidations);
        // Geography and the environmental layers keep what they have rendered; nothing is rebuilt or refetched.
        Assert.Equal(0, geography.Invalidations);
        Assert.Equal(0, environmentalChanges);
        Assert.Equal(layers, composition.Map.Layers.ToArray());
        Assert.All(layers.OfType<TileLayer>(), layer => Assert.Same(basemap.Source, layer.TileSource));
        Assert.Equal(styleVersion, basemap.Source.StyleVersion);
        Assert.Equal(0, fetcher.Calls);

        // A basemap installed later takes the language already chosen.
        var replacement = new OpenFreeMapBasemap(fetcher);
        composition.ReplaceBasemap(replacement);
        Assert.Equal("cy", replacement.LabelLanguage);
        composition.Map.Dispose();
    }

    [AvaloniaFact]
    public void ThePlannerMapTakesItsLabelLanguageFromTheApplicationLanguage()
    {
        var basemap = new OpenFreeMapBasemap(new CountingFetcher(TownBytes(("name", "Keld"))));
        var composition = new PlannerMapComposition(basemap);
        _ = new Controls.NoctaxisMapView(composition);
        Assert.Equal(AppLanguages.Current, basemap.LabelLanguage);
        Assert.Equal("en-GB", basemap.LabelLanguage);
        composition.Map.Dispose();
    }

    // ---- Machine values --------------------------------------------------------------------------

    [Fact]
    public async Task TileRequestsCarryNoLanguageAndAreTheSameInEveryCulture()
    {
        const string tileJson = """{"tilejson":"3.0.0","tiles":["https://tiles.example/planet/20260913_164504_pt/{z}/{x}/{y}.pbf"],"maxzoom":14}""";
        foreach (var culture in new[] { "en-GB", "de-DE", "ar-SA", "fa-IR" })
        {
            var http = new RecordingHttp(request => request.RequestUri!.AbsolutePath == "/planet"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tileJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TownBytes(("name", "Keld"))) });
            var fetcher = new OpenFreeMapTileFetcher(new HttpClient(http), cacheDirectory: null, new Uri("https://tiles.example/planet"));
            var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var source = new ThemedVectorTileSource(fetcher, Style, new BasemapTileRenderer(1));
                source.SetLabelLanguage("cy");
                Assert.NotNull(await source.GetTileAsync(Tile(14, 8171, 5446)));
            }
            finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous; }
            // The language is not part of the request: one downloaded tile serves every language.
            Assert.Equal(new[] { "https://tiles.example/planet", "https://tiles.example/planet/20260913_164504_pt/14/8171/5446.pbf" },
                http.Requested.ToArray());
        }
    }

    private static TileInfo Tile(int level, int column, int row) =>
        new() { Index = new TileIndex(column, row, level), Extent = new Extent(0, 0, 1, 1) };

    private sealed class CountingFetcher(byte[] bytes) : IVectorTileFetcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<byte[]?>(bytes);
        }
    }

    private sealed class RecordingHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested) Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }
}
