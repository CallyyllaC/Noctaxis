using System.Buffers.Binary;
using BitMiracle.LibTiff.Classic;
using Noctaxis.Core.Environment;

namespace Noctaxis.Core.Tests;

/// <summary>
/// R02 (phase-0 audit): a raster's declared size is checked against a decoded-memory budget before anything is
/// allocated. These files declare huge grids in a few hundred bytes; nothing here allocates more than a small grid.
/// </summary>
public sealed class DecodedInputBudgetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-decoded-budget-" + Guid.NewGuid().ToString("N"));

    public DecodedInputBudgetTests() => Directory.CreateDirectory(_directory);

    [Theory]
    // 30,000 x 30,000 would be a 7.2 GB double grid; 50,000 x 50,000 overflowed int before the budget existed.
    [InlineData(30_000, 30_000)]
    [InlineData(50_000, 50_000)]
    [InlineData(4097, 4096)]
    public void ARasterDeclaringMoreThanTheBudgetIsMalformedInputBeforeAllocation(int width, int height)
    {
        var path = SmallTiffClaiming(width, height);
        Assert.Equal((width, height), (GeoTiffRaster.Inspect(path).Width, GeoTiffRaster.Inspect(path).Height));

        var failure = Assert.Throws<InvalidDataException>(() => GeoTiffRaster.Load(path));
        Assert.Contains("exceeds", failure.Message);
    }

    [Fact]
    public void ARasterWithinTheBudgetStillLoads()
    {
        var path = SmallTiffClaiming(2, 2);
        var raster = GeoTiffRaster.Load(path);
        Assert.Equal((2, 2), (raster.Width, raster.Height));
        Assert.Throws<InvalidDataException>(() => GeoTiffRaster.Load(path, maximumPixels: 3));
    }

    /// <summary>A valid 2 x 2 8-bit TIFF whose width and height tags are then rewritten to claim another size.</summary>
    private string SmallTiffClaiming(int width, int height)
    {
        var path = Path.Combine(_directory, $"claims-{width}x{height}.tif");
        using (var tiff = Tiff.Open(path, "w"))
        {
            tiff.SetField(TiffTag.IMAGEWIDTH, 2);
            tiff.SetField(TiffTag.IMAGELENGTH, 2);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tiff.SetField(TiffTag.ROWSPERSTRIP, 2);
            tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.WriteScanline([1, 2], 0);
            tiff.WriteScanline([3, 4], 1);
            tiff.WriteDirectory();
        }
        if ((width, height) == (2, 2)) return path;

        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'I', bytes[0]); // Little-endian, as LibTiff writes on this platform.
        var directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(directory));
        for (var index = 0; index < entries; index++)
        {
            var entry = bytes.AsSpan(directory + 2 + index * 12, 12);
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (tag is not (256 or 257)) continue;
            var value = tag == 256 ? width : height;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            if (type == 3) BinaryPrimitives.WriteUInt16LittleEndian(entry[8..], checked((ushort)value));
            else BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)value);
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); }
        catch (IOException) { }
    }
}
