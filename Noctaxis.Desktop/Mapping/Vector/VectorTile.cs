using System.Text;

namespace Noctaxis.Desktop.Mapping.Vector;

public enum VectorTileGeometryType { Unknown = 0, Point = 1, LineString = 2, Polygon = 3 }

public readonly record struct VectorTilePoint(int X, int Y);

/// <summary>
/// One feature in tile coordinates (0..extent, y down). Parts are the MoveTo-separated point runs:
/// points for Point, lines for LineString, rings for Polygon (exterior and holes alike).
/// </summary>
public sealed record VectorTileFeature(VectorTileGeometryType Type, IReadOnlyList<VectorTilePoint[]> Parts,
    IReadOnlyDictionary<string, object> Properties)
{
    /// <summary>Coordinate extent of the layer this feature belongs to.</summary>
    public int Extent { get; init; } = 4096;

    public string? Text(string key) => Properties.TryGetValue(key, out var value) ? value as string : null;

    public double? Number(string key) => Properties.TryGetValue(key, out var value)
        ? value switch
        {
            long integer => integer, ulong natural => natural, double real => real, float single => single,
            bool flag => flag ? 1 : 0, _ => null
        }
        : null;
}

public sealed record VectorTileLayer(string Name, int Extent, IReadOnlyList<VectorTileFeature> Features);

/// <summary>
/// A decoded Mapbox Vector Tile (MVT 2.1). The decoder is deliberately small: the format is a fixed
/// protobuf schema, and no package in the Mapsui 5 stack reads it.
/// </summary>
public sealed class VectorTile
{
    public static VectorTile Empty { get; } = new([]);

    public VectorTile(IReadOnlyList<VectorTileLayer> layers) => Layers = layers;

    public IReadOnlyList<VectorTileLayer> Layers { get; }

    public VectorTileLayer? this[string name] => Layers.FirstOrDefault(layer => layer.Name == name);

    /// <summary>Decodes a tile. Malformed input raises <see cref="InvalidDataException"/>.</summary>
    public static VectorTile Decode(ReadOnlySpan<byte> data)
    {
        var layers = new List<VectorTileLayer>();
        var reader = new ProtobufReader(data);
        while (reader.Next(out var field, out var wire))
        {
            if (field == 3 && wire == 2) layers.Add(DecodeLayer(reader.Bytes()));
            else reader.Skip(wire);
        }
        return new VectorTile(layers);
    }

    private static VectorTileLayer DecodeLayer(ReadOnlySpan<byte> data)
    {
        string name = "";
        var extent = 4096;
        var keys = new List<string>();
        var values = new List<object>();
        var rawFeatures = new List<(VectorTileGeometryType Type, List<uint> Tags, List<uint> Geometry)>();
        var reader = new ProtobufReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: name = reader.String(); break;
                case 2 when wire == 2: rawFeatures.Add(DecodeFeature(reader.Bytes())); break;
                case 3 when wire == 2: keys.Add(reader.String()); break;
                case 4 when wire == 2: values.Add(DecodeValue(reader.Bytes())); break;
                case 5 when wire == 0: extent = reader.Int32("Vector tile layer extent"); break;
                default: reader.Skip(wire); break;
            }
        }
        if (extent <= 0) throw new InvalidDataException($"Vector tile layer '{name}' has extent {extent}.");

        var features = new VectorTileFeature[rawFeatures.Count];
        for (var index = 0; index < rawFeatures.Count; index++)
        {
            var (type, tags, geometry) = rawFeatures[index];
            if (tags.Count % 2 != 0) throw new InvalidDataException("Vector tile feature tags are not paired.");
            var properties = new Dictionary<string, object>(tags.Count / 2, StringComparer.Ordinal);
            for (var tag = 0; tag < tags.Count; tag += 2)
            {
                var key = tags[tag];
                var value = tags[tag + 1];
                if (key >= (uint)keys.Count || value >= (uint)values.Count)
                    throw new InvalidDataException("Vector tile feature tag is out of range.");
                properties[keys[(int)key]] = values[(int)value];
            }
            features[index] = new VectorTileFeature(type, DecodeGeometry(geometry), properties) { Extent = extent };
        }
        return new VectorTileLayer(name, extent, features);
    }

    private static (VectorTileGeometryType, List<uint>, List<uint>) DecodeFeature(ReadOnlySpan<byte> data)
    {
        var type = VectorTileGeometryType.Unknown;
        var tags = new List<uint>();
        var geometry = new List<uint>();
        var reader = new ProtobufReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 2: reader.UInt32s(wire, tags); break;
                case 3 when wire == 0: type = (VectorTileGeometryType)reader.Varint(); break;
                case 4: reader.UInt32s(wire, geometry); break;
                default: reader.Skip(wire); break;
            }
        }
        return (type, tags, geometry);
    }

    private static object DecodeValue(ReadOnlySpan<byte> data)
    {
        object value = "";
        var reader = new ProtobufReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: value = reader.String(); break;
                case 2 when wire == 5: value = BitConverter.Int32BitsToSingle((int)reader.Fixed32()); break;
                case 3 when wire == 1: value = BitConverter.Int64BitsToDouble((long)reader.Fixed64()); break;
                case 4 when wire == 0: value = (long)reader.Varint(); break;
                case 5 when wire == 0: value = reader.Varint(); break;
                case 6 when wire == 0: value = ZigZag(reader.Varint()); break;
                case 7 when wire == 0: value = reader.Varint() != 0; break;
                default: reader.Skip(wire); break;
            }
        }
        return value;
    }

    private static List<VectorTilePoint[]> DecodeGeometry(List<uint> commands)
    {
        var parts = new List<VectorTilePoint[]>();
        var current = new List<VectorTilePoint>();
        int x = 0, y = 0, index = 0;
        while (index < commands.Count)
        {
            var command = commands[index++];
            var id = command & 0x7;
            var count = (int)(command >> 3);
            switch (id)
            {
                case 1: // MoveTo starts a new part for each point.
                case 2: // LineTo extends the current part.
                    if (index + count * 2 > commands.Count)
                        throw new InvalidDataException("Vector tile geometry is truncated.");
                    for (var point = 0; point < count; point++)
                    {
                        x += (int)ZigZag(commands[index++]);
                        y += (int)ZigZag(commands[index++]);
                        if (id == 1 && current.Count > 0) { parts.Add([.. current]); current.Clear(); }
                        current.Add(new VectorTilePoint(x, y));
                    }
                    break;
                case 7: // ClosePath: the ring is closed by the renderer.
                    break;
                default:
                    throw new InvalidDataException($"Unknown vector tile geometry command {id}.");
            }
        }
        if (current.Count > 0) parts.Add([.. current]);
        return parts;
    }

    private static long ZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>Forward-only protobuf wire reader over a span.</summary>
    private ref struct ProtobufReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public bool Next(out int field, out int wire)
        {
            if (_position >= _data.Length) { field = wire = 0; return false; }
            var key = Varint();
            field = (int)(key >> 3);
            wire = (int)(key & 0x7);
            return true;
        }

        public ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length) throw new InvalidDataException("Truncated protobuf varint.");
                var b = _data[_position++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new InvalidDataException("Protobuf varint is too long.");
        }

        public uint Fixed32()
        {
            Require(4);
            var value = BitConverter.ToUInt32(_data.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public ulong Fixed64()
        {
            Require(8);
            var value = BitConverter.ToUInt64(_data.Slice(_position, 8));
            _position += 8;
            return value;
        }

        /// <summary>
        /// A non-negative varint that fits an <see cref="int"/>; anything larger is malformed input, never an
        /// arithmetic overflow.
        /// </summary>
        public int Int32(string what)
        {
            var value = Varint();
            return value <= int.MaxValue ? (int)value : throw new InvalidDataException($"{what} {value} is out of range.");
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var value = Varint();
            // Checked against what is left before any conversion or slicing, so no length is trusted.
            if (value > (ulong)(_data.Length - _position))
                throw new InvalidDataException("Truncated protobuf field.");
            var length = (int)value;
            var slice = _data.Slice(_position, length);
            _position += length;
            return slice;
        }

        public string String() => Encoding.UTF8.GetString(Bytes());

        /// <summary>Reads a repeated uint32 field, packed (wire 2) or unpacked (wire 0).</summary>
        public void UInt32s(int wire, List<uint> into)
        {
            if (wire == 0) { into.Add((uint)Varint()); return; }
            if (wire != 2) { Skip(wire); return; }
            var packed = new ProtobufReader(Bytes());
            while (packed._position < packed._data.Length) into.Add((uint)packed.Varint());
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Require(8); _position += 8; break;
                case 2: Bytes(); break;
                case 5: Require(4); _position += 4; break;
                default: throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
            }
        }

        private readonly void Require(int length)
        {
            if (length < 0 || length > _data.Length - _position)
                throw new InvalidDataException("Truncated protobuf field.");
        }
    }
}
