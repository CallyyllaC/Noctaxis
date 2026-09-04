using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Terrain;
using Xunit.Abstractions;

namespace Noctaxis.Core.Tests;

/// <summary>
/// Opt-in, machine-local terrain timing harness. Run with
/// NOCTAXIS_RUN_TERRAIN_BENCHMARK=1 and a populated application environmental cache.
/// Timings are diagnostics, never pass/fail thresholds.
/// </summary>
public sealed class TerrainPerformanceDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CachedQuarryProfileTimings()
    {
        if (System.Environment.GetEnvironmentVariable("NOCTAXIS_RUN_TERRAIN_BENCHMARK") != "1")
            return;

        var paths = new BenchmarkPaths();
        var cache = new EnvironmentalTileCache(paths, NullLogger<EnvironmentalTileCache>.Instance);
        using var terrainHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var terrain = new TerrariumTerrainProvider(terrainHttp, cache,
            NullLogger<TerrariumTerrainProvider>.Instance);
        var quarry = new GeoCoordinate(53.00737, -3.94847);

        await MeasureProgressiveAsync("cold-decoded quarry", new HorizonService(terrain,
            NullLogger<HorizonService>.Instance), quarry, new TerrainProfileRequest());
        await MeasureProgressiveAsync("warm-decoded nearby quarry", new HorizonService(terrain,
            NullLogger<HorizonService>.Instance), quarry with { Latitude = quarry.Latitude + 0.00001 },
            new TerrainProfileRequest());
        await MeasureAsync("warm-decoded 13 bearing", new HorizonService(terrain,
            NullLogger<HorizonService>.Instance), quarry with { Latitude = quarry.Latitude + 0.00002 },
            new TerrainProfileRequest(AzimuthSampleCount: 13));
        await MeasureProgressiveAsync("synthetic flat fixture", new HorizonService(new FlatGround(),
            NullLogger<HorizonService>.Instance), new GeoCoordinate(0, 0),
            new TerrainProfileRequest());
    }

    private async Task MeasureProgressiveAsync(string name, HorizonService service,
        GeoCoordinate observer, TerrainProfileRequest request)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        var stopwatch = Stopwatch.StartNew();
        var work = service.StartProfile(observer, request, default);
        var bearings = Enumerable.Range(0, 13).Select(index => 150d + index * 5).ToArray();
        var priority = await work.PrioritiseBearingsAsync(bearings, default);
        var fovMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var fovAllocated = GC.GetTotalAllocatedBytes(true) - before;
        var complete = await work.CompleteProfile;
        stopwatch.Stop();
        var totalAllocated = GC.GetTotalAllocatedBytes(true) - before;
        output.WriteLine($"{name} FOV ready: {fovMilliseconds:F1} ms; " +
                         $"allocated={fovAllocated / 1024d / 1024d:F1} MiB; " +
                         $"bearings={priority.EffectiveCompletedBearingCount}/{priority.Samples.Count}");
        output.WriteLine($"{name} full 360: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; " +
                         $"allocated={totalAllocated / 1024d / 1024d:F1} MiB; " +
                         $"workers={work.DegreeOfParallelism}; " +
                         $"coordinate={complete.PipelineTimings?.GeographicCoordinateGenerationMilliseconds:F1}ms; " +
                         $"discovery={complete.PipelineTimings?.RequiredTileDiscoveryMilliseconds:F1}ms; " +
                         $"cache={complete.PipelineTimings?.CacheLookupMilliseconds:F1}ms; " +
                         $"decode={complete.PipelineTimings?.DiskReadAndDecodeMilliseconds:F1}ms; " +
                         $"tiles={complete.PipelineTimings?.TilePreparationMilliseconds:F1}ms; " +
                         $"terrain={complete.PipelineTimings?.TerrainSamplingMilliseconds:F1}ms; " +
                         $"math={complete.PipelineTimings?.HorizonMathematicsMilliseconds:F1}ms");
    }

    private async Task MeasureAsync(string name, HorizonService service, GeoCoordinate observer,
        TerrainProfileRequest request)
    {
        var before = GC.GetTotalAllocatedBytes(true);
        var stopwatch = Stopwatch.StartNew();
        var profile = await service.GetProfileAsync(observer, request, default);
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        output.WriteLine($"{name}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; " +
                         $"allocated={allocated / 1024d / 1024d:F1} MiB; " +
                         $"samples={profile.Samples.Count}x{profile.Samples[0].Sightline?.Count ?? 0}");
    }

    private sealed class BenchmarkPaths : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Noctaxis");
    }

    private sealed class FlatGround : ITerrainElevationProvider
    {
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate,
            CancellationToken cancellationToken) => Task.FromResult(Value("ground"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken cancellationToken) => Task.FromResult(Batch(coordinates.Count, "ground"));
    }

    private static EnvironmentalValue<double> Value(string source) => new(
        EnvironmentalDataState.Available, 0, source, "fixture", "Flat fixture");

    private static ElevationBatchResult Batch(int count, string source) => new(
        EnvironmentalDataState.Available, Enumerable.Repeat<double?>(0, count).ToArray(),
        source, "fixture", "Flat fixture");
}
