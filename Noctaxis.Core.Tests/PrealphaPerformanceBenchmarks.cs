using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Time;
using NodaTime;
using SkiaSharp;

namespace Noctaxis.Core.Tests;

// Opt-in pre-alpha performance workloads (PF-01..PF-05, PF-09). Without NOCTAXIS_PERF_OUTPUT (a directory)
// every test returns at once. NOCTAXIS_PERF_LABEL names the run ("before", "after"). Timings never decide a
// result. Fixtures are deterministic and offline: no user cache, no network. Run this class on its own:
//   dotnet test Noctaxis.Core.Tests -c Release --filter FullyQualifiedName~PrealphaPerformanceBenchmarks
public sealed class PrealphaPerformanceBenchmarks
{
    private static readonly string? Output = System.Environment.GetEnvironmentVariable("NOCTAXIS_PERF_OUTPUT");
    private static string Label => System.Environment.GetEnvironmentVariable("NOCTAXIS_PERF_LABEL") is { Length: > 0 } label
        ? label : "run";

    // PF-03: the derived camera framing evaluated on the UI thread for every camera move.
    [Fact]
    public void CameraFraming()
    {
        if (string.IsNullOrWhiteSpace(Output)) return;
        var terrain = ProductionShapedProfile();
        var calculator = new FramingVisibilityCalculator();
        var lenses = new LensCalculator();
        var weather = new WeatherResult(DataState.Loading, null, "Offline");
        var rows = new List<object>();
        foreach (var focal in new[] { 14d, 24d, 50d })
        foreach (var bearings in new[] { "fractional", "whole-degree" })
        {
            var fov = lenses.Calculate(new LensConfiguration(SensorPreset.FullFrame, 36, 24, focal));
            var frame = new CameraTerrainFrame(5, fov.VerticalDegrees, 5);
            double Bearing(int index) => bearings == "fractional" ? 100 + index * 0.37 : 100 + index;
            for (var index = 0; index < 40; index++)
                calculator.Calculate(weather, terrain, 20, Bearing(index), fov.HorizontalDegrees,
                    verticalFovDegrees: fov.VerticalDegrees, cameraFrame: frame);
            var times = new List<double>();
            var bytes = new List<double>();
            var gc = Collections();
            var pause = GC.GetTotalPauseDuration();
            for (var index = 0; index < 240; index++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                calculator.Calculate(weather, terrain, 20, Bearing(index + 40), fov.HorizontalDegrees,
                    verticalFovDegrees: fov.VerticalDegrees, cameraFrame: frame);
                timer.Stop();
                times.Add(timer.Elapsed.TotalMilliseconds);
                bytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            }
            rows.Add(new
            {
                focalLengthMillimetres = focal, bearings, horizontalFovDegrees = fov.HorizontalDegrees,
                verticalFovDegrees = fov.VerticalDegrees, evaluations = times.Count,
                milliseconds = Summary(times), allocatedBytes = Summary(bytes),
                collections = Delta(gc), gcPauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                over16Point7Milliseconds = times.Count(time => time > 1000d / 60)
            });
        }
        Write("pf03-camera-framing", new { terrain = "360 bearings x 437 radial samples (production shape)", rows });
    }

    // PF-04: the core snapshot's astronomy, the Timeline and a single scrub position.
    [Fact]
    public async Task Astronomy()
    {
        if (string.IsNullOrWhiteSpace(Output)) return;
        var catalogue = new OpenNgcTargetCatalogue();
        var zones = new TimeZoneResolver();
        var astronomy = new AstronomyEngineService(zones);
        var planning = new PlanningService(catalogue, astronomy, new LensCalculator(), null!, null!, zones);
        var deepSky = catalogue.Targets.Where(target => !target.IsSun && !target.IsMoon && target.HasEquatorialCoordinates)
            .Take(6).Select(target => target.Id).ToArray();
        var rows = new List<object>();
        foreach (var (place, observer, zone) in new[]
                 {
                     ("London 51.5N", new GeoCoordinate(51.5074, -0.1278, 15), "Europe/London"),
                     ("Tromso 69.6N", new GeoCoordinate(69.6492, 18.9553, 10), "Europe/Oslo")
                 })
        foreach (var workload in new[] { "2 targets", "8 targets", "8 targets + Timeline" })
        {
            var ids = workload == "2 targets" ? new[] { "sun", "moon" } : new[] { "sun", "moon" }.Concat(deepSky).ToArray();
            var session = new PlanningSession(observer, Instant.FromUtc(2026, 10, 17, 21, 0), zone, ids[^1],
                LensConfiguration.ForPreset(SensorPreset.FullFrame),
                VisibleObjects: ids.Select((id, order) => new CelestialObjectSelection(id, true, order)).ToArray(),
                EnableTerrainCalculations: false);
            var timeline = workload.EndsWith("Timeline", StringComparison.Ordinal);
            async Task<(double Caller, double Total)> Run()
            {
                var timer = Stopwatch.StartNew();
                var core = planning.CalculateCoreSnapshotAsync(session, CancellationToken.None);
                Task? paths = timeline ? planning.CalculateTimelineAsync(session, session.Instant, CancellationToken.None) : null;
                Task? events = timeline ? planning.CalculateTimelineEventsAsync(session, session.Instant, CancellationToken.None) : null;
                var caller = timer.Elapsed.TotalMilliseconds;
                await core;
                if (paths is not null) await paths;
                if (events is not null) await events;
                return (caller, timer.Elapsed.TotalMilliseconds);
            }
            for (var warm = 0; warm < 3; warm++) await Run();
            var callers = new List<double>();
            var totals = new List<double>();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(true);
            const int runs = 15;
            for (var run = 0; run < runs; run++)
            {
                var (caller, total) = await Run();
                callers.Add(caller);
                totals.Add(total);
            }
            rows.Add(new
            {
                place, workload, runs,
                callerThreadMilliseconds = Summary(callers), wallMilliseconds = Summary(totals),
                cpuMillisecondsPerRun = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / runs,
                allocatedBytesPerRun = (GC.GetTotalAllocatedBytes(true) - allocated) / runs
            });
        }

        // A single date/time scrub position (CalculateCurrent), by target kind.
        var scrub = new List<object>();
        foreach (var id in new[] { "sun", "moon", deepSky[0] })
        {
            var session = PlanningSession.Default(Instant.FromUtc(2026, 10, 17, 21, 0), "Europe/London") with { TargetId = id };
            for (var warm = 0; warm < 5; warm++) planning.CalculateCurrent(session);
            var times = new List<double>();
            for (var step = 0; step < 30; step++)
            {
                var stepped = session with { Instant = session.Instant + Duration.FromMinutes(7 * step) };
                var timer = Stopwatch.StartNew();
                planning.CalculateCurrent(stepped);
                times.Add(timer.Elapsed.TotalMilliseconds);
            }
            scrub.Add(new { target = id, milliseconds = Summary(times) });
        }

        // Cancellation latency: 8 targets + Timeline cancelled 5 ms after starting.
        var cancellation = new List<double>();
        {
            var ids = new[] { "sun", "moon" }.Concat(deepSky).ToArray();
            var session = new PlanningSession(new GeoCoordinate(51.5074, -0.1278, 15), Instant.FromUtc(2026, 10, 17, 21, 0),
                "Europe/London", ids[^1], LensConfiguration.ForPreset(SensorPreset.FullFrame),
                VisibleObjects: ids.Select((id, order) => new CelestialObjectSelection(id, true, order)).ToArray(),
                EnableTerrainCalculations: false);
            for (var run = 0; run < 10; run++)
            {
                using var source = new CancellationTokenSource();
                var work = new List<Task>
                {
                    planning.CalculateCoreSnapshotAsync(session, source.Token),
                    planning.CalculateTimelineAsync(session, session.Instant, source.Token),
                    planning.CalculateTimelineEventsAsync(session, session.Instant, source.Token)
                };
                await Task.Delay(5);
                var timer = Stopwatch.StartNew();
                source.Cancel();
                try { await Task.WhenAll(work); } catch (OperationCanceledException) { }
                cancellation.Add(timer.Elapsed.TotalMilliseconds);
            }
        }
        Write("pf04-astronomy", new { rows, scrub, cancelAfter5MillisecondsToSettledMilliseconds = Summary(cancellation) });
    }

    // PF-05 and the catalogue part of PF-09.
    [Fact]
    public async Task CatalogueAndSearch()
    {
        if (string.IsNullOrWhiteSpace(Output)) return;
        var first = Stopwatch.StartNew();
        var catalogue = new OpenNgcTargetCatalogue();
        first.Stop();
        var constructions = new List<double>();
        for (var run = 0; run < 5; run++)
        {
            var timer = Stopwatch.StartNew();
            _ = new OpenNgcTargetCatalogue();
            constructions.Add(timer.Elapsed.TotalMilliseconds);
        }
        var search = new LocalTargetSearchService(catalogue);
        var firstSearch = Stopwatch.StartNew();
        await search.SearchAsync("andromeda", 50, CancellationToken.None);
        firstSearch.Stop();
        var queries = new List<object>();
        foreach (var query in new[] { "m3", "ngc", "ngc 7000", "orion", "andromeda", "pleiades", "veil", "zzqx" })
        {
            for (var warm = 0; warm < 3; warm++) await search.SearchAsync(query, 50, CancellationToken.None);
            var times = new List<double>();
            var bytes = new List<double>();
            var results = 0;
            var callers = new List<double>();
            for (var run = 0; run < 20; run++)
            {
                // Process-wide: the search may run on another thread.
                var allocated = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                var pending = search.SearchAsync(query, 50, CancellationToken.None);
                callers.Add(timer.Elapsed.TotalMilliseconds);
                results = (await pending).Count;
                times.Add(timer.Elapsed.TotalMilliseconds);
                bytes.Add(GC.GetTotalAllocatedBytes(true) - allocated);
            }
            queries.Add(new { query, results, milliseconds = Summary(times), callerThreadMilliseconds = Summary(callers), allocatedBytes = Summary(bytes) });
        }
        // Typing "andromeda" one key at a time, each query run to completion (the UI's worst case).
        var typing = Stopwatch.StartNew();
        var typed = "andromeda";
        for (var length = 1; length <= typed.Length; length++)
            await search.SearchAsync(typed[..length], 50, CancellationToken.None);
        typing.Stop();
        Write("pf05-pf09-catalogue", new
        {
            targets = catalogue.Targets.Count,
            firstConstructionMilliseconds = first.Elapsed.TotalMilliseconds,
            firstSearchMilliseconds = firstSearch.Elapsed.TotalMilliseconds,
            warmConstructionMilliseconds = Summary(constructions),
            queries, typingAndromedaTotalMilliseconds = typing.Elapsed.TotalMilliseconds
        });
    }

    // PF-01 and PF-02: environment builds behind rapid observer moves, on a deterministic Terrarium fixture
    // with simulated per-tile download latency (cold), no latency (warm) or a failing source (unavailable).
    [Fact]
    public async Task EnvironmentBuilds()
    {
        if (string.IsNullOrWhiteSpace(Output)) return;
        var directory = Path.Combine(Path.GetTempPath(), "Noctaxis-perf-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var png = Path.Combine(directory, "terrain.png");
            WriteTerrainFixture(png);
            var rows = new List<object>();
            foreach (var mode in new[] { "cold", "warm", "unavailable" })
            foreach (var workload in new[] { "1 move", "20 rapid moves" })
            for (var repeat = 0; repeat < 3; repeat++)
                rows.Add(await MeasureMovesAsync(png, mode, workload, repeat));

            // PF-02: decodes per full profile against unique tiles, first with nothing stored, then in a new
            // session (empty decoded cache) with every tile already stored on disk.
            var preload = new List<object>();
            foreach (var latitude in new[] { 0d, 51d, 65d })
            {
                var cache = new LatencyCache(png, TimeSpan.Zero, available: true);
                foreach (var disk in new[] { "nothing stored", "all tiles stored" })
                {
                    using var http = new HttpClient();
                    var terrain = new TerrariumTerrainProvider(http, cache, NullLogger<TerrariumTerrainProvider>.Instance);
                    var resolver = new TerrainSurfaceResolver(terrain, new FixedCover(), NullLogger<TerrainSurfaceResolver>.Instance);
                    var horizon = new HorizonService(resolver, NullLogger<HorizonService>.Instance);
                    var allocated = GC.GetTotalAllocatedBytes(true);
                    var timer = Stopwatch.StartNew();
                    await horizon.GetProfileAsync(new GeoCoordinate(latitude, 10.3), new TerrainProfileRequest(), CancellationToken.None);
                    timer.Stop();
                    var metrics = terrain.Metrics;
                    preload.Add(new
                    {
                        latitude, disk, wallMilliseconds = timer.Elapsed.TotalMilliseconds, uniqueTiles = cache.UniqueTiles,
                        tileDecodes = metrics.TileDecodes, decodeMilliseconds = metrics.DecodeMilliseconds,
                        tileLoads = metrics.TileLoads, decodedCacheHits = metrics.DecodedCacheHits,
                        allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated
                    });
                }
            }
            Write("pf01-pf02-environment", new
            {
                fixture = "Terrarium z12 PNG fixture (all tiles decode the same 256x256 pixels); cold = 40 ms simulated download per new tile, " +
                          "shared per tile like the real cache; moves 0.05 deg apart every 100 ms (beyond the 80 ms refresh debounce)",
                rows, preload
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    // PF-10: memory retained across visits to 10 locations; PF-07: the startup scan of a populated terrain cache.
    [Fact]
    public async Task RetentionAndCacheScan()
    {
        if (string.IsNullOrWhiteSpace(Output)) return;
        var directory = Path.Combine(Path.GetTempPath(), "Noctaxis-perf-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var png = Path.Combine(directory, "terrain.png");
            WriteTerrainFixture(png);
            using var http = new HttpClient();
            var cache = new LatencyCache(png, TimeSpan.Zero, available: true);
            var terrain = new TerrariumTerrainProvider(http, cache, NullLogger<TerrariumTerrainProvider>.Instance);
            var resolver = new TerrainSurfaceResolver(terrain, new FixedCover(), NullLogger<TerrainSurfaceResolver>.Instance);
            var horizon = new HorizonService(resolver, NullLogger<HorizonService>.Instance);
            var environment = new PlannerEnvironmentService(horizon, new UnavailableSettlementDataProvider(),
                NullLogger<PlannerEnvironmentService>.Instance);
            var visits = new List<object>();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var baseline = GC.GetTotalMemory(true);
            for (var visit = 0; visit < 10; visit++)
            {
                await environment.GetSnapshotAsync(new GeoCoordinate(52 + visit * 0.4, -1.5), CancellationToken.None);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                visits.Add(new
                {
                    visit = visit + 1,
                    managedHeapMegabytes = (GC.GetTotalMemory(true) - baseline) / 1048576d,
                    workingSetMegabytes = Process.GetCurrentProcess().WorkingSet64 / 1048576d,
                    horizonCompleted = horizon.CompletedCacheDiagnostics.Count,
                    horizonRetainedMegabytes = horizon.CompletedCacheDiagnostics.ApproximateRetainedBytes / 1048576d,
                    environmentCompleted = environment.CompletedCacheDiagnostics.Count,
                    environmentRetainedMegabytes = environment.CompletedCacheDiagnostics.ApproximateRetainedBytes / 1048576d
                });
            }

            // A cache tree the size of a real 1.2 GB one (6,600 files), with tiny files: the scan reads metadata only.
            var scanRoot = Path.Combine(directory, "scan");
            var paths = new ScanPaths(scanRoot);
            var tiles = Path.Combine(scanRoot, "EnvironmentalData", TerrariumTerrainProvider.SourceId, TerrariumTerrainProvider.SourceVersion, "z12");
            Directory.CreateDirectory(tiles);
            for (var index = 0; index < 6_600; index++)
                File.WriteAllBytes(Path.Combine(tiles, $"{2000 + index % 80}-{1300 + index / 80}.png"), new byte[16]);
            var scans = new List<double>();
            for (var run = 0; run < 5; run++)
            {
                var disk = new TerrainDiskCache(paths, NullLogger<TerrainDiskCache>.Instance);
                var timer = Stopwatch.StartNew();
                await disk.ConfigureAsync(TerrainDiskCache.DefaultLimitBytes, [new GeoCoordinate(53.4, -1.5)]);
                scans.Add(timer.Elapsed.TotalMilliseconds);
            }
            // PF-09: Lorenz atlas detection (2,016 tile files) against an installed atlas, read only. Set
            // NOCTAXIS_PERF_LORENZ_DATA to a Noctaxis data folder holding one (e.g. %APPDATA%\Noctaxis).
            object? lorenz = null;
            if (System.Environment.GetEnvironmentVariable("NOCTAXIS_PERF_LORENZ_DATA") is { Length: > 0 } lorenzData)
            {
                using var lorenzHttp = new HttpClient();
                var detections = new List<double>();
                var available = false;
                for (var run = 0; run < 5; run++)
                {
                    var installation = new Noctaxis.Core.LightPollution.LorenzInstallation(new ScanPaths(lorenzData), lorenzHttp);
                    var timer = Stopwatch.StartNew();
                    await installation.DetectAsync();
                    detections.Add(timer.Elapsed.TotalMilliseconds);
                    available = installation.IsAvailable;
                }
                lorenz = new { available, detectMilliseconds = Summary(detections) };
            }
            Write("pf07-pf10-retention-scan", new
            {
                visits, lorenz,
                configureWith6600FilesMilliseconds = Summary(scans),
                note = "Scan timings are warm file-system metadata; a cold first launch after reboot reads the directory from disk."
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ScanPaths(string root) : Noctaxis.Core.Persistence.IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => root;
    }

    private static async Task<object> MeasureMovesAsync(string png, string mode, string workload, int repeat)
    {
        using var http = new HttpClient();
        var cache = new LatencyCache(png, mode == "cold" ? TimeSpan.FromMilliseconds(40) : TimeSpan.Zero, mode != "unavailable");
        var terrain = new TerrariumTerrainProvider(http, cache, NullLogger<TerrariumTerrainProvider>.Instance,
            new TerrariumTerrainOptions(FailureRetryDelay: TimeSpan.Zero));
        var resolver = new TerrainSurfaceResolver(terrain, new FixedCover(), NullLogger<TerrainSurfaceResolver>.Instance);
        var horizon = new HorizonService(resolver, NullLogger<HorizonService>.Instance);
        var environment = new PlannerEnvironmentService(horizon, new UnavailableSettlementDataProvider(),
            NullLogger<PlannerEnvironmentService>.Instance);
        if (mode == "warm")
        {
            // Warm the decoded tiles the moves will need, then forget the completed profiles.
            for (var move = 0; move < 20; move++)
                await horizon.GetProfileAsync(Observer(move), new TerrainProfileRequest(), CancellationToken.None);
            horizon.InvalidateCache();
        }
        var moves = workload == "1 move" ? 1 : 20;
        var peakBuilds = 0;
        using var stopSampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stopSampling.IsCancellationRequested)
            {
                peakBuilds = Math.Max(peakBuilds, environment.ActiveBuildCount);
                try { await Task.Delay(2, stopSampling.Token); } catch (OperationCanceledException) { }
            }
        });
        var samplesBefore = terrain.Metrics.Samples;
        var decodesBefore = terrain.Metrics.TileDecodes;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var gc = Collections();
        var total = Stopwatch.StartNew();
        CancellationTokenSource? previous = null;
        Task<PlannerEnvironmentSnapshot>? last = null;
        double finalRequestedAt = 0;
        for (var move = 0; move < moves; move++)
        {
            previous?.Cancel();
            previous = new CancellationTokenSource();
            finalRequestedAt = total.Elapsed.TotalMilliseconds;
            last = environment.GetSnapshotAsync(Observer(move), new TerrainProfileRequest(), previous.Token);
            if (move < moves - 1) await Task.Delay(100);
        }
        var snapshot = await last!;
        var finalReady = total.Elapsed.TotalMilliseconds - finalRequestedAt;
        // Quiescence: no environment build left, and no terrain sampling for 300 ms.
        var quiet = Stopwatch.StartNew();
        var lastSamples = terrain.Metrics.Samples;
        while (quiet.Elapsed < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(300);
            var now = terrain.Metrics.Samples;
            if (environment.ActiveBuildCount == 0 && now == lastSamples) break;
            lastSamples = now;
        }
        var settled = total.Elapsed.TotalMilliseconds - 300;
        stopSampling.Cancel();
        await sampler;
        previous?.Dispose();
        return new
        {
            mode, workload, repeat,
            finalObserverReadyMilliseconds = finalReady,
            finalHorizonState = snapshot.HorizonProfile.HorizonState.ToString(),
            allWorkSettledMilliseconds = settled,
            peakActiveEnvironmentBuilds = peakBuilds,
            peakConcurrentDownloads = cache.PeakConcurrent,
            terrainSamples = terrain.Metrics.Samples - samplesBefore,
            tileDecodes = terrain.Metrics.TileDecodes - decodesBefore,
            cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            collections = Delta(gc)
        };
    }

    private static GeoCoordinate Observer(int move) => new(53.40 + move * 0.05, -1.50);

    internal static TerrainHorizonProfile ProductionShapedProfile()
    {
        const int bearings = 360, radial = 437;
        var distances = new double[radial];
        for (var index = 0; index < radial; index++)
            distances[index] = 30 + 50_000 * Math.Pow((index + 1d) / radial, 1.6);
        var samples = new TerrainHorizonSample[bearings];
        for (var bearing = 0; bearing < bearings; bearing++)
        {
            var line = new TerrainSightlineSample[radial];
            double best = double.NegativeInfinity, bestDistance = 0;
            for (var index = 0; index < radial; index++)
            {
                var distance = distances[index];
                var ground = 120 + 260 * Math.Sin(distance / 3_100 + bearing * 0.07) + 180 * Math.Cos(distance / 9_700 - bearing * 0.031);
                var drop = HorizonService.CurvatureDrop(distance);
                var slope = HorizonService.ElevationSlope(ground, 140, drop, 1 / distance);
                line[index] = TerrainSightlineSample.FromSlope(distance, ground, drop, slope);
                var angle = HorizonService.SlopeToElevationDegrees(slope);
                if (angle > best) { best = angle; bestDistance = distance; }
            }
            samples[bearing] = new TerrainHorizonSample(bearing, best, bestDistance, null, line);
        }
        return new TerrainHorizonProfile(new GeoCoordinate(53.4, -1.5), samples, true, "fixture",
            Instant.FromUtc(2026, 10, 17, 0, 0), HorizonState: EnvironmentalDataState.Available,
            MaximumAnalysisDistanceMetres: 50_000);
    }

    private static void WriteTerrainFixture(string path)
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
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static int[] Collections() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private static int[] Delta(int[] before) => Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - before[i]).ToArray();

    private static object Summary(List<double> values)
    {
        var sorted = values.Order().ToArray();
        double At(double fraction) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
        var mean = sorted.Average();
        return new
        {
            median = At(.5), p95 = At(.95), min = sorted[0], max = sorted[^1], mean,
            stdev = Math.Sqrt(sorted.Sum(value => (value - mean) * (value - mean)) / sorted.Length)
        };
    }

    private static void Write(string name, object value)
    {
        Directory.CreateDirectory(Output!);
        File.WriteAllText(Path.Combine(Output!, $"{name}.{Label}.json"),
            JsonSerializer.Serialize(new { label = Label, machine = new { System.Environment.ProcessorCount, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription }, data = value },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FixedCover : ILandCoverProvider
    {
        public Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<LandCoverClass>(EnvironmentalDataState.Available,
                LandCoverClass.PermanentWater, "fixture", "1", "Synthetic classification"));

        public Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var values = new LandCoverClass?[coordinates.Count];
            Array.Fill(values, LandCoverClass.PermanentWater);
            return Task.FromResult(new LandCoverBatchResult(EnvironmentalDataState.Available, values, "fixture", "1", "Synthetic classification"));
        }
    }

    // Every tile is the same PNG. The first request for a tile waits the simulated download, shared by every
    // caller of that tile and independent of their cancellation (as the real cache's single flight is).
    private sealed class LatencyCache(string path, TimeSpan latency, bool available) : IEnvironmentalTileCache
    {
        private readonly ConcurrentDictionary<string, Lazy<Task>> _downloads = new();
        private int _concurrent;
        public int PeakConcurrent;
        public int UniqueTiles => _downloads.Count;
        public string RootDirectory => Path.GetDirectoryName(path)!;

        // Stored once its simulated download has finished (and the source is available).
        public bool Contains(EnvironmentalTileDescriptor descriptor) => available &&
            _downloads.TryGetValue(descriptor.TileId, out var download) && download.IsValueCreated &&
            download.Value.IsCompletedSuccessfully;

        public Task<EnvironmentalCacheResult> GetOrCreateAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<byte[]?>> acquire, Func<string, bool> validate, CancellationToken token) =>
            throw new NotSupportedException();

        public async Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire, Func<string, bool> validate,
            CancellationToken token)
        {
            var download = _downloads.GetOrAdd(descriptor.TileId, _ => new Lazy<Task>(async () =>
            {
                var now = Interlocked.Increment(ref _concurrent);
                int peak;
                while (now > (peak = Volatile.Read(ref PeakConcurrent)) &&
                       Interlocked.CompareExchange(ref PeakConcurrent, now, peak) != peak) { }
                try { if (latency > TimeSpan.Zero) await Task.Delay(latency).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _concurrent); }
            }));
            await download.Value.WaitAsync(token).ConfigureAwait(false);
            return available
                ? new EnvironmentalCacheResult(EnvironmentalDataState.Cached, path, true, "Offline PNG fixture")
                : new EnvironmentalCacheResult(EnvironmentalDataState.SourceUnavailable, null, false, "Fixture source unavailable");
        }
    }
}
