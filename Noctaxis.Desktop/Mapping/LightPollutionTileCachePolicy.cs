using BruTile;
using BruTile.Cache;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Tiling.Fetcher;
using Mapsui.Tiling.Rendering;

namespace Noctaxis.Desktop.Mapping;

/// <summary>Retain the actual adjacent viewport working sets, including parent levels.
/// Uses Mapsui's public strategy/cache extension points; it does not choose tile levels.</summary>
internal sealed class LightPollutionTileCachePolicy(IDataFetchStrategy? fetchStrategy = null)
    : IDataFetchStrategy, IRenderFetchStrategy
{
    // 128 MiB of potential 256x256 RGBA native tile images. Encoded bytes and the
    // independently bounded numerical/source caches are additional (see the report).
    internal const int MaximumTiles = 128 * 1024 * 1024 / (256 * 256 * 4);
    private readonly IDataFetchStrategy _fetch = fetchStrategy ?? new DataFetchStrategy(3);
    private readonly RenderFetchStrategy _render = new();
    private readonly object _gate = new();
    private MemoryCache<IFeature?>? _cache;
    private HashSet<TileIndex> _previous = [];
    private TileIndex[] _deferred = [];
    private int _requestedCount;
    private int _capacity = 64;

    public IList<TileInfo> Get(ITileSchema schema, Extent extent, int level)
    {
        var tiles = _fetch.Get(schema, extent, level);
        lock (_gate)
        {
            _requestedCount = tiles.Count;
            var current = tiles.Take(MaximumTiles).Select(t => t.Index).ToHashSet();
            var needed = current.Union(_previous).Count() + TileFetchPlanner.DefaultNumberOfSimultaneousFetches;
            _capacity = Math.Clamp(Math.Max(_capacity, needed), 64, MaximumTiles);
            _previous = current;
            if (_cache is { } cache)
            {
                // Grow before dispatch, not on a later paint. Equal min/max avoids
                // a purge discarding half the working set when the ceiling is crossed.
                cache.MaxTiles = _capacity;
                cache.MinTiles = _capacity;
                _deferred = tiles.Where(t => cache.Find(t.Index) is null)
                    .Skip(FetchTracker.MaxTilesInOneRequest).Take(MaximumTiles).Select(t => t.Index).ToArray();
            }
        }
        return tiles;
    }

    internal bool HasUnpublishedDeferredTiles
    {
        get
        {
            lock (_gate)
                return _requestedCount <= MaximumTiles && _cache is { } cache &&
                    _deferred.Any(index => cache.Find(index) is null);
        }
    }

    public IList<IFeature> Get(MRect extent, double resolution, ITileSchema schema, ITileCache<IFeature?> cache)
    {
        lock (_gate) _cache = (MemoryCache<IFeature?>)cache;
        return _render.Get(extent, resolution, schema, cache);
    }
}
