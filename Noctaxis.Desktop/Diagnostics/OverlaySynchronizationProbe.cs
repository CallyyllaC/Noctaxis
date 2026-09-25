using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Rendering.Skia;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Diagnostics;

// Diagnostic-only timestamps and same-composition coordinate comparisons.
internal sealed class OverlaySynchronizationProbe(bool usePolling, GeoCoordinate observer)
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Sample> _samples = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private long _latestChange, _firstPending, _mapTime;
    private Viewport _mapViewport;
    private Viewport _latestViewport;
    private readonly Dictionary<Viewport, long> _changes = new();
    internal bool UsePolling { get; } = usePolling;
    internal string Stage { get; set; } = "settle";
    private double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    internal void ViewportChanged(Viewport viewport)
    {
        lock (_gate)
        {
            _latestChange = Stopwatch.GetTimestamp();
            _latestViewport = viewport;
            _changes[viewport] = _latestChange;
            if (_firstPending == 0) _firstPending = _latestChange;
            Add("viewport", _latestChange);
        }
    }
    internal void Coalesced() { lock (_gate) Add("coalesced", Stopwatch.GetTimestamp()); }
    internal void Invalidated() { lock (_gate) Add("invalidate", Stopwatch.GetTimestamp(), Delay(_latestChange)); }
    internal void OverlayRendered(Viewport viewport)
    {
        lock (_gate)
        {
            Add("overlay", Stopwatch.GetTimestamp(), _changes.TryGetValue(viewport, out var changed) ? Delay(changed) : 0,
                Delay(_firstPending));
            if (viewport == _latestViewport) _firstPending = 0;
        }
    }
    private double Delay(long since) => since == 0 ? 0 : Ms(Stopwatch.GetTimestamp() - since);
    private void Add(string kind, long now, double latestMs = 0, double oldestMs = 0, double error = 0) =>
        _samples.Enqueue(new Sample(Stage, kind, Ms(now - _origin), latestMs, oldestMs, error));
    internal Sample[] Samples => _samples.ToArray();

    internal MemoryLayer CreateReferenceLayer()
    {
        MapRenderer.RegisterLayerRenderer("Noctaxis.OverlaySynchronization", (canvas, viewport, layer, service) =>
        {
            if (layer is not ReferenceLayer reference) return;
            var probe = reference.Probe;
            lock (probe._gate)
            {
                probe._mapViewport = viewport;
                probe._mapTime = Stopwatch.GetTimestamp();
                probe.Add("map", probe._mapTime);
            }
            var (x, y) = PlannerPinProbe.Project(observer, viewport);
            using var paint = new SkiaSharp.SKPaint { IsAntialias = true, Color = SkiaSharp.SKColors.DeepSkyBlue,
                Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 2 };
            // Reference halo belongs to Mapsui. White/dark production pin should stay centred in it.
            canvas.DrawCircle((float)x, (float)y + 2, 22, paint);
        });
        return new ReferenceLayer(this) { CustomLayerRendererName = "Noctaxis.OverlaySynchronization", Style = null };
    }

    internal void RecordComposition(DrawingContext context, Rect bounds, Viewport viewport) =>
        context.Custom(new CompositionSample(this, bounds, viewport));

    private void Composed(Viewport overlayViewport)
    {
        lock (_gate)
        {
            if (_mapTime == 0) return;
            var mapPoint = PlannerPinProbe.Project(observer, _mapViewport);
            var overlayPoint = PlannerPinProbe.Project(observer, overlayViewport);
            var dx = mapPoint.X - overlayPoint.X;
            var dy = mapPoint.Y - overlayPoint.Y;
            Add("composition", Stopwatch.GetTimestamp(), Delay(_latestChange), 0, Math.Sqrt(dx * dx + dy * dy));
        }
    }
    internal sealed record Sample(string Stage, string Kind, double TimeMs, double LatestChangeMs,
        double OldestPendingMs, double PositionErrorDip);
    private sealed class ReferenceLayer(OverlaySynchronizationProbe probe) : MemoryLayer("Synchronization reference")
    { internal OverlaySynchronizationProbe Probe { get; } = probe; }
    private sealed class CompositionSample(OverlaySynchronizationProbe probe, Rect bounds, Viewport viewport) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context) => probe.Composed(viewport);
    }
}
