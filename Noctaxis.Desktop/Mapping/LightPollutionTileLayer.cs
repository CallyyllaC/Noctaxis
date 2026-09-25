using BruTile;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;
using Mapsui.Tiling.Fetcher;
using Mapsui.Fetcher;

namespace Noctaxis.Desktop.Mapping;

/// <summary>Retains the native layer while excluding an old palette's late fetch completion.
/// Mapsui commits features after the source Task returns, outside the source's cache lock.</summary>
internal sealed class LightPollutionTileLayer : TileLayer, IFetchableSource
{
    private readonly LightPollutionRasterSource _source;
    private readonly LightPollutionTileCachePolicy _cachePolicy;
    private readonly object _fetchGate = new();
    private FetchInfo? _latestViewport;
    private int _continuations;
    private bool _disposed;

    internal LightPollutionTileLayer(LightPollutionRasterSource source, IDataFetchStrategy? fetchStrategy = null)
        : this(source, new LightPollutionTileCachePolicy(fetchStrategy)) { }

    private LightPollutionTileLayer(LightPollutionRasterSource source, LightPollutionTileCachePolicy policy)
        : base(source, minTiles: 32, maxTiles: 64, dataFetchStrategy: policy, renderFetchStrategy: policy,
            fetchTileAsFeature: info => Fetch(source, info))
    {
        _source = source;
        _cachePolicy = policy;
        // Bind the public render strategy's cache argument before any native fetch.
        // The empty view starts no fetch or raster generation work.
        _ = base.GetFeatures(new MRect(0, 0, 0, 0), 1);
    }

    public override void ViewportChanged(FetchInfo fetchInfo)
    {
        lock (_fetchGate)
        {
            if (_disposed) return;
            _latestViewport = fetchInfo;
            _continuations = (LightPollutionTileCachePolicy.MaximumTiles - 1) / FetchTracker.MaxTilesInOneRequest;
            base.ViewportChanged(fetchInfo);
        }
    }

    public new FetchJob[] GetFetchJobs(int activeFetches, int availableFetchSlots)
    {
        lock (_fetchGate)
        {
            if (_disposed) return [];
            var jobs = base.GetFetchJobs(activeFetches, availableFetchSlots);
            // Mapsui truncates cache misses to 256 per update. Continue only after
            // the caller has awaited every native publication, never on Busy=false.
            // A finite pass budget also prevents retry loops on unavailable data.
            if (jobs.Length == 0 && activeFetches == 0 && availableFetchSlots > 0 &&
                _continuations > 0 && _latestViewport is { } viewport && _cachePolicy.HasUnpublishedDeferredTiles)
            {
                _continuations--;
                base.ViewportChanged(viewport);
                jobs = base.GetFetchJobs(0, availableFetchSlots);
            }
            return jobs;
        }
    }

    private sealed class PaletteFeature(MRaster raster, long revision) : RasterFeature(raster)
    { public long Revision { get; } = revision; }

    private static async Task<IFeature?> Fetch(LightPollutionRasterSource source, TileInfo info)
    {
        var result = await source.GetRasterAsync(info).ConfigureAwait(false);
        return result.Bytes is null ? null : new PaletteFeature(new MRaster(result.Bytes,
            new MRect(info.Extent.MinX, info.Extent.MinY, info.Extent.MaxX, info.Extent.MaxY)), result.Revision);
    }

    public override IEnumerable<IFeature> GetFeatures(MRect extent, double resolution)
    {
        var revision = _source.PaletteRevision;
        foreach (var feature in base.GetFeatures(extent, resolution))
            if (feature is PaletteFeature palette && palette.Revision == revision) yield return feature;
    }

    protected override void Dispose(bool disposing)
    {
        lock (_fetchGate) { _disposed = true; _latestViewport = null; }
        base.Dispose(disposing);
    }
}
