using System.Text.Json;
using Avalonia.Media;
using Mapsui;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainFanRasterTests
{
    [Fact]
    public void WrappedFanOwnsTransitionAndFovBoundariesWithoutChangingPatches()
    {
        var terrain = TerrainFanContrastTests.ProfileByBearing(b => b % 9 < 4 ? [.5, 3, 7] : [1, 2, 5, 12]);
        var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            terrain, 20, 359.5, 72.6, cameraFrame: new(2, 51.9, 5));
        var fan = PlanTerrainFan.Build(new(terrain.Observer, 359.5, 72.6, 500000), terrain, assessment.CameraDepth!);
        using var raster = new TerrainFanRaster(fan, TerrainFanLocalContrast.Prepare(fan, out _));
        foreach (var patch in fan.Patches)
        foreach (var t in new[] { 0d, .01, .3, .7, .99, 1 })
        {
            var offset = patch.LeftOffset + (patch.RightOffset - patch.LeftOffset) * t;
            var start = patch.LeftStart + (patch.RightStart - patch.LeftStart) * t;
            var end = patch.LeftEnd + (patch.RightEnd - patch.LeftEnd) * t;
            foreach (var distance in new[] { Math.BitDecrement(start), start, Math.BitIncrement(start),
                         (start + end) / 2, Math.BitDecrement(end), end, Math.BitIncrement(end) })
            {
                var owner = raster.Owner(offset, distance);
                Assert.Equal(fan.ContainsTerrain(offset, distance), owner >= 0);
                if (owner >= 0) Assert.True(fan.Patches[owner].Contains(offset, distance));
            }
        }
        Assert.Equal(-1, raster.Owner(-.001, 30000));
        Assert.Equal(-1, raster.Owner(72.601, 30000));
        Assert.Equal(-1, raster.Owner(36, 500000));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)]
    public void OneOwnerPreservesCoverageAndToneAtEveryZoom(int scale)
    {
        const int width = 512, height = 384;
        var terrain = TerrainFanContrastTests.ProfileByBearing(b =>
        {
            var shape = Math.Max(0, 1 - Math.Pow((b - 84) / 22d, 2));
            return [.6 * shape, 3 * shape, 10 * shape, 5 * shape];
        });
        var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            terrain, 20, 90, 72, cameraFrame: new(0, 50, 5));
        var sector = new GeoSector(terrain.Observer, 90, 72, 500000);
        var state = new EnvironmentalOverlayStateCoordinator().Update(terrain.Observer, sector, assessment,
            EnvironmentalOverlayStateFactory.CreateProfileKey(terrain, 1), terrain);
        var fan = state.TerrainFan!;
        var patchesBefore = fan.Patches;
        var tones = fan.Patches.Select((_, i) => TerrainFanLocalContrast.Enhance(fan, i)).ToArray();
        using var raster = new TerrainFanRaster(fan, tones);
        var centre = WebMercator.FromWgs84(Angles.Destination(terrain.Observer, 90, 22000));
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(centre.X, centre.Y, 400d / scale, 0, width, height),
            width, height, EnvironmentalRenderParameters.Default);
        using var field = new SKBitmap(width, height);
        using var fieldCanvas = new SKCanvas(field);
        fieldCanvas.Clear(SKColors.Transparent);
        raster.Draw(fieldCanvas, frame, terrain.Observer, 90, 72, 500000);
        using var second = new SKBitmap(width, height);
        using var secondCanvas = new SKCanvas(second);
        secondCanvas.Clear(SKColors.Transparent);
        raster.Draw(secondCanvas, frame, terrain.Observer, 90, 72, 500000);
        Assert.True(field.GetPixelSpan().SequenceEqual(second.GetPixelSpan()));
        Assert.Equal(patchesBefore, fan.Patches);

        // Independent double-precision inverse projection and original Contains.
        // A 0.05-pixel boundary neighbourhood reports numerical edge differences
        // separately; it never exempts an internal crack in continuous terrain.
        int Reference(double x, double y)
        {
            var key = frame.RenderKey;
            var geo = WebMercator.ToWgs84(key.WorldOriginX + x * key.WorldStepXX + y * key.WorldStepYX,
                key.WorldOriginY + x * key.WorldStepXY + y * key.WorldStepYY);
            var offset = Angles.NormaliseDegrees(Angles.InitialBearing(terrain.Observer, geo) - sector.LeftBearingDegrees);
            var distance = Angles.GreatCircleDistanceMetres(terrain.Observer, geo);
            for (var i = 0; i < fan.Patches.Length; i++)
                if (fan.Patches[i].Contains(offset, distance)) return i;
            return -1;
        }
        var gaps = 0; var internalGaps = 0; var outside = 0; var wrongTone = 0; var darkSeams = 0;
        var edgeDifferences = 0; var covered = 0; var ownershipMismatch = 0;
        var opacity = (byte)Math.Round(255 * Math.Clamp(frame.RenderKey.Parameters.TerrainTintOpacity + .25f, 0, .85f));
        var tint = Color.FromUInt32(frame.RenderKey.Parameters.TerrainColourArgb);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var expected = Reference(x + .5, y + .5);
            var key = frame.RenderKey;
            var geo = WebMercator.ToWgs84(key.WorldOriginX + (x + .5) * key.WorldStepXX + (y + .5) * key.WorldStepYX,
                key.WorldOriginY + (x + .5) * key.WorldStepXY + (y + .5) * key.WorldStepYY);
            var offset = Angles.NormaliseDegrees(Angles.InitialBearing(terrain.Observer, geo) - sector.LeftBearingDegrees);
            var distance = Angles.GreatCircleDistanceMetres(terrain.Observer, geo);
            if (raster.Owner(offset, distance) != expected) ownershipMismatch++;
            var actual = field.GetPixel(x, y);
            if (actual.Alpha > 0)
            {
                covered++;
                if (actual.Alpha != opacity) darkSeams++;
            }
            var mismatch = false;
            if (expected >= 0 && actual.Alpha == 0)
            {
                gaps++; mismatch = true;
                if (Reference(x - .5, y + .5) >= 0 && Reference(x + 1.5, y + .5) >= 0 &&
                    Reference(x + .5, y - .5) >= 0 && Reference(x + .5, y + 1.5) >= 0) internalGaps++;
            }
            if (expected < 0 && actual.Alpha > 0) mismatch = true;
            if (expected >= 0 && actual.Alpha > 0)
            {
                var red = Math.Round(255 * (.85 - .67 * tones[expected]) * .8 + tint.R * .2);
                if (Math.Abs(actual.Red - red) > 2) mismatch = true;
            }
            if (!mismatch) continue;
            if (Reference(x + .45, y + .5) != expected || Reference(x + .55, y + .5) != expected ||
                Reference(x + .5, y + .45) != expected || Reference(x + .5, y + .55) != expected) edgeDifferences++;
            else if (expected < 0) outside++;
            else if (actual.Alpha > 0) wrongTone++;
        }
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_FAN_CORRECTION");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);
            using var renderer = new SkiaEnvironmentalOverlayResources(new());
            using var after = new SKBitmap(width, height);
            using var afterCanvas = new SKCanvas(after);
            afterCanvas.Clear(SKColors.White); renderer.Draw(afterCanvas, state, frame, Colors.LightGray);
            Save(after, Path.Combine(output, $"fan-after-{scale}x.png"));
            Save(field, Path.Combine(output, $"field-after-{scale}x.png"));
            using var before = new SKBitmap(width, height);
            using var beforeCanvas = new SKCanvas(before);
            beforeCanvas.Clear(SKColors.White);
            renderer.Draw(beforeCanvas, state with { TerrainFan = null }, frame, Colors.LightGray);
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha(opacity) };
            using var paint = new SKPaint { IsAntialias = false };
            beforeCanvas.SaveLayer(layer);
            for (var i = 0; i < fan.Patches.Length; i++)
            {
                using var path = new SKPath();
                path.AddPoly(fan.Patches[i].Corners.Select(c =>
                {
                    var p = EnvironmentalOverlayMath.GeographicToScreen(frame, c);
                    return new SKPoint((float)p.X, (float)p.Y);
                }).ToArray(), true);
                byte Channel(byte channel) => (byte)Math.Clamp(Math.Round(255 * (.85 - .67 * tones[i]) * .8 + channel * .2), 0, 255);
                paint.Color = new(Channel(tint.R), Channel(tint.G), Channel(tint.B));
                beforeCanvas.DrawPath(path, paint);
            }
            beforeCanvas.Restore(); Save(before, Path.Combine(output, $"fan-before-{scale}x.png"));
            File.WriteAllText(Path.Combine(output, $"metrics-{scale}x.json"), JsonSerializer.Serialize(new
            {
                scale, patches = fan.Patches.Length, originalProjectedVertices = fan.Patches.Sum(p => p.Corners.Length),
                afterProjectedVertices = 0, afterPaths = 0, terrainDrawCalls = 1, covered,
                gaps, internalGaps, darkSeams, outside, wrongTone, edgeDifferences, ownershipMismatch,
                invalidOrDegeneratePolygonsAfter = 0,
                note = "One fragment per pixel, constant alpha; no triangle/path primitives or shared-edge overdraw. Tiny boundary differences reported separately."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.Equal(0, ownershipMismatch);
        Assert.Equal(0, internalGaps);
        Assert.Equal(0, darkSeams);
        Assert.Equal(0, outside);
        Assert.Equal(0, wrongTone);
        Assert.True(covered > 1000);
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
