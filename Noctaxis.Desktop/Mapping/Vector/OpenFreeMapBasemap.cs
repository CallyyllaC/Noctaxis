using BruTile;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;

namespace Noctaxis.Desktop.Mapping.Vector;

/// <summary>A basemap whose appearance is drawn from theme roles rather than fixed imagery.</summary>
public interface IThemeableBasemap
{
    BasemapStyle Style { get; }
    /// <summary>Restyles the existing native layers in place; returns false when nothing changed.</summary>
    bool ApplyStyle(BasemapStyle style);
}

/// <summary>
/// The Planner's street/topographic basemap: OpenFreeMap vector tiles (OpenMapTiles schema, OSM
/// data) drawn by Noctaxis in the selected theme. One source is shared by every activation, so its
/// decoded vector data survives basemap replacement and theme changes alike.
/// </summary>
public sealed class OpenFreeMapBasemap : IMapsuiBasemap, IThemeableBasemap
{
    public const string AttributionText = "OpenFreeMap © OpenMapTiles Data from OpenStreetMap";
    private readonly object _gate = new();
    private ThemedBasemapTileLayer[] _layers = [];

    public OpenFreeMapBasemap(IVectorTileFetcher? fetcher = null, BasemapStyle? style = null) =>
        Source = new ThemedVectorTileSource(fetcher ?? OpenFreeMapTileFetcher.CreateDefault(), style);

    public ThemedVectorTileSource Source { get; }

    public BasemapDefinition Definition { get; } = new(BasemapKind.Street,
        new("OpenFreeMap", AttributionText, "https://openfreemap.org",
            "ODbL", "https://opendatacommons.org/licenses/odbl/1-0/"));

    public BasemapStyle Style => Source.Style;

    /// <summary>The whole basemap in one layer, for a map without environmental layers.</summary>
    public IReadOnlyList<ILayer> CreateLayers()
    {
        var layer = new ThemedBasemapTileLayer(Source, BasemapPass.Full) { Name = "basemap-openfreemap" };
        lock (_gate) _layers = [layer];
        return [layer];
    }

    /// <summary>
    /// The basemap as two passes over one shared source: geography below the environmental layers,
    /// reference (boundaries, roads, labels) above them. See <see cref="BasemapPass"/>.
    /// </summary>
    public BasemapLayers CreateLayerSet()
    {
        var geography = new ThemedBasemapTileLayer(Source, BasemapPass.Geography) { Name = "basemap-openfreemap" };
        var reference = new ThemedBasemapTileLayer(Source, BasemapPass.Reference) { Name = "basemap-openfreemap-reference" };
        lock (_gate) _layers = [geography, reference];
        return new BasemapLayers([geography], [reference]);
    }

    public bool ApplyStyle(BasemapStyle style)
    {
        if (!Source.SetStyle(style)) return false;
        ThemedBasemapTileLayer[] layers;
        lock (_gate) layers = _layers;
        // Only rendered images are discarded; the layer, its source and the vector data stay.
        foreach (var layer in layers) layer.Restyle();
        return true;
    }
}

/// <summary>
/// Tile layer that never shows a tile drawn in a previous style. Mapsui publishes a fetched tile
/// after the source returns it, so a tile rendered just before a theme change can arrive after the
/// cache was cleared; such tiles are hidden and trigger one more refresh instead of lingering.
/// </summary>
internal sealed class ThemedBasemapTileLayer : TileLayer
{
    private readonly ThemedVectorTileSource _source;
    private int _healScheduled;

    public ThemedBasemapTileLayer(ThemedVectorTileSource source, BasemapPass pass)
        : base(source, fetchTileAsFeature: info => Fetch(source, info, pass))
    {
        _source = source;
        Pass = pass;
    }

    internal BasemapPass Pass { get; }

    internal void Restyle()
    {
        ClearCache();
        DataHasChanged();
    }

    public override IEnumerable<IFeature> GetFeatures(MRect extent, double resolution)
    {
        var version = _source.StyleVersion;
        var stale = false;
        foreach (var feature in base.GetFeatures(extent, resolution))
        {
            if (feature is StyledRasterFeature styled && styled.StyleVersion != version) { stale = true; continue; }
            yield return feature;
        }
        if (stale && Interlocked.Exchange(ref _healScheduled, 1) == 0)
            _ = Task.Run(() =>
            {
                Interlocked.Exchange(ref _healScheduled, 0);
                Restyle();
            });
    }

    private sealed class StyledRasterFeature(MRaster raster, long styleVersion) : RasterFeature(raster)
    {
        public long StyleVersion { get; } = styleVersion;
    }

    private static async Task<IFeature?> Fetch(ThemedVectorTileSource source, TileInfo info, BasemapPass pass)
    {
        var (bytes, version) = await source.RenderAsync(info, pass).ConfigureAwait(false);
        return bytes is null ? null : new StyledRasterFeature(new MRaster(bytes,
            new MRect(info.Extent.MinX, info.Extent.MinY, info.Extent.MaxX, info.Extent.MaxY)), version);
    }
}
