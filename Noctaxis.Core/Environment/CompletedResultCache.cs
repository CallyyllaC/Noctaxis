namespace Noctaxis.Core.Environment;

public sealed record CompletedResultCacheDiagnostics(int Count, int Capacity, long Hits,
    long Misses, long Evictions, long ApproximateRetainedBytes);

/// <summary>
/// Small completed-result LRU. Never owns or cancels active operations. An entry may be given a
/// lifetime, after which it is a miss: a result degraded by a transient failure (offline, a source
/// timing out) is reused only briefly, so a later request rebuilds it once the source is back.
/// </summary>
internal sealed class CompletedResultCache<TKey, TValue>(int capacity, Func<TValue, long> estimateBytes,
    TimeProvider? time = null)
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _recent = new();
    private long _hits, _misses, _evictions, _bytes;

    private readonly record struct Entry(TKey Key, TValue Value, long Bytes, DateTimeOffset? ExpiresAt);

    public CompletedResultCacheDiagnostics Diagnostics
    {
        get { lock (_gate) return new(_entries.Count, capacity, _hits, _misses, _evictions, _bytes); }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                if (node.Value.ExpiresAt is { } expiresAt && _time.GetUtcNow() >= expiresAt)
                {
                    Remove(node);
                }
                else
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    _hits++;
                    value = node.Value.Value;
                    return true;
                }
            }
            _misses++;
            value = default!;
            return false;
        }
    }

    /// <summary>Adds a result unless one is already held; <paramref name="lifetime"/> null keeps it until evicted.</summary>
    public void TryAdd(TKey key, TValue value, TimeSpan? lifetime = null)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.Value.ExpiresAt is not { } expiresAt || _time.GetUtcNow() < expiresAt) return;
                Remove(existing);
            }
            var bytes = estimateBytes(value);
            _entries.Add(key, _recent.AddFirst(new Entry(key, value, bytes, _time.GetUtcNow() + lifetime)));
            _bytes += bytes;
            while (_entries.Count > capacity)
            {
                Remove(_recent.Last!);
                _evictions++;
            }
        }
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _recent.Clear(); _bytes = 0; }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key);
        _bytes -= node.Value.Bytes;
        _recent.Remove(node);
    }
}
