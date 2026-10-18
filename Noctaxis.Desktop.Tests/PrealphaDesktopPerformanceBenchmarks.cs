using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

// Opt-in desktop performance workloads (PF-13). Without NOCTAXIS_PERF_OUTPUT (a directory) the test returns at
// once; NOCTAXIS_PERF_LABEL names the run. Headless Skia rendering: CPU-side cost only, no GPU or compositor.
public sealed class PrealphaDesktopPerformanceBenchmarks
{
    [AvaloniaFact]
    public void TimelineChartScrub()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return;
        var label = System.Environment.GetEnvironmentVariable("NOCTAXIS_PERF_LABEL") is { Length: > 0 } named ? named : "run";
        var catalogue = new OpenNgcTargetCatalogue();
        var start = Instant.FromUtc(2026, 10, 17, 12, 0);
        var targets = new[] { "sun", "moon" }.Concat(catalogue.Targets.Where(target => target.HasEquatorialCoordinates && !target.IsSun && !target.IsMoon)
            .Take(6).Select(target => target.Id)).ToArray();
        var rows = new List<object>();
        foreach (var count in new[] { 2, 8 })
        {
            var paths = targets.Take(count).Select((id, index) => new TimelinePath(catalogue.Get(id), new AstralPath(
                new LocalDate(2026, 10, 17), "UTC", Duration.FromMinutes(10),
                Enumerable.Range(0, 145).Select(step => new AstralPathSample(start + Duration.FromMinutes(10 * step),
                    new HorizontalCoordinate(step * 2.5 % 360, 60 * Math.Sin((step + index * 9) / 144d * 2 * Math.PI)))).ToArray(),
                new TargetEvents(null, null, null), start))).ToArray();
            var graph = new HorizonGraph
            {
                Width = 900, Height = 160, Snapshot = Snapshot(catalogue, start), Paths = paths,
                WindowStart = start, WindowEnd = start + Duration.FromHours(24), Zone = DateTimeZone.Utc
            };
            var window = new Window { Width = 940, Height = 240, Content = graph };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 160));
            var times = new List<double>();
            var bytes = new List<double>();
            for (var frame = 0; frame < 140; frame++)
            {
                // Scrubbing moves the selected moment, which re-renders the chart.
                graph.Snapshot = Snapshot(catalogue, start + Duration.FromMinutes(frame * 7));
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                bitmap.Render(graph);
                timer.Stop();
                if (frame < 20) continue;
                times.Add(timer.Elapsed.TotalMilliseconds);
                bytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            }
            // The UI thread's share: Render records drawing operations (and lays out text); rasterising is the
            // render thread's in the application. Recording into a drawing group isolates it.
            var recordTimes = new List<double>();
            var recordBytes = new List<double>();
            for (var frame = 0; frame < 140; frame++)
            {
                graph.Snapshot = Snapshot(catalogue, start + Duration.FromMinutes(frame * 7));
                var group = new Avalonia.Media.DrawingGroup();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var timer = Stopwatch.StartNew();
                using (var context = group.Open()) graph.Render(context);
                timer.Stop();
                if (frame < 20) continue;
                recordTimes.Add(timer.Elapsed.TotalMilliseconds);
                recordBytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            }
            window.Close();
            times.Sort(); bytes.Sort(); recordTimes.Sort(); recordBytes.Sort();
            rows.Add(new { targets = count, frames = times.Count, medianMilliseconds = times[times.Count / 2],
                p95Milliseconds = times[(int)(times.Count * .95)], medianAllocatedBytes = bytes[bytes.Count / 2],
                recordOnlyMedianMilliseconds = recordTimes[recordTimes.Count / 2],
                recordOnlyP95Milliseconds = recordTimes[(int)(recordTimes.Count * .95)],
                recordOnlyMedianAllocatedBytes = recordBytes[recordBytes.Count / 2] });
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, $"pf13-timeline-chart.{label}.json"),
            JsonSerializer.Serialize(new { label, rows }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static PlanningSnapshot Snapshot(OpenNgcTargetCatalogue catalogue, Instant selected)
    {
        var target = catalogue.Get("sun");
        var position = new TargetPosition(target, selected, new HorizontalCoordinate(180, 20), new TargetEvents(null, null, null));
        var session = PlanningSession.Default(selected, "UTC");
        var path = new AstralPath(new LocalDate(2026, 10, 17), "UTC", Duration.FromMinutes(10),
            [new AstralPathSample(selected, position.Horizontal)], position.Events, selected);
        var terrain = new TerrainHorizonProfile(session.Observer,
            Enumerable.Range(0, 360).Select(bearing => new TerrainHorizonSample(bearing, 2 + 3 * Math.Sin(bearing / 20d), 5_000)).ToArray(),
            true, "Fixture", selected, HorizonState: Noctaxis.Core.Environment.EnvironmentalDataState.Available);
        return new PlanningSnapshot(session, position, path, new FieldOfView(60, 40, 70), terrain, new TerrainCrossings(null, null),
            new WeatherResult(DataState.Loading, null, ""), new AstronomyContext(position, position));
    }
}
