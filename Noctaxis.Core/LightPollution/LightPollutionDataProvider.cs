namespace Noctaxis.Core.LightPollution;

public interface ILightPollutionDataProvider
{
    Task<double?> SampleAsync(double latitude, double longitude, bool bilinear = false, CancellationToken cancellationToken = default);
}

/// <summary>Local-only numerical reader. Read leases serialize bounded caches with remove/reinstall.
/// No HttpClient is accessible through this provider.</summary>
public sealed class LightPollutionDataProvider : ILightPollutionDataProvider, IAsyncDisposable
{
    private readonly LorenzInstallation _installation;
    private readonly LightPollutionCache<(int X, int Y), int[]> _decoded = new(24);
    private float[]? _overview;
    private bool _disposed;
    public int DecodedTileCount => _decoded.Count;
    public bool HasOverview => _overview is not null;
    public Noctaxis.Core.Diagnostics.LightPollutionCacheMetrics? Diagnostics { get => _decoded.Diagnostics; set => _decoded.Diagnostics = value; }
    public LightPollutionDataProvider(LorenzInstallation installation)
    {
        _installation = installation;
        installation.Changed += Clear;
    }
    private void Clear(object? sender, EventArgs args) { _decoded.Clear(); _overview = null; }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _installation.Changed -= Clear;
        await _installation.DataGate.WaitAsync();
        try { Clear(null, EventArgs.Empty); }
        finally { _installation.DataGate.Release(); }
    }

    public async Task<ReadSession?> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _installation.DataGate.WaitAsync(cancellationToken);
        if (_disposed) { _installation.DataGate.Release(); throw new ObjectDisposedException(nameof(LightPollutionDataProvider)); }
        if (_installation.CurrentDirectory is not { } path) { _installation.DataGate.Release(); return null; }
        return new(this, path, cancellationToken);
    }
    public async Task<double?> SampleAsync(double latitude, double longitude, bool bilinear = false, CancellationToken cancellationToken = default)
    {
        using var session = await OpenAsync(cancellationToken);
        return session is null ? null : await session.SampleAsync(latitude, longitude, bilinear);
    }

    public sealed class ReadSession : IDisposable
    {
        private readonly LightPollutionDataProvider owner;
        private readonly string directory;
        private readonly CancellationToken ct;
        internal ReadSession(LightPollutionDataProvider provider, string path, CancellationToken cancellationToken)
        { owner = provider; directory = path; ct = cancellationToken; }
        private bool _disposed;
        public long Generation => owner._installation.Generation;
        public async ValueTask<int[]> GetSourceTileAsync(int x, int y)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (x is < 1 or > 72 || y is < 1 or > 28) throw new ArgumentOutOfRangeException(nameof(x));
            if (owner._decoded.TryGet((x, y), out var tile)) return tile;
            try
            {
                await using var stream = File.OpenRead(Path.Combine(directory, "binary", LorenzAtlas.FileName(x, y)));
                tile = await LorenzDecoder.DecodeGzipAsync(stream, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { owner._installation.InvalidateRead(); throw; }
            owner._decoded.Add((x, y), tile);
            return tile;
        }

        public async ValueTask<double?> SampleAsync(double latitude, double longitude, bool bilinear = false, bool overview = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude < -65 || latitude >= 75) return null;
            var resolution = overview ? 12 : 120;
            var width = 360 * resolution;
            var height = 140 * resolution;
            var gx = LorenzAtlas.WrapLongitude(longitude) * resolution;
            var gy = (latitude + 65) * resolution;
            if (!bilinear) return await Value((int)Math.Floor(gx), Math.Min(height - 1, (int)Math.Floor(gy)), overview);
            gx -= .5; gy -= .5;
            var x0 = (int)Math.Floor(gx); var y0 = (int)Math.Floor(gy);
            var tx = gx - x0; var ty = gy - y0;
            var a = await Value((x0 + width) % width, Math.Clamp(y0, 0, height - 1), overview);
            var b = await Value((x0 + 1 + width) % width, Math.Clamp(y0, 0, height - 1), overview);
            var c = await Value((x0 + width) % width, Math.Clamp(y0 + 1, 0, height - 1), overview);
            var d = await Value((x0 + 1 + width) % width, Math.Clamp(y0 + 1, 0, height - 1), overview);
            return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
        }

        private async ValueTask<double> Value(int x, int y, bool overview)
        {
            if (!overview)
            {
                var tile = await GetSourceTileAsync(x / 600 + 1, y / 600 + 1);
                return LorenzAtlas.ToLpi(tile[y % 600 * 600 + x % 600]);
            }
            if (owner._overview is null)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "overview.f32"), ct);
                    if (bytes.Length != LorenzAtlas.OverviewWidth * LorenzAtlas.OverviewHeight * 4) throw new InvalidDataException("Invalid numerical overview.");
                    var values = new float[bytes.Length / 4];
                    for (var i = 0; i < values.Length; i++)
                    {
                        values[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4));
                        if (!float.IsFinite(values[i]) || values[i] < 0) throw new InvalidDataException("Invalid numerical overview value.");
                    }
                    owner._overview = values;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { owner._installation.InvalidateRead(); throw; }
            }
            return owner._overview[y * LorenzAtlas.OverviewWidth + x];
        }
        public void Dispose() { if (_disposed) return; _disposed = true; owner._installation.DataGate.Release(); }
    }
}
