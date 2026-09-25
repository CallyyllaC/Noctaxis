using BruTile;
using Noctaxis.Core.Diagnostics;

namespace Noctaxis.Desktop.Diagnostics;

/// <summary>Opt-in local probe hooks; absent during normal Planner use.</summary>
internal sealed class LightPollutionRasterDiagnostics
{
    public LightPollutionCacheMetrics Cache { get; } = new();
    public Action<TileIndex>? Requested;
    public Action<TileIndex, bool, double, double, double, int>? Completed;
    public long RejectedRevisions, EmptyResults, Failures;
}
