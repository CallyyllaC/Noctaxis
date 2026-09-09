using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;
using SkiaSharp;
using BitMiracle.LibTiff.Classic;
using System.Runtime.CompilerServices;

namespace Noctaxis.Core.Tests;

// Offline fixtures exercise the actual PNG decoder, interpolation, resolver and horizon.
// Geographic names describe workload origins, not surveyed terrain or live WorldCover evidence.
public sealed class TerrainComputeBenchmarkTests
{
    [Fact]
    public async Task OfflineComputeMatrix()
    {
        var destination = System.Environment.GetEnvironmentVariable("NOCTAXIS_COMPUTE_BENCHMARK_OUTPUT");
        if (string.IsNullOrWhiteSpace(destination)) return;
        var rows = new List<object>();
        var directory = Path.Combine(Path.GetTempPath(), "Noctaxis-compute-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var scenario in new[] { "Brigg", "Coast", "OpenWater", "Mountain" })
            {
                var path = Path.Combine(directory, scenario + ".png");
                using (var bitmap = new SKBitmap(256, 256))
                {
                    for (var y = 0; y < 256; y++)
                    for (var x = 0; x < 256; x++)
                    {
                        var height = scenario switch
                        {
                            "Brigg" => 10 + x / 32,
                            "Coast" => x < 128 ? -30 : 20,
                            "OpenWater" => -100,
                            _ => 100 + (x * 17 + y * 13) % 1200
                        };
                        var encoded = height + 32768;
                        bitmap.SetPixel(x, y, new SKColor((byte)(encoded / 256), (byte)encoded, 0));
                    }
                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    using var stream = File.Create(path);
                    data.SaveTo(stream);
                }
                foreach (var workers in new[] { 1, 3, HorizonService.DefaultDegreeOfParallelism }.Distinct())
                for (var iteration = 0; iteration < 4; iteration++)
                {
                    using var http = new HttpClient();
                    var cache = new FixtureCache(path);
                    var terrain = new TerrariumTerrainProvider(http, cache,
                        NullLogger<TerrariumTerrainProvider>.Instance);
                    var cover = new CountingCover();
                    var countedTerrain = new CountingTerrain(terrain);
                    var resolver = new TerrainSurfaceResolver(countedTerrain, cover, NullLogger<TerrainSurfaceResolver>.Instance);
                    var horizon = new HorizonService(resolver, NullLogger<HorizonService>.Instance, workers);
                    var observer = scenario switch
                    {
                        "Brigg" => new GeoCoordinate(53.552, -.492),
                        "Coast" => new GeoCoordinate(53.1, .3),
                        "OpenWater" => new GeoCoordinate(53, -6),
                        _ => new GeoCoordinate(56.796, -5.004)
                    };
                    async Task Measure(string operation, Func<Task> action)
                    {
                        var metrics = terrain.Metrics;
                        var classifications = cover.Coordinates;
                        var batches = cover.Batches;
                        var lookups = cache.Lookups;
                        var touches = cache.Touches;
                        var positive = countedTerrain.Positive;
                        var negative = countedTerrain.Negative;
                        var terrainBatches = countedTerrain.Batches;
                        var started = countedTerrain.Started;
                        var completed = countedTerrain.Completed;
                        var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                        var allocated = GC.GetTotalAllocatedBytes(true);
                        var watch = Stopwatch.StartNew();
                        await action();
                        watch.Stop();
                        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
                        if (iteration > 0) rows.Add(new { scenario, workers, iteration, operation,
                            milliseconds = watch.Elapsed.TotalMilliseconds, allocated,
                            samples = terrain.Metrics.Samples - metrics.Samples,
                            tileLoads = terrain.Metrics.TileLoads - metrics.TileLoads,
                            decodedHits = terrain.Metrics.DecodedCacheHits - metrics.DecodedCacheHits,
                            classifications = cover.Coordinates - classifications,
                            classificationBatches = cover.Batches - batches,
                            cacheLookups = cache.Lookups - lookups, touches = cache.Touches - touches,
                            positive = countedTerrain.Positive - positive, negative = countedTerrain.Negative - negative,
                            terrainBatches = countedTerrain.Batches - terrainBatches,
                            samplesStarted = countedTerrain.Started - started, samplesCompleted = countedTerrain.Completed - completed,
                            profileCache = operation == "warm" ? "hit (zero provider samples verified)" : operation is "360" or "FoV13" ? "miss" : "n/a",
                            collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray() });
                    }
                    await Measure("observer", async () => { await resolver.GetSurfaceSampleAsync(observer, default); });
                    await Measure("360", async () => { await horizon.GetProfileAsync(observer, new(), default); });
                    await Measure("warm", async () =>
                    {
                        var samples = terrain.Metrics.Samples;
                        await horizon.GetProfileAsync(observer, new(), default);
                        Assert.Equal(samples, terrain.Metrics.Samples);
                    });
                    await Measure("FoV13", async () => { await horizon.GetProfileAsync(observer, new(13), default); });
                    var map = new TerrainDebugMapService(resolver);
                    await Measure("map128", async () => { await map.GetMapAsync(observer, new(), default); });
                    await Measure("mapRepeat", async () => { await map.GetMapAsync(observer, new(), default); });
                    await Measure("rapid", async () =>
                    {
                        for (var move = 0; move < 4; move++)
                        {
                            using var cancellation = new CancellationTokenSource();
                            var work = horizon.StartProfile(observer with { Latitude = observer.Latitude + .01 * (move + 1) }, new(), cancellation.Token);
                            cancellation.CancelAfter(2);
                            try { await work.CompleteProfile; } catch (OperationCanceledException) { }
                        }
                    });
                }
            }
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(destination + ".layout.json", JsonSerializer.Serialize(new
            {
                coordinateBytes = Unsafe.SizeOf<GeoCoordinate>(), sightlineBytes = Unsafe.SizeOf<TerrainSightlineSample>(),
                nullableElevationBytes = Unsafe.SizeOf<double?>(), nullableClassificationBytes = Unsafe.SizeOf<LandCoverClass?>()
            }));
            await MeasureSelectiveClassification(directory, destination);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task MeasureSelectiveClassification(string directory, string destination)
    {
        var path = Path.Combine(directory, "classification.tif");
        using (var tiff = Tiff.Open(path, "w"))
        {
            tiff.SetField(TiffTag.IMAGEWIDTH, 1024);
            tiff.SetField(TiffTag.IMAGELENGTH, 1024);
            tiff.SetField(TiffTag.TILEWIDTH, 256);
            tiff.SetField(TiffTag.TILELENGTH, 256);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tiff.SetField(TiffTag.COMPRESSION, Compression.DEFLATE);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            var tile = new byte[256 * 256];
            Array.Fill(tile, (byte)LandCoverClass.PermanentWater);
            for (var index = 0; index < 16; index++) tiff.WriteEncodedTile(index, tile, tile.Length);
            tiff.WriteDirectory();
        }
        var coordinates = Enumerable.Range(0, 157320).Select(i =>
            new GeoCoordinate(51.01 + (i % 512) * .005, -2.99 + ((i / 512) % 512) * .005)).ToArray();
        var measurements = new List<object>();
        foreach (var batchCount in new[] { 1, 15 })
        {
            var batches = coordinates.Chunk(coordinates.Length / batchCount).ToArray();
            using var http = new HttpClient();
            var provider = new WorldCoverLandCoverProvider(http, new FixtureCache(path), NullLogger<WorldCoverLandCoverProvider>.Instance);
            for (var iteration = 0; iteration < 4; iteration++)
            {
                var allocated = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                foreach (var batch in batches)
                {
                    var result = await provider.GetLandCoversAsync(batch, default);
                    Assert.Equal(batch.Length, result.Classifications.Count);
                    Assert.Equal(LandCoverClass.PermanentWater, result.Classifications[0]);
                }
                timer.Stop();
                allocated = GC.GetTotalAllocatedBytes(true) - allocated;
                if (iteration > 0) measurements.Add(new { batchCount, iteration, milliseconds = timer.Elapsed.TotalMilliseconds, allocated });
            }
        }
        await File.WriteAllTextAsync(destination + ".worldcover.json", JsonSerializer.Serialize(measurements,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class CountingCover : ILandCoverProvider
    {
        public long Coordinates, Batches;
        public Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate, CancellationToken token)
        {
            Interlocked.Increment(ref Coordinates);
            return Task.FromResult(new EnvironmentalValue<LandCoverClass>(EnvironmentalDataState.Available,
                LandCoverClass.PermanentWater, "fixture", "1", "Synthetic classification"));
        }
        public Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Add(ref Coordinates, coordinates.Count);
            Interlocked.Increment(ref Batches);
            var values = new LandCoverClass?[coordinates.Count];
            Array.Fill(values, LandCoverClass.PermanentWater);
            return Task.FromResult(new LandCoverBatchResult(EnvironmentalDataState.Available, values, "fixture", "1", "Synthetic classification"));
        }
    }

    private sealed class CountingTerrain(ITerrainElevationProvider inner) : ITerrainElevationProvider
    {
        public long Positive, Negative, Batches, Started, Completed;
        public Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token) => inner.PreloadAsync(coordinates, token);
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) => inner.GetElevationAsync(coordinate, token);
        public Task<ElevationSampleResult> GetElevationSampleAsync(GeoCoordinate coordinate, CancellationToken token) => inner.GetElevationSampleAsync(coordinate, token);
        public async Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        {
            Interlocked.Increment(ref Batches);
            Interlocked.Add(ref Started, coordinates.Count);
            var result = await inner.GetElevationsAsync(coordinates, token);
            long positive = 0, negative = 0;
            foreach (var value in result.ElevationsMetres)
                if (value >= 0) positive++; else if (value < 0) negative++;
            Interlocked.Add(ref Positive, positive);
            Interlocked.Add(ref Negative, negative);
            Interlocked.Add(ref Completed, result.ElevationsMetres.Count);
            return result;
        }
    }

    private sealed class FixtureCache(string path) : IEnvironmentalTileCache
    {
        public long Lookups, Touches;
        public string RootDirectory => Path.GetDirectoryName(path)!;
        public void TouchTerrain(EnvironmentalTileDescriptor descriptor) => Interlocked.Increment(ref Touches);
        public Task<EnvironmentalCacheResult> GetOrCreateAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<byte[]?>> acquire, Func<string, bool> validate, CancellationToken token) => throw new NotSupportedException();
        public Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire, Func<string, bool> validate, CancellationToken token)
        {
            Interlocked.Increment(ref Lookups);
            return Task.FromResult(new EnvironmentalCacheResult(EnvironmentalDataState.Cached, path, true, "Offline PNG fixture"));
        }
    }
}
