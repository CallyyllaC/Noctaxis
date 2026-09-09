using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Persistence;
using Xunit.Abstractions;

namespace Noctaxis.Core.Tests;

public sealed class TerrainDiskCacheTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Noctaxis-cache-policy-" + Guid.NewGuid().ToString("N"));
    private TerrainDiskCache Manager() => new(new Paths(_root), NullLogger<TerrainDiskCache>.Instance);
    private EnvironmentalTileCache Cache(TerrainDiskCache manager) => new(new Paths(_root),
        NullLogger<EnvironmentalTileCache>.Instance, manager);
    private static EnvironmentalTileDescriptor Tile(int x, string version = "v1") =>
        new(TerrariumTerrainProvider.SourceId, version, "z2", $"{x}-1", "png");

    [Fact]
    public void SettingsLimit_RoundTripsAndDefaultsToTwoGiB()
    {
        Assert.Equal(TerrainDiskCache.DefaultLimitBytes, new AppSettings().EffectiveTerrainCacheLimitBytes);
        var settings = new AppSettings(TerrainCacheLimitBytes: 536870912);
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Equal(settings.TerrainCacheLimitBytes,
            System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.TerrainCacheLimitBytes);
        Assert.Equal(TerrainDiskCache.DefaultLimitBytes,
            System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.EffectiveTerrainCacheLimitBytes);
    }

    private static async Task Insert(TerrainDiskCache manager, EnvironmentalTileCache cache,
        EnvironmentalTileDescriptor descriptor, int bytes = 400)
    {
        using var lease = await manager.LeaseAsync(descriptor, default);
        var result = await cache.GetOrCreateAsync(descriptor, _ => Task.FromResult<byte[]?>(new byte[bytes]),
            path => new FileInfo(path).Length == bytes, default);
        Assert.True(result.IsAvailable);
        Assert.Equal(bytes, new FileInfo(result.Path!).Length);
    }

    [Fact]
    public async Task HardLimit_800Plus400_RetainsAtMost1000ActualBytes()
    {
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(1000, []);
        await Insert(manager, cache, Tile(0)); await Insert(manager, cache, Tile(1));
        Assert.Equal(800, manager.Usage.Bytes);
        await Insert(manager, cache, Tile(2));
        Assert.Equal(800, manager.Usage.Bytes);
        Assert.Equal(800, Directory.EnumerateFiles(manager.RootDirectory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length));
        Assert.Equal(1, manager.Usage.Evictions);
    }

    [Fact]
    public async Task DistanceOverridesRecency_AndUsesCurrentSavedLocations()
    {
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(1200, [new(40, -130)]);
        await Insert(manager, cache, Tile(0)); await Insert(manager, cache, Tile(1)); await Insert(manager, cache, Tile(3));
        await manager.ConfigureAsync(800, [new(40, -130)]);
        Assert.False(File.Exists(manager.PathFor(Tile(3))));
        Assert.True(File.Exists(manager.PathFor(Tile(0))));
        await manager.ConfigureAsync(1200, [new(40, 50)]);
        await Insert(manager, cache, Tile(3));
        await manager.ConfigureAsync(800, [new(40, 50)]);
        Assert.False(File.Exists(manager.PathFor(Tile(0))));
        Assert.True(File.Exists(manager.PathFor(Tile(3))));
    }

    [Fact]
    public async Task MultipleSavedLocations_PreservesAssetsNearEitherPoint()
    {
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(800, [new(40, -130), new(40, 130)]);
        await Insert(manager, cache, Tile(0)); await Insert(manager, cache, Tile(3)); await Insert(manager, cache, Tile(1));
        Assert.True(File.Exists(manager.PathFor(Tile(0))));
        Assert.True(File.Exists(manager.PathFor(Tile(3))));
        Assert.False(File.Exists(manager.PathFor(Tile(1))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualDistanceOrNoSavedPoints_UsesAccessRecency(bool saved)
    {
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(800, saved ? [new(40, -130)] : []);
        var first = Tile(0, "a"); var second = Tile(0, "b"); var third = Tile(0, "c");
        await Insert(manager, cache, first); await Insert(manager, cache, second);
        manager.Touch(first);
        await Insert(manager, cache, third);
        Assert.True(File.Exists(manager.PathFor(first)));
        Assert.False(File.Exists(manager.PathFor(second)));
    }

    [Fact]
    public void Footprints_ContainSavedPointAndWrapDateline()
    {
        var worldCover = TerrainDiskCache.Footprint(new(WorldCoverLandCoverProvider.SourceId,
            "2021-v200", "land-cover", "N51W003", "tif"))!.Value;
        Assert.Equal(0, TerrainDiskCache.DistanceToFootprint(new(51.01, -.01), worldCover));
        Assert.InRange(TerrainDiskCache.DistanceToFootprint(new(52, .01), worldCover), 600, 800);
        Assert.Equal(0, TerrainDiskCache.DistanceToFootprint(new(0, 179), new(-1, 178, 1, -178)));
        Assert.Equal(0, TerrainDiskCache.DistanceToFootprint(new(0, -179), new(-1, 178, 1, -178)));
        Assert.True(TerrainDiskCache.DistanceToFootprint(new(0, 0), worldCover) > 5000000);
        Assert.NotNull(TerrainDiskCache.Footprint(Tile(0)));
    }

    [Fact]
    public async Task LargeWorldCoverContainingSavedPoint_IsRetainedAheadOfDistantTile()
    {
        var manager = Manager(); var cache = Cache(manager);
        var raster = new EnvironmentalTileDescriptor(WorldCoverLandCoverProvider.SourceId,
            "2021-v200", "land-cover", "N51W003", "tif");
        await manager.ConfigureAsync(400, [new(51.01, -.01)]);
        await Insert(manager, cache, raster);
        await Insert(manager, cache, Tile(3));
        Assert.True(File.Exists(manager.PathFor(raster)));
        Assert.False(File.Exists(manager.PathFor(Tile(3))));
        Assert.Equal(400, manager.Usage.Bytes);
    }

    [Fact]
    public async Task ClearDuringDownload_DrainsLeaseThenDeletesAndRejectsOldGeneration()
    {
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(1000, []);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidations = 0;
        cache.RegisterTerrainInvalidation(() => Interlocked.Increment(ref invalidations));
        var generation = cache.TerrainGeneration;
        var outer = await manager.LeaseAsync(Tile(0), default);
        var pending = cache.GetOrCreateAsync(Tile(0), async _ =>
        { started.SetResult(); await finish.Task; return new byte[400]; }, _ => true, default);
        await started.Task;
        var clear = manager.ClearAsync();
        Assert.False(clear.IsCompleted);
        finish.SetResult();
        var entry = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(400, File.ReadAllBytes(entry.Path!).Length);
        Assert.False(clear.IsCompleted);
        outer.Dispose();
        await clear.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(entry.Path));
        Assert.Equal(0, manager.Usage.Bytes);
        Assert.Equal(2, invalidations);
        Assert.Equal(1, manager.Usage.Clears);
        Assert.Equal(0, manager.Usage.Evictions);
        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.LeaseAsync(Tile(0), default, generation));
        await Insert(manager, cache, Tile(0));
        Assert.Equal(400, manager.Usage.Bytes);
    }

    [Fact]
    public async Task ClearOnlyRemovesTerrain_AndReconcilesExternalChanges()
    {
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(10000, []);
        await Insert(manager, cache, Tile(0));
        var unrelated = new EnvironmentalTileDescriptor("wsf-3d", "v1", "height", "example", "bin");
        var other = await cache.GetOrCreateAsync(unrelated, _ => Task.FromResult<byte[]?>([1, 2, 3]), _ => true, default);
        File.Delete(manager.PathFor(Tile(0)));
        var extra = manager.PathFor(Tile(1)); Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
        await File.WriteAllBytesAsync(extra, new byte[123]);
        await manager.ReconcileAsync();
        Assert.Equal(123, manager.Usage.Bytes);
        var reopened = Manager(); await reopened.InitializeAsync();
        Assert.Equal(123, reopened.Usage.Bytes);
        await manager.ClearAsync();
        Assert.Equal(0, manager.Usage.Bytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(other.Path!));
    }

    [Fact]
    public async Task OversizedAsset_IsUsableUnderLeaseThenNotRetained()
    {
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(100, []);
        var calls = 0;
        using (await manager.LeaseAsync(Tile(0), default))
        {
            for (var index = 0; index < 4; index++)
            {
                var entry = await cache.GetOrCreateAsync(Tile(0), _ =>
                { calls++; return Task.FromResult<byte[]?>(new byte[120]); }, _ => true, default);
                Assert.Equal(120, File.ReadAllBytes(entry.Path!).Length);
            }
            Assert.Equal(1, calls);
        }
        Assert.Equal(0, manager.Usage.Bytes);
        Assert.Equal(1, manager.Usage.OversizedItems);
        Assert.False(File.Exists(manager.PathFor(Tile(0))));
    }

    [Fact]
    public async Task ConcurrentInsertion_AndLimitReduction_RespectBudgetAfterReadersFinish()
    {
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(1000, []);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(async index =>
        { await start.Task; await Insert(manager, cache, Tile(index % 4, $"v{index}")); }).ToArray();
        start.SetResult(); await Task.WhenAll(tasks);
        Assert.InRange(manager.Usage.Bytes, 0, 1000);
        await manager.ConfigureAsync(500, []);
        Assert.InRange(manager.Usage.Bytes, 0, 500);
    }

    [Fact]
    public async Task CachePolicyMeasurements()
    {
        if (System.Environment.GetEnvironmentVariable("NOCTAXIS_RUN_CACHE_BENCHMARK") != "1") return;
        var manager = Manager(); var cache = Cache(manager);
        await manager.ConfigureAsync(100000000, []);
        static EnvironmentalTileDescriptor BenchTile(int index) => new(TerrariumTerrainProvider.SourceId,
            TerrariumTerrainProvider.SourceVersion, "z12", $"{index}-2000", "png");
        for (var index = 0; index < 1000; index++)
            await Insert(manager, cache, index < 980 ? BenchTile(index) :
                new(WorldCoverLandCoverProvider.SourceId, WorldCoverLandCoverProvider.SourceVersion,
                    "land-cover", $"N{(index - 980) * 3:00}E000", "tif"), index < 980 ? 4096 : 1024 * 1024);
        var retainedBefore = GC.GetTotalMemory(true);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var reopened = Manager(); await reopened.InitializeAsync();
        output.WriteLine($"Reconcile 1000 files: {timer.Elapsed.TotalMilliseconds:F2} ms; allocated {GC.GetTotalAllocatedBytes(true) - allocated} bytes");
        output.WriteLine($"Approximate retained metadata overhead: {GC.GetTotalMemory(true) - retainedBefore} bytes; payload {manager.Usage.Bytes} bytes");
        foreach (var count in new[] { 0, 10, 100, 250 })
        {
            var saved = Enumerable.Range(0, count).Select(i => new GeoCoordinate(i % 80, i % 360 - 180)).ToArray();
            await manager.ConfigureAsync(100000000, saved);
            timer.Restart(); await Insert(manager, cache, BenchTile(2000 + count), 4096);
            output.WriteLine($"Insert below limit, {count} saved: {timer.Elapsed.TotalMilliseconds:F2} ms");
            await manager.ConfigureAsync(manager.Usage.Bytes, saved);
            timer.Restart(); await Insert(manager, cache, BenchTile(3000 + count), 4096);
            output.WriteLine($"Insert plus eviction, {count} saved: {timer.Elapsed.TotalMilliseconds:F2} ms");
            timer.Restart(); await Insert(manager, cache, BenchTile(3500 + count), 4096);
            output.WriteLine($"Insert plus eviction with unchanged priorities, {count} saved: {timer.Elapsed.TotalMilliseconds:F2} ms");
        }
        timer.Restart(); await manager.ClearAsync();
        output.WriteLine($"Clear 1000 files: {timer.Elapsed.TotalMilliseconds:F2} ms");
        GC.KeepAlive(reopened);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Paths(string root) : IUserDataPathProvider
    { public string GetApplicationDataDirectory() => root; }
}
