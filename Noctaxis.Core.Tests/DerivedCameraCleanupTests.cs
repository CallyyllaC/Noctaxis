using System.Diagnostics;
using System.Text.Json;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed class DerivedCameraCleanupTests
{
    [Theory]
    [InlineData(10, 70, 71)]
    [InlineData(25, 70, 71)]
    [InlineData(45, 70, 71)]
    [InlineData(45, 3.5, 5)]
    public void LegacyDetailCannotChangeDirectFirstHitSampling(double oldDetail, double fov, int count)
    {
        var terrain = Fixture();
        var sightlines = terrain.Samples.Select(s => s.Sightline).ToArray();
        var settings = JsonSerializer.Deserialize<CameraFramingSettings>(
            $"{{\"TerrainCastAngularDetailDegrees\":{oldDetail}}}")!;
        Assert.Equal(1, settings.Normalised().TerrainCastAngularDetailDegrees);
        var result = new FramingVisibilityCalculator().Calculate(
            new WeatherResult(DataState.Loading, null, "Offline"), terrain, 5, 90.35, fov, oldDetail);
        var legacyRays = new LocalHorizonCalculator().GetConeProfiles(terrain, 90.35, fov, 1);
        Assert.Equal(count, result.EffectiveTerrainObstructions.Count);
        for (var i = 0; i < count; i++)
        {
            var actual = result.EffectiveTerrainObstructions[i];
            Assert.Equal(legacyRays[i].BearingDegrees, actual.BearingDegrees);
            Assert.Equal(terrain.TerrainObstructionAt(legacyRays[i].BearingDegrees)
                .EffectiveFirstObstructionDistanceMetres, actual.FirstObstructionDistanceMetres);
        }
        Assert.Equal(360, terrain.Samples.Count);
        for (var i = 0; i < 360; i++)
        {
            Assert.Same(sightlines[i], terrain.Samples[i].Sightline);
            Assert.Equal(655, terrain.Samples[i].Sightline!.Count);
        }
    }

    internal static TerrainHorizonProfile Fixture()
    {
        var samples = Enumerable.Range(0, 360).Select(bearing => new TerrainHorizonSample(bearing, 4, null,
            Sightline: Enumerable.Range(1, 655).Select(i => new TerrainSightlineSample(i * 75d,
                i < 20 + bearing % 7 ? -2 : 10, 0, i < 20 + bearing % 7 ? -.1 : .1)).ToArray())).ToArray();
        return new TerrainHorizonProfile(new GeoCoordinate(53, -1), samples, true, "Offline",
            Instant.FromUtc(2026, 1, 1, 0, 0));
    }

    [Fact]
    public void MeasureDerivedCamera()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_DERIVED_CAMERA_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        var terrain = Fixture();
        var calculator = new FramingVisibilityCalculator();
        var weather = new WeatherResult(DataState.Loading, null, "Offline");
        var rows = new List<object>();
        foreach (var bearing in new[] { 90d, 90.35 })
        {
            var times = new List<double>();
            var bytes = new List<long>();
            for (var run = 0; run < 9; run++)
            {
                var start = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                for (var i = 0; i < 20; i++) calculator.Calculate(weather, terrain, 5, bearing, 70);
                clock.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
                if (run < 2) continue;
                times.Add(clock.Elapsed.TotalMilliseconds / 20);
                bytes.Add(allocated / 20);
            }
            times.Sort(); bytes.Sort();
            rows.Add(new { bearing, milliseconds = times[3], allocatedBytes = bytes[3], providerCalls = 0 });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
