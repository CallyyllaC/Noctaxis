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
            using var session = await _provider.OpenAsync(_lifetime.Token);
            if (session is null) { if (trace is not null) Interlocked.Increment(ref trace.EmptyResults); return ((byte[]?)null, PaletteRevision); }
            var acquired = trace is null ? 0 : Stopwatch.GetTimestamp();
            var z = tileInfo.Index.Level; var x = tileInfo.Index.Col; var y = tileInfo.Index.Row;
            if (z < 0 || z > MaximumNativeZoom) return ((byte[]?)null, PaletteRevision);
            // A palette is immutable and captured once per tile. If it changes while this
            // tile is being prepared, retry using the same numerical lease/cache.
            while (true)
            {
                ILightPollutionColorMap colors; long revision;
                lock (_visualGate) { colors = _colors; revision = _paletteRevision; }
                var key = (session.Generation, z, x, y, LightPollutionDisplayScale.Version, colors.Id, colors.Version);
                lock (_visualGate) if (_cache.TryGet(key, out var cached))
                {
                    trace?.Completed?.Invoke(tileInfo.Index, true, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds, 0, cached.Length);
                    return ((byte[]?)cached, revision);
                }
                using var bitmap = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
                var pixels = new SKColor[256 * 256];
                var n = Math.Pow(2, z);
                for (var py = 0; py < 256; py++)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (y + (py + .5) / 256) / n))) * 180 / Math.PI;
                    for (var px = 0; px < 256; px++)
                    {
                        var longitude = (x + (px + .5) / 256) / n * 360 - 180;
                        var lpi = await session.SampleAsync(latitude, longitude, bilinear: true, overview: z <= 4);
                        pixels[py * 256 + px] = lpi is { } value ? colors.Map(_scale.Normalize(value)) : SKColors.Transparent;
                    }
                }
                bitmap.Pixels = pixels;
                using var image = SKImage.FromBitmap(bitmap);
                var encodeStarted = trace is null ? 0 : Stopwatch.GetTimestamp();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                var bytes = data.ToArray();
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
    public void Dispose()
    {
        _lifetime.Cancel(); _installation.Changed -= Clear;
        // The active read owns the cache until it observes cancellation; do not clear concurrently.
    }
}
