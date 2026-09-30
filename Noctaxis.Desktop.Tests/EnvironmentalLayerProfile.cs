using System.Diagnostics;
using System.Globalization;
using BruTile;
using Mapsui;
using Mapsui.Fetcher;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Environment;
using Noctaxis.Core.LightPollution;
using Noctaxis.Desktop.Diagnostics;
using Noctaxis.Desktop.Mapping;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Opt-in timing profile of the Planner's two environmental layers through Mapsui's own fetch
/// interface (four concurrent jobs, as the map runs them): set NOCTAXIS_LAYER_PROFILE to an output
/// directory. Reads an installed light-pollution atlas without writing; terrain uses a temporary cache,
/// filled over the network by a first pass so later passes measure disk and rendering only.
/// </summary>
public sealed class EnvironmentalLayerProfile
{
    private const int Width = 3440, Height = 1440, Concurrency = 4;
    private sealed record Stage(string Name, double Zoom, double DxPixels, double DyPixels);

    // Northern England; a pan is a third of the screen, as a drag would be.
    private static readonly Stage[] Stages =
    [
        new("initial", 8, 0, 0), new("pan-east", 8, 1150, 0), new("pan-south", 8, 1150, 480),
        new("zoom-out", 7, 1150, 480), new("zoom-in", 9, 1150, 480), new("return", 8, 0, 0)
    ];

    [Fact]
    public async Task Profile()
    {
        var output = Environment.GetEnvironmentVariable("NOCTAXIS_LAYER_PROFILE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var report = new List<string>();

        // ---- Light pollution ----------------------------------------------------------------------
        var atlas = new LorenzInstallation(new Noctaxis.Core.Persistence.PlatformUserDataPathProvider(), new HttpClient(new NoNetwork()));
        await atlas.DetectAsync();
        Assert.True(atlas.IsAvailable, "An installed light-pollution atlas is required.");
        await using (var data = new LightPollutionDataProvider(atlas))
        {
            using var source = new LightPollutionRasterSource(data, atlas, LightPollutionColorMaps.Resolve("turbo"));
            var trace = new LightPollutionRasterDiagnostics();
            source.Diagnostics = trace;
            data.Diagnostics = new Noctaxis.Core.Diagnostics.LightPollutionCacheMetrics();
            using var layer = new LightPollutionTileLayer(source);
            report.Add("## Light pollution (process-cold: atlas on disk, nothing decoded)");
            report.Add("| Stage | Wall ms | Requests | Generated | Cache hits | Read wait ms/tile | Source decode ms (count) | Sample ms/tile | Palette ms/tile | Encode ms/tile | PNG decode ms/tile |");
            report.Add("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var stage in Stages)
            {
                var before = Snapshot(trace, data.Diagnostics);
                var requests = 0;
                trace.Requested = _ => Interlocked.Increment(ref requests);
                var (wall, decodeMs, published) = await DrainAsync(layer, source.Schema, stage);
                var after = Snapshot(trace, data.Diagnostics);
                var generated = after.Generated - before.Generated;
                double PerTile(long ticks) => generated == 0 ? 0 : Ms(ticks) / generated;
                report.Add(Row(stage.Name, wall, requests, generated, after.RasterHits - before.RasterHits,
                    PerTile(after.ReadWait - before.ReadWait),
                    $"{Ms(after.Load - before.Load):F0} ({after.Decodes - before.Decodes})",
                    PerTile(after.Sample - before.Sample), PerTile(after.Palette - before.Palette),
                    PerTile(after.Encode - before.Encode), published == 0 ? 0 : decodeMs / published));
            }
            // A palette change: every visible tile is coloured afresh, as the Settings change does.
            source.SetColorMap(LightPollutionColorMaps.Grayscale);
            layer.ClearCache();
            {
                var before = Snapshot(trace, data.Diagnostics);
                var (wall, _, _) = await DrainAsync(layer, source.Schema, Stages[^1]);
                var after = Snapshot(trace, data.Diagnostics);
                report.Add(string.Create(CultureInfo.InvariantCulture,
                    $"| palette-change (return view) | {wall:F0} | – | {after.Generated - before.Generated} | {after.RasterHits - before.RasterHits} | – | – | {Ms(after.Sample - before.Sample) / Math.Max(1, after.Generated - before.Generated):F1} | {Ms(after.Palette - before.Palette) / Math.Max(1, after.Generated - before.Generated):F1} | {Ms(after.Encode - before.Encode) / Math.Max(1, after.Generated - before.Generated):F1} | – |"));
            }
            report.Add("");
            report.Add(await Interrupted("Light pollution", layer, source.Schema, () => trace.Generated));
        }

        // ---- Terrain relief -------------------------------------------------------------------------
        var temp = Path.Combine(Path.GetTempPath(), "noctaxis-layer-profile");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/1.0 (environmental layer profile)");
        var cache = new EnvironmentalTileCache(new Paths(temp), NullLogger<EnvironmentalTileCache>.Instance);
        foreach (var pass in new[] { "network-cold (fills the temporary disk cache)", "process-cold (disk warm, nothing decoded)" })
        {
            var terrarium = new TerrariumTerrainProvider(http, cache, NullLogger<TerrariumTerrainProvider>.Instance);
            var source = new TerrainReliefTileSource(terrarium);
            using var layer = new TerrainReliefTileLayer(source);
            report.Add("");
            report.Add($"## Terrain relief ({pass})");
            report.Add("| Stage | Wall ms | Shaded | Cache hits | Shade ms/tile | Render+encode ms/tile | PNG decode ms/tile |");
            report.Add("|---|---|---|---|---|---|---|");
            foreach (var stage in Stages)
            {
                var before = source.Metrics;
                var (wall, decodeMs, published) = await DrainAsync(layer, source.Schema, stage);
                var after = source.Metrics;
                var shaded = after.ReliefComputations - before.ReliefComputations;
                var rendered = after.TilesRendered - before.TilesRendered;
                report.Add(string.Create(CultureInfo.InvariantCulture,
                    $"| {stage.Name} | {wall:F0} | {shaded} | {after.ReliefCacheHits - before.ReliefCacheHits} | {(shaded == 0 ? 0 : (after.ShadeMilliseconds - before.ShadeMilliseconds) / shaded):F1} | {(rendered == 0 ? 0 : (after.RenderMilliseconds - before.RenderMilliseconds) / rendered):F1} | {(published == 0 ? 0 : decodeMs / published):F2} |"));
            }
            if (pass.StartsWith("process"))
            {
                report.Add("");
                report.Add(await Interrupted("Terrain relief", layer, source.Schema, () => source.Metrics.ReliefComputations));
                report.Add($"Terrain abandoned tiles: {source.Metrics.AbandonedTiles}");
            }
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "layer-profile.md"), report);
    }

    // A pan that starts loading one view and is overtaken by the next after the first batch.
    private static async Task<string> Interrupted(string name, TileLayer layer, ITileSchema schema, Func<long> work)
    {
        var start = work();
        var first = new Stage("interrupt-a", 8, -2300, -960);
        var second = new Stage("interrupt-b", 8, -3450, -960);
        var (extentA, resolution) = View(first);
        layer.ViewportChanged(new FetchInfo(new MSection(extentA, resolution)));
        var fetcher = (IFetchableSource)layer;
        await Task.WhenAll(fetcher.GetFetchJobs(0, Concurrency).Select(job => job.FetchFunc()));
        var (wall, _, _) = await DrainAsync(layer, schema, second);
        var needed = schema.GetTileInfos(ToExtent(View(second).Extent), BruTile.Utilities.GetNearestLevel(schema.Resolutions, resolution)).Count();
        return string.Create(CultureInfo.InvariantCulture,
            $"{name} interrupted pan: second view complete after {wall:F0} ms; tiles produced during both {work() - start}, current tiles in the second view {needed}.");
    }

    private static async Task<(double Wall, double DecodeMs, int Published)> DrainAsync(TileLayer layer, ITileSchema schema, Stage stage)
    {
        var (extent, resolution) = View(stage);
        var timer = Stopwatch.StartNew();
        layer.ViewportChanged(new FetchInfo(new MSection(extent, resolution)));
        var fetcher = (IFetchableSource)layer;
        for (var batch = 0; batch < 1000; batch++)
        {
            var jobs = fetcher.GetFetchJobs(0, Concurrency);
            if (jobs.Length == 0) break;
            await Task.WhenAll(jobs.Select(job => job.FetchFunc()));
        }
        var wall = timer.Elapsed.TotalMilliseconds;
        // What Mapsui then does with each published tile before drawing it: decode the image.
        var decode = Stopwatch.StartNew();
        var published = 0;
        foreach (var feature in layer.GetFeatures(extent, resolution).OfType<RasterFeature>())
        {
            if (feature.Raster?.Data is not { } bytes) continue;
            using var image = SKImage.FromEncodedData(bytes);
            using var raster = image?.ToRasterImage();
            published++;
        }
        return (wall, decode.Elapsed.TotalMilliseconds, published);
    }

    private static (MRect Extent, double Resolution) View(Stage stage)
    {
        var resolution = 156543.03392804097 / Math.Pow(2, stage.Zoom) / 2; // 2x device pixels
        const double centreX = -250_000, centreY = 7_150_000; // northern England, Web Mercator
        var x = centreX + stage.DxPixels * resolution;
        var y = centreY - stage.DyPixels * resolution;
        return (new MRect(x - Width / 2d * resolution, y - Height / 2d * resolution, x + Width / 2d * resolution, y + Height / 2d * resolution), resolution);
    }

    private static Extent ToExtent(MRect r) => new(r.MinX, r.MinY, r.MaxX, r.MaxY);

    private sealed record Counters(long Generated, long RasterHits, long ReadWait, long Load, long Decodes, long Sample, long Palette, long Encode);

    private static Counters Snapshot(LightPollutionRasterDiagnostics trace, Noctaxis.Core.Diagnostics.LightPollutionCacheMetrics numerical) =>
        new(Interlocked.Read(ref trace.Generated), trace.Cache.Hits, Interlocked.Read(ref trace.ReadWaitTicks),
            Interlocked.Read(ref numerical.LoadTicks), numerical.Misses, Interlocked.Read(ref trace.SampleTicks),
            Interlocked.Read(ref trace.PaletteTicks), Interlocked.Read(ref trace.EncodeTicks));

    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static string Row(string stage, double wall, int requests, long generated, long hits, double wait, string decode,
        double sample, double palette, double encode, double pngDecode) => string.Create(CultureInfo.InvariantCulture,
        $"| {stage} | {wall:F0} | {requests} | {generated} | {hits} | {wait:F1} | {decode} | {sample:F1} | {palette:F1} | {encode:F1} | {pngDecode:F2} |");

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
