namespace Noctaxis.Core.Environment;

public sealed record CompletedResultCacheDiagnostics(int Count, int Capacity, long Hits,
    long Misses, long Evictions, long ApproximateRetainedBytes);

/// <summary>Small completed-result LRU. Never owns or cancels active operations.</summary>
internal sealed class CompletedResultCache<TKey, TValue>(int capacity, Func<TValue, long> estimateBytes)
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Bytes)>> _entries = new();
    private readonly LinkedList<(TKey Key, TValue Value, long Bytes)> _recent = new();
    private long _hits, _misses, _evictions, _bytes;

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
                _recent.Remove(node);
                _recent.AddFirst(node);
                _hits++;
                value = node.Value.Value;
                return true;
            }
            _misses++;
            value = default!;
            return false;
        }
    }

    public void TryAdd(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) return;
            var bytes = estimateBytes(value);
            _entries.Add(key, _recent.AddFirst((key, value, bytes)));
            _bytes += bytes;
            while (_entries.Count > capacity)
            {
                var oldest = _recent.Last!;
                _entries.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Bytes;
                _recent.RemoveLast();
                _evictions++;
            }
        }
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _recent.Clear(); _bytes = 0; }
    }
}
