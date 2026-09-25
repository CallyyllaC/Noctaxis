using System.Collections.Concurrent;
using BruTile;
using Mapsui;
using Mapsui.Fetcher;
using Mapsui.Layers;
using Noctaxis.Core.LightPollution;
using Noctaxis.Desktop.Diagnostics;
using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.Tests;

public sealed class LightPollutionViewportTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3440, 1440)]
    [InlineData(3840, 2160)]
    public async Task NativeLayerRetainsPublishedDetailAcrossLondonPanAndZoom(int width, int height)
    {
        await using var paths = new AtlasTestData();
        using var http = new HttpClient(new LightPollutionInstallationTests.Handler((_, _) => throw new InvalidOperationException("No network")));
        var store = new LorenzInstallation(paths, http);
        await paths.SeedAsync(store);
        await using var provider = new LightPollutionDataProvider(store);
        using var source = new LightPollutionRasterSource(provider, store, LightPollutionColorMaps.Grayscale);
        using var layer = new LightPollutionTileLayer(source);
        var requests = new ConcurrentDictionary<TileIndex, int>();
        source.Diagnostics = new LightPollutionRasterDiagnostics { Requested = tile => requests.AddOrUpdate(tile, 1, (_, n) => n + 1) };
        HashSet<TileIndex> previous = [];
        foreach (var (zoom, dx, dy) in new[] { (8d, 0d, 0d), (8d, 512d, 0d), (8d, 512d, -384d), (7d, 512d, -384d), (7.5, 512d, -384d), (8d, 0d, 0d) })
        {
            var (extent, resolution) = View(width, height, zoom, dx, dy);
            var level = BruTile.Utilities.GetNearestLevel(source.Schema.Resolutions, resolution);
            var tiles = source.Schema.GetTileInfos(new Extent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY), level).ToArray();
            var before = requests.ToDictionary();
            await DrainAsync(layer, extent, resolution);
            var features = layer.GetFeatures(extent, resolution).ToArray();
            // Parent coverage cannot satisfy this: each detailed tile must actually be published.
            Assert.All(tiles, tile => Assert.Contains(features, feature => feature.Extent is { } e &&
                Math.Abs(e.MinX - tile.Extent.MinX) < .001 && Math.Abs(e.MinY - tile.Extent.MinY) < .001 &&
                Math.Abs(e.Width - tile.Extent.Width) < .001));
            foreach (var tile in tiles.Where(t => previous.Contains(t.Index)))
                Assert.Equal(before.GetValueOrDefault(tile.Index), requests.GetValueOrDefault(tile.Index));
            previous = tiles.Select(t => t.Index).ToHashSet();
        }
        Assert.True(requests.Count > 64);
    }

    [Fact]
    public async Task ColdIntermediate4KPublishesBeyondNativeSingleRequestLimit()
    {
        await using var paths = new AtlasTestData();
        using var http = new HttpClient(new LightPollutionInstallationTests.Handler((_, _) => throw new InvalidOperationException("No network")));
        var store = new LorenzInstallation(paths, http);
        await paths.SeedAsync(store);
        await using var provider = new LightPollutionDataProvider(store);
        using var source = new LightPollutionRasterSource(provider, store, LightPollutionColorMaps.Grayscale);
        using var layer = new LightPollutionTileLayer(source);
        var (extent, resolution) = View(3840, 2160, 7.5, 512, -384);
        await DrainAsync(layer, extent, resolution);
        var tiles = source.Schema.GetTileInfos(new Extent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY), 8).ToArray();
        Assert.True(tiles.Length > 256);
        var features = layer.GetFeatures(extent, resolution).ToArray();
        Assert.All(tiles, tile => Assert.Contains(features, feature => feature.Extent is { } e &&
            Math.Abs(e.MinX - tile.Extent.MinX) < .001 && Math.Abs(e.MinY - tile.Extent.MinY) < .001 &&
            Math.Abs(e.Width - tile.Extent.Width) < .001));
    }

    [Fact]
    public async Task LatestViewportCompletesWhenFourOlderNativeFetchesArePending()
    {
        await using var paths = new AtlasTestData();
        using var http = new HttpClient(new LightPollutionInstallationTests.Handler((_, _) => throw new InvalidOperationException("No network")));
        var store = new LorenzInstallation(paths, http);
        await paths.SeedAsync(store);
        await using var provider = new LightPollutionDataProvider(store);
        using var source = new LightPollutionRasterSource(provider, store, LightPollutionColorMaps.Grayscale);
        using var layer = new LightPollutionTileLayer(source);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        source.Diagnostics = new LightPollutionRasterDiagnostics { Requested = _ =>
        {
            var number = Interlocked.Increment(ref requests);
            if (number == 4) started.TrySetResult();
            if (number <= 4) release.Wait();
        } };
        var (oldExtent, oldResolution) = View(3840, 2160, 8, 0, 0);
        var (extent, resolution) = View(3840, 2160, 7.5, 4096, -384);
        layer.ViewportChanged(new FetchInfo(new MSection(oldExtent, oldResolution)));
        var fetcher = (IFetchableSource)layer;
        var pending = Task.WhenAll(fetcher.GetFetchJobs(0, 4).Select(job => job.FetchFunc()));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            layer.ViewportChanged(new FetchInfo(new MSection(extent, resolution)));
            Assert.Empty(fetcher.GetFetchJobs(4, 0));
        }
        finally { release.Set(); await pending.WaitAsync(TimeSpan.FromSeconds(30)); }
        await DrainAsync(layer, extent, resolution, refresh: false);
        var features = layer.GetFeatures(extent, resolution).ToArray();
        var tiles = source.Schema.GetTileInfos(new Extent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY), 8).ToArray();
        Assert.All(tiles, tile => Assert.Contains(features, feature => feature.Extent is { } e &&
            Math.Abs(e.MinX - tile.Extent.MinX) < .001 && Math.Abs(e.MinY - tile.Extent.MinY) < .001 &&
            Math.Abs(e.Width - tile.Extent.Width) < .001));
        Assert.True(requests > 256);
    }

    [Fact]
    public async Task UnavailableDatasetDoesNotCreateAnUnboundedContinuationLoop()
    {
        await using var paths = new AtlasTestData();
        using var http = new HttpClient(new LightPollutionInstallationTests.Handler((_, _) => throw new InvalidOperationException("No network")));
        var store = new LorenzInstallation(paths, http);
        await using var provider = new LightPollutionDataProvider(store);
        using var source = new LightPollutionRasterSource(provider, store, LightPollutionColorMaps.Grayscale);
        using var layer = new LightPollutionTileLayer(source);
        var requests = 0;
        source.Diagnostics = new LightPollutionRasterDiagnostics { Requested = _ => Interlocked.Increment(ref requests) };
        var (extent, resolution) = View(3840, 2160, 7.5, 512, -384);
        await DrainAsync(layer, extent, resolution);
        Assert.InRange(requests, 1, LightPollutionTileCachePolicy.MaximumTiles);
        Assert.Empty(((IFetchableSource)layer).GetFetchJobs(0, 4));
        Assert.Empty(layer.GetFeatures(extent, resolution));
    }

    private static async Task DrainAsync(LightPollutionTileLayer layer, MRect extent, double resolution, bool refresh = true)
    {
        if (refresh) layer.ViewportChanged(new FetchInfo(new MSection(extent, resolution)));
        // Exercise the interface used by Mapsui, awaiting fetch tasks through cache publication.
        var fetcher = (IFetchableSource)layer;
        for (var batch = 0; batch < 256; batch++)
        {
            var jobs = fetcher.GetFetchJobs(0, 4);
            if (jobs.Length == 0) return;
            await Task.WhenAll(jobs.Select(job => job.FetchFunc())).WaitAsync(TimeSpan.FromSeconds(30));
            _ = layer.GetFeatures(extent, resolution).ToArray();
        }
        Assert.Fail("Native fetch did not reach a bounded completion.");
    }

    private static (MRect Extent, double Resolution) View(int width, int height, double zoom, double dx, double dy)
    {
        var resolution = 156543.03392804097 / Math.Pow(2, zoom);
        var x = -.1278 / 180 * 20037508.342789244 + dx * 611.49622628141;
        var y = Math.Log(Math.Tan(Math.PI / 4 + 51.5074 * Math.PI / 360)) * 6378137 + dy * 611.49622628141;
        return (new MRect(x - width * resolution / 2, y - height * resolution / 2, x + width * resolution / 2, y + height * resolution / 2), resolution);
    }
}
