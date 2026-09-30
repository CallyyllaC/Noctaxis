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
            var started = owner._decoded.Diagnostics is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await using var stream = File.OpenRead(Path.Combine(directory, "binary", LorenzAtlas.FileName(x, y)));
                tile = await LorenzDecoder.DecodeGzipAsync(stream, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { owner._installation.InvalidateRead(); throw; }
            if (owner._decoded.Diagnostics is { } metrics)
                Interlocked.Add(ref metrics.LoadTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
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
        /// <summary>
        /// Reads every source tile needed to sample the given area (degrees; west to east may not wrap),
        /// so sampling can run after this lease is released instead of holding the atlas for a whole
        /// map tile. With <paramref name="overview"/> the whole-world overview is used instead.
        /// </summary>
        public async ValueTask<Region> ReadRegionAsync(double south, double north, double west, double east, bool overview)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (overview)
            {
                await Value(0, 0, overview: true);
                return new Region(null, owner._overview);
            }
            const int resolution = 120, width = 360 * resolution, height = 140 * resolution;
            var tiles = new Dictionary<(int X, int Y), int[]>();
            var southern = Math.Max(south, -65);
            var northern = Math.Min(north, 75);
            if (southern < northern)
            {
                // The same pixel arithmetic as SampleAsync, widened by one pixel for bilinear neighbours.
                var firstRow = Math.Clamp((int)Math.Floor((southern + 65) * resolution - .5), 0, height - 1);
                var lastRow = Math.Clamp((int)Math.Floor((northern + 65) * resolution - .5) + 1, 0, height - 1);
                var firstColumn = (int)Math.Floor(LorenzAtlas.WrapLongitude(west) * resolution - .5);
                var columns = (int)Math.Ceiling((east - west) * resolution) + 2;
                var tileColumns = new SortedSet<int>();
                for (var i = 0; i <= columns; i++) tileColumns.Add(((firstColumn + i) % width + width) % width / 600);
                for (var row = firstRow / 600; row <= lastRow / 600; row++)
                    foreach (var column in tileColumns)
                        tiles[(column + 1, row + 1)] = await GetSourceTileAsync(column + 1, row + 1);
            }
            return new Region(tiles, null);
        }

        public void Dispose() { if (_disposed) return; _disposed = true; owner._installation.DataGate.Release(); }
    }

    /// <summary>
    /// Immutable source data for one area, sampled synchronously and without the atlas lease.
    /// <see cref="Sample"/> gives exactly what <see cref="ReadSession.SampleAsync"/> gives there.
    /// </summary>
    public sealed class Region
    {
        private readonly Dictionary<(int X, int Y), int[]>? _tiles;
        private readonly float[]? _overview;
        internal Region(Dictionary<(int X, int Y), int[]>? tiles, float[]? overview) { _tiles = tiles; _overview = overview; }

        public double? Sample(double latitude, double longitude, bool bilinear = false)
        {
            if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude < -65 || latitude >= 75) return null;
            var resolution = _overview is not null ? 12 : 120;
            var width = 360 * resolution;
            var height = 140 * resolution;
            var gx = LorenzAtlas.WrapLongitude(longitude) * resolution;
            var gy = (latitude + 65) * resolution;
            if (!bilinear) return Value((int)Math.Floor(gx), Math.Min(height - 1, (int)Math.Floor(gy)));
            gx -= .5; gy -= .5;
            var x0 = (int)Math.Floor(gx); var y0 = (int)Math.Floor(gy);
            var tx = gx - x0; var ty = gy - y0;
            var a = Value((x0 + width) % width, Math.Clamp(y0, 0, height - 1));
            var b = Value((x0 + 1 + width) % width, Math.Clamp(y0, 0, height - 1));
            var c = Value((x0 + width) % width, Math.Clamp(y0 + 1, 0, height - 1));
            var d = Value((x0 + 1 + width) % width, Math.Clamp(y0 + 1, 0, height - 1));
            return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
        }

        private double Value(int x, int y) => _overview is { } overview
            ? overview[y * LorenzAtlas.OverviewWidth + x]
            : LorenzAtlas.ToLpi(_tiles![(x / 600 + 1, y / 600 + 1)][y % 600 * 600 + x % 600]);

        /// <summary>
        /// Bilinear samples for every (latitude, longitude) pair of a grid, row-major into
        /// <paramref name="output"/>, NaN where there is no data. Each value is exactly what
        /// <see cref="Sample"/> gives; row and column terms are simply worked out once, not per pixel.
        /// </summary>
        public void SampleGrid(ReadOnlySpan<double> latitudes, ReadOnlySpan<double> longitudes, Span<double> output)
        {
            if (output.Length != latitudes.Length * longitudes.Length)
                throw new ArgumentException("The output must hold one value per grid point.", nameof(output));
            var resolution = _overview is not null ? 12 : 120;
            var width = 360 * resolution;
            var height = 140 * resolution;
            var columns = longitudes.Length;
            var x0 = new int[columns]; var x1 = new int[columns]; var tx = new double[columns]; var valid = new bool[columns];
            for (var i = 0; i < columns; i++)
            {
                valid[i] = double.IsFinite(longitudes[i]);
                if (!valid[i]) continue;
                var gx = LorenzAtlas.WrapLongitude(longitudes[i]) * resolution - .5;
                var column = (int)Math.Floor(gx);
                tx[i] = gx - column;
                x0[i] = (column + width) % width;
                x1[i] = (column + 1 + width) % width;
            }
            for (var row = 0; row < latitudes.Length; row++)
            {
                var values = output.Slice(row * columns, columns);
                var latitude = latitudes[row];
                if (!double.IsFinite(latitude) || latitude < -65 || latitude >= 75) { values.Fill(double.NaN); continue; }
                var gy = (latitude + 65) * resolution - .5;
                var y0 = (int)Math.Floor(gy);
                var ty = gy - y0;
                var r0 = Math.Clamp(y0, 0, height - 1);
                var r1 = Math.Clamp(y0 + 1, 0, height - 1);
                for (var i = 0; i < columns; i++)
                {
                    if (!valid[i]) { values[i] = double.NaN; continue; }
                    double a = Value(x0[i], r0), b = Value(x1[i], r0), c = Value(x0[i], r1), d = Value(x1[i], r1);
                    values[i] = (a * (1 - tx[i]) + b * tx[i]) * (1 - ty) + (c * (1 - tx[i]) + d * tx[i]) * ty;
                }
            }
        }
    }
}
