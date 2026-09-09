using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using NodaTime;
using System.Diagnostics;
using System.Text.Json;

namespace Noctaxis.Core.Tests;

public sealed class CameraTerrainFrameTests
{
    internal static TerrainHorizonProfile Profile(params (double Distance, double Angle)[] points) =>
        new(new GeoCoordinate(53, -1), Enumerable.Range(0, 360).Select(b =>
            new TerrainHorizonSample(b, points.Max(p => p.Angle), null,
                Sightline: points.Select(p => new TerrainSightlineSample(p.Distance, 10, 0, p.Angle)).ToArray())).ToArray(),
            true, "Deterministic", Instant.FromUtc(2026, 1, 1, 0, 0));

    [Theory]
    [InlineData(-6, 0)] [InlineData(0, .25)] [InlineData(3, .4)]
    [InlineData(5, .5)] [InlineData(10, .75)] [InlineData(15, 1)]
    public void CoverageAndThresholdAreNumericallyDefined(double horizon, double raw)
    {
        var sample = new CameraTerrainFrame(5, 20, 5).Scan(Profile((1000, horizon)), 0);
        Assert.Equal(raw, sample.RawCoverage, 10);
        Assert.Equal(raw <= .05 ? 0 : (raw - .05) / .95, sample.EffectiveCoverage, 10);
        Assert.Equal(-4, sample.ThresholdAltitudeDegrees);
    }

    [Theory]
    [InlineData(.5)] [InlineData(1)]
    public void TinyTerrainAndExactThresholdAreSuppressed(double horizon)
    {
        var result = new CameraTerrainFrame(10, 20, 5).Scan(Profile((1000, horizon)), 0);
        Assert.True(result.RawCoverage > 0);
        Assert.Equal(0, result.EffectiveCoverage); Assert.False(result.IsObstructed);
        Assert.Null(result.FirstObstructionDistanceMetres);
    }

    [Fact]
    public void FrontierUsesFirstMeaningfulSampleInsteadOfHorizontalHit()
    {
        var terrain = Profile((500, 1), (1000, 3), (2000, 6.5));
        Assert.Equal(500, terrain.TerrainObstructionAt(0).EffectiveFirstObstructionDistanceMetres);
        var result = new CameraTerrainFrame(15, 20, 5).Scan(terrain, .35);
        Assert.Equal(6, result.ThresholdAltitudeDegrees);
        Assert.Equal(2000, result.FirstObstructionDistanceMetres);
    }

    [Fact]
    public void DepthStoresFirstNearAndFarIntersectionsWithSkyAbove()
    {
        var terrain = Profile((100, -2), (1000, 3), (10000, 8));
        var calculator = new FramingVisibilityCalculator();
        FramingVisibilityAssessment Calculate(double pitch, double threshold) => calculator.Calculate(
            new(DataState.Loading, null, "Offline"), terrain, -1, .35, 70,
            verticalFovDegrees: 20, cameraFrame: new(pitch, 20, threshold));
        var result = Calculate(0, 5);
        var depth = result.CameraDepth!;
        Assert.Equal(71, depth.Width); Assert.Equal(71 * 72, depth.DistancesMetres.Length);
        Assert.Equal(0, depth.DistanceAt(0, 0));
        Assert.Equal(10000, depth.DistanceAt(0, 15));
        Assert.Equal(1000, depth.DistanceAt(0, 30));
        Assert.Equal(100, depth.DistanceAt(0, 71));
        Assert.Equal(depth.DistancesMetres.ToArray(), Calculate(0, 50).CameraDepth!.DistancesMetres.ToArray());
        var above = Calculate(30, 5);
        Assert.All(above.CameraDepth!.DistancesMetres, d => Assert.Equal(0, d));
        Assert.All(above.EffectiveTerrainObstructions, s => Assert.False(s.IsObstructed));
        Assert.True(above.IsTargetTerrainObstructed);
        Assert.All(Calculate(-30, 5).CameraDepth!.DistancesMetres, d => Assert.True(d > 0));
        var visibleTarget = calculator.Calculate(new(DataState.Loading, null, "Offline"), terrain, 40, 0, 70,
            cameraFrame: new(0, 20, 5));
        Assert.False(visibleTarget.IsTargetTerrainObstructed);
        Assert.Contains(visibleTarget.EffectiveTerrainObstructions, s => s.IsObstructed);
    }

    [Fact]
    public void ExistingInterpolatedSightlinesDriveBothDepthAndSilhouette()
    {
        var terrain = Profile((1000, 0));
        terrain = terrain with { Samples = terrain.Samples.Select((s, i) => s with
            { Sightline = [new TerrainSightlineSample(1000, 10, 0, i % 2 == 0 ? 0 : 10)] }).ToArray() };
        var frame = new CameraTerrainFrame(5, 20, 0);
        var atZero = frame.Scan(terrain, 0); var atOne = frame.Scan(terrain, 1);
        var middle = frame.Scan(terrain, .5);
        Assert.InRange(middle.RawCoverage, atZero.RawCoverage, atOne.RawCoverage);
        Assert.Equal(terrain.SightlineAt(.5)[0].TerrainElevationAngleDegrees, middle.FrameTerrainHorizonDegrees);
        Assert.Equal(360, terrain.Samples.Count);
    }

    [Fact]
    public void CameraAwareHitNoHitTransitionsRemainRefined()
    {
        var terrain = Profile((1000, -20));
        terrain = terrain with { Samples = terrain.Samples.Select((s, b) => s with
            { Sightline = [new TerrainSightlineSample(1000, 10, 0, b == 0 ? 10 : -20)] }).ToArray() };
        var result = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"), terrain,
            30, 0, 4, cameraFrame: new(0, 20, 5));
        Assert.False(result.EffectiveTerrainObstructions[0].IsObstructed);
        Assert.False(result.EffectiveTerrainObstructions[^1].IsObstructed);
        Assert.Contains(result.EffectiveTerrainObstructions, s => s.BearingDegrees == 0 && s.IsObstructed);
        var transitions = result.EffectiveTerrainObstructions.Zip(result.EffectiveTerrainObstructions.Skip(1))
            .Where(pair => pair.First.IsObstructed != pair.Second.IsObstructed).ToArray();
        Assert.Equal(2, transitions.Length);
        Assert.All(transitions, pair => Assert.InRange(Angles.NormaliseDegrees(pair.Second.BearingDegrees - pair.First.BearingDegrees), 0, .125));
        Assert.True(result.CameraDepth!.DistanceAt(2, 36) > 0);
        Assert.Equal(0, result.CameraDepth.DistanceAt(0, 36));
    }

    [Fact]
    public void MeasurePassCDerivedWork()
    {
        var path = System.Environment.GetEnvironmentVariable("NOCTAXIS_PASS_C_OUTPUT");
        if (string.IsNullOrEmpty(path)) return;
        var terrain = DerivedCameraCleanupTests.Fixture();
        var calculator = new FramingVisibilityCalculator();
        var rows = new List<object>();
        foreach (var bearing in new[] { 90d, 90.35 })
        foreach (var mode in new[] { "legacy", "coverage", "coverage-and-depth" })
        {
            var options = new CameraTerrainFrame(0, 20, 5);
            var times = new List<double>(); var allocations = new List<long>();
            for (var run = 0; run < 30; run++)
            {
                var start = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                for (var iteration = 0; iteration < 20; iteration++)
                    if (mode == "coverage")
                        for (var i = 0; i < 71; i++) options.Scan(terrain, bearing - 35 + i);
                    else calculator.Calculate(new(DataState.Loading, null, "Offline"), terrain, 5, bearing, 70,
                        cameraFrame: mode == "legacy" ? null : options);
                timer.Stop(); var bytes = GC.GetAllocatedBytesForCurrentThread() - start;
                if (run >= 23) { times.Add(timer.Elapsed.TotalMilliseconds / 20); allocations.Add(bytes / 20); }
            }
            times.Sort(); allocations.Sort();
            rows.Add(new { bearing, mode, milliseconds = times[3], allocatedBytes = allocations[3],
                bearings = 71, radialSamplesScanned = 71 * 655, depthRows = mode == "coverage-and-depth" ? 72 : 0, additionalProviderCalls = 0 });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}

