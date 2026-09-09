using System.Diagnostics;
using System.Text.Json;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>Opt-in deterministic Pass B comparison of camera-cone angular density.</summary>
public sealed class TerrainAngularDensityBenchmarkTests
{
    [Fact]
    public void DensityVariantsAreOptInAndPreserveRadialPolicy()
    {
        var calculator = new LocalHorizonCalculator();
        var terrain = Fixture("NarrowRidge");
        var counts = new[] { 1, 2, 4, 8 }.Select(multiplier =>
            calculator.GetConeProfiles(terrain, 90, 70, 10d / multiplier).Count).ToArray();
        Assert.Equal(new[] { 8, 15, 29, 57 }, counts);
        Assert.All(terrain.Samples, sample => Assert.Equal(60, sample.Sightline!.Count));
        Assert.Equal(CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees, 1);
    }

    [Fact]
    public void CompareAngularDensityVariants()
    {
        var destination = System.Environment.GetEnvironmentVariable("NOCTAXIS_ANGULAR_BENCHMARK_OUTPUT");
        if (string.IsNullOrWhiteSpace(destination)) return;

        var calculator = new LocalHorizonCalculator();
        var rows = new List<object>();
        foreach (var scenario in new[] { "Flat", "Coastal", "Mountain", "NarrowRidge", "Quarry" })
        {
            var terrain = Fixture(scenario);
            var reference = calculator.GetConeProfiles(terrain, 90, 70, 1.25);
            var referenceHits = reference.Select(FirstHit).ToArray();
            foreach (var multiple in new[] { 1, 2, 4, 8 })
            {
                var detail = 10d / multiple;
                var timings = new List<double>();
                var allocations = new List<long>();
                IReadOnlyList<HorizonRayProfile>? result = null;
                for (var iteration = 0; iteration < 6; iteration++)
                {
                    GC.Collect();
                    var allocated = GC.GetTotalAllocatedBytes(true);
                    var watch = Stopwatch.StartNew();
                    result = calculator.GetConeProfiles(terrain, 90, 70, detail);
                    watch.Stop();
                    if (iteration > 0)
                    {
                        timings.Add(watch.Elapsed.TotalMilliseconds);
                        allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
                    }
                }
                var hits = result!.Select(FirstHit).ToArray();
                var errors = hits.Select((hit, i) => hit.HasValue && referenceHits[Math.Min(i, referenceHits.Length - 1)].HasValue
                    ? Math.Abs(hit.Value - referenceHits[Math.Min(i, referenceHits.Length - 1)]!.Value) : 0).ToArray();
                rows.Add(new
                {
                    scenario, density = multiple + "x", detailDegrees = detail,
                    bearings = result!.Count, radialSamples = terrain.Samples[0].Sightline!.Count,
                    medianMilliseconds = Median(timings), medianAllocatedBytes = MedianLong(allocations),
                    frontierVertices = result.Sum(ray => ray.Segments.Count),
                    differingHitSectors = hits.Zip(referenceHits).Count(pair => pair.First.HasValue != pair.Second.HasValue),
                    medianFirstHitErrorMetres = Median(errors)
                });
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.WriteAllText(destination, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static TerrainHorizonProfile Fixture(string scenario)
    {
        var samples = new TerrainHorizonSample[360];
        for (var bearing = 0; bearing < samples.Length; bearing++)
        {
            var sightline = new TerrainSightlineSample[60];
            for (var radial = 0; radial < sightline.Length; radial++)
            {
                var distance = (radial + 1) * 500d;
                var delta = Math.Abs(Angles.NormaliseDegrees(bearing - 90));
                var ridge = scenario switch
                {
                    "Flat" => 0,
                    "Coastal" => delta < 18 ? .002 : 0,
                    "Mountain" => .03 + .01 * Math.Sin(bearing * Angles.DegreesToRadians),
                    "NarrowRidge" => delta < 2.5 ? .06 : 0,
                    _ => delta < 4 ? (distance < 4_000 ? .08 : .01) : 0
                };
                sightline[radial] = TerrainSightlineSample.FromSlope(distance, ridge * distance, 0, ridge);
            }
            var horizon = sightline.Max(sample => sample.TerrainElevationAngleDegrees ?? 0);
            samples[bearing] = new TerrainHorizonSample(bearing, horizon,
                sightline[^1].DistanceMetres, Sightline: sightline);
        }
        return new TerrainHorizonProfile(new GeoCoordinate(53, -1), samples, true, scenario,
            Instant.FromUtc(2024, 1, 1, 0, 0), MaximumAnalysisDistanceMetres: 30_000);
    }

    private static double? FirstHit(HorizonRayProfile ray)
    {
        foreach (var segment in ray.Segments)
            if (segment.State == HorizonVisibilityState.TerrainOccluded) return segment.StartDistanceMetres;
        return null;
    }

    private static double Median(IReadOnlyList<double> values) => values.Count == 0 ? 0 : values.OrderBy(value => value).ElementAt(values.Count / 2);
    private static long MedianLong(IReadOnlyList<long> values) => values.Count == 0 ? 0 : values.OrderBy(value => value).ElementAt(values.Count / 2);
}
