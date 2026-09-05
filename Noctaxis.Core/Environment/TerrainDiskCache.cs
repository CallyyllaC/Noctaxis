using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;

namespace Noctaxis.Core.Environment;

public sealed record TerrainCacheUsage(long Bytes, long LimitBytes, int Entries, long Evictions,
    long BytesEvicted, long Clears, long OversizedItems);

/// <summary>Owns only terrain source directories. File leases span acquisition AND decode.</summary>
public sealed class TerrainDiskCache(IUserDataPathProvider paths, ILogger<TerrainDiskCache> logger)
{
    public const long DefaultLimitBytes = 2L * 1024 * 1024 * 1024;
    private sealed record Entry(long Size, GeoBounds? Bounds, long Access);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _maintenance = new(1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);
    private Task? _initialization;
    private TaskCompletionSource? _clearing;
    private TaskCompletionSource? _drained;
    private GeoCoordinate[] _saved = [];
    private long _bytes, _limit = DefaultLimitBytes, _clock, _evictions, _evictedBytes, _clears, _oversized;
    private long _generation;
    public string RootDirectory { get; } = Path.Combine(paths.GetApplicationDataDirectory(), "EnvironmentalData");
    public long Generation => Interlocked.Read(ref _generation);
    public event Action? Invalidated;
    public event Action? Changed;
    public TerrainCacheUsage Usage { get { lock (_gate) return new(_bytes, _limit, _entries.Count,
        _evictions, _evictedBytes, _clears, _oversized); } }
    public static bool Governs(EnvironmentalTileDescriptor descriptor) =>
        descriptor.SourceId is TerrariumTerrainProvider.SourceId or WorldCoverLandCoverProvider.SourceId;

    public Task InitializeAsync()
    {
        lock (_gate) return _initialization ??= Task.Run(() =>
        {
            lock (_gate) { Scan(); Evict(); }
            Changed?.Invoke();
        });
    }

    public async Task ConfigureAsync(long limitBytes, IEnumerable<GeoCoordinate> saved)
    {
        if (limitBytes < 0) throw new ArgumentOutOfRangeException(nameof(limitBytes));
        var points = saved.Select(point => point.Normalised()).ToArray();
        // Install persisted policy before the lazy reconciliation can evict anything.
        lock (_gate) { _limit = limitBytes; _saved = points; }
        await InitializeAsync().ConfigureAwait(false);
        await Task.Run(() => { lock (_gate) Evict(); }).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public void SetSavedLocations(IEnumerable<GeoCoordinate> saved)
    {
        var points = saved.Select(point => point.Normalised()).ToArray();
        lock (_gate) _saved = points;
    }

    public async Task<IDisposable> LeaseAsync(EnvironmentalTileDescriptor descriptor, CancellationToken token,
        long? expectedGeneration = null)
    {
        await InitializeAsync().WaitAsync(token).ConfigureAwait(false);
        var path = PathFor(descriptor);
        while (true)
        {
            Task? wait;
            lock (_gate)
            {
                if (expectedGeneration.HasValue && expectedGeneration != _generation)
                    throw new OperationCanceledException("Terrain cache generation changed");
                // Nested acquisition inside an already leased read must be able to finish during clear.
                wait = _leases.ContainsKey(path) ? null : _clearing?.Task;
                if (wait is null)
                {
                    _leases[path] = _leases.GetValueOrDefault(path) + 1;
                    TouchCore(path);
                    return new Lease(this, path);
                }
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public void Touch(EnvironmentalTileDescriptor descriptor)
    {
        lock (_gate) TouchCore(PathFor(descriptor));
    }
    private void TouchCore(string path)
    {
        if (_entries.TryGetValue(path, out var entry)) _entries[path] = entry with { Access = ++_clock };
    }

    public async Task InsertedAsync(EnvironmentalTileDescriptor descriptor)
    {
        await InitializeAsync().ConfigureAwait(false);
        await Task.Run(() =>
        {
            lock (_gate)
            {
                var path = PathFor(descriptor);
                var size = new FileInfo(path).Length;
                _bytes -= _entries.GetValueOrDefault(path)?.Size ?? 0;
                _entries[path] = new(size, Footprint(descriptor), ++_clock);
                _bytes += size;
                if (size > _limit) _oversized++;
                Evict();
            }
        }).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private void Release(string path)
    {
        lock (_gate)
        {
            if (--_leases[path] == 0) _leases.Remove(path);
            if (_clearing is null) Evict();
            if (_leases.Count == 0) _drained?.TrySetResult();
        }
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        await _maintenance.WaitAsync().ConfigureAwait(false);
        try
        {
            Task wait;
            lock (_gate)
            {
                _clearing = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Increment(ref _generation);
                if (_leases.Count == 0) _drained.TrySetResult();
                wait = _drained.Task;
            }
            Invalidated?.Invoke();
            await wait.ConfigureAwait(false);
            await Task.Run(() =>
            {
                lock (_gate)
                {
                    Scan(); // Includes files introduced externally since startup.
                    foreach (var path in _entries.Keys.ToArray()) Delete(path);
                    _clears++;
                }
            }).ConfigureAwait(false);
            logger.LogInformation("Terrain cache manually cleared");
        }
        finally
        {
            lock (_gate)
            {
                // Drop decoded objects completed by readers that were already leased at clear start.
                Interlocked.Increment(ref _generation);
                _clearing?.TrySetResult();
                _clearing = null;
                _drained = null;
            }
            Invalidated?.Invoke();
            _maintenance.Release();
            Changed?.Invoke();
        }
    }

    public async Task ReconcileAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        await Task.Run(() => { lock (_gate) { Scan(); Evict(); } }).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private void Scan()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { TerrariumTerrainProvider.SourceId, WorldCoverLandCoverProvider.SourceId })
        {
            var directory = Path.Combine(RootDirectory, source);
            if (!Directory.Exists(directory)) continue;
            // Reparse points are never traversed: scope stays within the two owned source trees.
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (_leases.Count == 0) File.Delete(path);
                    continue;
                }
                found.Add(path);
                var info = new FileInfo(path);
                var pieces = Path.GetRelativePath(RootDirectory, path).Split(Path.DirectorySeparatorChar);
                var bounds = pieces.Length == 4 ? Footprint(new(pieces[0], pieces[1], pieces[2],
                    Path.GetFileNameWithoutExtension(pieces[3]), info.Extension)) : null;
                var access = _entries.GetValueOrDefault(path)?.Access ?? info.LastWriteTimeUtc.Ticks;
                _clock = Math.Max(_clock, access);
                _entries[path] = new(info.Length, bounds, access);
            }
        }
        foreach (var path in _entries.Keys.Where(path => !found.Contains(path)).ToArray()) _entries.Remove(path);
        _bytes = _entries.Values.Sum(entry => entry.Size);
    }

    private void Evict()
    {
        if (_bytes <= _limit) return;
        var count = _evictions;
        var bytes = _evictedBytes;
        foreach (var item in _entries
            .OrderByDescending(item => _saved.Length == 0 ? 0 : item.Value.Bounds is { } bounds
                ? _saved.Min(point => DistanceToFootprint(point, bounds)) : double.PositiveInfinity)
            .ThenBy(item => item.Value.Access).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray())
        {
            if (_bytes <= _limit) break;
            if (_leases.ContainsKey(item.Key)) break;
            Delete(item.Key);
        }
        if (_evictions > count) logger.LogInformation("Terrain cache limit reached: evicted {Count} entries / {Bytes} bytes",
            _evictions - count, _evictedBytes - bytes);
    }

    private void Delete(string path)
    {
        File.Delete(path); // Fail visibly rather than reporting an unenforced hard limit.
        if (_entries.Remove(path, out var entry))
        { _bytes -= entry.Size; _evictions++; _evictedBytes += entry.Size; }
    }

    public string PathFor(EnvironmentalTileDescriptor descriptor)
    {
        static string Safe(string value)
        {
            if (value is "" or "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Invalid terrain cache key");
            return value;
        }
        return Path.Combine(RootDirectory, Safe(descriptor.SourceId), Safe(descriptor.SourceVersion),
            Safe(descriptor.Layer), Safe(descriptor.TileId) + "." + Safe(descriptor.Extension.TrimStart('.')));
    }

    public static GeoBounds? Footprint(EnvironmentalTileDescriptor descriptor)
    {
        if (descriptor.SourceId == TerrariumTerrainProvider.SourceId && descriptor.Layer.StartsWith('z') &&
            int.TryParse(descriptor.Layer.AsSpan(1), out var zoom) && zoom is >= 0 and <= 22)
        {
            var xy = descriptor.TileId.Split('-');
            if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y)) return null;
            var n = (double)(1 << zoom);
            if (x < 0 || y < 0 || x >= n || y >= n) return null;
            double Latitude(double row) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * row / n))) * 180 / Math.PI;
            return new(Latitude(y + 1), x / n * 360 - 180, Latitude(y), (x + 1) / n * 360 - 180);
        }
        var tile = descriptor.TileId;
        if (descriptor.SourceId == WorldCoverLandCoverProvider.SourceId && tile.Length == 7 &&
            tile[0] is 'N' or 'S' && tile[3] is 'E' or 'W' &&
            int.TryParse(tile.AsSpan(1, 2), out var lat) && int.TryParse(tile.AsSpan(4, 3), out var lon))
        {
            lat *= tile[0] == 'S' ? -1 : 1; lon *= tile[3] == 'W' ? -1 : 1;
            return new(lat, lon, lat + 3, lon + 3);
        }
        return null;
    }

    public static double DistanceToFootprint(GeoCoordinate point, GeoBounds bounds)
    {
        // Nearest meridian edge on a sphere; parallel edges use the closest longitude.
        var longitude = Angles.NormaliseLongitude(point.Longitude);
        var insideLongitude = bounds.West <= bounds.East
            ? longitude >= bounds.West && longitude <= bounds.East
            : longitude >= bounds.West || longitude <= bounds.East;
        if (insideLongitude) return Angles.GreatCircleDistanceMetres(point,
            new GeoCoordinate(Math.Clamp(point.Latitude, bounds.South, bounds.North), longitude));
        double Edge(double edge)
        {
            var delta = Angles.NormaliseSignedDegrees(longitude - edge) * Math.PI / 180;
            var latitude = Math.Atan2(Math.Sin(point.Latitude * Math.PI / 180),
                Math.Cos(point.Latitude * Math.PI / 180) * Math.Cos(delta)) * 180 / Math.PI;
            return Angles.GreatCircleDistanceMetres(point,
                new GeoCoordinate(Math.Clamp(latitude, bounds.South, bounds.North), edge));
        }
        return Math.Min(Edge(bounds.West), Edge(bounds.East));
    }

    private sealed class Lease(TerrainDiskCache owner, string path) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(path); }
    }
}
