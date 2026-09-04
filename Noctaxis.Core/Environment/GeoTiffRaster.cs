using BitMiracle.LibTiff.Classic;
using Noctaxis.Core.Domain;
using System.Globalization;

namespace Noctaxis.Core.Environment;

public sealed record GeoTiffRasterMetadata(
    int Width,
    int Height,
    int BitsPerSample,
    SampleFormat SampleFormat,
    int SamplesPerPixel,
    Photometric Photometric,
    int? EpsgCode,
    GeoBounds? Bounds,
    double? NoDataValue);

public sealed record RasterValueStatistics(int ValidCount, int NoDataCount, int NonZeroCount,
    double? Minimum, double? Maximum);

/// <summary>Small single-band GeoTIFF reader used after source tiles enter the shared cache.</summary>
public sealed class GeoTiffRaster
{
    private static readonly TiffFieldInfo[] GeoTiffFieldInfo =
    [
        new(TiffTag.GEOTIFF_MODELPIXELSCALETAG, TiffFieldInfo.Variable2, TiffFieldInfo.Variable2,
            TiffType.DOUBLE, FieldBit.Custom, true, true, "ModelPixelScaleTag"),
        new(TiffTag.GEOTIFF_MODELTIEPOINTTAG, TiffFieldInfo.Variable2, TiffFieldInfo.Variable2,
            TiffType.DOUBLE, FieldBit.Custom, true, true, "ModelTiepointTag"),
        new(TiffTag.GEOTIFF_MODELTRANSFORMATIONTAG, TiffFieldInfo.Variable2, TiffFieldInfo.Variable2,
            TiffType.DOUBLE, FieldBit.Custom, true, true, "ModelTransformationTag"),
        new(TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG, TiffFieldInfo.Variable2, TiffFieldInfo.Variable2,
            TiffType.SHORT, FieldBit.Custom, true, true, "GeoKeyDirectoryTag"),
        new(TiffTag.GDAL_NODATA, TiffFieldInfo.Variable, TiffFieldInfo.Variable,
            TiffType.ASCII, FieldBit.Custom, true, false, "GDALNoDataValue")
    ];
    private static readonly Tiff.TiffExtendProc? ParentTagExtender = RegisterGeoTiffTags();
    private readonly double[] _values;

    private GeoTiffRaster(GeoTiffRasterMetadata metadata, double[] values)
    {
        Metadata = metadata;
        _values = values;
    }

    public GeoTiffRasterMetadata Metadata { get; }
    public int Width => Metadata.Width;
    public int Height => Metadata.Height;
    public long ApproximateMemoryBytes => (long)_values.Length * sizeof(double);

    public static GeoTiffRaster Load(string path)
    {
        EnsureGeoTiffTagsRegistered();
        using var tiff = Tiff.Open(path, "r") ?? throw new InvalidDataException("GeoTIFF could not be opened.");
        var metadata = Inspect(tiff);
        if (metadata.Width <= 0 || metadata.Height <= 0 || metadata.SamplesPerPixel != 1 ||
            metadata.BitsPerSample is not (8 or 16 or 32 or 64))
            throw new InvalidDataException("Unsupported environmental GeoTIFF layout.");

        var values = new double[checked(metadata.Width * metadata.Height)];
        if (tiff.IsTiled()) ReadTiles(tiff, metadata.Width, metadata.Height,
            metadata.BitsPerSample, metadata.SampleFormat, values);
        else ReadScanlines(tiff, metadata.Width, metadata.Height,
            metadata.BitsPerSample, metadata.SampleFormat, values);
        return new GeoTiffRaster(metadata, values);
    }

    public static GeoTiffRasterMetadata Inspect(string path)
    {
        EnsureGeoTiffTagsRegistered();
        using var tiff = Tiff.Open(path, "r") ?? throw new InvalidDataException("GeoTIFF could not be opened.");
        return Inspect(tiff);
    }

    public static bool IsValid(string path)
    {
        try
        {
            EnsureGeoTiffTagsRegistered();
            using var tiff = Tiff.Open(path, "r");
            return tiff is not null && RequiredInt(tiff, TiffTag.IMAGEWIDTH) > 0 &&
                   RequiredInt(tiff, TiffTag.IMAGELENGTH) > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
    }

    public RasterValueStatistics GetStatistics(Func<double, bool>? isNoData = null)
    {
        var valid = 0;
        var noData = 0;
        var nonZero = 0;
        double? minimum = null;
        double? maximum = null;
        foreach (var value in _values)
        {
            if (!double.IsFinite(value) || isNoData is not null && isNoData(value))
            {
                noData++;
                continue;
            }
            valid++;
            if (Math.Abs(value) > double.Epsilon) nonZero++;
            minimum = minimum.HasValue ? Math.Min(minimum.Value, value) : value;
            maximum = maximum.HasValue ? Math.Max(maximum.Value, value) : value;
        }
        return new RasterValueStatistics(valid, noData, nonZero, minimum, maximum);
    }

    /// <summary>
    /// Reads only the internal TIFF blocks containing the requested coordinates. This is used for
    /// WorldCover's 3-degree 10 m rasters, whose full uncompressed grid is intentionally never
    /// expanded into application memory.
    /// </summary>
    public static IReadOnlyList<double?> ReadNearestBatch(string path, GeoBounds bounds,
        IReadOnlyList<GeoCoordinate> coordinates, Func<double, bool>? isNoData = null)
    {
        EnsureGeoTiffTagsRegistered();
        using var tiff = Tiff.Open(path, "r") ?? throw new InvalidDataException("GeoTIFF could not be opened.");
        var width = RequiredInt(tiff, TiffTag.IMAGEWIDTH);
        var height = RequiredInt(tiff, TiffTag.IMAGELENGTH);
        var bits = OptionalInt(tiff, TiffTag.BITSPERSAMPLE, 8);
        var samplesPerPixel = OptionalInt(tiff, TiffTag.SAMPLESPERPIXEL, 1);
        var sampleFormat = (SampleFormat)OptionalInt(tiff, TiffTag.SAMPLEFORMAT, (int)SampleFormat.UINT);
        if (width <= 0 || height <= 0 || samplesPerPixel != 1 || bits is not (8 or 16 or 32 or 64))
            throw new InvalidDataException("Unsupported environmental GeoTIFF layout.");

        var pixels = coordinates.Select(coordinate => PixelAt(bounds, coordinate, width, height)).ToArray();
        var values = new double?[coordinates.Count];
        if (tiff.IsTiled())
            ReadNearestTiles(tiff, pixels, bits, sampleFormat, values, isNoData);
        else
            ReadNearestScanlines(tiff, pixels, bits, sampleFormat, values, isNoData);
        return values;
    }

    public double? Interpolate(GeoBounds bounds, double latitude, double longitude,
        Func<double, bool>? isNoData = null)
    {
        if (!bounds.Contains(latitude, longitude)) return null;
        var x = LongitudeFraction(bounds, longitude) * (Width - 1);
        var y = (bounds.North - latitude) / Math.Max(1e-12, bounds.North - bounds.South) * (Height - 1);
        var x0 = Math.Clamp((int)Math.Floor(x), 0, Width - 1);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, Height - 1);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = x - x0;
        var fy = y - y0;
        var weighted = 0d;
        var weight = 0d;
        double? first = null;
        Add(_values[y0 * Width + x0], (1 - fx) * (1 - fy));
        Add(_values[y0 * Width + x1], fx * (1 - fy));
        Add(_values[y1 * Width + x0], (1 - fx) * fy);
        Add(_values[y1 * Width + x1], fx * fy);
        return !first.HasValue ? null : weight <= double.Epsilon ? first.Value : weighted / weight;

        void Add(double sample, double sampleWeight)
        {
            if (sampleWeight <= double.Epsilon || !double.IsFinite(sample) ||
                isNoData is not null && isNoData(sample)) return;
            first ??= sample;
            weighted += sample * sampleWeight;
            weight += sampleWeight;
        }
    }

    public double? Nearest(GeoBounds bounds, double latitude, double longitude,
        Func<double, bool>? isNoData = null)
    {
        if (!bounds.Contains(latitude, longitude)) return null;
        var x = Math.Clamp((int)Math.Round(LongitudeFraction(bounds, longitude) * (Width - 1)), 0, Width - 1);
        var y = Math.Clamp((int)Math.Round((bounds.North - latitude) /
            Math.Max(1e-12, bounds.North - bounds.South) * (Height - 1)), 0, Height - 1);
        var value = _values[y * Width + x];
        return double.IsFinite(value) && (isNoData is null || !isNoData(value)) ? value : null;
    }

    private static void ReadScanlines(Tiff tiff, int width, int height, int bits,
        SampleFormat format, double[] values)
    {
        var buffer = new byte[tiff.ScanlineSize()];
        for (var row = 0; row < height; row++)
        {
            if (!tiff.ReadScanline(buffer, row)) throw new InvalidDataException("GeoTIFF scanline decode failed.");
            for (var column = 0; column < width; column++)
                values[row * width + column] = ReadValue(buffer, column * bits / 8, bits, format);
        }
    }

    private static void ReadTiles(Tiff tiff, int width, int height, int bits,
        SampleFormat format, double[] values)
    {
        var tileWidth = RequiredInt(tiff, TiffTag.TILEWIDTH);
        var tileHeight = RequiredInt(tiff, TiffTag.TILELENGTH);
        var bytesPerSample = bits / 8;
        var buffer = new byte[tiff.TileSize()];
        for (var top = 0; top < height; top += tileHeight)
        for (var left = 0; left < width; left += tileWidth)
        {
            Array.Clear(buffer);
            var tile = tiff.ComputeTile(left, top, 0, 0);
            if (tiff.ReadEncodedTile(tile, buffer, 0, buffer.Length) < 0)
                throw new InvalidDataException("GeoTIFF tile decode failed.");
            var copyHeight = Math.Min(tileHeight, height - top);
            var copyWidth = Math.Min(tileWidth, width - left);
            for (var row = 0; row < copyHeight; row++)
            for (var column = 0; column < copyWidth; column++)
            {
                var offset = (row * tileWidth + column) * bytesPerSample;
                values[(top + row) * width + left + column] = ReadValue(buffer, offset, bits, format);
            }
        }
    }

    private static void ReadNearestTiles(Tiff tiff, IReadOnlyList<(int X, int Y)?> pixels,
        int bits, SampleFormat format, double?[] values, Func<double, bool>? isNoData)
    {
        var tileWidth = RequiredInt(tiff, TiffTag.TILEWIDTH);
        var tileHeight = RequiredInt(tiff, TiffTag.TILELENGTH);
        var bytesPerSample = bits / 8;
        var decoded = new Dictionary<int, byte[]>();
        for (var index = 0; index < pixels.Count; index++)
        {
            if (pixels[index] is not { } pixel) continue;
            var tileIndex = tiff.ComputeTile(pixel.X, pixel.Y, 0, 0);
            if (!decoded.TryGetValue(tileIndex, out var buffer))
            {
                buffer = new byte[tiff.TileSize()];
                if (tiff.ReadEncodedTile(tileIndex, buffer, 0, buffer.Length) < 0)
                    throw new InvalidDataException("GeoTIFF tile decode failed.");
                decoded[tileIndex] = buffer;
            }
            var withinX = pixel.X % tileWidth;
            var withinY = pixel.Y % tileHeight;
            var value = ReadValue(buffer, (withinY * tileWidth + withinX) * bytesPerSample, bits, format);
            if (double.IsFinite(value) && (isNoData is null || !isNoData(value))) values[index] = value;
        }
    }

    private static void ReadNearestScanlines(Tiff tiff, IReadOnlyList<(int X, int Y)?> pixels,
        int bits, SampleFormat format, double?[] values, Func<double, bool>? isNoData)
    {
        var bytesPerSample = bits / 8;
        foreach (var row in pixels.Select((pixel, index) => (pixel, index))
                     .Where(item => item.pixel.HasValue)
                     .GroupBy(item => item.pixel!.Value.Y))
        {
            var buffer = new byte[tiff.ScanlineSize()];
            if (!tiff.ReadScanline(buffer, row.Key))
                throw new InvalidDataException("GeoTIFF scanline decode failed.");
            foreach (var item in row)
            {
                var value = ReadValue(buffer, item.pixel!.Value.X * bytesPerSample, bits, format);
                if (double.IsFinite(value) && (isNoData is null || !isNoData(value))) values[item.index] = value;
            }
        }
    }

    private static double ReadValue(byte[] bytes, int offset, int bits, SampleFormat format) => (bits, format) switch
    {
        (8, SampleFormat.INT) => unchecked((sbyte)bytes[offset]),
        (8, _) => bytes[offset],
        (16, SampleFormat.INT) => BitConverter.ToInt16(bytes, offset),
        (16, _) => BitConverter.ToUInt16(bytes, offset),
        (32, SampleFormat.IEEEFP) => BitConverter.ToSingle(bytes, offset),
        (32, SampleFormat.INT) => BitConverter.ToInt32(bytes, offset),
        (32, _) => BitConverter.ToUInt32(bytes, offset),
        (64, SampleFormat.IEEEFP) => BitConverter.ToDouble(bytes, offset),
        (64, SampleFormat.INT) => BitConverter.ToInt64(bytes, offset),
        (64, _) => BitConverter.ToUInt64(bytes, offset),
        _ => throw new InvalidDataException("Unsupported GeoTIFF sample type.")
    };

    private static GeoTiffRasterMetadata Inspect(Tiff tiff)
    {
        var width = RequiredInt(tiff, TiffTag.IMAGEWIDTH);
        var height = RequiredInt(tiff, TiffTag.IMAGELENGTH);
        var bits = OptionalInt(tiff, TiffTag.BITSPERSAMPLE, 8);
        var samplesPerPixel = OptionalInt(tiff, TiffTag.SAMPLESPERPIXEL, 1);
        var sampleFormat = (SampleFormat)OptionalInt(tiff, TiffTag.SAMPLEFORMAT, (int)SampleFormat.UINT);
        var photometric = (Photometric)OptionalInt(tiff, TiffTag.PHOTOMETRIC, (int)Photometric.MINISBLACK);
        return new GeoTiffRasterMetadata(width, height, bits, sampleFormat, samplesPerPixel,
            photometric, ReadEpsgCode(tiff), ReadBounds(tiff, width, height), ReadNoData(tiff));
    }

    /// <summary>
    /// Registers the standard GeoTIFF/GDAL extension tags before LibTiff opens a directory. This is
    /// required because LibTiff.NET otherwise discards those tags as unknown while opening the file.
    /// </summary>
    public static void EnsureGeoTiffTagsRegistered() => _ = ParentTagExtender;

    private static Tiff.TiffExtendProc? RegisterGeoTiffTags() => Tiff.SetTagExtender(ExtendGeoTiffTags);

    private static void ExtendGeoTiffTags(Tiff tiff)
    {
        ParentTagExtender?.Invoke(tiff);
        tiff.MergeFieldInfo(GeoTiffFieldInfo, GeoTiffFieldInfo.Length);
    }

    private static int? ReadEpsgCode(Tiff tiff)
    {
        var values = OptionalUShortArray(tiff, TiffTag.GEOTIFF_GEOKEYDIRECTORYTAG);
        if (values is null || values.Length < 4) return null;
        var keys = Math.Min(values[3], (ushort)((values.Length - 4) / 4));
        for (var index = 0; index < keys; index++)
        {
            var offset = 4 + index * 4;
            var keyId = values[offset];
            var tagLocation = values[offset + 1];
            var count = values[offset + 2];
            var value = values[offset + 3];
            if (tagLocation == 0 && count == 1 && keyId is 2048 or 3072) return value;
        }
        return null;
    }

    private static GeoBounds? ReadBounds(Tiff tiff, int width, int height)
    {
        var transform = OptionalDoubleArray(tiff, TiffTag.GEOTIFF_MODELTRANSFORMATIONTAG);
        if (transform is { Length: >= 16 })
        {
            var corners = new[]
            {
                Transform(0, 0), Transform(width, 0), Transform(0, height), Transform(width, height)
            };
            return new GeoBounds(corners.Min(point => point.Y), corners.Min(point => point.X),
                corners.Max(point => point.Y), corners.Max(point => point.X));

            (double X, double Y) Transform(double column, double row) =>
                (transform[0] * column + transform[1] * row + transform[3],
                    transform[4] * column + transform[5] * row + transform[7]);
        }

        var scale = OptionalDoubleArray(tiff, TiffTag.GEOTIFF_MODELPIXELSCALETAG);
        var tie = OptionalDoubleArray(tiff, TiffTag.GEOTIFF_MODELTIEPOINTTAG);
        if (scale is not { Length: >= 2 } || tie is not { Length: >= 6 } || scale[0] <= 0 || scale[1] <= 0)
            return null;
        var west = tie[3] - tie[0] * scale[0];
        var north = tie[4] + tie[1] * scale[1];
        return new GeoBounds(north - height * scale[1], west, north, west + width * scale[0]);
    }

    private static double? ReadNoData(Tiff tiff)
    {
        var values = tiff.GetField(TiffTag.GDAL_NODATA);
        if (values is null) return null;
        foreach (var value in values)
        {
            var text = value.ToString()?.Trim().TrimEnd('\0');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    private static double[]? OptionalDoubleArray(Tiff tiff, TiffTag tag)
    {
        var values = tiff.GetField(tag);
        if (values is null) return null;
        foreach (var value in values.Reverse())
        {
            try
            {
                var array = value.ToDoubleArray();
                if (array.Length > 0) return array;
            }
            catch (Exception) when (value.Value is not null) { }
        }
        return null;
    }

    private static ushort[]? OptionalUShortArray(Tiff tiff, TiffTag tag)
    {
        var values = tiff.GetField(tag);
        if (values is null) return null;
        foreach (var value in values.Reverse())
        {
            try
            {
                var array = value.ToUShortArray();
                if (array.Length > 0) return array;
            }
            catch (Exception) when (value.Value is not null) { }
        }
        return null;
    }

    private static int RequiredInt(Tiff tiff, TiffTag tag) =>
        tiff.GetField(tag)?[0].ToInt() ?? throw new InvalidDataException($"GeoTIFF tag {tag} is missing.");

    private static int OptionalInt(Tiff tiff, TiffTag tag, int fallback) => tiff.GetField(tag)?[0].ToInt() ?? fallback;

    private static (int X, int Y)? PixelAt(GeoBounds bounds, GeoCoordinate coordinate, int width, int height)
    {
        if (!bounds.Contains(coordinate.Latitude, coordinate.Longitude)) return null;
        var x = Math.Clamp((int)Math.Round(LongitudeFraction(bounds, coordinate.Longitude) * (width - 1)),
            0, width - 1);
        var y = Math.Clamp((int)Math.Round((bounds.North - coordinate.Latitude) /
                                           Math.Max(1e-12, bounds.North - bounds.South) * (height - 1)),
            0, height - 1);
        return (x, y);
    }

    private static double LongitudeFraction(GeoBounds bounds, double longitude)
    {
        var span = bounds.East >= bounds.West ? bounds.East - bounds.West : bounds.East + 360 - bounds.West;
        var offset = longitude - bounds.West;
        if (offset < 0) offset += 360;
        return offset / Math.Max(1e-12, span);
    }
}
