using System.Runtime.CompilerServices;

namespace Noctaxis.Desktop.Mapping.Vector;

/// <summary>
/// Decoded OpenFreeMap vector tiles, shared by everything that reads them: the Planner basemap, saved-location map
/// data and location descriptions. One fetcher (one HTTP client, one disk cache, one download per tile at a time) and
/// one decoded cache serve them all, so a tile one of them has read is not downloaded or decoded again by another.
/// </summary>
/// <remarks>
/// A tile that is unavailable right now (offline, server error) is not remembered, so a later request tries again; an
/// unreadable stored tile is discarded from the disk cache so a retry downloads it. An empty result means the source
/// reported no data for the tile.
/// </remarks>
public sealed class VectorTileRepository
{
    public const int DefaultDecodedCapacity = 128;
    private static readonly Lazy<VectorTileRepository> SharedOpenFreeMap =
        new(() => new VectorTileRepository(OpenFreeMapTileFetcher.CreateDefault()));
    private readonly IVectorTileFetcher _fetcher;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<(int, int, int), Task<VectorTile?>> _decoded = [];
    private readonly LinkedList<(int, int, int)> _recency = [];
    private long _decodes;
    private long _requests;

    public VectorTileRepository(IVectorTileFetcher fetcher, int decodedCapacity = DefaultDecodedCapacity)
    {
        _fetcher = fetcher;
        _capacity = Math.Max(4, decodedCapacity);
    }

    /// <summary>The application's one OpenFreeMap repository (NX-38 cache folder, shared client).</summary>
    public static VectorTileRepository OpenFreeMap => SharedOpenFreeMap.Value;

    internal long DecodeCount => Interlocked.Read(ref _decodes);
    /// <summary>Tiles asked of the fetcher (disk or network), for tests and diagnostics.</summary>
    internal long FetchRequestCount => Interlocked.Read(ref _requests);

    /// <summary>The decoded tile, <see cref="VectorTile.Empty"/> when the source has no data for it, or null when it
    /// is unavailable right now.</summary>
    public Task<VectorTile?> GetAsync(int zoom, int column, int row, CancellationToken cancellationToken = default)
    {
        var key = (zoom, column, row);
        Task<VectorTile?> task;
        lock (_gate)
        {
            if (_decoded.TryGetValue(key, out var existing))
            {
                _recency.Remove(key);
                _recency.AddFirst(key);
                task = existing;
            }
            else
            {
                var registration = new StrongBox<Task<VectorTile?>>();
                task = LoadAsync(zoom, column, row, registration);
                registration.Value = task;
                _decoded[key] = task;
                _recency.AddFirst(key);
                while (_recency.Count > _capacity)
                {
                    _decoded.Remove(_recency.Last!.Value);
                    _recency.RemoveLast();
                }
            }
        }
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task<VectorTile?> LoadAsync(int zoom, int column, int row, StrongBox<Task<VectorTile?>> registration)
    {
        // Always complete after GetAsync has registered this task, so an unavailable result
        // below removes the registration instead of racing ahead of it.
        await Task.Yield();
        VectorTile? tile = null;
        try
        {
            Interlocked.Increment(ref _requests);
            var bytes = await _fetcher.FetchAsync(zoom, column, row, CancellationToken.None).ConfigureAwait(false);
            if (bytes is not null)
            {
                tile = bytes.Length == 0 ? VectorTile.Empty : VectorTile.Decode(bytes);
                Interlocked.Increment(ref _decodes);
            }
        }
        catch (InvalidDataException)
        {
            // Malformed input, from the server or the disk cache: forget the stored bytes so a retry fetches.
            _fetcher.Discard(zoom, column, row);
        }
        finally
        {
            // Unavailable tiles, and failures that are not malformed input (which still reach the caller), are
            // not remembered, so a later request tries again. Only this load's own registration is removed:
            // after an eviction the key may already belong to a newer load of the same tile.
            if (tile is null)
                lock (_gate)
                    if (_decoded.TryGetValue((zoom, column, row), out var current) &&
                        ReferenceEquals(current, registration.Value))
                    {
                        _decoded.Remove((zoom, column, row));
                        _recency.Remove((zoom, column, row));
                    }
        }
        return tile;
    }
}
