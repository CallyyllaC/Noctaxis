using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Diagnostics;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--api"))
        {
            foreach (var m in typeof(SkiaSharp.GRContext).GetMethods().Where(m => m.Name is "Flush" or "Submit")) Console.WriteLine(m);
            foreach (var p in typeof(Avalonia.Skia.ISkiaSharpApiLease).GetProperties()) Console.WriteLine(p);
            return;
        }
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class ProbeApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var services = ConfigureServices(s => s.AddSingleton<IUserDataStore>(new ReadOnlyStore()));
        var vm = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow(vm, services.GetRequiredService<DesktopDialogService>())
            { Width = 1100, Height = 800 };
        lifetime.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var output = Environment.GetEnvironmentVariable("NOCTAXIS_VIEWPORT_PROBE")
                ?? throw new InvalidOperationException("Set NOCTAXIS_VIEWPORT_PROBE to an output JSON path.");
            var samples = new ConcurrentQueue<object>();
            var stage = "startup";
            long input = Stopwatch.GetTimestamp();
            try
            {
                await vm.InitializeAsync();
                vm.ShowPlannerCommand.Execute(null);
                vm.MoveObserver(new(53.00562745652107, -3.9519223528039116));
                await vm.WaitForPlannerRefreshAsync().WaitAsync(TimeSpan.FromSeconds(120));
                vm.SelectedLens = vm.Lenses.OrderBy(l => l.MinimumFocalLengthMillimetres).First();
                vm.FocalLength = 16;
                vm.CameraBearingDegrees = 90;
                vm.CameraPitchDegrees = 2;
                var map = window.FindControl<NoctaxisMapView>("PlannerMap")!;
                var navigator = map.MapControlForTesting.Map!.Navigator;
                var origin = navigator.Viewport;
                async Task<object> CaptureGpuAsync(string captureOutput)
                {
                    var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ViewportRenderProbe.GpuValidation = (context, state) =>
                    {
                        ViewportRenderProbe.GpuValidation = null;
                        try { completion.TrySetResult(NativeTerrainCapture.Capture(context, state, captureOutput)); }
                        catch (Exception e) { completion.TrySetException(e); }
                    };
                    map.OverlayForTesting.InvalidateVisual();
                    return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                if (vm.Snapshot?.Terrain.EffectiveCompletedBearingCount != 360)
                    throw new InvalidOperationException("Native timing requires the complete 360-bearing terrain profile.");
                // Reject an empty/incorrect GPU field before collecting any timings.
                // Repeat after the interactions to validate the changed fan too.
                var gpuPreflight = await CaptureGpuAsync(Path.ChangeExtension(output, "preflight.json"));
                ViewportRenderProbe.SynchronizeGpu = true;
                ViewportRenderProbe.Sink = sample => samples.Enqueue(new { stage, sample,
                    sinceInputMs = Stopwatch.GetElapsedTime(Interlocked.Read(ref input)).TotalMilliseconds });
                foreach (var operation in new[] { "static", "pan", "zoom", "bearing", "pitch" })
                {
                    stage = operation;
                    for (var i = 0; i < 12; i++)
                    {
                        Interlocked.Exchange(ref input, Stopwatch.GetTimestamp());
                        switch (operation)
                        {
                            case "pan": navigator.CenterOnAndZoomTo(new Mapsui.MPoint(origin.CenterX + i * 200, origin.CenterY), origin.Resolution, 0); break;
                            case "zoom": navigator.CenterOnAndZoomTo(new Mapsui.MPoint(origin.CenterX, origin.CenterY), origin.Resolution / Math.Pow(2, i % 5), 0); break;
                            case "bearing": vm.CameraBearingDegrees = 90 + i * .2; break;
                            case "pitch": vm.CameraPitchDegrees = 2 + i * .1; break;
                            default: map.OverlayForTesting.InvalidateVisual(); break;
                        }
                        await Task.Delay(150);
                    }
                }
                ViewportRenderProbe.Sink = null;
                var gpuCapture = await CaptureGpuAsync(output);
                var preparation = map.EnvironmentalStateForTesting?.TerrainFan is { } fan
                    ? ContrastPreparationMeasurements.Measure(fan) : null;
                File.WriteAllText(output, JsonSerializer.Serialize(new { source = "Native production MainWindow; read-only user state; GPU flag from Skia lease",
                    profileBearings = vm.Snapshot?.Terrain.EffectiveCompletedBearingCount, vm.Snapshot?.FieldOfView,
                    gpuValidatedBeforeTiming = true, gpuPreflight, samples, preparation, gpuCapture }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { File.WriteAllText(output + ".error", e.ToString()); }
            finally { ViewportRenderProbe.Sink = null; ViewportRenderProbe.GpuValidation = null; lifetime.Shutdown(); }
        };
    }
}

file sealed class ReadOnlyStore : IUserDataStore
{
    private readonly JsonUserDataStore _source = new(new PlatformUserDataPathProvider(), NullLogger<JsonUserDataStore>.Instance);
    public string StorageDirectory => _source.StorageDirectory;
    public Task<PersistedState> LoadAsync(CancellationToken token) => _source.LoadAsync(token);
    public Task SaveAsync(PersistedState state, CancellationToken token) => Task.CompletedTask;
}
