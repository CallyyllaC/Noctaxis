using System.IO.Compression;
using System.Text;
using Noctaxis.Core.Environment;

namespace Noctaxis.Desktop.Services;

/// <summary>Compact location-specific derivative of shared WSF source tiles for offline restyling.</summary>
public static class SettlementRasterCodec
{
    // V2 guarantees that BuildingFraction is 0..1 and BuildingHeightMetres has already had the
    // WSF raw 0.1 gain applied. V1 derivatives are intentionally not interpreted as V2 data.
    public const int SchemaVersion = 2;
    private static readonly byte[] Magic = "NXWSF2"u8.ToArray();

    public static byte[] Encode(SettlementRaster raster)
    {
        if (raster.BuildingFraction.Length != raster.CellCount ||
            raster.BuildingHeightMetres.Length != raster.CellCount)
            throw new InvalidDataException("Settlement raster dimensions are inconsistent.");
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: false))
        {
            writer.Write(Magic);
            writer.Write(SchemaVersion);
            writer.Write(raster.DatasetId);
            writer.Write(raster.DatasetVersion);
            writer.Write(raster.Grid.Bounds.South);
            writer.Write(raster.Grid.Bounds.West);
            writer.Write(raster.Grid.Bounds.North);
            writer.Write(raster.Grid.Bounds.East);
            writer.Write(raster.Grid.Width);
            writer.Write(raster.Grid.Height);
            writer.Write((int)raster.Grid.Projection);
            writer.Write(raster.IsPartial);
            foreach (var value in raster.BuildingFraction) writer.Write(value);
            foreach (var value in raster.BuildingHeightMetres) writer.Write(value);
        }
        return output.ToArray();
    }

    public static SettlementRaster Decode(ReadOnlySpan<byte> bytes)
    {
        using var input = new MemoryStream(bytes.ToArray(), writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadInt32() != SchemaVersion)
            throw new InvalidDataException("Settlement raster schema is not supported.");
        var dataset = reader.ReadString();
        var version = reader.ReadString();
        var bounds = new GeoBounds(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var projection = (GeoRasterProjection)reader.ReadInt32();
        var partial = reader.ReadBoolean();
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 ||
            !Enum.IsDefined(projection)) throw new InvalidDataException("Settlement raster header is invalid.");
        var count = checked(width * height);
        var fractions = new float[count];
        var heights = new float[count];
        for (var index = 0; index < count; index++) fractions[index] = reader.ReadSingle();
        for (var index = 0; index < count; index++) heights[index] = reader.ReadSingle();
        return new SettlementRaster(dataset, version,
            new GeoRasterRequest(bounds, width, height, projection), fractions, heights, partial);
    }
}
