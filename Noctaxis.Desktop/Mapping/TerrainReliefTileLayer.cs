using BruTile;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;

namespace Noctaxis.Desktop.Mapping;

/// <summary>
/// Native layer for <see cref="TerrainReliefTileSource"/>. It remembers the latest viewport so
/// relief is not computed for tiles the map has already left during a rapid pan or zoom; such a tile
/// is simply requested again if it comes back into view.
/// </summary>
internal sealed class TerrainReliefTileLayer : TileLayer
{
    private readonly Relevance _relevance;

    internal TerrainReliefTileLayer(TerrainReliefTileSource source) : this(source, new Relevance(source.Schema)) { }

    private TerrainReliefTileLayer(TerrainReliefTileSource source, Relevance relevance)
        : base(source, fetchTileAsFeature: info => Fetch(source, relevance, info)) => _relevance = relevance;

    public override void ViewportChanged(FetchInfo fetchInfo)
    {
        _relevance.Update(fetchInfo);
        base.ViewportChanged(fetchInfo);
    }

    internal bool IsWanted(TileInfo info) => _relevance.IsWanted(info);

    private static async Task<IFeature?> Fetch(TerrainReliefTileSource source, Relevance relevance, TileInfo info)
    {
        var bytes = await source.RenderAsync(info, () => relevance.IsWanted(info)).ConfigureAwait(false);
        return bytes is null ? null : new RasterFeature(new MRaster(bytes,
            new MRect(info.Extent.MinX, info.Extent.MinY, info.Extent.MaxX, info.Extent.MaxY)));
    }

    /// <summary>
    /// A tile is wanted while it overlaps the latest view (with a one-tile margin) at the view's level
    /// or a coarser one, which the map uses while zooming. Deeper tiles left behind by zooming out, and
    /// tiles panned out of view, are not.
    /// </summary>
    private sealed class Relevance(ITileSchema schema)
    {
        private readonly object _gate = new();
        private (MRect Extent, int Level)? _latest;

        public void Update(FetchInfo fetchInfo)
        {
            var level = BruTile.Utilities.GetNearestLevel(schema.Resolutions, fetchInfo.Resolution);
            lock (_gate) _latest = (fetchInfo.Extent, level);
        }

        public bool IsWanted(TileInfo info)
        {
            (MRect Extent, int Level)? latest;
            lock (_gate) latest = _latest;
            if (latest is not { } view) return true;
            if (info.Index.Level > view.Level) return false;
            var margin = info.Extent.Width;
            return info.Extent.MaxX >= view.Extent.MinX - margin && info.Extent.MinX <= view.Extent.MaxX + margin &&
                   info.Extent.MaxY >= view.Extent.MinY - margin && info.Extent.MinY <= view.Extent.MaxY + margin;
        }
    }
}
