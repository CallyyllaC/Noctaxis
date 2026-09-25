using System.Collections.Immutable;
using System.Text.Json;
using Avalonia.Media;
using Mapsui;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainFanArtifactDiagnosticsTests
{
    [Fact]
    public void ExportOptionalHighZoomArtifactDiagnostics()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_FAN_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        var terrain = TerrainFanContrastTests.ProfileByBearing(b =>
        {
            var shape = Math.Max(0, 1 - Math.Pow((b - 84) / 22d, 2));
            return [.6 * shape, 3 * shape, 10 * shape, 5 * shape];
        });
        var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            terrain, 20, 90, 72, cameraFrame: new(0, 50, 5));
        var sector = new GeoSector(terrain.Observer, 90, 72, 500000);
        var fan = PlanTerrainFan.Build(sector, terrain, assessment.CameraDepth!);
        var records = new List<object>();
        foreach (var scale in new[] { 1, 2, 4, 8, 16 })
        {
            const int width = 512, height = 384;
            var centre = WebMercator.FromWgs84(Angles.Destination(terrain.Observer, 90, 22000));
            var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(centre.X, centre.Y,
                400 / scale, 0, width, height), width, height, EnvironmentalRenderParameters.Default);
            var paths = fan.Patches.Select(p =>
            {
                var path = new SKPath();
                foreach (var (corner, index) in p.Corners.Select((c, i) => (c, i)))
                {
                    var point = EnvironmentalOverlayMath.GeographicToScreen(frame, corner);
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return (Path: path, Valid: false);
                    if (index == 0) path.MoveTo((float)point.X, (float)point.Y);
                    else path.LineTo((float)point.X, (float)point.Y);
                }
                path.Close();
                return (Path: path, Valid: true);
            }).ToArray();
            var overlap = 0; var gaps = 0; var covered = 0; var maxCoverage = 0;
            object? firstOverlap = null;
            var polarOverlap = 0; var polarSamples = 0; var polarMax = 0;
            for (var offset = .25; offset < 72; offset += .5)
            for (var distance = 250d; distance < 500000; distance *= 1.08)
            {
                var count = fan.Patches.Count(p => p.Contains(offset, distance));
                polarSamples++; polarMax = Math.Max(polarMax, count);
                if (count > 1) polarOverlap++;
            }
            using var diagnostic = new SKBitmap(width, height);
            using var canvas = new SKCanvas(diagnostic);
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = new SKColor(80, 80, 80, 100), IsAntialias = false };
            foreach (var item in paths.Where(p => p.Valid)) canvas.DrawPath(item.Path, paint);
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var count = paths.Count(p => p.Valid && p.Path.Contains(x + .5f, y + .5f));
                maxCoverage = Math.Max(maxCoverage, count);
                var coordinate = EnvironmentalOverlayMath.ScreenToGeographic(frame, x + .5, y + .5);
                var pixelOffset = Angles.NormaliseDegrees(Angles.InitialBearing(terrain.Observer, coordinate) - sector.LeftBearingDegrees);
                var pixelDistance = Angles.GreatCircleDistanceMetres(terrain.Observer, coordinate);
                var expectedTerrain = fan.ContainsTerrain(pixelOffset, pixelDistance);
                if (count == 0)
                {
                    if (expectedTerrain) gaps++;
                    continue;
                }
                covered++;
                if (count > 1) overlap++;
                if (firstOverlap is null && count > 1)
                {
                    var hits = paths.Select((p, index) => (p, index)).Where(p => p.p.Valid && p.p.Path.Contains(x + .5f, y + .5f)).Select(p => p.index).ToArray();
                    firstOverlap = new { x, y, hits, patches = hits.Select(i => fan.Patches[i]).ToArray() };
                }
            }
            foreach (var patch in fan.Patches)
            {
                if (patch.Corners.Length < 3) continue;
                var points = patch.Corners.Select(c => EnvironmentalOverlayMath.GeographicToScreen(frame, c)).ToArray();
                if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) continue;
                var minX = Math.Max(0, (int)Math.Floor(points.Min(p => p.X)));
                var maxX = Math.Min(width - 1, (int)Math.Ceiling(points.Max(p => p.X)));
                var minY = Math.Max(0, (int)Math.Floor(points.Min(p => p.Y)));
                var maxY = Math.Min(height - 1, (int)Math.Ceiling(points.Max(p => p.Y)));
                if (minX > maxX || minY > maxY) continue;
            }
            var png = Path.Combine(Path.GetDirectoryName(output)!, $"fan-artifacts-{scale}x.png");
            using (var image = SKImage.FromBitmap(diagnostic)) using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var file = File.Create(png)) data.SaveTo(file);
            records.Add(new { scale, rays = fan.Rays.Length, patches = fan.Patches.Length,
                validPaths = paths.Count(p => p.Valid), coveredPixels = covered,
                overlapPixels = overlap, maxCoverage, gapPixels = gaps,
                polarSamples, polarOverlap, polarMax, firstOverlap,
                invalidPaths = paths.Count(p => !p.Valid) });
            if (scale == 16)
            {
                var coordinator = new EnvironmentalOverlayStateCoordinator();
                var state = coordinator.Update(terrain.Observer, sector, assessment,
                    EnvironmentalOverlayStateFactory.CreateProfileKey(terrain, 1), terrain);
                using var production = new SKBitmap(width, height);
                using var productionCanvas = new SKCanvas(production);
                productionCanvas.Clear(SKColors.White);
                using var renderer = new SkiaEnvironmentalOverlayResources(new());
                renderer.Draw(productionCanvas, state, frame, Colors.LightGray);
                using var image = SKImage.FromBitmap(production);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(Path.Combine(Path.GetDirectoryName(output)!, "fan-production-16x.png"));
                data.SaveTo(file);
            }
            foreach (var item in paths) item.Path.Dispose();
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { source = "deterministic fan; path Contains raster probe", records },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
