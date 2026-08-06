using System.Buffers.Binary;

namespace Noctaxis.Core.Terrain;

public sealed class HgtTile
{
    public const short VoidValue = -32768;
    private readonly short[] _samples;

    private HgtTile(int southLatitude, int westLongitude, int size, short[] samples)
    {
        SouthLatitude = southLatitude;
        WestLongitude = westLongitude;
        Size = size;
        _samples = samples;
    }

    public int SouthLatitude { get; }
    public int WestLongitude { get; }
    public int Size { get; }

    public static async Task<HgtTile> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var size = DetectSize(info.Length);
        var bytes = new byte[info.Length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var (south, west) = ParseTileName(Path.GetFileNameWithoutExtension(path));
        return FromBytes(south, west, bytes, size);
    }

    public static HgtTile FromBytes(int southLatitude, int westLongitude, ReadOnlySpan<byte> bytes, int? expectedSize = null)
    {
        var size = expectedSize ?? DetectSize(bytes.Length);
        if (bytes.Length != checked(size * size * 2)) throw new InvalidDataException("HGT byte length does not match its resolution.");
        if (size is not (1201 or 3601) && expectedSize is null) throw new InvalidDataException("Only 1201×1201 and 3601×3601 HGT tiles are supported.");
        var samples = new short[size * size];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(i * 2, 2));
        return new HgtTile(southLatitude, westLongitude, size, samples);
    }

    public double? Interpolate(double latitude, double longitude)
    {
        if (latitude < SouthLatitude || latitude > SouthLatitude + 1 || longitude < WestLongitude || longitude > WestLongitude + 1) return null;
        var max = Size - 1;
        var row = (SouthLatitude + 1 - latitude) * max;
        var column = (longitude - WestLongitude) * max;
        var r0 = Math.Clamp((int)Math.Floor(row), 0, max);
        var c0 = Math.Clamp((int)Math.Floor(column), 0, max);
        var r1 = Math.Min(r0 + 1, max);
        var c1 = Math.Min(c0 + 1, max);
        var rf = row - r0;
        var cf = column - c0;
        var points = new[]
        {
            (_samples[r0 * Size + c0], (1-rf)*(1-cf)),
            (_samples[r0 * Size + c1], (1-rf)*cf),
            (_samples[r1 * Size + c0], rf*(1-cf)),
            (_samples[r1 * Size + c1], rf*cf)
        };
        var valid = points.Where(x => x.Item1 != VoidValue).ToArray();
        if (valid.Length == 0) return null;
        var weight = valid.Sum(x => x.Item2);
        return weight <= double.Epsilon ? valid[0].Item1 : valid.Sum(x => x.Item1 * x.Item2) / weight;
    }

    public static string GetTileName(double latitude, double longitude)
    {
        var south = (int)Math.Floor(latitude);
        var west = (int)Math.Floor(longitude);
        return $"{(south >= 0 ? 'N' : 'S')}{Math.Abs(south):00}{(west >= 0 ? 'E' : 'W')}{Math.Abs(west):000}";
    }

    public static (int SouthLatitude, int WestLongitude) ParseTileName(string name)
    {
        if (name.Length != 7 || (name[0] is not ('N' or 'S' or 'n' or 's')) || (name[3] is not ('E' or 'W' or 'e' or 'w')) ||
            !int.TryParse(name.AsSpan(1, 2), out var lat) || !int.TryParse(name.AsSpan(4, 3), out var lon))
            throw new InvalidDataException($"Invalid SRTM tile name '{name}'.");
        if (char.ToUpperInvariant(name[0]) == 'S') lat = -lat;
        if (char.ToUpperInvariant(name[3]) == 'W') lon = -lon;
        return (lat, lon);
    }

    private static int DetectSize(long byteLength)
    {
        if (byteLength == 1201L * 1201 * 2) return 1201;
        if (byteLength == 3601L * 3601 * 2) return 3601;
        throw new InvalidDataException($"Unsupported HGT file length {byteLength:N0} bytes.");
    }
}
