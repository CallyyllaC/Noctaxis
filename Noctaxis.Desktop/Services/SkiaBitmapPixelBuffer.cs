using SkiaSharp;

namespace Noctaxis.Desktop.Services;

internal readonly record struct UnpremultipliedPixel(byte Red, byte Green, byte Blue, byte Alpha);

/// <summary>
/// Direct BGRA access for renderer-owned Skia bitmaps. This avoids hundreds of thousands of
/// managed GetPixel/SetPixel transitions while retaining Skia's premultiplied-alpha semantics.
/// </summary>
internal readonly unsafe struct SkiaBitmapPixelBuffer
{
    private readonly byte* _pixels;
    private readonly int _rowBytes;

    public SkiaBitmapPixelBuffer(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.ColorType != SKColorType.Bgra8888)
            throw new ArgumentException("The accelerated renderer requires a BGRA8888 bitmap.", nameof(bitmap));
        _pixels = (byte*)bitmap.GetPixels().ToPointer();
        _rowBytes = bitmap.RowBytes;
        Width = bitmap.Width;
        Height = bitmap.Height;
    }

    public int Width { get; }
    public int Height { get; }

    public UnpremultipliedPixel Read(int x, int y)
    {
        var pixel = _pixels + y * _rowBytes + x * 4;
        var alpha = pixel[3];
        if (alpha == 0) return new UnpremultipliedPixel(0, 0, 0, 0);
        if (alpha == 255) return new UnpremultipliedPixel(pixel[2], pixel[1], pixel[0], 255);
        return new UnpremultipliedPixel(Unpremultiply(pixel[2], alpha), Unpremultiply(pixel[1], alpha),
            Unpremultiply(pixel[0], alpha), alpha);
    }

    public void Write(int x, int y, double red, double green, double blue, byte alpha)
    {
        var pixel = _pixels + y * _rowBytes + x * 4;
        var r = ToByte(red); var g = ToByte(green); var b = ToByte(blue);
        if (alpha == 255)
        {
            pixel[0] = b; pixel[1] = g; pixel[2] = r; pixel[3] = 255;
            return;
        }
        pixel[0] = Premultiply(b, alpha);
        pixel[1] = Premultiply(g, alpha);
        pixel[2] = Premultiply(r, alpha);
        pixel[3] = alpha;
    }

    public void Write(int x, int y, UnpremultipliedPixel value) =>
        Write(x, y, value.Red, value.Green, value.Blue, value.Alpha);

    private static byte Premultiply(byte channel, byte alpha) =>
        (byte)((channel * alpha + 127) / 255);

    private static byte Unpremultiply(byte channel, byte alpha) =>
        (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
