using BruTile;
using BruTile.Predefined;

namespace Noctaxis.Desktop.Mapping.Vector;

/// <summary>
/// Supplies vector tile bytes by web-mercator zoom/column/row (XYZ, row 0 at the north).
/// Returns null when the tile is unavailable right now (offline, server error), so it is retried
/// later, and an empty array when the source reports that there is no data for the tile.
/// </summary>
public interface IVectorTileFetcher
{
    Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken);

    /// <summary>Forgets a stored tile that turned out to be unreadable.</summary>
    void Discard(int zoom, int column, int row) { }
}

/// <summary>
/// BruTile source that draws vector tiles with the current <see cref="BasemapStyle"/>. Decoded
/// vector data is cached independently of the style, so a theme change re-renders from memory
/// without downloading or decoding anything again.
/// </summary>
public sealed class ThemedVectorTileSource : ILocalTileSource
{
    public const int MaximumDisplayZoom = 19;
    private readonly VectorTileRepository _tiles;
    private readonly BasemapTileRenderer _renderer;
    private readonly object _gate = new();
    private BasemapStyle _style;
    private string _labelLanguage = Noctaxis.Core.Localisation.AppLanguages.DefaultId;
    private long _styleVersion;
    private long _labelVersion;
    private long _renders;

    public ThemedVectorTileSource(IVectorTileFetcher fetcher, BasemapStyle? style = null,
        BasemapTileRenderer? renderer = null, int decodedCapacity = 96)
        : this(new VectorTileRepository(fetcher, decodedCapacity), style, renderer) { }

    /// <summary>A source over a shared repository, so its decoded tiles serve other readers too.</summary>
    public ThemedVectorTileSource(VectorTileRepository tiles, BasemapStyle? style = null,
        BasemapTileRenderer? renderer = null)
    {
        _tiles = tiles;
        _style = style ?? BasemapStyle.Default;
        _renderer = renderer ?? new BasemapTileRenderer();
    }

    /// <summary>The decoded vector tiles this source draws.</summary>
    public VectorTileRepository Tiles => _tiles;

    public ITileSchema Schema { get; } =
        new GlobalSphericalMercator("png", YAxis.OSM, 0, MaximumDisplayZoom, "Noctaxis themed vector basemap");
    public string Name => "OpenFreeMap vector tiles, drawn in the Noctaxis theme";
    public Attribution Attribution { get; } = new(OpenFreeMapBasemap.AttributionText, MapAttributions.OpenFreeMap.Url!.AbsoluteUri);

    public BasemapStyle Style { get { lock (_gate) return _style; } }
    public long StyleVersion => Interlocked.Read(ref _styleVersion);
    internal long RenderCount => Interlocked.Read(ref _renders);
    internal long DecodeCount => _tiles.DecodeCount;

    /// <summary>Adopts a style. Returns false when it is the style already in use.</summary>
    public bool SetStyle(BasemapStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        lock (_gate)
        {
            if (_style == style) return false;
            _style = style;
            Interlocked.Increment(ref _styleVersion);
            return true;
        }
    }

    /// <summary>The language place names are preferred in; see <see cref="BasemapLabelNames"/>.</summary>
    public string LabelLanguage { get { lock (_gate) return _labelLanguage; } }
    public long LabelVersion => Interlocked.Read(ref _labelVersion);

    /// <summary>
    /// Adopts a label language. Returns false when it is the language already in use. Only tiles
    /// that draw names are affected; the vector data holds every language and is kept.
    /// </summary>
    public bool SetLabelLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        lock (_gate)
        {
            if (string.Equals(_labelLanguage, language, StringComparison.Ordinal)) return false;
            _labelLanguage = language;
            Interlocked.Increment(ref _labelVersion);
            return true;
        }
    }

    public async Task<byte[]?> GetTileAsync(TileInfo tileInfo) => (await RenderAsync(tileInfo).ConfigureAwait(false)).Bytes;

    /// <summary>
    /// Renders one pass of a tile and reports the style and label versions it was drawn with. Every
    /// pass is drawn from the same decoded vector tile, so splitting the basemap never fetches or
    /// decodes twice.
    /// </summary>
    internal async Task<(byte[]? Bytes, long StyleVersion, long LabelVersion)> RenderAsync(TileInfo tileInfo,
        BasemapPass pass = BasemapPass.Full)
    {
        var index = tileInfo.Index;
        if (index.Level < 0 || index.Level > MaximumDisplayZoom) return (null, StyleVersion, LabelVersion);
        var request = new BasemapTileRequest(index.Level, index.Col, index.Row);
        var tile = await _tiles.GetAsync(request.SourceZoom, request.SourceColumn, request.SourceRow)
            .ConfigureAwait(false);
        if (tile is null) return (null, StyleVersion, LabelVersion);
        return await Task.Run(() =>
        {
            while (true)
            {
                BasemapStyle style;
                string language;
                long version, labels;
                lock (_gate) { style = _style; version = _styleVersion; language = _labelLanguage; labels = _labelVersion; }
                var bytes = _renderer.RenderPng(tile, request, style, pass, language);
                Interlocked.Increment(ref _renders);
                // A theme change during the render would leave this tile in the old style, and a
                // language change would leave its names in the old language: draw it again.
                if (version == StyleVersion && (pass == BasemapPass.Geography || labels == LabelVersion))
                    return ((byte[]?)bytes, version, labels);
            }
        }).ConfigureAwait(false);
    }
}
