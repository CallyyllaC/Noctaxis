using BruTile;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Mapping.Vector;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// D06 (phase-0 audit): malformed vector-tile bytes, from the server or the disk cache, are malformed input
/// (<see cref="InvalidDataException"/>) and never an arithmetic or range exception; lengths are checked before
/// anything is sliced or allocated. The decoded cache never keeps a failed load, removes only its own
/// registration, and does not swallow failures that are not malformed input.
/// </summary>
public sealed class VectorTileMalformedInputTests
{
    public static TheoryData<string, byte[]> MalformedTiles => new()
    {
        // Astra's case: a layer length of 2^31, beyond int.
        { "length beyond int", [0x1A, 0x80, 0x80, 0x80, 0x80, 0x08] },
        // int.MaxValue length after a field, so position + length overflows int.
        { "length overflowing the position", [0x08, 0x01, 0x1A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07] },
        // A layer extent of 2^31.
        { "extent beyond int", [0x1A, 0x06, 0x28, 0x80, 0x80, 0x80, 0x80, 0x08] },
        // A feature tag key index of uint.MaxValue, which is negative as an int, in a layer with one key and value.
        { "tag index beyond int", [0x1A, 0x11, 0x12, 0x08, 0x12, 0x06, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x00,
            0x1A, 0x01, 0x6B, 0x22, 0x02, 0x20, 0x01] },
        // A packed field whose declared length runs past its layer.
        { "packed length past the end", [0x1A, 0x04, 0x12, 0x02, 0x12, 0x7F] },
    };

    [Theory]
    [MemberData(nameof(MalformedTiles))]
    public void MalformedLengthsAreMalformedInput(string shape, byte[] bytes)
    {
        var failure = Record.Exception(() => VectorTile.Decode(bytes));
        Assert.True(failure is InvalidDataException, $"{shape}: {failure?.GetType().Name ?? "no exception"}");
    }

    [Fact]
    public void ArbitraryBytesDecodeOrAreMalformedInput()
    {
        // Bounded and reproducible: 20,000 short inputs, many of them starting like real layers and features.
        var random = new Random(20261016);
        var prefixes = new byte[][] { [], [0x1A], [0x1A, 0x10, 0x12], [0x1A, 0x10, 0x28], [0x1A, 0x10, 0x22] };
        for (var run = 0; run < 20_000; run++)
        {
            var prefix = prefixes[run % prefixes.Length];
            var bytes = new byte[prefix.Length + random.Next(0, 24)];
            prefix.CopyTo(bytes, 0);
            random.NextBytes(bytes.AsSpan(prefix.Length));
            var failure = Record.Exception(() => VectorTile.Decode(bytes));
            Assert.True(failure is null or InvalidDataException,
                $"{Convert.ToHexString(bytes)}: {failure?.GetType().Name}");
        }
    }

    [Fact]
    public async Task AMalformedTileIsDiscardedAndFetchedAgainOnTheNextRequest()
    {
        var fetcher = new ScriptedFetcher(_ => Task.FromResult<byte[]?>([0x1A, 0x80, 0x80, 0x80, 0x80, 0x08]));
        var source = new ThemedVectorTileSource(fetcher, renderer: new BasemapTileRenderer(1));

        Assert.Null(await source.GetTileAsync(Tile(12, 1, 1)));
        Assert.Null(await source.GetTileAsync(Tile(12, 1, 1)));

        Assert.Equal(2, fetcher.Calls);
        Assert.Equal(2, fetcher.Discarded.Count);
    }

    [Fact]
    public async Task AnUnexpectedFailureReachesTheCallerAndIsNotKept()
    {
        var fail = true;
        var fetcher = new ScriptedFetcher(_ => fail
            ? throw new InvalidOperationException("Not malformed input")
            : Task.FromResult<byte[]?>([]));
        var source = new ThemedVectorTileSource(fetcher, renderer: new BasemapTileRenderer(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTileAsync(Tile(12, 1, 1)));
        Assert.Empty(fetcher.Discarded);

        fail = false;
        Assert.NotNull(await source.GetTileAsync(Tile(12, 1, 1)));
        Assert.Equal(2, fetcher.Calls);
    }

    [Fact]
    public async Task AFailedLoadNeverRemovesANewerLoadOfTheSameTile()
    {
        var gates = new Dictionary<(int, int, int), Queue<TaskCompletionSource<byte[]?>>>();
        var fetcher = new ScriptedFetcher(request =>
        {
            var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates)
            {
                if (!gates.TryGetValue(request, out var queue)) gates[request] = queue = new();
                queue.Enqueue(completion);
            }
            return completion.Task;
        });
        var source = new ThemedVectorTileSource(fetcher, renderer: new BasemapTileRenderer(1), decodedCapacity: 4);
        TaskCompletionSource<byte[]?> Next((int, int, int) request) { lock (gates) return gates[request].Dequeue(); }

        var first = source.GetTileAsync(Tile(12, 0, 0));
        // Four other tiles evict the first registration while its load is still running.
        var others = Enumerable.Range(1, 4).Select(column => source.GetTileAsync(Tile(12, column, 0))).ToArray();
        await WaitForCallsAsync(fetcher, 5);
        var second = source.GetTileAsync(Tile(12, 0, 0));
        await WaitForCallsAsync(fetcher, 6);

        // The evicted load fails as malformed input; the newer load of the same tile must stay registered.
        Next((12, 0, 0)).SetResult([0x1A, 0x80, 0x80, 0x80, 0x80, 0x08]);
        Assert.Null(await first);
        var third = source.GetTileAsync(Tile(12, 0, 0));
        await Task.Delay(50);
        Assert.Equal(6, fetcher.Calls);

        Next((12, 0, 0)).SetResult([]);
        Assert.NotNull(await second);
        Assert.NotNull(await third);
        foreach (var column in Enumerable.Range(1, 4)) Next((12, column, 0)).SetResult([]);
        await Task.WhenAll(others);
    }

    private static async Task WaitForCallsAsync(ScriptedFetcher fetcher, int calls)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fetcher.Calls < calls && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(calls, fetcher.Calls);
    }

    private static TileInfo Tile(int level, int column, int row) =>
        new() { Index = new TileIndex(column, row, level), Extent = new Extent(0, 0, 1, 1) };

    private sealed class ScriptedFetcher(Func<(int Zoom, int Column, int Row), Task<byte[]?>> fetch) : IVectorTileFetcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public List<(int, int, int)> Discarded { get; } = [];

        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return fetch((zoom, column, row));
        }

        public void Discard(int zoom, int column, int row)
        {
            lock (Discarded) Discarded.Add((zoom, column, row));
        }
    }
}
