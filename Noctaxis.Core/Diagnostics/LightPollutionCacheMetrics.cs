namespace Noctaxis.Core.Diagnostics;

/// <summary>Opt-in probe counters. Cache owners already serialize access.</summary>
public sealed class LightPollutionCacheMetrics
{
    public long Hits, Misses, Evictions;
    /// <summary>Stopwatch ticks spent reading and decompressing source tiles (probe only).</summary>
    public long LoadTicks;
    public int PeakCount;
    public Action<object>? Evicted;
}
