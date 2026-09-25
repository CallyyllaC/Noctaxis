namespace Noctaxis.Core.Diagnostics;

/// <summary>Opt-in probe counters. Cache owners already serialize access.</summary>
public sealed class LightPollutionCacheMetrics
{
    public long Hits, Misses, Evictions;
    public int PeakCount;
    public Action<object>? Evicted;
}
