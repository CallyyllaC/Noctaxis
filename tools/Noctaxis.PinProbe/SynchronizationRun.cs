using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Diagnostics;
using SkiaSharp;

public sealed class SyncProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var observer = new GeoCoordinate(53, -1);
        var view = new NoctaxisMapView { Observer = observer };
        var map = view.MapControlForTesting.Map!;
        map.Layers.Clear();
        var window = new Window { Width = 800, Height = 600, Content = view, Title = "Overlay synchronization: blue map halo / white pin" };
        lifetime.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var output = lifetime.Args!.First(a => a != "--sync");
            var runs = new List<object>();
            try
            {
                await Task.Delay(500);
                var world = WebMercator.FromWgs84(observer);
                for (var repeat = 0; repeat < 2; repeat++)
                foreach (var polling in repeat == 0 ? new[] { true, false } : new[] { false, true })
                {
                    map.Layers.Clear();
                    var probe = new OverlaySynchronizationProbe(polling, observer);
                    view.SyncProbe = probe;
                    map.Layers.Add(probe.CreateReferenceLayer());
                    foreach (var stage in new[] { "idle", "pan", "zoom", "combined", "burst", "animated", "resize", "loading" })
                    {
                        probe.Stage = "settle";
                        window.Width = 800;
                        view.PinActivity = stage == "loading" ? PlannerPinActivity.CoreLoading : PlannerPinActivity.None;
                        map.Navigator.CenterOnAndZoomTo(new(world.X + 20000, world.Y), 500, 0);
                        await Task.Delay(300);
                        probe.Stage = stage;
                        var allocated = GC.GetTotalAllocatedBytes(false);
                        var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                        if (stage == "animated") map.Navigator.CenterOnAndZoomTo(new(world.X - 60000, world.Y + 20000), 200, 1500);
                        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var index = 0;
                        void Advance(TimeSpan timestamp)
                        {
                            if (index++ >= 120) { complete.TrySetResult(); return; }
                            var phase = index * .055;
                            var offset = Math.Sin(phase) * 75000;
                            var resolution = 500 * Math.Pow(2, Math.Sin(phase));
                            switch (stage)
                            {
                                case "pan": map.Navigator.CenterOnAndZoomTo(new(world.X + offset, world.Y), 500, 0); break;
                                case "zoom": map.Navigator.CenterOnAndZoomTo(new(world.X + 50000, world.Y), resolution, 0); break;
                                case "combined": map.Navigator.CenterOnAndZoomTo(new(world.X + offset, world.Y + offset / 3), resolution, 0); break;
                                case "burst":
                                    for (var j = 0; j < 8; j++) map.Navigator.CenterOnAndZoomTo(new(world.X + offset + j, world.Y), resolution, 0);
                                    break;
                                case "resize": window.Width = 800 + index % 20 * 4; break;
                            }
                            window.RequestAnimationFrame(Advance);
                        }
                        window.RequestAnimationFrame(Advance);
                        await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
                        var bytes = GC.GetTotalAllocatedBytes(false) - allocated;
                        var collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray();
                        probe.Stage = "settle";
                        var samples = probe.Samples.Where(s => s.Stage == stage).ToArray();
                        runs.Add(new { repeat, polling, stage, allocatedBytes = bytes, collections, samples });
                        Console.WriteLine($"{repeat} {(polling ? "poll" : "event")} {stage}: {samples.Count(s => s.Kind == "composition")} composition samples");
                        File.WriteAllText(output, JsonSerializer.Serialize(new { renderScaling = window.RenderScaling,
                            source = "Native RequestAnimationFrame-driven MapControl; blank map with supported Mapsui reference halo; shared production overlay",
                            runs }, new JsonSerializerOptions { WriteIndented = true }));
                        Plot(samples, Path.ChangeExtension(output, $"{repeat}-{(polling ? "poll" : "event")}-{stage}.png"));
                    }
                }
                // Exercise real dispatcher ticks across repeated native tree transitions.
                var lifecycleProbe = new OverlaySynchronizationProbe(false, observer);
                view.SyncProbe = lifecycleProbe;
                var lifecycle = new List<object>();
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    view.PinActivity = PlannerPinActivity.CoreLoading;
                    await Task.Delay(150);
                    if (!view.AnimationTimerEnabled) throw new InvalidOperationException("Attached loading timer did not start.");
                    window.Content = null;
                    var detachedCount = lifecycleProbe.Samples.Count(s => s.Kind == "invalidate");
                    map.Navigator.CenterOnAndZoomTo(new(world.X + cycle * 1000, world.Y), 300, 0);
                    await Task.Delay(150);
                    if (view.AnimationTimerEnabled || lifecycleProbe.Samples.Count(s => s.Kind == "invalidate") != detachedCount)
                        throw new InvalidOperationException("Detached view continued invalidating.");
                    window.Content = view;
                    var attachedCount = lifecycleProbe.Samples.Count(s => s.Kind == "invalidate");
                    await Task.Delay(150);
                    if (!view.AnimationTimerEnabled || lifecycleProbe.Samples.Count(s => s.Kind == "invalidate") <= attachedCount)
                        throw new InvalidOperationException("Reattached loading animation did not resume.");
                    view.PinActivity = PlannerPinActivity.None;
                    await Task.Delay(150);
                    var idleCount = lifecycleProbe.Samples.Count(s => s.Kind == "invalidate");
                    await Task.Delay(150);
                    if (view.AnimationTimerEnabled || lifecycleProbe.Samples.Count(s => s.Kind == "invalidate") != idleCount)
                        throw new InvalidOperationException("Idle view retained timer work.");
                    lifecycle.Add(new { cycle, detachedQuiet = true, loadingResumed = true, idleQuiet = true });
                }
                File.WriteAllText(output, JsonSerializer.Serialize(new { renderScaling = window.RenderScaling,
                    source = "Native RequestAnimationFrame-driven MapControl; blank map with supported Mapsui reference halo; shared production overlay",
                    lifecycle, runs }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { File.WriteAllText(output + ".error", ex.ToString()); }
            finally { view.SyncProbe = null; lifetime.Shutdown(); }
        };
        base.OnFrameworkInitializationCompleted();
    }

    // Diagnostic trace, not a screenshot or a claim about presented monitor frames.
    private static void Plot(OverlaySynchronizationProbe.Sample[] samples, string path)
    {
        var frames = samples.Where(s => s.Kind == "composition").ToArray();
        if (frames.Length < 2) return;
        using var bitmap = new SKBitmap(1000, 220);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(20, 24, 32));
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.DeepSkyBlue, StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var line = new SKPath();
        var duration = Math.Max(1, frames[^1].TimeMs - frames[0].TimeMs);
        for (var i = 0; i < frames.Length; i++)
        {
            var x = 20 + (float)((frames[i].TimeMs - frames[0].TimeMs) / duration * 960);
            var y = 200 - (float)Math.Min(180, frames[i].PositionErrorDip * 6); // fixed 0-30 DIP scale
            if (i == 0) line.MoveTo(x, y); else line.LineTo(x, y);
        }
        canvas.DrawPath(line, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        png.SaveTo(file);
    }
}
