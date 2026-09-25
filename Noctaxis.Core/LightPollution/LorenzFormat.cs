using System.IO.Compression;

namespace Noctaxis.Core.LightPollution;

public static class LorenzAtlas
{
    public const string ProviderId = "david-lorenz-2025";
    public const string Name = "David Lorenz Light Pollution Atlas 2025";
    public const string InformationUrl = "https://djlorenz.github.io/astronomy/lp/";
    public const int Side = 600, Columns = 72, Rows = 28, TileCount = Columns * Rows;
    public const int OverviewSide = 60, OverviewWidth = Columns * OverviewSide, OverviewHeight = Rows * OverviewSide;
    public static string FileName(int x, int y) => $"binary_tile_{x}_{y}.dat.gz";
    public static Uri Source(int x, int y) => new($"https://djlorenz.github.io/astronomy/binary_tiles/2025/{FileName(x, y)}");
    public static double ToLpi(int value) => (5.0 / 195.0) * (Math.Exp(.0195 * value) - 1.0);
    public static double WrapLongitude(double longitude) => ((longitude + 180) % 360 + 360) % 360;
}

public static class LorenzDecoder
{
    public const int PayloadLength = LorenzAtlas.Side * LorenzAtlas.Side + 1;

    public static async Task<int[]> DecodeGzipAsync(Stream input, CancellationToken cancellationToken = default)
    {
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        var bytes = new byte[PayloadLength];
        try { await gzip.ReadExactlyAsync(bytes, cancellationToken); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated Lorenz tile.", ex); }
        if (await gzip.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new InvalidDataException("Overlong Lorenz tile.");
        return Decode(bytes);
    }

    public static int[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != PayloadLength) throw new InvalidDataException("Lorenz tiles require 360001 decompressed bytes.");
        var values = new int[PayloadLength - 1];
        values[0] = 128 * (sbyte)bytes[0] + (sbyte)bytes[1];
        for (var row = 0; row < 600; row++)
        {
            var start = row * 600;
            if (row > 0) values[start] = values[start - 600] + (sbyte)bytes[start + 1];
            for (var col = 1; col < 600; col++) values[start + col] = values[start + col - 1] + (sbyte)bytes[start + col + 1];
        }
        return values;
    }
}

/// <summary>Count-bounded LRU; owners serialize access and choose capacity from fixed tile sizes.</summary>
public sealed class LightPollutionCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _items = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = [];
    public int Count => _items.Count;
    public Noctaxis.Core.Diagnostics.LightPollutionCacheMetrics? Diagnostics { get; set; }
    public bool TryGet(TKey key, out TValue value)
    {
        if (!_items.TryGetValue(key, out var node)) { if (Diagnostics is { } miss) miss.Misses++; value = default!; return false; }
        if (Diagnostics is { } hit) hit.Hits++;
        _order.Remove(node); _order.AddFirst(node); value = node.Value.Value; return true;
    }
    public void Add(TKey key, TValue value)
    {
        if (_items.Remove(key, out var old)) _order.Remove(old);
        _items.Add(key, _order.AddFirst((key, value)));
        while (_items.Count > capacity && _order.Last is { } last)
        {
            if (Diagnostics is { } trace) { trace.Evictions++; trace.Evicted?.Invoke(last.Value.Key); }
            _items.Remove(last.Value.Key); _order.RemoveLast();
        }
        if (Diagnostics is { } metrics) metrics.PeakCount = Math.Max(metrics.PeakCount, Count);
    }
    public void Clear() { _items.Clear(); _order.Clear(); }
}
