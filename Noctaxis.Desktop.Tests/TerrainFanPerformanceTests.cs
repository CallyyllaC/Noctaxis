using System.Diagnostics;
using System.Text.Json;
using Avalonia.Media;
using Mapsui;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using SkiaSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Environment;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainFanPerformanceTests
{
    [Fact]
    public void ExportOptionalPerformanceMeasurements()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_FAN_PERFORMANCE");
        if (string.IsNullOrEmpty(output)) return;
        var profile = TerrainFanContrastTests.ProfileByBearing(b =>
            Enumerable.Range(1, 24).Select(i => i * .35 * (1 + .2 * Math.Sin(b * .3))).ToArray());
        Run(profile, output);
        Run(profile, Path.ChangeExtension(output, ".baseline.json"), baseline: true);
    }

    [Fact]
    public async Task ExportOptionalCachedPerformanceMeasurements()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_FAN_CACHED_PERFORMANCE");
        if (string.IsNullOrEmpty(output)) return;
        File.AppendAllText(output + ".progress", $"{DateTime.UtcNow:O} acquiring cached Blaenau Ffestiniog profile\n");
        using var http = new HttpClient();
        var cache = new TerrainAvailabilityTests.ReadOnlyCache(Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Noctaxis", "EnvironmentalData"));
        var provider = new TerrariumTerrainProvider(http, cache, NullLogger<TerrariumTerrainProvider>.Instance);
        var cover = new WorldCoverLandCoverProvider(http, cache, NullLogger<WorldCoverLandCoverProvider>.Instance);
        var resolver = new TerrainSurfaceResolver(provider, cover, NullLogger<TerrainSurfaceResolver>.Instance);
        var horizons = new HorizonService(resolver, NullLogger<HorizonService>.Instance);
        var acquisitionStart = Stopwatch.GetTimestamp();
        var profile = await horizons.GetProfileAsync(new(53.00562745652107, -3.9519223528039116), new(), default);
        var firstProfileMs = Stopwatch.GetElapsedTime(acquisitionStart).TotalMilliseconds;
        Assert.True(profile.HasTerrainCoverage);
        Assert.Equal(360, profile.CompletedBearingCount);
        File.AppendAllText(output + ".progress", $"{DateTime.UtcNow:O} complete profile; {profile.Samples.Sum(s => s.Sightline?.Count ?? 0)} sightline points\n");
        acquisitionStart = Stopwatch.GetTimestamp();
        var cached = await horizons.GetProfileAsync(profile.Observer, new(), default);
        var cachedProfileMs = Stopwatch.GetElapsedTime(acquisitionStart).TotalMilliseconds;
        Assert.Same(profile, cached);
        acquisitionStart = Stopwatch.GetTimestamp();
        var moved = await horizons.GetProfileAsync(new(profile.Observer.Latitude + .005, profile.Observer.Longitude), new(), default);
        var movedProfileMs = Stopwatch.GetElapsedTime(acquisitionStart).TotalMilliseconds;
        acquisitionStart = Stopwatch.GetTimestamp();
        var returned = await horizons.GetProfileAsync(profile.Observer, new(), default);
        var switchBackMs = Stopwatch.GetElapsedTime(acquisitionStart).TotalMilliseconds;
        Assert.Same(profile, returned);
        File.WriteAllText(output + ".acquisition.json", JsonSerializer.Serialize(new {
            firstProfileMs, cachedProfileMs, movedProfileMs, switchBackMs, moved.CompletedBearingCount,
            moved.HasTerrainCoverage, source = "Read-only existing DEM cache; cold and warm production horizon service, no network acquisition" },
            new JsonSerializerOptions { WriteIndented = true }));
        Run(profile, output);
        Run(profile, Path.ChangeExtension(output, ".baseline.json"), baseline: true);
    }

    private static void Run(TerrainHorizonProfile profile, string output, bool baseline = false)
    {
        var records = new List<object>();
        foreach (var fov in new[] { (H: 72d, V: 50d), (H: 24d, V: 20d), (H: 4.5d, V: 3d) })
        {
            var calculator = new FramingVisibilityCalculator();
            FramingVisibilityAssessment Frame(double bearing, double pitch) => calculator.Calculate(
                new(DataState.Loading, null, "Offline"), profile, 20, bearing, fov.H,
                cameraFrame: new(pitch, fov.V, 5));
            var assessment = Frame(90, 2);
            var sector = new GeoSector(profile.Observer, 90, fov.H, 500000);
            var fan = PlanTerrainFan.Build(sector, profile, assessment.CameraDepth!);
            var verifiedTones = TerrainFanLocalContrast.Prepare(fan, out _);
            for (var p = 0; p < fan.Patches.Length; p++)
                Assert.Equal(TerrainFanLocalContrast.Enhance(fan, p), verifiedTones[p], 14);
            var coordinator = new EnvironmentalOverlayStateCoordinator();
            var key = EnvironmentalOverlayStateFactory.CreateProfileKey(profile, 1);
            var state = coordinator.Update(profile.Observer, sector, assessment, key, profile);
            using var bitmap = new SKBitmap(800, 600);
            using var canvas = new SKCanvas(bitmap);
            using var renderer = baseline
                ? new SkiaEnvironmentalOverlayResources(new(), (f, i, _) => TerrainFanLocalContrast.Enhance(f, i))
                : new SkiaEnvironmentalOverlayResources(new());
            var centre = WebMercator.FromWgs84(profile.Observer);
            EnvironmentalOverlayFrame Projection(int i) => EnvironmentalOverlayMath.CreateFrame(
                new Viewport(centre.X + i * 50, centre.Y, 100 + i, 0, 800, 600), 800, 600,
                EnvironmentalRenderParameters.Default);
            var projection = Projection(0);
            using var baseRenderer = new SkiaEnvironmentalOverlayResources(new());
            object Measure(string stage, Action<int> action)
            {
                const int iterations = 10;
                File.AppendAllText(output + ".progress", $"{DateTime.UtcNow:O} FoV={fov.H} {stage}\n");
                for (var i = 0; i < 2; i++) action(i);
                var times = new double[iterations];
                var tonePreparations = renderer.TonePreparationCount;
                var rayEvaluations = coordinator.PlanSampleEvaluations;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < iterations; i++)
                {
                    var start = Stopwatch.GetTimestamp(); action(i);
                    times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                return new { stage, iterations, elapsedMs = times.Sum(), averageMs = times.Average(),
                    medianMs = times.Order().ElementAt(iterations / 2), worstMs = times.Max(), bytesPerUpdate = allocated / iterations,
                    tonePreparations = renderer.TonePreparationCount - tonePreparations,
                    fanRayEvaluations = coordinator.PlanSampleEvaluations - rayEvaluations };
            }
            var stages = new List<object>
            {
                Measure("cameraDepthBearing", i => Frame(90 + i * .1, 2)),
                Measure("cameraDepthPitch", i => Frame(90, 2 + i * .02)),
                Measure("fovThroughSkia", i => {
                    var h = fov.H + i * .1;
                    var derived = calculator.Calculate(new(DataState.Loading, null, "Offline"), profile, 20, 90, h,
                        cameraFrame: new(2, fov.V + i * .1, 5));
                    var next = coordinator.Update(profile.Observer, sector with { HorizontalFovDegrees = h }, derived, key, profile);
                    renderer.Draw(canvas, next, projection, Colors.LimeGreen);
                }),
                Measure("fan", _ => PlanTerrainFan.Build(sector, profile, assessment.CameraDepth!)),
                Measure("identicalState", i => coordinator.Update(profile.Observer, sector, assessment, key, profile)),
                Measure("localContrast", _ => { for (var p = 0; p < fan.Patches.Length; p++) TerrainFanLocalContrast.Enhance(fan, p); }),
                Measure("preparedLocalContrast", i => TerrainFanLocalContrast.Prepare(fan, out _)),
                Measure("projectionAndPaths", i => {
                    var frame = Projection(i);
                    foreach (var patch in fan.Patches) {
                        using var path = new SKPath();
                        for (var p = 0; p < patch.Corners.Length; p++) {
                            var point = EnvironmentalOverlayMath.GeographicToScreen(frame, patch.Corners[p]);
                            if (p == 0) path.MoveTo((float)point.X, (float)point.Y); else path.LineTo((float)point.X, (float)point.Y);
                        }
                        path.Close();
                    }
                }),
                Measure("baseShaderDraw", i => baseRenderer.Draw(canvas, state with { TerrainFan = null }, projection, Colors.LimeGreen)),
                Measure("staticDraw", _ => renderer.Draw(canvas, state, projection, Colors.LimeGreen)),
                Measure("panZoomDraw", i => renderer.Draw(canvas, state, Projection(i), Colors.LimeGreen)),
                Measure("bearingThroughSkia", i => {
                    var bearing = 90 + i * .1;
                    var next = coordinator.Update(profile.Observer, sector with { CentreBearingDegrees = bearing }, Frame(bearing, 2), key, profile);
                    renderer.Draw(canvas, next, projection, Colors.LimeGreen);
                }),
                Measure("pitchThroughSkia", i => {
                    var next = coordinator.Update(profile.Observer, sector, Frame(90, 2 + i * .02), key, profile);
                    renderer.Draw(canvas, next, projection, Colors.LimeGreen);
                })
            };
            TerrainFanLocalContrast.Prepare(fan, out var indexedComparisons);
            records.Add(new { fov.H, fov.V, rays = fan.Rays.Length, transitions = fan.Rays.Sum(r => r.Bands.Length),
                patches = fan.Patches.Length, points = fan.Patches.Sum(p => p.Corners.Length),
                neighbourComparisons = (long)fan.Patches.Length * (fan.Patches.Length - 1), indexedComparisons, stages });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { profile.Observer, profile.Status, profile.CompletedBearingCount,
            baseline, source = "CPU Skia 800x600; no native presentation; managed current-thread allocations exclude native Skia allocations",
#if DEBUG
            configuration = "Debug",
#else
            configuration = "Release",
#endif
            records }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
