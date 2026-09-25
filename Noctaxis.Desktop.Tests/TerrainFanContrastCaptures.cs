using Avalonia.Media;
using Mapsui;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using SkiaSharp;
using System.Text.Json;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainFanContrastCaptures
{
    [Fact]
    public void ExportOptionalContrastCandidates()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_CONTRAST_CAPTURE");
        if (string.IsNullOrEmpty(output)) return;
        var scenarios = new[]
        {
            (Name: "Shallow 0.5 / 1 / 2 / 3 · wide", Angles: new[] { .5, 1, 2, 3 }, H: 72d, V: 50d, Pitch: 0d, Asymmetric: false),
            (Name: "Moderate 2 / 5 / 8 · wide", Angles: new[] { 2d, 5, 8 }, H: 72d, V: 50d, Pitch: 0d, Asymmetric: false),
            (Name: "Near 1 / far 5 · wide", Angles: new[] { 1d, 5 }, H: 72d, V: 50d, Pitch: 0d, Asymmetric: false),
            (Name: "Shallow / high / clear · wide", Angles: new[] { 1d, 3, 8 }, H: 72d, V: 50d, Pitch: 0d, Asymmetric: true),
            (Name: "Shallow sequence · moderate", Angles: new[] { .5, 1, 2, 3 }, H: 24d, V: 20d, Pitch: 0d, Asymmetric: false),
            (Name: "Shallow sequence · tele", Angles: new[] { .5, 1, 2, 3 }, H: 4.5d, V: 3d, Pitch: 2d, Asymmetric: false)
        };
        var names = new[] { "Current · absolute", "Bearing · 18%", "Radial · 18%", "Combined · 18%" };
        using var bitmap = new SKBitmap(1168, 1512);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(16, 22, 32));
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 14);
        using var label = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var records = new List<object>();
        for (var row = 0; row < scenarios.Length; row++)
        {
            var s = scenarios[row];
            var profile = TerrainFanContrastTests.Profile(s.Angles, s.Asymmetric);
            var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
                profile, 20, 90, s.H, cameraFrame: new(s.Pitch, s.V, 5));
            var state = new EnvironmentalOverlayStateCoordinator().Update(profile.Observer,
                new(profile.Observer, 90, s.H, 500000), assessment,
                EnvironmentalOverlayStateFactory.CreateProfileKey(profile, 1), profile);
            for (var column = 0; column < 4; column++)
            {
                var mode = column switch
                {
                    1 => TerrainFanLocalContrastMode.Bearing,
                    2 => TerrainFanLocalContrastMode.Radial,
                    _ => TerrainFanLocalContrastMode.Combined
                };
                canvas.DrawText(names[column], 12 + column * 292, 22 + row * 252, font, label);
                canvas.DrawText(s.Name, 12 + column * 292, 43 + row * 252, font, label);
                canvas.Save(); canvas.Translate(12 + column * 292, 53 + row * 252);
                DrawMap(canvas, state, EnvironmentalRenderParameters.Default,
                    column == 0 ? (fan, index, p) => p.Strength :
                    (fan, index, p) => TerrainFanLocalContrast.Enhance(fan, index, mode));
                canvas.Restore();
            }
            records.Add(new { s.Name, s.H, s.V, s.Pitch, rays = state.TerrainFan!.Rays.Length,
                polygons = state.TerrainFan.Patches.Length, sameGeometryForAllCandidates = true });
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var file = File.Create(output)) data.SaveTo(file);
        var angles = new[] { .5, 1, 2, 3, 5, 8, 10 };
        File.WriteAllText(Path.ChangeExtension(output, ".json"), JsonSerializer.Serialize(new
        {
            referenceAngle = 10, scenarios = records,
            values = angles.Select(a => new { angle = a, currentWide = Math.Min(a / 25, 1),
                currentModerate = Math.Min(a / 10, 1), currentTele = Math.Min(a / 1.5, 1),
                mild = Math.Pow(a / 10, .9), medium = Math.Pow(a / 10, .75), strong = Math.Sqrt(a / 10) })
        }, new JsonSerializerOptions { WriteIndented = true }));

        var baseProfile = TerrainFanContrastTests.Profile([.5, 1, 2, 3]);
        var baseDepth = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"),
            baseProfile, 20, 90, 72, cameraFrame: new(0, 50, 5));
        var baseState = new EnvironmentalOverlayStateCoordinator().Update(baseProfile.Observer,
            new(baseProfile.Observer, 90, 72, 500000), baseDepth,
            EnvironmentalOverlayStateFactory.CreateProfileKey(baseProfile, 1), baseProfile);
        using var baseBitmap = new SKBitmap(876, 230);
        using var baseCanvas = new SKCanvas(baseBitmap);
        baseCanvas.Clear(new SKColor(16, 22, 32));
        for (var column = 0; column < 3; column++)
        {
            var opacity = new[] { .1f, .07f, .04f }[column];
            baseCanvas.DrawText($"Medium curve · base opacity {opacity:P0}", 12 + column * 292, 22, font, label);
            baseCanvas.Save(); baseCanvas.Translate(12 + column * 292, 32);
            DrawMap(baseCanvas, baseState, EnvironmentalRenderParameters.Default with { ConeOpacity = opacity },
                (fan, index, p) => TerrainFanTone.Strength(p.ApparentAltitudeDegrees));
            baseCanvas.Restore();
        }
        using var baseImage = SKImage.FromBitmap(baseBitmap);
        using var baseData = baseImage.Encode(SKEncodedImageFormat.Png, 100);
        using var baseFile = File.Create(Path.ChangeExtension(output, ".base.png"));
        baseData.SaveTo(baseFile);
    }

    private static void DrawMap(SKCanvas canvas, EnvironmentalOverlayState state,
        EnvironmentalRenderParameters parameters,
        Func<PlanTerrainFan, int, PlanTerrainPatch, double> mapping)
    {
        using var restore = new SKAutoCanvasRestore(canvas);
        canvas.ClipRect(new SKRect(0, 0, 268, 184));
        using var paint = new SKPaint { Color = new SKColor(242, 239, 233), IsAntialias = true };
        canvas.DrawRect(0, 0, 268, 184, paint);
        paint.Color = new SKColor(217, 231, 206); canvas.DrawRect(110, 20, 78, 55, paint);
        foreach (var road in new[] { (X: 70f, Y: 0f, EndX: 200f, EndY: 184f), (X: 0f, Y: 140f, EndX: 268f, EndY: 70f) })
        {
            paint.Color = new SKColor(200, 194, 183); paint.StrokeWidth = 8;
            canvas.DrawLine(road.X, road.Y, road.EndX, road.EndY, paint);
            paint.Color = SKColors.White; paint.StrokeWidth = 5;
            canvas.DrawLine(road.X, road.Y, road.EndX, road.EndY, paint);
        }
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
        paint.Color = new SKColor(85, 82, 79);
        canvas.DrawText("Hill Lane", 156, 109, font, paint);
        canvas.DrawText("Meadow", 120, 46, font, paint);
        var point = WebMercator.FromWgs84(Angles.Destination(state.Observer, 90, 18500));
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(point.X, point.Y, 300, 0, 268, 184),
            268, 184, parameters);
        using var renderer = new SkiaEnvironmentalOverlayResources(new(), mapping);
        renderer.Draw(canvas, state, frame, Colors.LimeGreen);
    }
}
