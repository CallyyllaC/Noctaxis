using BruTile;
using BruTile.Predefined;
using Noctaxis.Core.LightPollution;
using SkiaSharp;
using System.Diagnostics;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.Mapping;

/// <summary>BruTile 6 async local source. Mapsui 5 consumes encoded rasters; no URLs/HTTP here.</summary>
public sealed class LightPollutionRasterSource : ILocalTileSource, IDisposable
{
    public const int MaximumNativeZoom = 8;
    private readonly LightPollutionDataProvider _provider;
    private readonly LorenzInstallation _installation;
    private ILightPollutionColorMap _colors;
    private readonly object _visualGate = new();
    private long _paletteRevision;
    public long PaletteRevision { get { lock (_visualGate) return _paletteRevision; } }
    public string PaletteId { get { lock (_visualGate) return _colors.Id; } }
    private readonly LightPollutionDisplayScale _scale = new();
    private readonly LightPollutionCache<(long Generation, int Z, int X, int Y, string Scale, string Palette, int PaletteVersion), byte[]> _cache = new(64);
    private readonly CancellationTokenSource _lifetime = new();
    public int CachedTileCount { get { lock (_visualGate) return _cache.Count; } }
    private LightPollutionRasterDiagnostics? _diagnostics;
    internal LightPollutionRasterDiagnostics? Diagnostics
    {
        get => _diagnostics;
        set { _diagnostics = value; _cache.Diagnostics = value?.Cache; }
    }
    public ITileSchema Schema { get; } = new GlobalSphericalMercator(0, MaximumNativeZoom);
    public string Name => "Local Lorenz 2025 numerical atlas";
    public Attribution Attribution { get; } = new(LorenzAtlas.Name, LorenzAtlas.InformationUrl);
    public LightPollutionRasterSource(LightPollutionDataProvider provider, LorenzInstallation installation, ILightPollutionColorMap colors)
    {
        _provider = provider; _installation = installation; _colors = colors;
        installation.Changed += Clear;
    }
    private void Clear(object? sender, EventArgs args) { lock (_visualGate) _cache.Clear(); }
    public bool SetColorMap(ILightPollutionColorMap colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        lock (_visualGate)
        {
            if (_colors.Id == colors.Id && _colors.Version == colors.Version) return false;
            _colors = colors; _paletteRevision++; _cache.Clear(); return true;
        }
    }
    public async Task<byte[]?> GetTileAsync(TileInfo tileInfo) => (await GetRasterAsync(tileInfo)).Bytes;
    internal Task<(byte[]? Bytes, long Revision)> GetRasterAsync(TileInfo tileInfo) => Task.Run(async () =>
    {
        var trace = _diagnostics;
        var started = trace is null ? 0 : Stopwatch.GetTimestamp();
        trace?.Requested?.Invoke(tileInfo.Index);
        try
        {
            var z = tileInfo.Index.Level; var x = tileInfo.Index.Col; var y = tileInfo.Index.Row;
            if (z < 0 || z > MaximumNativeZoom) return ((byte[]?)null, PaletteRevision);
            var n = Math.Pow(2, z);
            static double Latitude(double row, double tiles) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * row / tiles))) * 180 / Math.PI;
            // The atlas lease is held only to look up the cache and gather the source tiles this map
            // tile needs. Sampling, colouring and encoding then run without it, so tiles are prepared
            // in parallel instead of one at a time behind the lease.
            long generation, acquired;
            LightPollutionDataProvider.Region region;
            using (var session = await _provider.OpenAsync(_lifetime.Token))
            {
                if (session is null) { if (trace is not null) Interlocked.Increment(ref trace.EmptyResults); return ((byte[]?)null, PaletteRevision); }
                acquired = trace is null ? 0 : Stopwatch.GetTimestamp();
                generation = session.Generation;
                lock (_visualGate)
                    if (_cache.TryGet((generation, z, x, y, LightPollutionDisplayScale.Version, _colors.Id, _colors.Version), out var cached))
                    {
                        trace?.Completed?.Invoke(tileInfo.Index, true, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds, 0, cached.Length);
                        return ((byte[]?)cached, _paletteRevision);
                    }
                region = await session.ReadRegionAsync(Latitude(y + 1, n), Latitude(y, n), x / n * 360 - 180, (x + 1) / n * 360 - 180,
                    overview: z <= 4);
            }
            if (trace is not null) Interlocked.Add(ref trace.ReadWaitTicks, acquired - started);
            var sampleStarted = trace is null ? 0 : Stopwatch.GetTimestamp();
            var latitudes = new double[256];
            var longitudes = new double[256];
            for (var i = 0; i < 256; i++)
            {
                latitudes[i] = Latitude(y + (i + .5) / 256, n);
                longitudes[i] = (x + (i + .5) / 256) / n * 360 - 180;
            }
            _lifetime.Token.ThrowIfCancellationRequested();
            var intensity = new double[256 * 256];
            region.SampleGrid(latitudes, longitudes, intensity);
            for (var i = 0; i < intensity.Length; i++)
                if (!double.IsNaN(intensity[i])) intensity[i] = _scale.Normalize(intensity[i]);
            if (trace is not null) Interlocked.Add(ref trace.SampleTicks, Stopwatch.GetTimestamp() - sampleStarted);
            // A palette is immutable and captured once per tile. If it changes while this tile is
            // being coloured, colour the same intensities again with the new one.
            while (true)
            {
                ILightPollutionColorMap colors; long revision;
                lock (_visualGate) { colors = _colors; revision = _paletteRevision; }
                var key = (generation, z, x, y, LightPollutionDisplayScale.Version, colors.Id, colors.Version);
                var paletteStarted = trace is null ? 0 : Stopwatch.GetTimestamp();
                var pixels = new SKColor[256 * 256];
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = double.IsNaN(intensity[i]) ? SKColors.Transparent : colors.Map(intensity[i]);
                using var bitmap = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
                bitmap.Pixels = pixels;
                var encodeStarted = trace is null ? 0 : Stopwatch.GetTimestamp();
                using var pixmap = bitmap.PeekPixels();
                using var data = pixmap?.Encode(FastPng);
                if (data is null) return ((byte[]?)null, revision);
                var bytes = data.ToArray();
                if (trace is not null)
                {
                    Interlocked.Add(ref trace.PaletteTicks, encodeStarted - paletteStarted);
                    Interlocked.Add(ref trace.EncodeTicks, Stopwatch.GetTimestamp() - encodeStarted);
                    Interlocked.Increment(ref trace.Generated);
                }
                var encodeMs = trace is null ? 0 : Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
                lock (_visualGate)
                {
                    if (revision != _paletteRevision) { if (trace is not null) Interlocked.Increment(ref trace.RejectedRevisions); continue; }
                    _cache.Add(key, bytes);
                    trace?.Completed?.Invoke(tileInfo.Index, false, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds, encodeMs, bytes.Length);
                    return ((byte[]?)bytes, revision);
                }
            }
        }
        catch (OperationCanceledException) { return ((byte[]?)null, PaletteRevision); }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { return ((byte[]?)null, PaletteRevision); }
        catch { if (trace is not null) Interlocked.Increment(ref trace.Failures); throw; }
    });
    // Fast compression: the image is kept briefly and decoded once, so a smaller file is not worth
    // the encoding time a thorough filter search and deflate cost on every tile.
    private static readonly SKPngEncoderOptions FastPng = new(SKPngEncoderFilterFlags.Sub, 1);
    public void Dispose()
    {
        _lifetime.Cancel(); _installation.Changed -= Clear;
        // The active read owns the cache until it observes cancellation; do not clear concurrently.
    }
}
