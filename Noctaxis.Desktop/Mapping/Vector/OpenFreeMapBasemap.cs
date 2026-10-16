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

/// <summary>A basemap that draws its own place names and can draw them in a preferred language.</summary>
public interface ILabelledBasemap
{
    /// <summary>The preferred language, a culture identifier such as <c>en-GB</c>.</summary>
    string LabelLanguage { get; }
    /// <summary>
    /// Redraws the names in another language, in place: only tiles that contain names are rendered
    /// again. Returns false when nothing changed.
    /// </summary>
    bool ApplyLabelLanguage(string language);
}

/// <summary>
/// The Planner's street/topographic basemap: OpenFreeMap vector tiles (OpenMapTiles schema, OSM
/// data) drawn by Noctaxis in the selected theme. One source is shared by every activation, so its
/// decoded vector data survives basemap replacement and theme changes alike.
/// </summary>
public sealed class OpenFreeMapBasemap : IMapsuiBasemap, IThemeableBasemap, ILabelledBasemap
{
    /// <summary>OpenFreeMap's own attribution wording, carried by the tile source. The map credits it as
    /// <see cref="MapAttributions.OpenStreetMap"/>, <see cref="MapAttributions.OpenFreeMap"/> and
    /// <see cref="MapAttributions.OpenMapTiles"/>, each with its own link.</summary>
    public const string AttributionText = "OpenFreeMap © OpenMapTiles Data from OpenStreetMap";
    private readonly object _gate = new();
    private ThemedBasemapTileLayer[] _layers = [];
    // What this basemap's own layers were last drawn in. Another view over the same source restyles the
    // shared source first, so whether these layers need redrawing is this basemap's to remember.
    private BasemapStyle _appliedStyle;
    private string _appliedLanguage;

    public OpenFreeMapBasemap(IVectorTileFetcher? fetcher = null, BasemapStyle? style = null)
        : this(new ThemedVectorTileSource(fetcher ?? OpenFreeMapTileFetcher.CreateDefault(), style)) { }

    /// <summary>
    /// Another view of an existing basemap, such as the Picture-in-Picture map's: its own layers over
    /// the same source, so the decoded vector tiles, the disk cache and the downloads are shared.
    /// </summary>
    public OpenFreeMapBasemap(ThemedVectorTileSource source)
    {
        Source = source;
        _appliedStyle = source.Style;
        _appliedLanguage = source.LabelLanguage;
    }

    public ThemedVectorTileSource Source { get; }

    public BasemapDefinition Definition { get; } = new(BasemapKind.Street,
        new("OpenFreeMap", Credits: [MapAttributions.OpenStreetMap, MapAttributions.OpenFreeMap, MapAttributions.OpenMapTiles]));

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
        if (Equals(_appliedStyle, style)) return false;
        Source.SetStyle(style);
        _appliedStyle = style;
        ThemedBasemapTileLayer[] layers;
        lock (_gate) layers = _layers;
        // Only rendered images are discarded; the layer, its source and the vector data stay.
        foreach (var layer in layers) layer.Restyle();
        return true;
    }

    public string LabelLanguage => Source.LabelLanguage;

    public bool ApplyLabelLanguage(string language)
    {
        if (string.Equals(_appliedLanguage, language, StringComparison.Ordinal)) return false;
        Source.SetLabelLanguage(language);
        _appliedLanguage = Source.LabelLanguage;
        ThemedBasemapTileLayer[] layers;
        lock (_gate) layers = _layers;
        // Names are drawn by the reference pass (or the single full pass). The geography pass holds
        // none, so its rendered tiles stay; so does the vector data, which has every language.
        foreach (var layer in layers.Where(layer => layer.DrawsLabels)) layer.Restyle();
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

    /// <summary>Whether this layer's tiles contain names. The geography pass never does.</summary>
    internal bool DrawsLabels => Pass != BasemapPass.Geography;

    /// <summary>Times this layer's rendered tiles were discarded, by a theme or a language change.</summary>
    internal int Invalidations { get; private set; }

    internal void Restyle()
    {
        Invalidations++;
        ClearCache();
        DataHasChanged();
    }

    public override IEnumerable<IFeature> GetFeatures(MRect extent, double resolution)
    {
        var version = _source.StyleVersion;
        var labels = _source.LabelVersion;
        var stale = false;
        foreach (var feature in base.GetFeatures(extent, resolution))
        {
            if (feature is StyledRasterFeature styled &&
                (styled.StyleVersion != version || (DrawsLabels && styled.LabelVersion != labels))) { stale = true; continue; }
            yield return feature;
        }
        if (stale && Interlocked.Exchange(ref _healScheduled, 1) == 0)
            _ = Task.Run(() =>
            {
                Interlocked.Exchange(ref _healScheduled, 0);
                Restyle();
            });
    }

    private sealed class StyledRasterFeature(MRaster raster, long styleVersion, long labelVersion) : RasterFeature(raster)
    {
        public long StyleVersion { get; } = styleVersion;
        public long LabelVersion { get; } = labelVersion;
    }

    private static async Task<IFeature?> Fetch(ThemedVectorTileSource source, TileInfo info, BasemapPass pass)
    {
        var (bytes, version, labels) = await source.RenderAsync(info, pass).ConfigureAwait(false);
        return bytes is null ? null : new StyledRasterFeature(new MRaster(bytes,
            new MRect(info.Extent.MinX, info.Extent.MinY, info.Extent.MaxX, info.Extent.MaxY)), version, labels);
    }
}
