using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    /// <summary>
    /// Pixel tests read colours the same way whatever the platform's byte order: macOS renders RGBA, Windows and
    /// Linux BGRA. Both orders are written here directly, so the RGBA case is checked on every system.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void RenderedPixelsAreReadAsTheirColourInEitherByteOrder(bool rgba)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(2, 1), new Vector(96, 96),
            rgba ? PixelFormat.Rgba8888 : PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            byte[] pixel = rgba ? [10, 20, 30, 255] : [30, 20, 10, 255];
            Marshal.Copy(pixel, 0, buffer.Address + 4, 4);
        }

        var colour = RenderedPixels.At(RenderedPixels.Bgra(bitmap), 2, 1, 0);

        Assert.Equal((255, 10, 20, 30), (colour.A, colour.R, colour.G, colour.B));
    }
}
