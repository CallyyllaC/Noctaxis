using System.Diagnostics;
using System.Globalization;
using BruTile;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.LightPollution;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Mapping.Vector;
using Noctaxis.Desktop.Themes;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Opt-in visual captures of the Planner's layer composition over real terrain, for tuning and
/// review: set NOCTAXIS_TERRAIN_RELIEF_CAPTURES to an output directory. Uses the network (Terrarium,
/// OpenFreeMap) through temporary caches, and reads an installed light-pollution atlas without writing.
/// Layers are composited in the Planner's semantic order: geography, relief, light pollution, reference.
/// </summary>
public sealed class TerrainReliefCaptures
{
    private const double WorldHalf = 20037508.342789244;
    private const int TilePixels = 512; // the vector basemap's 2x tiles; the map's high-DPI scale
    private const int Width = 1400, Height = 900;

    private sealed record Place(string Name, double Latitude, double Longitude);

    [Fact]
    public async Task Captures()
    {
        var output = Environment.GetEnvironmentVariable("NOCTAXIS_TERRAIN_RELIEF_CAPTURES");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var temp = Path.Combine(Path.GetTempPath(), "noctaxis-relief-captures");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/1.0 (terrain relief visual check)");
        var terrainCache = new EnvironmentalTileCache(new Paths(temp), NullLogger<EnvironmentalTileCache>.Instance);
        var terrarium = new TerrariumTerrainProvider(http, terrainCache, NullLogger<TerrariumTerrainProvider>.Instance);
        var relief = new TerrainReliefTileSource(terrarium);
        var vector = new ThemedVectorTileSource(new OpenFreeMapTileFetcher(http, Path.Combine(temp, "openfreemap")));
        var atlas = new LorenzInstallation(new Noctaxis.Core.Persistence.PlatformUserDataPathProvider(),
            new HttpClient(new NoNetwork()));
        await atlas.DetectAsync();
        await using var lightPollutionData = new LightPollutionDataProvider(atlas);
        using var lightPollution = new LightPollutionRasterSource(lightPollutionData, atlas, LightPollutionColorMaps.Grayscale);
        var log = new List<string> { $"light pollution atlas available: {atlas.IsAvailable}" };

        Place[] places =
        [
            new("mountain-lake-district", 54.454, -3.211), new("rolling-lincolnshire-wolds", 53.40, -0.20),
            new("flat-fens", 52.45, 0.12), new("urban-manchester", 53.48, -2.24), new("dark-kielder", 55.23, -2.58)
        ];
        (string Name, bool Terrain, bool Pollution)[] combinations =
            [("neither", false, false), ("terrain", true, false), ("lp", false, true), ("both", true, true)];
        var dark = Theme(ThemeCatalogue.DefaultId, AppearanceMode.Dark);
        foreach (var place in places)
            foreach (var zoom in new[] { 12, 9, 6 })
                foreach (var combination in combinations)
                    await Capture($"{place.Name}-z{zoom}-{combination.Name}", place, zoom, dark, combination.Terrain, combination.Pollution);
        // Local scouting scale, down to and beyond Terrarium's deepest zoom (15).
        var llangollen = new Place("local-llangollen", 52.965, -3.19);
        foreach (var zoom in new[] { 13, 14, 15, 16 })
            await Capture($"{llangollen.Name}-z{zoom}-terrain", llangollen, zoom, dark, true, false);
        // A colour palette at about the default opacity, on a supporter theme: the combination in real use.
        lightPollution.SetColorMap(LightPollutionColorMaps.Resolve("turbo"));
        var neon = Theme("supporter.noctaxis-neon", AppearanceMode.Dark);
        foreach (var place in places)
            foreach (var zoom in new[] { 12, 9, 6 })
            {
                await Capture($"{place.Name}-z{zoom}-turbo-lp", place, zoom, neon, false, true);
                await Capture($"{place.Name}-z{zoom}-turbo-both", place, zoom, neon, true, true);
            }
        lightPollution.SetColorMap(LightPollutionColorMaps.Grayscale);
        // Every theme family over the mountain and the city, with both environmental layers.
        foreach (var (id, mode) in new[] { (ThemeCatalogue.DefaultId, AppearanceMode.Light), ("builtin.highcontrast", AppearanceMode.Dark),
                     ("builtin.highcontrast", AppearanceMode.Light), ("supporter.foxfire", AppearanceMode.Light), ("supporter.moonlit", AppearanceMode.Dark) })
            foreach (var place in new[] { places[0], places[3] })
                await Capture($"{place.Name}-z10-both-{id.Split('.')[1]}-{mode.ToString().ToLowerInvariant()}", place, 10, Theme(id, mode), true, true);
        var metrics = relief.Metrics;
        log.Add(FormattableString.Invariant(
            $"relief: {metrics.ReliefComputations} tiles shaded in {metrics.ShadeMilliseconds:F0} ms ({metrics.ShadeMilliseconds / Math.Max(1, metrics.ReliefComputations):F2} ms each), {metrics.TilesRendered} rendered in {metrics.RenderMilliseconds:F0} ms, {metrics.ReliefCacheHits} cache hits, {metrics.UnavailableTiles} unavailable"));
        await File.WriteAllLinesAsync(Path.Combine(output, "captures.log"), log);

        async Task Capture(string name, Place place, int zoom, (BasemapStyle Style, double Strength, SKColor Surface) theme,
            bool terrain, bool pollution)
        {
            var timer = Stopwatch.StartNew();
            vector.SetStyle(theme.Style);
            using var bitmap = new SKBitmap(Width, Height);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(theme.Surface);
            var worldPixels = TilePixels * Math.Pow(2, zoom);
            var centreX = (place.Longitude + 180) / 360 * worldPixels;
            var latitude = place.Latitude * Math.PI / 180;
            var centreY = (1 - Math.Asinh(Math.Tan(latitude)) / Math.PI) / 2 * worldPixels;
            var left = centreX - Width / 2d;
            var top = centreY - Height / 2d;
            var tiles = new List<(int X, int Y, SKRect Destination)>();
            for (var y = (int)Math.Floor(top / TilePixels); y * TilePixels < top + Height; y++)
                for (var x = (int)Math.Floor(left / TilePixels); x * TilePixels < left + Width; x++)
                    tiles.Add((x, y, SKRect.Create((float)(x * TilePixels - left), (float)(y * TilePixels - top), TilePixels, TilePixels)));

            async Task Layer(Func<TileInfo, Task<byte[]?>> render, int maximumLevel, double opacity)
            {
                var images = await Task.WhenAll(tiles.Select(async tile =>
                {
                    var level = Math.Min(zoom, maximumLevel);
                    var shift = zoom - level;
                    var bytes = await render(Info(level, tile.X >> shift, tile.Y >> shift));
                    return (tile, shift, bytes);
                }));
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * opacity)) };
                foreach (var (tile, shift, bytes) in images)
                {
                    if (bytes is null) continue;
                    using var image = SKImage.FromEncodedData(bytes);
                    if (image is null) continue;
                    var scale = 1 << shift;
                    var size = (float)image.Width / scale;
                    var source = SKRect.Create((tile.X - (tile.X >> shift << shift)) * size, (tile.Y - (tile.Y >> shift << shift)) * size, size, size);
                    canvas.DrawImage(image, source, tile.Destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
                }
            }

            await Layer(info => vector.RenderAsync(info, BasemapPass.Geography).ContinueWith(t => t.Result.Bytes), 19, 1);
            if (terrain) await Layer(info => relief.RenderAsync(info, null), 19, TerrainReliefPreferences.DefaultOpacity * theme.Strength);
            if (pollution && atlas.IsAvailable)
                await Layer(info => lightPollution.GetTileAsync(info), LightPollutionRasterSource.MaximumNativeZoom, LightPollutionPreferences.DefaultOpacity);
            await Layer(info => vector.RenderAsync(info, BasemapPass.Reference).ContinueWith(t => t.Result.Bytes), 19, 1);
            using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(output, name + ".png"), data.ToArray());
            log.Add(FormattableString.Invariant($"{name}: {timer.ElapsedMilliseconds} ms"));
        }
    }

    private static (BasemapStyle Style, double Strength, SKColor Surface) Theme(string id, AppearanceMode mode)
    {
        var palette = new ThemeCatalogue(isAvailable: _ => true).Resolve(new(id, AppearanceMode: mode)).Palette;
        var strength = id == "builtin.highcontrast" ? .5 : 1;
        return (BasemapStyle.FromPalette(palette), strength, SKColor.Parse(palette[MapThemeTokens.Surface]));
    }

    private static TileInfo Info(int level, int column, int row)
    {
        var size = 2 * WorldHalf / Math.Pow(2, level);
        var minX = -WorldHalf + column * size;
        var maxY = WorldHalf - row * size;
        return new TileInfo { Index = new TileIndex(column, row, level), Extent = new Extent(minX, maxY - size, minX + size, maxY) };
    }

    private sealed record Paths(string Root) : Noctaxis.Core.Persistence.IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Root;
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The light pollution atlas is only read here.");
    }
}
