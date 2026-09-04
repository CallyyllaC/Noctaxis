using System.Collections.Concurrent;

namespace Noctaxis.Core.Environment;

/// <summary>
/// Thread-safe bounded cache for decoded immutable rasters. Concurrent callers share one decode,
/// while approximate LRU eviction prevents nearby observer moves from growing DEM memory forever.
/// </summary>
public sealed class BoundedDecodedRasterCache<TKey, TValue> where TKey : notnull
{
    private sealed class Entry(Lazy<Task<TValue?>> value, long access)
    {
        public Lazy<Task<TValue?>> Value { get; } = value;
        public long LastAccess = access;
    }

    private readonly ConcurrentDictionary<TKey, Entry> _entries = new();
    private readonly object _evictionGate = new();
    private readonly int _capacity;
    private readonly Action<TValue>? _onEvicted;
    private long _clock;

    public BoundedDecodedRasterCache(int capacity, Action<TValue>? onEvicted = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _onEvicted = onEvicted;
    }

    public int Count => _entries.Count;
    public int Capacity => _capacity;

    public bool TryGetValue(TKey key, out TValue? value)
    {
        value = default;
        if (!_entries.TryGetValue(key, out var entry) || !entry.Value.IsValueCreated ||
            !entry.Value.Value.IsCompletedSuccessfully || entry.Value.Value.Result is not { } cached)
            return false;
        Volatile.Write(ref entry.LastAccess, Interlocked.Increment(ref _clock));
        value = cached;
        return true;
    }

    public async Task<TValue?> GetOrCreateAsync(TKey key,
        Func<CancellationToken, Task<TValue?>> factory, CancellationToken cancellationToken)
    {
        var access = Interlocked.Increment(ref _clock);
        var entry = _entries.GetOrAdd(key, _ => new Entry(
            new Lazy<Task<TValue?>>(() => factory(CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication), access));
        Volatile.Write(ref entry.LastAccess, access);
        TValue? value;
        try
        {
            value = await entry.Value.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (entry.Value.IsValueCreated && entry.Value.Value.IsCompleted &&
                !entry.Value.Value.IsCompletedSuccessfully)
                _entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));
            throw;
        }
        if (value is null)
        {
            _entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));
            return default;
        }
        EvictIfNeeded(key);
        return value;
    }

    private void EvictIfNeeded(TKey protectedKey)
    {
        if (_entries.Count <= _capacity) return;
        lock (_evictionGate)
        {
            while (_entries.Count > _capacity)
            {
                KeyValuePair<TKey, Entry>? candidate = null;
                foreach (var item in _entries)
                {
                    if (EqualityComparer<TKey>.Default.Equals(item.Key, protectedKey) ||
                        !item.Value.Value.IsValueCreated || !item.Value.Value.Value.IsCompletedSuccessfully)
                        continue;
                    if (candidate is null || Volatile.Read(ref item.Value.LastAccess) <
                        Volatile.Read(ref candidate.Value.Value.LastAccess)) candidate = item;
                }
                if (candidate is null || !_entries.TryRemove(candidate.Value)) break;
                if (_onEvicted is not null && candidate.Value.Value.Value.Value.Result is { } evicted)
                    _onEvicted(evicted);
            }
        }
    }
}
