using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Noctaxis.Desktop.Mapping.Preview;

/// <summary>
/// Compact on-disk form of a <see cref="LocationPreviewScene"/>, stored beside the assets it was built
/// from with the key of those inputs. It is a pure cache: any mismatch or damage means rebuild.
/// </summary>
public static class LocationPreviewSceneCodec
{
    /// <summary>Bump when the builder's output changes, so older caches are rebuilt.</summary>
    public const int Version = 1;
    public const string FileName = "preview-scene.bin";
    private static readonly byte[] Magic = "NXPREV"u8.ToArray();

    public static byte[] Encode(LocationPreviewScene scene, string inputKey)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
        {
            writer.Write(Magic); writer.Write(Version); writer.Write(inputKey);
            writer.Write(scene.LocationId.ToByteArray()); writer.Write(scene.Width); writer.Write(scene.Height);
            writer.Write(scene.Pin.X); writer.Write(scene.Pin.Y); writer.Write(scene.Fingerprint);
            foreach (var mask in new[] { scene.Water, scene.Vegetation, scene.BuiltUp, scene.Settlement }) writer.Write(mask);
            writer.Write(scene.Lines.Count);
            foreach (var line in scene.Lines)
            {
                writer.Write((byte)line.Class); writer.Write(line.Points.Length);
                foreach (var p in line.Points) { writer.Write(p.X); writer.Write(p.Y); }
            }
            writer.Write(scene.Lights.Count);
            foreach (var l in scene.Lights) { writer.Write(l.X); writer.Write(l.Y); writer.Write(l.Intensity); }
        }
        return output.ToArray();
    }

    /// <summary>The cached scene when it was built from exactly <paramref name="inputKey"/>; otherwise null.</summary>
    public static LocationPreviewScene? TryDecode(byte[] bytes, string inputKey)
    {
        try
        {
            using var reader = new BinaryReader(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress), Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != Version ||
                reader.ReadString() != inputKey) return null;
            var id = new Guid(reader.ReadBytes(16));
            var width = reader.ReadInt32(); var height = reader.ReadInt32();
            if (width is < 2 or > 8192 || height is < 2 or > 8192) return null;
            var pin = new SKPoint(reader.ReadSingle(), reader.ReadSingle());
            var fingerprint = reader.ReadString();
            var count = Math.Max(1, width / LocationPreviewScene.MaskScale) * Math.Max(1, height / LocationPreviewScene.MaskScale);
            byte[] Mask() { var m = reader.ReadBytes(count); return m.Length == count ? m : throw new EndOfStreamException(); }
            var water = Mask(); var vegetation = Mask(); var builtUp = Mask(); var settlement = Mask();
            var lines = new PreviewLine[reader.ReadInt32()];
            for (var i = 0; i < lines.Length; i++)
            {
                var lineClass = (PreviewLineClass)reader.ReadByte();
                var points = new SKPoint[reader.ReadInt32()];
                for (var p = 0; p < points.Length; p++) points[p] = new SKPoint(reader.ReadSingle(), reader.ReadSingle());
                lines[i] = new(lineClass, points);
            }
            var lights = new PreviewLight[reader.ReadInt32()];
            for (var i = 0; i < lights.Length; i++) lights[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return new LocationPreviewScene(id, width, height, pin, water, vegetation, builtUp, settlement, lines, lights, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or OverflowException or OutOfMemoryException or ArgumentException)
        {
            return null;
        }
    }
}
