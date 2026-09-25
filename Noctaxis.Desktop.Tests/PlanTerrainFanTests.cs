using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using NodaTime;
using Avalonia.Media;
using Mapsui;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class PlanTerrainFanTests
{
    private static TerrainHorizonProfile Profile(Func<int, (double Distance, double Angle)[]> points) => new(
        new(53, -1), Enumerable.Range(0, 360).Select(b => new TerrainHorizonSample(b,
            points(b).Max(p => p.Angle), null, Sightline: points(b).Select(p =>
                new TerrainSightlineSample(p.Distance, 10, 0, p.Angle)).ToArray())).ToArray(),
        true, "Synthetic", Instant.FromUtc(2026, 1, 1, 0, 0));

    private static (PlanTerrainFan Fan, CameraTerrainDepth Depth) Build(TerrainHorizonProfile profile,
        double pitch, double fov = 24, double bearing = 90)
    {
        var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            profile, 20, bearing, fov, cameraFrame: new(pitch, 20, 5));
        return (PlanTerrainFan.Build(new(profile.Observer, bearing, fov, 500000), profile, assessment.CameraDepth!),
            assessment.CameraDepth!);
    }

    [Theory]
    [InlineData(-3)] [InlineData(0)]
    [InlineData(-.000001)]
    public void FlatAndSubHorizontalFramePixelsDoNotProduceObstruction(double angle)
    {
        var profile = Profile(_ => [(500, angle), (1000, angle)]);
        var (fan, depth) = Build(profile, 0);
        Assert.Contains(depth.DistancesMetres, d => d > 0);
        Assert.All(fan.Rays, r => Assert.Empty(r.Bands));
        Assert.False(fan.GroundFacing);
        var downward = Build(profile, -20);
        Assert.True(downward.Fan.GroundFacing);
        Assert.NotEmpty(downward.Fan.GroundOutline);
        Assert.All(downward.Fan.Rays, r => Assert.Empty(r.Bands));
    }

    [Theory]
    [InlineData(24)] [InlineData(4.5)]
    public void AsymmetricRidgeAndVisibleDepthLayersFollowTheFrameColumns(double fov)
    {
        var profile = Profile(b => b < 90 ? [(500, -2), (1000, 0)]
            : [(500, 1), (1000, 3), (3000, 6), (4000, 4)]);
        var (fan, depth) = Build(profile, 0, fov);
        Assert.Equal(depth.Width, fan.Rays.Length);
        Assert.Equal((int)Math.Ceiling(fov) + 1, fan.Rays.Length);
        Assert.All(fan.Rays.Where(r => r.BearingDegrees < 89), r => Assert.Empty(r.Bands));
        var ridge = fan.Rays.Last();
        Assert.Equal(new[] { 500d, 1000, 3000 }, ridge.Bands.Select(b => b.StartDistanceMetres));
        Assert.Equal(500000, ridge.Bands[^1].EndDistanceMetres);
        Assert.True(fan.ContainsTerrain(fov, 4500));
        Assert.All(Build(profile, 20, fov).Fan.Rays, r => Assert.Empty(r.Bands));
        Assert.Equal(6, profile.TerrainAltitudeAt(92));
        Assert.True(profile.OccultationAt(92, 2).HasTerrainData);
    }

    [Fact]
    public void SkylineIncreasesContinueOutwardAndHiddenSamplesDoNotCreateTransitions()
    {
        var profile = Profile(_ => [(2000, 1), (5000, .5), (9000, 3), (15000, 2), (30000, 6)]);
        var fan = Build(profile, 0).Fan;
        Assert.All(fan.Rays, ray =>
        {
            Assert.Equal(new[] { 2000d, 9000, 30000 }, ray.Bands.Select(b => b.StartDistanceMetres));
            Assert.Equal(new[] { 9000d, 30000, 500000 }, ray.Bands.Select(b => b.EndDistanceMetres));
        });
        Assert.False(fan.ContainsTerrain(12, 1999));
        Assert.True(fan.ContainsTerrain(12, 499999));
    }

    [Fact]
    public void PeakAboveFrameRetainsItsVisibleSurfaceAndOccludesFartherLowerTerrain()
    {
        var profile = Profile(_ => [(2000, 15), (9000, 5)]);
        var (fan, depth) = Build(profile, 0);
        Assert.True(15 > depth.UpperAltitude);
        Assert.True(depth.AltitudeAtRow(0) > 0);
        Assert.Equal(2000, depth.DistanceAt(12, 0));
        // The peak is above the image, but its slope fills visible positive rows.
        Assert.All(fan.Rays, ray =>
        {
            var band = Assert.Single(ray.Bands);
            Assert.Equal(2000, band.StartDistanceMetres);
            Assert.Equal(500000, band.EndDistanceMetres);
            Assert.Equal(depth.AltitudeAtRow(0), band.ApparentAltitudeDegrees);
        });
        Assert.True(fan.ContainsTerrain(12, 9000));
    }

    [Fact]
    public void PitchAboveLowRidgeRetainsOnlyTheTallerVisibleSkyline()
    {
        var profile = Profile(_ => [(2000, 5), (9000, 10)]);
        Assert.All(Build(profile, 17).Fan.Rays, ray =>
            Assert.Equal(9000, Assert.Single(ray.Bands).StartDistanceMetres));
    }

    [Fact]
    public void AngularToneIsIndependentOfDistanceAndPitchWhileFeatureRemainsVisible()
    {
        var near = Profile(_ => [(2000, 1), (9000, 5)]);
        var far = Profile(_ => [(20000, 1), (90000, 5)]);
        var original = Build(near, 0).Fan.Rays[12].Bands;
        Assert.True(original[0].Strength < original[1].Strength);
        Assert.Equal(original.Select(b => b.Strength), Build(far, 0).Fan.Rays[12].Bands.Select(b => b.Strength));
        Assert.Equal(original.Select(b => b.Strength), Build(near, 5).Fan.Rays[12].Bands.Select(b => b.Strength));
        Assert.Equal(.5, original[1].Strength, 8);
    }

    [Fact]
    public void SingleRidgeFrontierJoinsAdjacentDistancesAndPersistsToConeEnd()
    {
        var fan = Build(Profile(b => [(2000 + (b - 78) * 100, 5)]), 0).Fan;
        var patch = Assert.Single(fan.Patches, p => p.LeftOffset == 0);
        Assert.Equal(2000, patch.LeftStart);
        Assert.Equal(2100, patch.RightStart);
        Assert.Equal(500000, patch.LeftEnd);
        Assert.False(fan.ContainsTerrain(.5, 2049));
        Assert.True(fan.ContainsTerrain(.5, 2051));
        Assert.Equal(patch.RightStart, fan.Patches[1].LeftStart);
        Assert.False(fan.ContainsTerrain(-.01, 10000));
        Assert.False(fan.ContainsTerrain(24.01, 10000));
    }

    [Fact]
    public void UnequalSkylineCountsProduceOrderedNonOverlappingContinuousRegions()
    {
        var profile = Profile(b => (b % 3) switch
        {
            0 => [(2000, 1), (8000, 4), (25000, 7)],
            1 => [(4000, 2), (20000, 6), (25000, 4)],
            _ => [(2000, 0), (8000, -1), (25000, -2)]
        });
        var fan = Build(profile, 0).Fan;
        Assert.NotEmpty(fan.Patches);
        foreach (var patch in fan.Patches)
        {
            Assert.InRange(patch.LeftOffset, 0, 24);
            Assert.InRange(patch.RightOffset, patch.LeftOffset, 24);
            Assert.InRange(patch.LeftStart, 0, patch.LeftEnd);
            Assert.InRange(patch.RightStart, 0, patch.RightEnd);
            Assert.All(patch.Corners, c => { Assert.True(double.IsFinite(c.Latitude)); Assert.True(double.IsFinite(c.Longitude)); });
        }
        // Every strip partitions its radial envelope, including unequal/clear endpoints.
        foreach (var strip in fan.Patches.GroupBy(p => p.LeftOffset))
        {
            var ordered = strip.OrderBy(p => p.Strength).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                Assert.Equal(ordered[i - 1].LeftEnd, ordered[i].LeftStart);
                Assert.Equal(ordered[i - 1].RightEnd, ordered[i].RightStart);
            }
            for (var distance = 1000; distance < 500000; distance += 1103)
                Assert.InRange(ordered.Count(p => p.Contains(strip.Key + .5, distance)), 0, 1);
        }
        for (var i = 0; i < fan.Rays.Length; i++)
            if (fan.Rays[i].Bands.IsEmpty) Assert.False(fan.ContainsTerrain(i, 499999));
    }

    [Theory]
    [InlineData(72, 73)] [InlineData(24, 25)] [InlineData(4.5, 6)]
    public void ResolutionAndWrappedBearingsReuseTheUnmodifiedProfile(double fov, int rays)
    {
        var profile = Profile(_ => [(2000, 1), (5000, .5), (9000, 5)]);
        var original = profile.Samples;
        var fan = Build(profile, 0, fov, 359.5).Fan;
        Assert.Equal(rays, fan.Rays.Length);
        Assert.Equal(rays * 3, fan.SamplesInspected);
        Assert.Same(original, profile.Samples);
        Assert.Equal(2 * (rays - 1), fan.Patches.Length);
        Assert.All(fan.Patches, p => Assert.InRange(p.RightOffset, 0, fov + 1e-6));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void JoinedRegionsHaveNoClearRasterSeamsInsideTheEnvelope(bool enhancedContrast)
    {
        var terrain = Profile(b =>
        {
            var height = Math.Max(0, 1 - Math.Pow((b - 84) / 22d, 2));
            var shift = 1 + .2 * Math.Sin(b * .27) + .12 * Math.Cos(b * .61);
            return [(2000 * shift, height), (9000 * shift, 3 * height), (30000 * shift, 10 * height)];
        });
        var sector = new GeoSector(terrain.Observer, 90, 72, 500000);
        var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            terrain, 20, 90, 72, cameraFrame: new(0, 50, 5));
        var state = new EnvironmentalOverlayStateCoordinator().Update(terrain.Observer, sector, assessment,
            EnvironmentalOverlayStateFactory.CreateProfileKey(terrain, 1), terrain);
        var point = WebMercator.FromWgs84(Angles.Destination(terrain.Observer, 90, 22000));
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(point.X, point.Y, 400, 0, 208, 160),
            208, 160, EnvironmentalRenderParameters.Default);
        using var bitmap = new SKBitmap(208, 160);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var renderer = new SkiaEnvironmentalOverlayResources(new(), (fan, index, p) => enhancedContrast
            ? TerrainFanLocalContrast.Enhance(fan, index) : p.Strength);
        renderer.Draw(canvas, state, frame, Colors.LightGray);
        using var baseBitmap = new SKBitmap(208, 160);
        using var baseCanvas = new SKCanvas(baseBitmap);
        baseCanvas.Clear(SKColors.White);
        renderer.Draw(baseCanvas, state with { TerrainFan = new([], false, []) }, frame, Colors.LightGray);
        var gaps = 0; var checkedPixels = 0; var wrongTone = 0; var tooDark = 0;
        var referenceTones = state.TerrainFan!.Patches.Select((p, i) => enhancedContrast
            ? TerrainFanLocalContrast.Enhance(state.TerrainFan, i) : p.Strength).ToArray();
        bool Inside(double x, double y)
        {
            var geo = EnvironmentalOverlayMath.ScreenToGeographic(frame, x, y);
            var bearing = Angles.InitialBearing(terrain.Observer, geo);
            var distance = Angles.GreatCircleDistanceMetres(terrain.Observer, geo);
            return state.TerrainFan!.ContainsTerrain(Angles.NormaliseDegrees(bearing - sector.LeftBearingDegrees), distance);
        }
        for (var y = 2; y < 158; y++)
        for (var x = 2; x < 206; x++)
            if (Inside(x + .5, y + .5) && Inside(x - 1.5, y + .5) && Inside(x + 2.5, y + .5) &&
                Inside(x + .5, y - 1.5) && Inside(x + .5, y + 2.5))
            {
                checkedPixels++;
                if (bitmap.GetPixel(x, y).Red > 225) gaps++;
                // The renderer now gives the semantic polar partition sole ownership.
                // Independent projected paths are the defect, so they cannot be the
                // expected-tone oracle. Use the original Contains and Enhance rules.
                var geo = EnvironmentalOverlayMath.ScreenToGeographic(frame, x + .5, y + .5);
                var offset = Angles.NormaliseDegrees(Angles.InitialBearing(terrain.Observer, geo) - sector.LeftBearingDegrees);
                var distance = Angles.GreatCircleDistanceMetres(terrain.Observer, geo);
                var patchIndex = Enumerable.Range(0, state.TerrainFan.Patches.Length)
                    .First(i => state.TerrainFan.Patches[i].Contains(offset, distance));
                var strength = referenceTones[patchIndex];
                var tint = Color.FromUInt32(frame.RenderKey.Parameters.TerrainColourArgb);
                var red = Math.Round(255 * (.85 - .67 * strength) * .8 + tint.R * .2);
                var opacity = Math.Round(255 * Math.Clamp(frame.RenderKey.Parameters.TerrainTintOpacity + .25f, 0, .85f)) / 255;
                var expected = red * opacity + baseBitmap.GetPixel(x, y).Red * (1 - opacity);
                if (Math.Abs(bitmap.GetPixel(x, y).Red - expected) > 2) wrongTone++;
                if (bitmap.GetPixel(x, y).Red < expected - 2) tooDark++;
            }
        Assert.True(checkedPixels > 1000);
        Assert.Equal(0, gaps);
        if (wrongTone > Math.Max(32, checkedPixels / 500))
            throw new InvalidOperationException($"Unexpected tonal seams in {wrongTone} of {checkedPixels} interior pixels; {tooDark} too dark.");
    }

    [Fact]
    public void GroundFanDrawingIsClippedToItsViewport()
    {
        var terrain = Profile(_ => [(500, 0), (1000, -1)]);
        var sector = new GeoSector(terrain.Observer, 90, 72, 500000);
        var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            terrain, 20, 90, 72, cameraFrame: new(-40, 50, 5));
        var state = new EnvironmentalOverlayStateCoordinator().Update(terrain.Observer, sector, assessment,
            EnvironmentalOverlayStateFactory.CreateProfileKey(terrain, 1), terrain);
        var point = WebMercator.FromWgs84(Angles.Destination(terrain.Observer, 90, 500));
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(point.X, point.Y, 20, 0, 64, 64),
            64, 64, EnvironmentalRenderParameters.Default);
        using var bitmap = new SKBitmap(128, 128);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Magenta);
        using var renderer = new SkiaEnvironmentalOverlayResources(new());
        renderer.Draw(canvas, state, frame, Colors.LightGray);
        Assert.NotEqual(SKColors.Magenta, bitmap.GetPixel(32, 32));
        Assert.Equal(SKColors.Magenta, bitmap.GetPixel(90, 32));
        Assert.Equal(SKColors.Magenta, bitmap.GetPixel(32, 90));
    }
}
