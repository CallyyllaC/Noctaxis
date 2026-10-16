using System.Diagnostics;
using BruTile;
using BruTile.Predefined;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;
using SkiaSharp;

namespace Noctaxis.Desktop.Mapping;

/// <summary>Counters for the terrain relief layer; they cost nothing on the draw path.</summary>
public sealed record TerrainReliefMetrics(long ReliefComputations, long ReliefCacheHits, long TilesRendered,
    long UnavailableTiles, long AbandonedTiles, double ShadeMilliseconds, double RenderMilliseconds);

/// <summary>
/// Map tiles of terrain relief drawn from Terrarium elevation, for the Planner's Environmental group.
/// </summary>
/// <remarks>
/// <para>Elevation comes from <see cref="ITerrariumTileSource"/>, the terrain calculations' own
/// download and disk cache, so the relief shows the terrain the calculations use and nothing is
/// downloaded or stored twice. Only derived relief is kept here: a bounded in-memory cache of shaded
/// grids keyed by tile, algorithm version and terrain-cache generation, so clearing stored terrain
/// also retires everything derived from it.</para>
/// <para>The image is neutral: light slopes are white and shaded slopes black, each partly
/// transparent, and flat ground is fully transparent. It lightens and darkens whatever is beneath,
/// so it needs no theme colour and does not depend on the theme; the theme and the user adjust only
/// the layer's opacity.</para>
/// <para>Map zooms deeper than <see cref="ITerrariumTileSource.DetailZoom"/> (the calculations' zoom,
/// 12) enlarge part of a tile at that zoom: in many regions Terrarium's deeper tiles are coarser
/// sources enlarged in steps, which shading turns into blocks. Work for a tile the map has moved
/// away from is abandoned before shading.</para>
/// </remarks>
public sealed class TerrainReliefTileSource : ILocalTileSource
{
    public const int MaximumDisplayZoom = 19;
    // Shaded slopes darken more than lit slopes lighten, as in a conventional hillshade, but both
    // stay translucent so the land's own colour and any light pollution above still read through.
    internal const double ShadowGain = 1.1;
    internal const double HighlightGain = 1.5;
    internal const double MaximumAlpha = .7;

    /// <summary>
    /// How strongly shaded relief is drawn: how far a slope's shading darkens or lightens, and the most it may
    /// cover. <see cref="Normal"/> is the Planner's usual relief; <see cref="Enhanced"/> separates the tones a
    /// little more for a small view (the Picture-in-Picture map). It applies when a tile is drawn from the shared
    /// shaded grid, so it costs nothing else: no elevation is loaded and nothing is shaded again.
    /// </summary>
    public readonly record struct ReliefTone(double ShadowGain, double HighlightGain, double MaximumAlpha)
    {
        public static ReliefTone Normal { get; } = new(TerrainReliefTileSource.ShadowGain, TerrainReliefTileSource.HighlightGain, TerrainReliefTileSource.MaximumAlpha);
        public static ReliefTone Enhanced { get; } = new(1.45, 1.95, .82);
    }
    private const int TileSize = TerrainRelief.TileSize;
    private readonly ITerrariumTileSource _elevation;
    private readonly BoundedDecodedRasterCache<ReliefKey, float[]> _relief;
    private long _generation;
    private long _computations, _cacheHits, _rendered, _unavailable, _abandoned, _shadeTicks, _renderTicks;

    private readonly record struct ReliefKey(TerrariumTileKey Tile, long Generation, int Version);

    public TerrainReliefTileSource(ITerrariumTileSource elevation, int reliefCapacity = 192)
    {
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        // 192 shaded grids of 256 x 256 floats is 48 MiB at most: a few screens of tiles and their neighbours.
        _relief = new BoundedDecodedRasterCache<ReliefKey, float[]>(Math.Max(16, reliefCapacity));
        _generation = elevation.Generation;
    }

    public ITileSchema Schema { get; } =
        new GlobalSphericalMercator("png", YAxis.OSM, 0, MaximumDisplayZoom, "Noctaxis terrain relief");
    public string Name => "Terrain relief";
    public Attribution Attribution { get; } = new(TerrainReliefMapBinding.AttributionText, TerrainReliefMapBinding.AttributionUrl);

    public TerrainReliefMetrics Metrics => new(Interlocked.Read(ref _computations), Interlocked.Read(ref _cacheHits),
        Interlocked.Read(ref _rendered), Interlocked.Read(ref _unavailable), Interlocked.Read(ref _abandoned),
        Interlocked.Read(ref _shadeTicks) * 1000d / Stopwatch.Frequency,
        Interlocked.Read(ref _renderTicks) * 1000d / Stopwatch.Frequency);

    public Task<byte[]?> GetTileAsync(TileInfo tileInfo) => RenderAsync(tileInfo, null);

    /// <summary>
    /// The PNG for a map tile, or null when there is no relief for it right now: elevation is
    /// unavailable, or <paramref name="wanted"/> reports the map no longer needs the tile. Null is not
    /// remembered by the map, so the tile is requested again when it is next in view.
    /// </summary>
    internal async Task<byte[]?> RenderAsync(TileInfo tileInfo, Func<bool>? wanted, ReliefTone? tone = null)
    {
        var index = tileInfo.Index;
        if (index.Level is < 0 or > MaximumDisplayZoom) return null;
        var sourceZoom = Math.Min(index.Level, Math.Clamp(_elevation.DetailZoom, 0, ITerrariumTileSource.MaximumZoom));
        var shift = index.Level - sourceZoom;
        var key = new TerrariumTileKey(sourceZoom, index.Col >> shift, index.Row >> shift);
        var relief = await GetReliefAsync(key, wanted).ConfigureAwait(false);
        if (relief is null) return null;
        if (wanted?.Invoke() == false) { Interlocked.Increment(ref _abandoned); return null; }
        var scale = 1 << shift;
        var (subColumn, subRow) = (index.Col - key.X * scale, index.Row - key.Y * scale);
        return await Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            var png = Encode(relief, scale, subColumn, subRow, tone);
            Interlocked.Add(ref _renderTicks, timer.ElapsedTicks);
            Interlocked.Increment(ref _rendered);
            return png;
        }).ConfigureAwait(false);
    }

    private async Task<float[]?> GetReliefAsync(TerrariumTileKey tile, Func<bool>? wanted)
    {
        var generation = _elevation.Generation;
        // Stored terrain was cleared: nothing shaded from the previous data may be reused.
        if (Interlocked.Exchange(ref _generation, generation) != generation) _relief.Clear();
        var key = new ReliefKey(tile, generation, TerrainRelief.AlgorithmVersion);
        if (_relief.TryGetValue(key, out var cached)) { Interlocked.Increment(ref _cacheHits); return cached; }
        return await _relief.GetOrCreateAsync(key, _ => ComputeAsync(tile, wanted), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<float[]?> ComputeAsync(TerrariumTileKey tile, Func<bool>? wanted)
    {
        var centre = await LoadAsync(tile).ConfigureAwait(false);
        if (centre is null) { Interlocked.Increment(ref _unavailable); return null; }
        // Neighbours are only worth loading for a tile that is still on its way to the screen.
        if (wanted?.Invoke() == false) { Interlocked.Increment(ref _abandoned); return null; }
        var count = 1 << tile.Zoom;
        var neighbours = new Task<TerrariumTile?>[9];
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var y = tile.Y + dy;
                // Longitude wraps; beyond a pole there is no neighbour and the edge is extrapolated.
                neighbours[(dy + 1) * 3 + dx + 1] = (dx, dy) == (0, 0) ? Task.FromResult<TerrariumTile?>(centre)
                    : y < 0 || y >= count ? Task.FromResult<TerrariumTile?>(null)
                    : LoadAsync(new TerrariumTileKey(tile.Zoom, ((tile.X + dx) % count + count) % count, y));
            }
        var loaded = await Task.WhenAll(neighbours).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            var padded = TerrainRelief.Pad(centre, (dx, dy) => loaded[(dy + 1) * 3 + dx + 1]);
            var relief = TerrainRelief.Shade(padded, tile.Zoom, tile.Y);
            Interlocked.Add(ref _shadeTicks, timer.ElapsedTicks);
            Interlocked.Increment(ref _computations);
            return relief;
        }).ConfigureAwait(false);
    }

    // Terrain relief is supplementary: any failure to obtain elevation simply leaves no relief.
    private async Task<TerrariumTile?> LoadAsync(TerrariumTileKey key)
    {
        try { return (await _elevation.GetTerrainTileAsync(key, CancellationToken.None).ConfigureAwait(false)).Tile; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Draws relief as translucent white (lit) and black (shaded) over transparency.</summary>
    internal static byte[]? Encode(float[] relief, int scale, int subColumn, int subRow, ReliefTone? tone = null)
    {
        var (shadowGain, highlightGain, maximumAlpha) = tone ?? ReliefTone.Normal;
        using var full = new SKBitmap(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = new byte[TileSize * TileSize * 4];
        for (var i = 0; i < relief.Length; i++)
        {
            var value = relief[i];
            if (value == 0) continue;
            var alpha = (byte)Math.Round(255 * Math.Min(maximumAlpha, Math.Abs(value) * (value > 0 ? highlightGain : shadowGain)));
            // Premultiplied: white at alpha a is (a, a, a, a); black is (0, 0, 0, a).
            var grey = value > 0 ? alpha : (byte)0;
            pixels[i * 4] = grey;
            pixels[i * 4 + 1] = grey;
            pixels[i * 4 + 2] = grey;
            pixels[i * 4 + 3] = alpha;
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, full.GetPixels(), pixels.Length);
        if (scale == 1) return EncodePng(full);
        // Deeper than the detail zoom: enlarge this tile's share of that tile's relief.
        var size = (float)TileSize / scale;
        using var enlarged = new SKBitmap(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(enlarged))
        {
            canvas.Clear(SKColors.Transparent);
            using var image = SKImage.FromBitmap(full);
            canvas.DrawImage(image, SKRect.Create(subColumn * size, subRow * size, size, size),
                SKRect.Create(0, 0, TileSize, TileSize), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        return EncodePng(enlarged);
    }

    // Fast compression: the image lives briefly in memory and is decoded once, so a smaller file is not
    // worth the encoding time a thorough filter search and deflate would cost on every tile.
    private static readonly SKPngEncoderOptions FastPng = new(SKPngEncoderFilterFlags.Sub, 1);

    private static byte[]? EncodePng(SKBitmap bitmap)
    {
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap?.Encode(FastPng);
        return data?.ToArray();
    }
}
