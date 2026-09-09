namespace Noctaxis.Desktop.Services;

/// <summary>Bounded encoded tile reuse. Eviction never disposes data held by an active render.</summary>
internal sealed class TileMemoryCache<TKey>(long maximumBytes) where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, byte[] Bytes)>> _entries = new();
    private readonly LinkedList<(TKey Key, byte[] Bytes)> _recent = new();
    private long _bytes;
    internal long RetainedBytes { get { lock (_gate) return _bytes; } }

    public bool TryGetValue(TKey key, out byte[] bytes)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
            bytes = null!;
            return false;
        }
    }

    public void Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_entries.Remove(key, out var node)) return;
            _recent.Remove(node);
            _bytes -= node.Value.Bytes.LongLength;
        }
    }

    public void Store(TKey key, byte[] bytes)
    {
        lock (_gate)
        {
            Remove(key);
            if (bytes.LongLength > maximumBytes) return;
            while (_recent.Last is { } oldest && _bytes + bytes.LongLength > maximumBytes)
                Remove(oldest.Value.Key);
            _entries.Add(key, _recent.AddFirst((key, bytes)));
            _bytes += bytes.LongLength;
        }
    }
}
