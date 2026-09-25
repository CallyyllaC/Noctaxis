using Avalonia.Media;
using System.Collections.Immutable;
using Mapsui;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainFanPreparedContrastTests
{
    [Fact]
    public void BearingBucketEdgesRetainInclusiveNeighbourRule()
    {
        var patch = TerrainFanContrastTests.Fan(TerrainFanContrastTests.Profile([1, 3]), 0, 72, 50).Patches[0];
        var patches = new[] { 0d, Math.BitDecrement(2.1), 2.1, Math.BitIncrement(2.1), 4.2, 6.3 }
            .Select((c, i) => patch with { LeftOffset = c, RightOffset = c, ApparentAltitudeDegrees = i + 1 })
            .ToImmutableArray();
        var fan = new PlanTerrainFan([], false, []) { Patches = patches };
        var prepared = TerrainFanLocalContrast.Prepare(fan, out _);
        for (var i = 0; i < patches.Length; i++) Assert.Equal(TerrainFanLocalContrast.Enhance(fan, i), prepared[i], 14);
    }
    [Theory]
    [InlineData(TerrainFanLocalContrastMode.None)]
    [InlineData(TerrainFanLocalContrastMode.Bearing)]
    [InlineData(TerrainFanLocalContrastMode.Radial)]
    [InlineData(TerrainFanLocalContrastMode.Combined)]
    public void PreparedTonesMatchOriginalNeighbourhoods(TerrainFanLocalContrastMode mode)
    {
        foreach (var h in new[] { 72d, 24, 4.5 })
        foreach (var pitch in new[] { -40d, 0, 3, 20 })
        {
            var profile = TerrainFanContrastTests.ProfileByBearing(b =>
                new[] { .5, 1, 3, 8, 15 }.Select(a => a * (1 + .3 * Math.Sin(b))).ToArray());
            var fan = TerrainFanContrastTests.Fan(profile, pitch, h, h * .7);
            foreach (var amount in new[] { 0d, .18, .3 })
            {
                var tones = TerrainFanLocalContrast.Prepare(fan, out var comparisons, mode, amount);
                for (var i = 0; i < tones.Length; i++)
                    Assert.Equal(TerrainFanLocalContrast.Enhance(fan, i, mode, amount), tones[i], 14);
                if (h == 72 && tones.Length > 100)
                    Assert.True(comparisons < (long)tones.Length * tones.Length / 4);
            }
        }
    }

    [Fact]
    public void PreparedOutputIsPixelIdenticalAndAllocationBounded()
    {
        var profile = TerrainFanContrastTests.ProfileByBearing(b => new[] { .5, 1, 3, 8 }
            .Select(a => a * (1 + .2 * Math.Sin(b * .3))).ToArray());
        var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            profile, 20, 90, 72, cameraFrame: new(0, 50, 5));
        var state = new EnvironmentalOverlayStateCoordinator().Update(profile.Observer,
            new(profile.Observer, 90, 72, 500000), assessment,
            EnvironmentalOverlayStateFactory.CreateProfileKey(profile, 1), profile);
        var fan = state.TerrainFan!;
        TerrainFanLocalContrast.Prepare(fan, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        TerrainFanLocalContrast.Prepare(fan, out _);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes <= 256 + fan.Patches.Length * 24L, $"Allocated {bytes} bytes for {fan.Patches.Length} patches");
        using var original = new SkiaEnvironmentalOverlayResources(new(), (f, i, _) => TerrainFanLocalContrast.Enhance(f, i));
        using var prepared = new SkiaEnvironmentalOverlayResources(new());
        using var a = new SKBitmap(208, 160);
        using var b = new SKBitmap(208, 160);
        using var ca = new SKCanvas(a);
        using var cb = new SKCanvas(b);
        var centre = WebMercator.FromWgs84(Angles.Destination(profile.Observer, 90, 20000));
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(centre.X, centre.Y, 300, 0, 208, 160),
            208, 160, EnvironmentalRenderParameters.Default);
        ca.Clear(SKColors.White); cb.Clear(SKColors.White);
        original.Draw(ca, state, frame, Colors.Green);
        prepared.Draw(cb, state, frame, Colors.Green);
        Assert.True(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()));
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_FAN_PERFORMANCE_CAPTURE");
        if (!string.IsNullOrEmpty(output))
        {
            using var comparison = new SKBitmap(416, 160);
            using var canvas = new SKCanvas(comparison);
            canvas.DrawBitmap(a, 0, 0); canvas.DrawBitmap(b, 208, 0);
            using var image = SKImage.FromBitmap(comparison);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(output);
            png.SaveTo(file);
        }
    }

    [Fact]
    public void IdenticalTargetAndProjectionChangesReuseFanAndPreparedTones()
    {
        var profile = TerrainFanContrastTests.Profile([1, 3, 8]);
        var calc = new FramingVisibilityCalculator();
        FramingVisibilityAssessment Frame(double altitude) => calc.Calculate(new(DataState.Loading, null, "Offline"),
            profile, altitude, 90, 72, cameraFrame: new(0, 50, 5));
        var coordinator = new EnvironmentalOverlayStateCoordinator();
        var sector = new GeoSector(profile.Observer, 90, 72, 500000);
        var key = EnvironmentalOverlayStateFactory.CreateProfileKey(profile, 1);
        var state = coordinator.Update(profile.Observer, sector, Frame(20), key, profile);
        var evaluations = coordinator.PlanSampleEvaluations;
        using var renderer = new SkiaEnvironmentalOverlayResources(new());
        using var bitmap = new SKBitmap(80, 60);
        using var canvas = new SKCanvas(bitmap);
        var centre = WebMercator.FromWgs84(profile.Observer);
        for (var i = 0; i < 8; i++)
        {
            var updated = coordinator.Update(profile.Observer, sector, Frame(i), key, profile);
            Assert.Same(state.TerrainFan, updated.TerrainFan);
            var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(centre.X + i, centre.Y,
                100 + i, 0, 80, 60), 80, 60, EnvironmentalRenderParameters.Default);
            renderer.Draw(canvas, updated, frame, Colors.Green);
        }
        Assert.Equal(evaluations, coordinator.PlanSampleEvaluations);
        Assert.Equal(1, renderer.TonePreparationCount);
        var replacement = profile with { Observer = new(53.001, -1) };
        var next = coordinator.Update(replacement.Observer, sector with { Origin = replacement.Observer }, Frame(20),
            EnvironmentalOverlayStateFactory.CreateProfileKey(replacement, 2), replacement);
        Assert.NotSame(state.TerrainFan, next.TerrainFan);
        var lastFrame = EnvironmentalOverlayMath.CreateFrame(new Viewport(centre.X, centre.Y, 100, 0, 80, 60),
            80, 60, EnvironmentalRenderParameters.Default);
        renderer.Draw(canvas, next, lastFrame, Colors.Green);
        Assert.Equal(2, renderer.TonePreparationCount);
        Assert.True(renderer.HasPreparedTerrain);
        renderer.Draw(canvas, next with { TerrainFan = null }, lastFrame, Colors.Green);
        Assert.False(renderer.HasPreparedTerrain);
        renderer.Draw(canvas, next, lastFrame, Colors.Green);
        Assert.Equal(3, renderer.TonePreparationCount);
    }

    [Theory]
    [InlineData(TerrainFanLocalContrastMode.Bearing)]
    [InlineData(TerrainFanLocalContrastMode.Radial)]
    [InlineData(TerrainFanLocalContrastMode.Combined)]
    public void OrderedWindowMatchesReferenceAcrossWrapsAndUnequalTransitions(TerrainFanLocalContrastMode mode)
    {
        var profile = TerrainFanContrastTests.ProfileByBearing(b => b % 7 < 3
            ? [.4, 1.5, 3, 9, 14] : [.7, 2, 8]);
        var calculator = new FramingVisibilityCalculator();
        foreach (var bearing in new[] { .2, 359.9, 87.333 })
        foreach (var h in new[] { 72.6, 24d, 4.5 })
        foreach (var pitch in new[] { -35d, 0, 7, 30 })
        {
            var assessment = calculator.Calculate(new(DataState.Loading, null, "Offline"), profile, 20, bearing, h,
                cameraFrame: new(pitch, h * .72, 5));
            var fan = PlanTerrainFan.Build(new(profile.Observer, bearing, h, 500000), profile, assessment.CameraDepth!);
            var output = TerrainFanLocalContrast.Prepare(fan, out _, mode);
            for (var i = 0; i < output.Length; i++)
                Assert.Equal(TerrainFanLocalContrast.Enhance(fan, i, mode), output[i], 14);
        }
    }

    [Fact]
    public void UnorderedPresentationInputRetainsReferenceAccumulationOrder()
    {
        var fan = TerrainFanContrastTests.Fan(TerrainFanContrastTests.Profile([.3, 1, 6, 12]), 0, 72, 50);
        fan = fan with { Patches = fan.Patches.Reverse().ToImmutableArray() };
        foreach (var mode in new[] { TerrainFanLocalContrastMode.Bearing, TerrainFanLocalContrastMode.Radial, TerrainFanLocalContrastMode.Combined })
        {
            var output = TerrainFanLocalContrast.Prepare(fan, out _, mode);
            for (var i = 0; i < output.Length; i++)
                Assert.Equal(TerrainFanLocalContrast.Enhance(fan, i, mode), output[i], 14);
        }
    }
}
