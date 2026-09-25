using System.Diagnostics;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Rendering.Skia;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;
using SkiaSharp;

namespace Noctaxis.Desktop.Diagnostics;

// Explicitly attached by the investigation executable/tests only. No environment flag,
// logging, layer registration or counter collection in the normal application.
internal enum PlannerPinMode { Floating, Layer, Debounced, NoPin }

internal sealed class PlannerPinProbe
{
    private readonly long[] _counts = new long[8];
    private readonly long[] _ticks = new long[8];
    private readonly long[] _bytes = new long[8];
    private long _lastMovement = long.MinValue;
    private PinState _state = new(new GeoCoordinate(51.5074, -.1278), PlannerPinActivity.None);
    internal PlannerPinMode Mode { get; }
    internal bool HideFloatingPin { get; private set; }
    internal bool IsDebouncedHidden { get; private set; }
    internal PlannerPinProbe(PlannerPinMode mode)
    {
        Mode = mode;
        HideFloatingPin = mode is PlannerPinMode.Layer or PlannerPinMode.NoPin;
    }

    internal bool Poll(bool changed, bool dragging, GeoCoordinate observer, PlannerPinActivity activity,
        long? milliseconds = null)
    {
        Interlocked.Increment(ref _counts[0]);
        if (changed) Interlocked.Increment(ref _counts[1]);
        var state = Volatile.Read(ref _state);
        if (state.Observer != observer || state.Activity != activity)
            Volatile.Write(ref _state, new PinState(observer, activity));
        var now = milliseconds ?? Environment.TickCount64;
        if (changed && !dragging) _lastMovement = now;
        var hide = Mode == PlannerPinMode.Debounced && !dragging &&
                   _lastMovement != long.MinValue && now - _lastMovement < 200;
        var visibilityChanged = hide != IsDebouncedHidden;
        IsDebouncedHidden = hide;
        HideFloatingPin = hide || Mode is PlannerPinMode.Layer or PlannerPinMode.NoPin;
        return visibilityChanged;
    }

    internal void CountTimerInvalidation() => Interlocked.Increment(ref _counts[2]);
    internal void CountMeasure() => Interlocked.Increment(ref _counts[3]);
    internal void CountArrange() => Interlocked.Increment(ref _counts[4]);
    internal Scope MeasureOverlay() => new(this, 5);
    internal Scope MeasurePin() => new(this, 6);
    internal Scope MeasureProjection() => new(this, 7);
    internal object Snapshot() => new { mode = Mode.ToString(), polls = _counts[0], viewportChanges = _counts[1],
        timerInvalidations = _counts[2], measures = _counts[3], arranges = _counts[4],
        overlay = Metric(5), pin = Metric(6), projection = Metric(7) };
    private object Metric(int i) => new { calls = _counts[i], totalMs = _ticks[i] * 1000d / Stopwatch.Frequency,
        allocatedBytes = _bytes[i] };
    internal readonly struct Scope : IDisposable
    {
        private readonly PlannerPinProbe _owner;
        private readonly int _index;
        private readonly long _start, _allocated;
        internal Scope(PlannerPinProbe owner, int index)
        { _owner = owner; _index = index; _start = Stopwatch.GetTimestamp(); _allocated = GC.GetAllocatedBytesForCurrentThread(); }
        public void Dispose()
        {
            Interlocked.Increment(ref _owner._counts[_index]);
            Interlocked.Add(ref _owner._ticks[_index], Stopwatch.GetTimestamp() - _start);
            Interlocked.Add(ref _owner._bytes[_index], GC.GetAllocatedBytesForCurrentThread() - _allocated);
        }
    }

    // Supported Mapsui custom layer hook avoids feature extent culling at wrapped worlds.
    // Deliberately one diagnostic layer, not a replacement map abstraction.
    internal MemoryLayer CreateLayer()
    {
        const string rendererName = "Noctaxis.PlannerPinInvestigation";
        MapRenderer.RegisterLayerRenderer(rendererName, (canvas, viewport, layer, service) =>
        {
            if (layer is PinLayer pinLayer) pinLayer.Probe.DrawLayerPin(canvas, viewport);
        });
        return new PinLayer(this) { CustomLayerRendererName = rendererName, Style = null };
    }

    internal static (double X, double Y) Project(GeoCoordinate observer, Viewport viewport)
    {
        var world = WebMercator.FromWgs84(observer);
        return Mapsui.Extensions.ViewportExtensions.WorldToScreenXY(viewport,
            WebMercator.WrapXNear(world.X, viewport.CenterX), world.Y);
    }

    private void DrawLayerPin(SKCanvas canvas, Viewport viewport)
    {
        if (viewport.Resolution <= 0) return;
        var state = Volatile.Read(ref _state);
        var projection = MeasureProjection();
        var (x, y) = Project(state.Observer, viewport);
        projection.Dispose();
        using var measurement = MeasurePin();
        canvas.Save();
        try
        {
            canvas.Translate((float)x, (float)y);
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(11, 15, 23) };
            // The production two semicircles form a circle centred at (0, 2).
            if (state.Activity != PlannerPinActivity.None)
            {
                var core = state.Activity == PlannerPinActivity.CoreLoading;
                var period = core ? 1250d : 2400d;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = core ? 1.7f : 1.1f;
                paint.Color = new SKColor(174, 218, 255, core ? (byte)220 : (byte)135);
                canvas.DrawArc(new SKRect(-18, -18, 18, 18),
                    (float)(Environment.TickCount64 % period / period * 360 - 90), core ? 255 : 165, false, paint);
            }
            paint.Style = SKPaintStyle.Fill;
            paint.Color = new SKColor(11, 15, 23);
            canvas.DrawCircle(0, 2, 12, paint);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 3;
            paint.Color = SKColors.White;
            canvas.DrawCircle(0, 2, 12, paint);
        }
        finally { canvas.Restore(); }
    }
    private sealed record PinState(GeoCoordinate Observer, PlannerPinActivity Activity);
    private sealed class PinLayer(PlannerPinProbe probe) : MemoryLayer("Planner pin investigation")
    { internal PlannerPinProbe Probe { get; } = probe; }
}
