using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Diagnostics;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--sync")) AppBuilder.Configure<SyncProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        else AppBuilder.Configure<PinProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

// Native map/control rendering, offline and without reading/writing user state.
// Blank tile background deliberately removes network/decode noise; not a whole-Planner benchmark.
public sealed class PinProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        var map = view.MapControlForTesting.Map!;
        map.Layers.Clear();
        var window = new Window { Width = 800, Height = 600, Content = view, Title = "Planner pin investigation" };
        lifetime.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var output = lifetime.Args?.FirstOrDefault() ?? "pin-probe.json";
            var results = new List<object>();
            try
            {
                await Task.Delay(500);
                var world = WebMercator.FromWgs84(view.Observer);
                // Repeat in reverse order to expose warmup/order effects.
                for (var repeat = 0; repeat < 2; repeat++)
                foreach (var mode in repeat == 0 ? Enum.GetValues<PlannerPinMode>() : Enum.GetValues<PlannerPinMode>().Reverse())
                foreach (var scenario in new[] { "idle", "pan", "zoom", "combined", "rapid", "resize", "loading" })
                {
                    map.Layers.Clear();
                    view.PinProbe = null;
                    view.PinActivity = scenario == "loading" ? PlannerPinActivity.CoreLoading : PlannerPinActivity.None;
                    window.Width = 800;
                    map.Navigator.CenterOnAndZoomTo(new Mapsui.MPoint(world.X, world.Y), 500, 0);
                    await Task.Delay(250);
                    var probe = new PlannerPinProbe(mode);
                    view.PinProbe = probe;
                    probe.Poll(false, false, view.Observer, view.PinActivity);
                    if (mode == PlannerPinMode.Layer) map.Layers.Add(probe.CreateLayer());
                    map.RefreshGraphics();
                    view.OverlayForTesting.InvalidateVisual();
                    await Task.Delay(250);
                    var before = probe.Snapshot();
                    var gaps = new List<double>();
                    var allocations = GC.GetTotalAllocatedBytes(false);
                    var gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                    using var process = Process.GetCurrentProcess();
                    var cpu = process.TotalProcessorTime;
                    var start = Stopwatch.GetTimestamp();
                    var last = start;
                    var invalidations = 0;
                    EventHandler refresh = (_, _) => Interlocked.Increment(ref invalidations);
                    map.RefreshGraphicsRequest += refresh;
                    for (var i = 0; i < 90; i++)
                    {
                        var offset = Math.Sin(i * .14) * 20000;
                        var resolution = 500 * Math.Pow(2, Math.Sin(i * .11));
                        switch (scenario)
                        {
                            case "pan": map.Navigator.CenterOnAndZoomTo(new(world.X + offset, world.Y), 500, 0); break;
                            case "zoom": map.Navigator.CenterOnAndZoomTo(new(world.X, world.Y), resolution, 0); break;
                            case "combined": case "rapid":
                                map.Navigator.CenterOnAndZoomTo(new(world.X + offset, world.Y + offset / 2), resolution, 0); break;
                            case "resize": window.Width = 800 + i % 20 * 4; break;
                        }
                        await Task.Delay(scenario == "rapid" ? 4 : 16);
                        var now = Stopwatch.GetTimestamp();
                        gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                        last = now;
                    }
                    map.RefreshGraphicsRequest -= refresh;
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var allocated = GC.GetTotalAllocatedBytes(false) - allocations;
                    var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                    var collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gc[i]).ToArray();
                    var after = probe.Snapshot();
                    gaps.Sort();
                    results.Add(new { repeat, mode = mode.ToString(), scenario, elapsedMs = elapsed, processCpuMs = cpuMs,
                        processAllocatedBytes = allocated, collections, mapRefreshRequests = invalidations,
                        uiGapMedianMs = gaps[gaps.Count / 2], uiGapP95Ms = gaps[(int)(gaps.Count * .95)],
                        uiGapMaxMs = gaps[^1], gapsOver33Ms = gaps.Count(g => g > 33), before, after });
                    // Include idle restoration as a separate observable condition.
                    await Task.Delay(250);
                    if (probe.IsDebouncedHidden) throw new InvalidOperationException("Pin did not return after idle.");
                }
                File.WriteAllText(output, JsonSerializer.Serialize(new { source = "Native Avalonia MapControl; offline blank map; no terrain or user state",
                    renderScaling = window.RenderScaling, note = "UI gaps are scheduler latency, not presented frames; counters are cumulative (subtract before from after).",
                    results }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { File.WriteAllText(output + ".error", ex.ToString()); }
            finally { view.PinProbe = null; lifetime.Shutdown(); }
        };
        base.OnFrameworkInitializationCompleted();
    }
}
