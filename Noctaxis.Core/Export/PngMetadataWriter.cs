using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Hjg.Pngcs;
using Hjg.Pngcs.Chunks;

namespace Noctaxis.Core.Export;

public static class PngMetadataWriter
{
    public static byte[] AddText(byte[] png, string key, string value)
    {
        using var input = new MemoryStream(png, writable: false);
        using var output = new MemoryStream();
        var reader = new PngReader(input);
        var writer = new PngWriter(output, reader.ImgInfo) { ShouldCloseStream = false };
        writer.CopyChunksFirst(reader, ChunkCopyBehaviour.COPY_ALL_SAFE);
        writer.GetMetadata().SetText(key, value, false, false);
        for (var row = 0; row < reader.ImgInfo.Rows; row++)
            writer.WriteRow(reader.ReadRowInt(row), row);
        reader.End();
        writer.CopyChunksLast(reader, ChunkCopyBehaviour.COPY_ALL_SAFE);
        writer.End();
        return output.ToArray();
    }

    /// <summary>The largest text chunk read (compressed or not), and the most any compressed text may expand to.</summary>
    public const int MaximumTextBytes = 1024 * 1024;

    /// <summary>The text stored under <paramref name="key"/>, or an empty string when there is none.</summary>
    public static string ReadText(byte[] png, string key) => ReadTexts(png, key)[key];

    /// <summary>
    /// The texts stored under <paramref name="keys"/> (empty for an absent key), from one bounded walk of the PNG's
    /// chunks: no pixel is decoded, whatever size the image claims (NX-32). Every chunk length is checked against the
    /// bytes actually present and its CRC verified; the file must start with IHDR, hold image data and end with IEND.
    /// tEXt, zTXt and iTXt are read; a text chunk over <see cref="MaximumTextBytes"/>, or compressed text expanding past
    /// it, is refused. Anything malformed is an <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadTexts(byte[] png, params string[] keys)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new InvalidDataException("Not a PNG.");
        var found = keys.ToDictionary(key => key, _ => string.Empty, StringComparer.Ordinal);
        var position = signature.Length;
        var first = true;
        var imageData = false;
        while (true)
        {
            if (png.Length - position < 12) throw new InvalidDataException("Truncated PNG chunk.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            var type = Encoding.ASCII.GetString(png, position + 4, 4);
            if (length > (uint)(png.Length - position - 12)) throw new InvalidDataException("PNG chunk runs past the end of the file.");
            var data = png.AsSpan(position + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + (int)length));
            if (Crc32(png.AsSpan(position + 4, 4 + (int)length)) != crc) throw new InvalidDataException("PNG chunk CRC mismatch.");
            if (first && type != "IHDR") throw new InvalidDataException("PNG does not start with IHDR.");
            if (first && (length != 13 || BinaryPrimitives.ReadUInt32BigEndian(data) == 0 || BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == 0))
                throw new InvalidDataException("PNG header is invalid.");
            first = false;
            switch (type)
            {
                case "IDAT": imageData = true; break;
                case "IEND":
                    if (!imageData) throw new InvalidDataException("PNG holds no image data.");
                    return found;
                case "tEXt" or "zTXt" or "iTXt":
                    if (length > MaximumTextBytes) throw new InvalidDataException("PNG text chunk is too large.");
                    var (key, text) = ParseText(type, data);
                    if (found.TryGetValue(key, out var existing) && existing.Length == 0) found[key] = text;
                    break;
            }
            position += 12 + (int)length;
        }
    }

    private static (string Key, string Text) ParseText(string type, ReadOnlySpan<byte> data)
    {
        var keyEnd = data.IndexOf((byte)0);
        if (keyEnd <= 0) throw new InvalidDataException("PNG text chunk has no keyword.");
        var key = Encoding.Latin1.GetString(data[..keyEnd]);
        var rest = data[(keyEnd + 1)..];
        switch (type)
        {
            case "tEXt":
                return (key, Encoding.Latin1.GetString(rest));
            case "zTXt":
                if (rest.Length < 1 || rest[0] != 0) throw new InvalidDataException("Unknown PNG text compression.");
                return (key, Encoding.Latin1.GetString(Inflate(rest[1..])));
            default: // iTXt: compression flag, method, language tag \0, translated keyword \0, UTF-8 text.
                if (rest.Length < 2) throw new InvalidDataException("Truncated PNG iTXt chunk.");
                var compressed = rest[0] == 1;
                if (compressed && rest[1] != 0) throw new InvalidDataException("Unknown PNG text compression.");
                rest = rest[2..];
                var language = rest.IndexOf((byte)0);
                if (language < 0) throw new InvalidDataException("Truncated PNG iTXt chunk.");
                rest = rest[(language + 1)..];
                var translated = rest.IndexOf((byte)0);
                if (translated < 0) throw new InvalidDataException("Truncated PNG iTXt chunk.");
                rest = rest[(translated + 1)..];
                return (key, Encoding.UTF8.GetString(compressed ? Inflate(rest) : rest));
        }
    }

    /// <summary>zlib-compressed text, stopping as soon as it would expand past <see cref="MaximumTextBytes"/>.</summary>
    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        using var input = new ZLibStream(new MemoryStream(compressed.ToArray(), writable: false), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaximumTextBytes) throw new InvalidDataException("PNG text expands too far.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
