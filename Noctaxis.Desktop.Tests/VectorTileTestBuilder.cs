namespace Noctaxis.Desktop.Tests;

/// <summary>A signed integer that the builder encodes with the MVT sint64 (zigzag) value type.</summary>
internal readonly record struct SignedValue(long Value);

/// <summary>Minimal Mapbox Vector Tile encoder for tests: exact geometry, no network.</summary>
internal sealed class VectorTileTestBuilder
{
    private readonly List<VectorTileLayerBuilder> _layers = [];

    public VectorTileLayerBuilder Layer(string name, int extent = 4096)
    {
        var layer = new VectorTileLayerBuilder(name, extent);
        _layers.Add(layer);
        return layer;
    }

    public byte[] Build()
    {
        var tile = new Protobuf();
        foreach (var layer in _layers) tile.Bytes(3, layer.Encode());
        return tile.ToArray();
    }
}

internal sealed class VectorTileLayerBuilder(string name, int extent)
{
    private readonly List<(int Type, List<uint> Tags, List<uint> Geometry)> _features = [];
    private readonly List<string> _keys = [];
    private readonly List<object> _values = [];

    public VectorTileLayerBuilder Point(int x, int y, params (string Key, object Value)[] properties) =>
        Add(1, [[(x, y)]], closed: false, properties);

    public VectorTileLayerBuilder Line((int X, int Y)[] points, params (string Key, object Value)[] properties) =>
        Add(2, [points], closed: false, properties);

    public VectorTileLayerBuilder Polygon((int X, int Y)[][] rings, params (string Key, object Value)[] properties) =>
        Add(3, rings, closed: true, properties);

    /// <summary>A square polygon, clockwise in tile coordinates (y down), as MVT exteriors are.</summary>
    public VectorTileLayerBuilder Rectangle(int left, int top, int right, int bottom, params (string Key, object Value)[] properties) =>
        Polygon([[(left, top), (right, top), (right, bottom), (left, bottom)]], properties);

    private VectorTileLayerBuilder Add(int type, (int X, int Y)[][] parts, bool closed, (string Key, object Value)[] properties)
    {
        var tags = new List<uint>();
        foreach (var (key, value) in properties)
        {
            var keyIndex = _keys.IndexOf(key);
            if (keyIndex < 0) { keyIndex = _keys.Count; _keys.Add(key); }
            var valueIndex = _values.IndexOf(value);
            if (valueIndex < 0) { valueIndex = _values.Count; _values.Add(value); }
            tags.Add((uint)keyIndex);
            tags.Add((uint)valueIndex);
        }
        var geometry = new List<uint>();
        int cx = 0, cy = 0;
        foreach (var part in parts)
        {
            geometry.Add(Command(1, 1));
            geometry.Add(ZigZag(part[0].X - cx)); geometry.Add(ZigZag(part[0].Y - cy));
            (cx, cy) = part[0];
            if (part.Length > 1)
            {
                geometry.Add(Command(2, part.Length - 1));
                foreach (var (x, y) in part.Skip(1))
                {
                    geometry.Add(ZigZag(x - cx)); geometry.Add(ZigZag(y - cy));
                    (cx, cy) = (x, y);
                }
            }
            if (closed) geometry.Add(Command(7, 1));
        }
        _features.Add((type, tags, geometry));
        return this;
    }

    internal byte[] Encode()
    {
        var layer = new Protobuf();
        layer.Varint(15, 2);
        layer.String(1, name);
        foreach (var (type, tags, geometry) in _features)
        {
            var feature = new Protobuf();
            feature.Packed(2, tags);
            feature.Varint(3, (ulong)type);
            feature.Packed(4, geometry);
            layer.Bytes(2, feature.ToArray());
        }
        foreach (var key in _keys) layer.String(3, key);
        foreach (var value in _values)
        {
            var encoded = new Protobuf();
            switch (value)
            {
                case string text: encoded.String(1, text); break;
                case float single: encoded.Fixed32(2, BitConverter.SingleToUInt32Bits(single)); break;
                case double real: encoded.Fixed64(3, BitConverter.DoubleToUInt64Bits(real)); break;
                case int integer: encoded.Varint(4, (ulong)(long)integer); break;
                case long integer: encoded.Varint(4, (ulong)integer); break;
                case ulong natural: encoded.Varint(5, natural); break;
                case SignedValue signed: encoded.Varint(6, (ulong)((signed.Value << 1) ^ (signed.Value >> 63))); break;
                case bool flag: encoded.Varint(7, flag ? 1UL : 0UL); break;
                default: throw new ArgumentException($"Unsupported value {value}");
            }
            layer.Bytes(4, encoded.ToArray());
        }
        layer.Varint(5, (ulong)extent);
        return layer.ToArray();
    }

    private static uint Command(int id, int count) => (uint)((id & 0x7) | (count << 3));
    private static uint ZigZag(int value) => (uint)((value << 1) ^ (value >> 31));
}

internal sealed class Protobuf
{
    private readonly MemoryStream _stream = new();

    public void Varint(int field, ulong value) { Key(field, 0); Raw(value); }
    public void Fixed32(int field, uint value) { Key(field, 5); _stream.Write(BitConverter.GetBytes(value)); }
    public void Fixed64(int field, ulong value) { Key(field, 1); _stream.Write(BitConverter.GetBytes(value)); }
    public void String(int field, string value) => Bytes(field, System.Text.Encoding.UTF8.GetBytes(value));

    public void Bytes(int field, byte[] value)
    {
        Key(field, 2);
        Raw((ulong)value.Length);
        _stream.Write(value);
    }

    public void Packed(int field, IEnumerable<uint> values)
    {
        var packed = new Protobuf();
        foreach (var value in values) packed.Raw(value);
        Bytes(field, packed.ToArray());
    }

    public byte[] ToArray() => _stream.ToArray();

    private void Key(int field, int wire) => Raw((ulong)((field << 3) | wire));

    private void Raw(ulong value)
    {
        while (value >= 0x80) { _stream.WriteByte((byte)(value | 0x80)); value >>= 7; }
        _stream.WriteByte((byte)value);
    }
}
