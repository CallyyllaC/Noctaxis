using SkiaSharp;

namespace Noctaxis.Desktop.Branding;

/// <summary>
/// Renders the Windows application icon from <see cref="NoctaxisLogo"/> in its fixed brand colours.
/// The committed <c>Assets/noctaxis.ico</c> is this output; a test keeps the two in step.
/// </summary>
public static class NoctaxisIcon
{
    /// <summary>Icon frames: the Windows shell sizes at 100–200% scaling, plus the large tile size.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 20, 24, 32, 40, 48, 64, 256];

    public const string IcoFileName = "noctaxis.ico", SvgFileName = "noctaxis-logo.svg";

    /// <summary>One square frame, transparent outside the mark, drawn at the weight for its size.</summary>
    public static SKBitmap Render(int size, string frame = NoctaxisLogo.BrandFrame, string star = NoctaxisLogo.BrandStar)
    {
        var weight = NoctaxisLogo.ForPixelSize(size);
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)(size / NoctaxisLogo.GridSize));
        using var brackets = SKPath.ParseSvgPathData(NoctaxisLogo.BracketsPathData(weight));
        using var starPath = SKPath.ParseSvgPathData(NoctaxisLogo.StarPathData(weight));
        using (var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)weight.BracketStroke,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = SKColor.Parse(frame)
        })
            canvas.DrawPath(brackets, stroke);
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColor.Parse(star) })
            canvas.DrawPath(starPath, fill);
        canvas.Flush();
        return bitmap;
    }

    public static byte[] RenderPng(int size)
    {
        using var bitmap = Render(size);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>A multi-resolution .ico with one PNG-compressed 32-bit frame per entry of <see cref="Sizes"/>.</summary>
    public static byte[] BuildIco()
    {
        var frames = Sizes.Select(size => (Size: size, Png: RenderPng(size))).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);             // reserved
        writer.Write((ushort)1);             // type: icon
        writer.Write((ushort)frames.Length);
        var offset = 6 + 16 * frames.Length;
        foreach (var (size, png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);           // no palette
            writer.Write((byte)0);           // reserved
            writer.Write((ushort)1);         // colour planes
            writer.Write((ushort)32);        // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) writer.Write(png);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Regenerates the committed icon and SVG in <paramref name="assetsDirectory"/>.</summary>
    public static void WriteAssets(string assetsDirectory)
    {
        Directory.CreateDirectory(assetsDirectory);
        File.WriteAllBytes(Path.Combine(assetsDirectory, IcoFileName), BuildIco());
        File.WriteAllText(Path.Combine(assetsDirectory, SvgFileName), NoctaxisLogo.ToSvg(NoctaxisLogo.Regular));
    }

    /// <summary>The macOS bundle icon's entries: its OSType and pixel size, each a PNG.</summary>
    public static IReadOnlyList<(string Type, int Size)> IcnsEntries { get; } =
        [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];

    public const string IcnsFileName = "Noctaxis.icns", LinuxIconFileName = "noctaxis.png";
    /// <summary>The Linux desktop icon's size, a standard hicolor size.</summary>
    public const int LinuxIconSize = 256;

    /// <summary>
    /// A macOS .icns: the "icns" header and its length, then each <see cref="IcnsEntries"/> entry as its type, its
    /// length and a PNG, all lengths big-endian and counting their own 8-byte headers.
    /// </summary>
    public static byte[] BuildIcns()
    {
        static void BigEndian(Stream stream, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
        var entries = IcnsEntries.Select(entry => (entry.Type, Png: RenderPng(entry.Size))).ToArray();
        using var stream = new MemoryStream();
        stream.Write("icns"u8);
        BigEndian(stream, 8 + entries.Sum(entry => 8 + entry.Png.Length));
        foreach (var (type, png) in entries)
        {
            stream.Write(System.Text.Encoding.ASCII.GetBytes(type));
            BigEndian(stream, 8 + png.Length);
            stream.Write(png);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Regenerates the release packages' icons under <paramref name="packagingDirectory"/> (the repository's
    /// <c>packaging</c> folder): the macOS bundle icon and the Linux desktop icon. They are not application resources.
    /// </summary>
    public static void WritePackagingAssets(string packagingDirectory)
    {
        Directory.CreateDirectory(Path.Combine(packagingDirectory, "macos"));
        Directory.CreateDirectory(Path.Combine(packagingDirectory, "linux"));
        File.WriteAllBytes(Path.Combine(packagingDirectory, "macos", IcnsFileName), BuildIcns());
        File.WriteAllBytes(Path.Combine(packagingDirectory, "linux", LinuxIconFileName), RenderPng(LinuxIconSize));
    }
}
