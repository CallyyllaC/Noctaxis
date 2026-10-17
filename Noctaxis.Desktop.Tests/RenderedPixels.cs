using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Reads a rendered bitmap as BGRA bytes. A render target keeps the platform's own byte order, BGRA on Windows
/// and Linux but RGBA on macOS, so its raw bytes cannot be read as BGRA everywhere; copying through a BGRA
/// framebuffer converts them.
/// </summary>
internal static class RenderedPixels
{
    /// <summary>The whole bitmap, row after row with no padding: pixel (x, y) starts at (y × width + x) × 4.</summary>
    public static byte[] Bgra(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        using var copy = new WriteableBitmap(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock();
        bitmap.CopyPixels(buffer);
        var pixels = new byte[size.Width * size.Height * 4];
        for (var y = 0; y < size.Height; y++)
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, pixels, y * size.Width * 4, size.Width * 4);
        return pixels;
    }

    /// <summary>The colour at (<paramref name="x"/>, <paramref name="y"/>) of <see cref="Bgra"/>'s bytes.</summary>
    public static Avalonia.Media.Color At(byte[] bgra, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return Avalonia.Media.Color.FromArgb(bgra[i + 3], bgra[i + 2], bgra[i + 1], bgra[i]);
    }
}
