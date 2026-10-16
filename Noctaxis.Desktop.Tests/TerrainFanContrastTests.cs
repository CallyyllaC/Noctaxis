using System.Text.Json;
using System.Text.Json.Nodes;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainFanContrastTests
{
    [Fact]
    public void SelectedCurveIsMonotonicBoundedAndSeparatesCommonTerrainAngles()
    {
        var previous = 0d;
        for (var angle = 0d; angle <= 90; angle += .01)
        {
            var strength = TerrainFanTone.Strength(angle);
            Assert.InRange(strength, previous, 1);
            previous = strength;
        }
        Assert.InRange(TerrainFanTone.Strength(2) - TerrainFanTone.Strength(1), .12, .13);
        Assert.InRange(TerrainFanTone.Strength(5) - TerrainFanTone.Strength(2), .29, .30);
        Assert.Equal(1, TerrainFanTone.Strength(10));
        Assert.Equal(1, TerrainFanTone.Strength(double.MaxValue));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void NonpositiveOrInvalidAnglesCannotCreateOrdinaryStrength(double angle) =>
        Assert.Equal(0, TerrainFanTone.Strength(angle));

    [Theory]
    [InlineData(72, 50)] [InlineData(24, 20)] [InlineData(4.5, 3)]
    public void SameVisibleRidgeKeepsItsToneAcrossPitchLensAndDistance(double horizontal, double vertical)
    {
        var profile = Profile([4]);
        var distant = profile with { Samples = profile.Samples.Select(s => s with
            { Sightline = s.Sightline!.Select(p => new TerrainSightlineSample(p.DistanceMetres * 10,
                p.TerrainElevationMetres, p.CurvatureDropMetres, p.TerrainElevationAngleDegrees)).ToArray() }).ToArray() };
        foreach (var pitch in new[] { 3d, 4, 5 })
        foreach (var terrain in new[] { profile, distant })
        {
            var fan = Fan(terrain, pitch, horizontal, vertical);
            Assert.NotEmpty(fan.Patches);
            Assert.All(fan.Patches, p =>
            {
                Assert.Equal(4, p.ApparentAltitudeDegrees, 8);
                Assert.Equal(Math.Pow(.4, .75), TerrainFanTone.Strength(p.ApparentAltitudeDegrees), 8);
            });
        }
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(.1)] [InlineData(1)] [InlineData(float.NaN)]
    public void BaseAndTerrainOpacityRemainBounded(float opacity)
    {
        var parameters = (EnvironmentalRenderParameters.Default with { ConeOpacity = opacity,
            TerrainTintOpacity = opacity }).Normalised();
        Assert.InRange(parameters.ConeOpacity, 0, .5f);
        Assert.InRange(parameters.TerrainTintOpacity + .25f, .4f, .8f);
        Assert.Equal(.1f, EnvironmentalRenderParameters.Default.ConeOpacity);
    }

    internal static TerrainHorizonProfile Profile(double[] angles, bool asymmetric = false) => new(
        new(53, -1), Enumerable.Range(0, 360).Select(b =>
        {
            var scale = asymmetric ? Math.Max(0, 1 - Math.Pow((b - 84) / 22d, 2)) : 1;
            var shift = 1 + .15 * Math.Sin(b * .25);
            var points = angles.Select((a, i) => new TerrainSightlineSample(
                (2000 + i * 8000) * shift, 10, 0, a * scale)).ToArray();
            return new TerrainHorizonSample(b, angles.Max() * scale, null, Sightline: points);
        }).ToArray(), true, "Synthetic contrast", Instant.FromUtc(2026, 1, 1, 0, 0));

    internal static TerrainHorizonProfile ProfileByBearing(Func<int, double[]> angles) => new(
        new(53, -1), Enumerable.Range(0, 360).Select(b =>
        {
            var values = angles(b);
            var points = values.Select((a, i) => new TerrainSightlineSample(
                2000 + i * 8000, 10, 0, a)).ToArray();
            return new TerrainHorizonSample(b, values.Max(), null, Sightline: points);
        }).ToArray(), true, "Synthetic contrast", Instant.FromUtc(2026, 1, 1, 0, 0));

    internal static PlanTerrainFan Fan(TerrainHorizonProfile profile, double pitch, double horizontal, double vertical)
    {
        var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            profile, 20, 90, horizontal, cameraFrame: new(pitch, vertical, 5));
        return PlanTerrainFan.Build(new(profile.Observer, 90, horizontal, 500000), profile, assessment.CameraDepth!);
    }

    [Fact]
    public void GeometryMatchesThePreContrastBaseline()
    {
        var fans = new List<object>();
        foreach (var fov in new[] { (H: 72d, V: 50d), (H: 24d, V: 20d), (H: 4.5d, V: 3d) })
        foreach (var pitch in new[] { -40d, 0, 6, 32 })
        foreach (var angles in new[] { new[] { -.1, 0 }, new[] { .5, 1, 2, 3 }, new[] { 2d, 5, 8 }, new[] { 15d, 5 } })
        {
            var fan = Fan(Profile(angles, true), pitch, fov.H, fov.V);
            fans.Add(new { fan.GroundFacing, fan.GroundOutline, fan.SamplesInspected,
                fan.Rays, Patches = fan.Patches.Select(p => new { p.LeftOffset, p.RightOffset,
                    p.LeftStart, p.RightStart, p.LeftEnd, p.RightEnd, p.Strength, p.Corners }) });
        }
        var summary = Summarise(JsonNode.Parse(JsonSerializer.Serialize(fans))!);
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_CONTRAST_GEOMETRY");
        if (!string.IsNullOrEmpty(output))
            File.WriteAllLines(output, ["{", .. summary.Select((entry, i) =>
                $"  {JsonSerializer.Serialize(entry.Key)}: {JsonSerializer.Serialize(entry.Value)}{(i < summary.Count - 1 ? "," : "")}"), "}"]);

        // The baseline summarises the geometry from before the contrast work: the geometry whose serialised form
        // had SHA-256 F2042013CCA56C8DB3676A415C41C04CBB4A017268A5895BD5531F078367F2AF, which this test once
        // compared bit for bit. That held on one machine only: trigonometric and power functions can differ in
        // their last bit between processors and runtimes. So, for each fan and each field, the number of values
        // and every flag are compared exactly, and the values' sum, absolute sum, minimum and maximum within a
        // relative 1e-7, far below anything visible but far above last-bit differences.
        var baseline = JsonSerializer.Deserialize<SortedDictionary<string, double[]>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Baselines", "terrain-fan-geometry.json")))!;
        Assert.Equal(baseline.Keys, summary.Keys);
        foreach (var (key, expected) in baseline)
        {
            var actual = summary[key];
            Assert.True(expected.Length == actual.Length && expected[0] == actual[0],
                $"{key}: {actual[0]} values, expected {expected[0]}");
            if (expected.Length == 1) continue;
            var scale = Math.Max(1, expected[2]);
            for (var i = 1; i < expected.Length; i++)
                Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-7 * (i <= 2 ? scale : Math.Max(1, Math.Abs(expected[i]))),
                    $"{key}[{i}]: {actual[i]:R}, expected {expected[i]:R}");
        }
    }

    /// <summary>
    /// Per fan and per field (array positions removed from the path): [count, sum, absolute sum, min, max] of
    /// its numbers, or [count] for each distinct other value, such as a flag.
    /// </summary>
    private static SortedDictionary<string, double[]> Summarise(JsonNode fans)
    {
        var summary = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
        var list = fans.AsArray();
        for (var fan = 0; fan < list.Count; fan++) Visit(list[fan], $"{fan:D2}");
        return summary;

        void Visit(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject value:
                    foreach (var (key, child) in value) Visit(child, $"{path}.{key}");
                    break;
                case JsonArray value:
                    foreach (var child in value) Visit(child, path + "[]");
                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                    var number = value.GetValue<double>();
                    if (summary.TryGetValue(path, out var s))
                    {
                        s[0]++; s[1] += number; s[2] += Math.Abs(number);
                        s[3] = Math.Min(s[3], number); s[4] = Math.Max(s[4], number);
                    }
                    else summary[path] = [1, number, Math.Abs(number), number, number];
                    break;
                default:
                    var flag = $"{path}={node?.ToJsonString() ?? "null"}";
                    summary[flag] = [summary.TryGetValue(flag, out var count) ? count[0] + 1 : 1];
                    break;
            }
        }
    }
}
