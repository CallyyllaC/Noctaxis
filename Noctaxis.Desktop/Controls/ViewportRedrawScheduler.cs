namespace Noctaxis.Desktop.Controls;

// One outstanding invalidation until the overlay reads the latest viewport.
// Mapsui animation notifications can arrive on its rendering thread.
internal sealed class ViewportRedrawScheduler(Func<bool> onUiThread, Action<Action> post, Action invalidate)
{
    private readonly object _gate = new();
    private bool _active, _pending;
    private long _generation;

    internal void Attach() { lock (_gate) { _active = true; _pending = false; _generation++; } }
    internal void Detach() { lock (_gate) { _active = false; _pending = false; _generation++; } }
    internal void RenderStarting() { lock (_gate) { _pending = false; _generation++; } }

    internal bool Request()
    {
        long generation;
        lock (_gate)
        {
            if (!_active || _pending) return false;
            _pending = true;
            generation = _generation;
        }
        if (onUiThread()) Deliver(generation);
        else post(() => Deliver(generation));
        return true;
    }

    private void Deliver(long generation)
    {
        lock (_gate)
            if (!_active || generation != _generation || !_pending) return;
        invalidate();
    }
}
