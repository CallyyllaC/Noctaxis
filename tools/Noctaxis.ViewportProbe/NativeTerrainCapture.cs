using Mapsui;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using SkiaSharp;

internal static class NativeTerrainCapture
{
    internal static object Capture(GRContext context, EnvironmentalOverlayState state, string output)
    {
        const int width = 512, height = 384;
        var fan = state.TerrainFan ?? throw new InvalidOperationException("Native terrain fan is missing.");
        var tones = TerrainFanLocalContrast.Prepare(fan, out _);
        using var raster = new TerrainFanRaster(fan, tones);
        var rows = new List<object>();
        foreach (var scale in new[] { 1, 2, 4, 8, 16 })
        {
            var centre = WebMercator.FromWgs84(Angles.Destination(state.Observer, state.CentreBearingDegrees, 22000));
            var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(centre.X, centre.Y, 400d / scale, 0, width, height),
                width, height, EnvironmentalRenderParameters.Default);
            using var surface = SKSurface.Create(context, false, new SKImageInfo(width, height)) ??
                throw new InvalidOperationException("Could not create native GPU validation surface.");
            surface.Canvas.Clear(SKColors.Transparent);
            raster.Draw(surface.Canvas, frame, state.Observer, state.CentreBearingDegrees, state.HorizontalFovDegrees, state.MaximumDistanceMetres);
            surface.Flush(); context.Flush(); context.Submit(true);
            using var gpu = surface.Snapshot();
            using var pixels = SKBitmap.FromImage(gpu);
            using var data = gpu.Encode(SKEncodedImageFormat.Png, 100);
            var path = Path.ChangeExtension(output, $"gpu-field-{scale}x.png");
            using (var file = File.Create(path)) data.SaveTo(file);
            var covered = 0; var alphaErrors = 0;
            var expectedAlpha = (byte)Math.Round(255 * Math.Clamp(frame.RenderKey.Parameters.TerrainTintOpacity + .25f, 0, .85f));
            using var cpu = new SKBitmap(width, height);
            using var canvas = new SKCanvas(cpu);
            canvas.Clear(SKColors.Transparent);
            raster.Draw(canvas, frame, state.Observer, state.CentreBearingDegrees, state.HorizontalFovDegrees, state.MaximumDistanceMetres);
            var coverageDifferences = 0; var colourDifferences = 0; var maximumChannelError = 0;
            var boundaryColourDifferences = 0; var interiorColourDifferences = 0;
            var differenceDetails = new List<object>();
            bool MatchesBoundaryTone(SKColor actual, double x, double y)
            {
                var key = frame.RenderKey;
                var geo = WebMercator.ToWgs84(key.WorldOriginX + x * key.WorldStepXX + y * key.WorldStepYX,
                    key.WorldOriginY + x * key.WorldStepXY + y * key.WorldStepYY);
                var offset = Angles.NormaliseDegrees(Angles.InitialBearing(state.Observer, geo) -
                    (state.CentreBearingDegrees - state.HorizontalFovDegrees / 2));
                var owner = raster.Owner(offset, Angles.GreatCircleDistanceMetres(state.Observer, geo));
                if (owner < 0) return actual.Alpha == 0;
                var tint = Avalonia.Media.Color.FromUInt32(frame.RenderKey.Parameters.TerrainColourArgb);
                double Channel(byte c) => Math.Round(255 * (.85 - .67 * tones[owner]) * .8 + c * .2);
                return Math.Abs(actual.Red - Channel(tint.R)) <= 2 && Math.Abs(actual.Green - Channel(tint.G)) <= 2 &&
                    Math.Abs(actual.Blue - Channel(tint.B)) <= 2;
            }
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var actual = pixels.GetPixel(x, y);
                var expected = cpu.GetPixel(x, y);
                if (actual.Alpha > 0) { covered++; if (actual.Alpha != expectedAlpha) alphaErrors++; }
                if ((actual.Alpha > 0) != (expected.Alpha > 0)) coverageDifferences++;
                else if (actual.Alpha > 0)
                {
                    var error = Math.Max(Math.Abs(actual.Red - expected.Red), Math.Max(Math.Abs(actual.Green - expected.Green), Math.Abs(actual.Blue - expected.Blue)));
                    maximumChannelError = Math.Max(maximumChannelError, error);
                    if (error > 2)
                    {
                        colourDifferences++;
                        var matchRadius = -1d;
                        for (var step = 0; step <= 10 && matchRadius < 0; step++)
                        {
                            var radius = step * .05;
                            if (MatchesBoundaryTone(actual, x + .5 - radius, y + .5) || MatchesBoundaryTone(actual, x + .5 + radius, y + .5) ||
                                MatchesBoundaryTone(actual, x + .5, y + .5 - radius) || MatchesBoundaryTone(actual, x + .5, y + .5 + radius)) matchRadius = radius;
                        }
                        // A band taper can end exactly where angular and radial
                        // boundaries meet; axial probes alone can miss that corner.
                        for (var dy = -10; dy <= 10 && matchRadius < 0; dy++)
                        for (var dx = -10; dx <= 10 && matchRadius < 0; dx++)
                            if (MatchesBoundaryTone(actual, x + .5 + dx * .005, y + .5 + dy * .005))
                                matchRadius = Math.Max(Math.Abs(dx), Math.Abs(dy)) * .005;
                        if (matchRadius >= 0) boundaryColourDifferences++; else interiorColourDifferences++;
                        differenceDetails.Add(new { x, y, matchRadius, actual = actual.ToString(), expected = expected.ToString() });
                    }
                }
            }
            if (alphaErrors != 0 || covered == 0 || coverageDifferences != 0 || interiorColourDifferences != 0)
                throw new InvalidOperationException($"Native field mismatch at {scale}x: covered={covered}, alpha={alphaErrors}, coverage={coverageDifferences}, colour={colourDifferences}: {System.Text.Json.JsonSerializer.Serialize(differenceDetails)}");
            rows.Add(new { scale, covered, alphaErrors, coverageDifferences, colourDifferences, boundaryColourDifferences,
                interiorColourDifferences, maximumChannelError, differenceDetails, path });
        }
        return new { source = "Same GRContext as production window; GPU surface readback after timed interactions", rows };
    }
}
