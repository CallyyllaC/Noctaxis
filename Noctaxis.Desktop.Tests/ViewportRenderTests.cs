using System.Text.Json;
using Avalonia.Media;
using Mapsui;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Diagnostics;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class ViewportRenderTests
{
    [Fact]
    public void ExportOptionalViewportStages()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_VIEWPORT_CPU");
        if (string.IsNullOrEmpty(output)) return;
        var profile = TerrainFanContrastTests.ProfileByBearing(b => Enumerable.Range(1, 24)
            .Select(i => i * .35 * (1 + .2 * Math.Sin(b * .3))).ToArray());
        var coordinator = new EnvironmentalOverlayStateCoordinator();
        var calc = new FramingVisibilityCalculator();
        var key = EnvironmentalOverlayStateFactory.CreateProfileKey(profile, 1);
        var centre = WebMercator.FromWgs84(Angles.Destination(profile.Observer, 90, 22000));
        using var bitmap = new SKBitmap(800, 600);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new SkiaEnvironmentalOverlayResources(new());
        var records = new List<object>();
        var stage = "warmup";
        ViewportRenderProbe.Sink = sample => records.Add(new { stage, sample });
        try
        {
            foreach (var operation in new[] { "warmup", "static", "pan", "zoom", "bearing", "pitch" })
            {
                stage = operation;
                for (var i = 0; i < 10; i++)
                {
                    var bearing = stage == "bearing" ? 90 + i * .1 : 90;
                    var pitch = stage == "pitch" ? 2 + i * .1 : 2;
                    var assessment = calc.Calculate(new(DataState.Loading, null, "Offline"), profile, 20, bearing, 72,
                        cameraFrame: new(pitch, 50, 5));
                    var state = coordinator.Update(profile.Observer, new(profile.Observer, bearing, 72, 500000), assessment, key, profile);
                    var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(centre.X + (stage == "pan" ? i * 100 : 0),
                        centre.Y, stage == "zoom" ? 100 / Math.Pow(2, i % 5) : 100, 0, 800, 600), 800, 600,
                        EnvironmentalRenderParameters.Default);
                    renderer.Draw(canvas, state, frame, Colors.LimeGreen, false);
                }
            }
        }
        finally { ViewportRenderProbe.Sink = null; }
        File.WriteAllText(output, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }
}
