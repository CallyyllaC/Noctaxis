using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Noctaxis.Core.Export;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// NX-32 (Astra R02): reading a plan from an untrusted PNG walks its chunks within fixed bounds and never decodes a pixel,
/// whatever dimensions the file claims. Malformed input is a controlled "not an image" result. Every fixture is a few
/// kilobytes at most.
/// </summary>
public sealed class PngInputSafetyTests
{
    private static readonly NoctaxisPlan Plan = new(Instant.FromUtc(2026, 10, 7, 21, 0), 52.59302, -3.64697, 293.3, 7.2, null);

    private static byte[] Chunk(string type, byte[] data, bool corruptCrc = false)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crc = Crc(chunk.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), corruptCrc ? crc ^ 1 : crc);
        return chunk;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] Header(uint width, uint height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data, width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), height);
        data[8] = 8; data[9] = 6; // 8-bit RGBA
        return data;
    }

    private static byte[] ITxt(string key, string text, bool compress = false)
    {
        var body = Encoding.UTF8.GetBytes(text);
        if (compress) body = Zlib(body);
        return [.. Encoding.Latin1.GetBytes(key), 0, (byte)(compress ? 1 : 0), 0, 0, 0, .. body];
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    private static byte[] Png(params byte[][] chunks) => [137, 80, 78, 71, 13, 10, 26, 10, .. chunks.SelectMany(chunk => chunk)];

    private static byte[] Image(params byte[][] middle) =>
        Png([Chunk("IHDR", Header(4, 4)), .. middle, Chunk("IDAT", Zlib(new byte[4 * 17])), Chunk("IEND", [])]);

    [Fact]
    public void APlanIsReadFromAnImageClaimingHugeDimensionsWithoutDecodingIt()
    {
        // 20,000 x 20,000 RGBA would be 1.6 GB of pixels; the file is a few hundred bytes and nothing is decoded.
        var png = Png(Chunk("IHDR", Header(20_000, 20_000)), Chunk("iTXt", ITxt(NoctaxisPlanImage.MetadataKey, Plan.ToJson())),
            Chunk("IDAT", Zlib(new byte[16])), Chunk("IEND", []));
        var timer = Stopwatch.StartNew();
        var result = NoctaxisPlanImage.Read(png);
        Assert.Equal(Plan, result.Plan);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), timer.Elapsed.ToString());
    }

    public static TheoryData<string, byte[]> MalformedImages => new()
    {
        { "chunk length past the end", Png(Chunk("IHDR", Header(4, 4)), [0x7F, 0xFF, 0xFF, 0xFF, (byte)'i', (byte)'T', (byte)'X', (byte)'t', 1, 2, 3]) },
        { "crc mismatch", Image(Chunk("iTXt", ITxt(NoctaxisPlanImage.MetadataKey, Plan.ToJson()), corruptCrc: true)) },
        { "no IHDR first", Png(Chunk("IDAT", Zlib(new byte[4])), Chunk("IEND", [])) },
        { "zero width", Png(Chunk("IHDR", Header(0, 4)), Chunk("IDAT", Zlib(new byte[4])), Chunk("IEND", [])) },
        { "no image data", Png(Chunk("IHDR", Header(4, 4)), Chunk("IEND", [])) },
        { "truncated, no IEND", Png(Chunk("IHDR", Header(4, 4)), Chunk("IDAT", Zlib(new byte[4]))) },
        { "text chunk over the limit", Image(Chunk("tEXt", [.. "Comment"u8, 0, .. new byte[PngMetadataWriter.MaximumTextBytes + 1]])) },
        // 8 MiB of spaces compresses to a few KiB; it is refused once the expansion passes the 1 MiB limit.
        { "compressed text bomb", Image(Chunk("iTXt", ITxt(NoctaxisPlanImage.MetadataKey, new string(' ', 8 * 1024 * 1024), compress: true))) },
        { "text chunk without a keyword", Image(Chunk("tEXt", [0, .. "text"u8])) },
    };

    [Theory]
    [MemberData(nameof(MalformedImages))]
    public void MalformedImagesAreNotAnImage(string shape, byte[] png)
    {
        var result = NoctaxisPlanImage.Read(png);
        Assert.True(result.Plan is null && result.Error == NoctaxisPlanError.NotAnImage, $"{shape}: {result.Error}");
    }

    [Fact]
    public void PlansStoredAsOtherTextChunkKindsAreRead()
    {
        Assert.Equal(Plan, NoctaxisPlanImage.Read(Image(Chunk("iTXt", ITxt(NoctaxisPlanImage.MetadataKey, Plan.ToJson(), compress: true)))).Plan);
        Assert.Equal(Plan, NoctaxisPlanImage.Read(Image(Chunk("tEXt", [.. Encoding.Latin1.GetBytes(NoctaxisPlanImage.MetadataKey), 0,
            .. Encoding.Latin1.GetBytes(Plan.ToJson())]))).Plan);
        Assert.Equal(Plan, NoctaxisPlanImage.Read(Image(Chunk("zTXt", [.. Encoding.Latin1.GetBytes(NoctaxisPlanImage.MetadataKey), 0, 0,
            .. Zlib(Encoding.Latin1.GetBytes(Plan.ToJson()))]))).Plan);
    }

    [Fact]
    public void AnImageWithoutAPlanHasNoPlanAndOtherTextIsIgnored()
    {
        Assert.Equal(NoctaxisPlanError.NoPlan, NoctaxisPlanImage.Read(Image(Chunk("tEXt", [.. "Software"u8, 0, .. "Editor"u8]))).Error);
        Assert.Equal(NoctaxisPlanError.LegacyScoutingCard,
            NoctaxisPlanImage.Read(Image(Chunk("iTXt", ITxt(NoctaxisPlanImage.LegacyScoutingCardKey, "{}")))).Error);
    }

    [Fact]
    public void AnOrdinaryHighResolutionExportStillRoundTrips()
    {
        using var bitmap = new SkiaSharp.SKBitmap(1600, 1100);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap)) canvas.Clear(new SkiaSharp.SKColor(20, 30, 40));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var png = NoctaxisPlanImage.Embed(encoded.ToArray(), Plan);
        Assert.Equal(Plan, NoctaxisPlanImage.Read(png).Plan);
        Assert.Equal(Plan.ToJson(), PngMetadataWriter.ReadText(png, NoctaxisPlanImage.MetadataKey));
    }
}
