using System.IO.Compression;
using System.Text.Json;
using Noctaxis.Core.LightPollution;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Mapping;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class LightPollutionNumericalTests
{
    [Theory]
    [InlineData(0, 0, 0)] [InlineData(127, 1, 0)] [InlineData(500, 0, -1)]
    [InlineData(1000, -1, 1)] [InlineData(-130, 1, -1)]
    public async Task FullSignedDeltaDecodeMatchesEverySample(int initial, int dx, int dy)
    {
        var raw = AtlasTestData.Encode((x, y) => initial + dx * x + dy * y);
        var decoded = LorenzDecoder.Decode(raw);
        for (var y = 0; y < 600; y++) for (var x = 0; x < 600; x++) Assert.Equal(initial + dx * x + dy * y, decoded[y * 600 + x]);
        using var gzip = new MemoryStream(AtlasTestData.Gzip(raw));
        Assert.Equal(decoded, await LorenzDecoder.DecodeGzipAsync(gzip));
    }
    [Fact]
    public async Task SignedInitialLowByteAndMalformedPayloadsAreNotReinterpreted()
    {
        var raw = new byte[LorenzDecoder.PayloadLength]; raw[0] = 1; raw[1] = 255;
        Assert.All(LorenzDecoder.Decode(raw), value => Assert.Equal(127, value));
        foreach (var length in new[] { 0, 2, 360000, 360002 })
        {
            Assert.Throws<InvalidDataException>(() => LorenzDecoder.Decode(new byte[length]));
            using var stream = new MemoryStream(AtlasTestData.Gzip(new byte[length]));
            await Assert.ThrowsAsync<InvalidDataException>(() => LorenzDecoder.DecodeGzipAsync(stream));
        }
        using var corrupt = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => LorenzDecoder.DecodeGzipAsync(corrupt));
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(100, 0.154581732835623)]
    public void LpiConversion(int compressed, double expected) => Assert.Equal(expected, LorenzAtlas.ToLpi(compressed), 12);

    [Fact]
    public void DisplayScaleIsFixedMonotonicLogInterpolatedAndSaturates()
    {
        var scale = new LightPollutionDisplayScale();
        Assert.Equal(0, scale.Normalize(0)); Assert.Equal(0, scale.Normalize(.01));
        Assert.Equal(1, scale.Normalize(46.77)); Assert.Equal(1, scale.Normalize(500));
        Assert.Equal(1d / 13, scale.Normalize(.06), 12);
        Assert.Equal(1.5 / 13, scale.Normalize(Math.Sqrt(.06 * .11)), 12);
        var previous = 0d;
        for (var i = 0; i < 10000; i++) { var value = scale.Normalize(i / 100d); Assert.True(value >= previous); previous = value; }
        Assert.Equal(scale.Normalize(1.234), new LightPollutionDisplayScale().Normalize(1.234));
    }
    [Fact]
    public void GrayscaleIsIndependentAndContinuous()
    {
        ILightPollutionColorMap palette = new GrayscaleLightPollutionColorMap();
        Assert.Equal(SKColors.Black, palette.Map(0)); Assert.Equal(SKColors.White, palette.Map(1));
        Assert.Equal(new SKColor(128, 128, 128, 255), palette.Map(.5));
        Assert.True(palette.Map(.2).Red < palette.Map(.8).Red);
    }
}

internal sealed class AtlasTestData : IUserDataPathProvider, IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Noctaxis-Lorenz-" + Guid.NewGuid().ToString("N"));
    public string GetApplicationDataDirectory() => Root;
    public static byte[] Encode(Func<int, int, int> value)
    {
        var raw = new byte[LorenzDecoder.PayloadLength];
        var first = value(0, 0); raw[0] = unchecked((byte)(sbyte)(first / 128)); raw[1] = unchecked((byte)(sbyte)(first % 128));
        for (var y = 0; y < 600; y++) for (var x = 0; x < 600; x++)
        {
            if (x == 0 && y == 0) continue;
            var delta = value(x, y) - (x == 0 ? value(0, y - 1) : value(x - 1, y));
            raw[y * 600 + x + 1] = unchecked((byte)checked((sbyte)delta));
        }
        return raw;
    }
    public static byte[] Gzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw);
        return output.ToArray();
    }
    public async Task<string> SeedAsync(LorenzInstallation store)
    {
        var name = Guid.NewGuid().ToString("N"); var directory = Path.Combine(store.StorageDirectory, name);
        Directory.CreateDirectory(Path.Combine(directory, "binary"));
        var bytes = Gzip(Encode((_, _) => 100));
        var sizes = new Dictionary<string, long>();
        foreach (var tile in LorenzInstallation.ExpectedTiles())
        {
            var filename = LorenzAtlas.FileName(tile.X, tile.Y);
            await File.WriteAllBytesAsync(Path.Combine(directory, "binary", filename), bytes);
            sizes.Add(filename, bytes.Length);
        }
        var overview = new byte[LorenzAtlas.OverviewWidth * LorenzAtlas.OverviewHeight * 4];
        for (var i = 0; i < overview.Length; i += 4)
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(overview.AsSpan(i, 4), (float)LorenzAtlas.ToLpi(100));
        await File.WriteAllBytesAsync(Path.Combine(directory, "overview.f32"), overview);
        var manifest = new LorenzManifest(LorenzAtlas.ProviderId, LorenzAtlas.Name, 2025, "signed-int8-delta-v1", 1,
            1d / 120, -65, 75, 2016, sizes, sizes.Values.Sum(), true);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
        await File.WriteAllTextAsync(Path.Combine(store.StorageDirectory, "current"), name);
        await store.DetectAsync();
        Assert.True(store.IsAvailable);
        return directory;
    }
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root) && Path.GetFileName(Root).StartsWith("Noctaxis-Lorenz-", StringComparison.Ordinal)) Directory.Delete(Root, true);
        return ValueTask.CompletedTask;
    }
}
