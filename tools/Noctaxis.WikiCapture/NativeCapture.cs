using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using SkiaSharp;

internal static class NativeCapture
{
    internal static byte[] PopupPng(Window window, Control content)
    {
        var top = TopLevel.GetTopLevel(content) ?? throw new InvalidOperationException("Flyout has no native host.");
        if (top.TryGetPlatformHandle()?.Handle == window.TryGetPlatformHandle()?.Handle)
            return WindowPng(window, content);
        using var bitmap = Client(top);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        ValidatePng(png);
        return png;
    }

    // Capture the real native client, including a separately hosted Avalonia flyout. Never
    // substitute a headless render or fixture drawing when native capture fails.
    internal static byte[] WindowPng(Window window, Control? crop, Control? flyoutContent = null)
    {
        using var bitmap = Client(window);
        using var canvas = new SKCanvas(bitmap);
        if (flyoutContent is not null && TopLevel.GetTopLevel(flyoutContent) is { } popup &&
            popup.TryGetPlatformHandle()?.Handle != window.TryGetPlatformHandle()?.Handle)
        {
            using var overlay = Client(popup);
            var origin = popup.PointToScreen(new(0, 0));
            var mainOrigin = window.PointToScreen(new(0, 0));
            canvas.DrawBitmap(overlay, origin.X - mainOrigin.X, origin.Y - mainOrigin.Y);
        }
        canvas.Flush();
        SKBitmap? region = null;
        try
        {
            if (crop is not null)
            {
                var point = crop.TranslatePoint(new(0, 0), window) ?? throw new InvalidOperationException("Crop is not attached.");
                var scale = window.RenderScaling;
                var rect = new SKRectI((int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale),
                    (int)Math.Round((point.X + crop.Bounds.Width) * scale), (int)Math.Round((point.Y + crop.Bounds.Height) * scale));
                if (rect.Left < 0 || rect.Top < 0 || rect.Right > bitmap.Width || rect.Bottom > bitmap.Height)
                    throw new InvalidOperationException("The requested screenshot crop is outside the native client.");
                region = new SKBitmap();
                if (!bitmap.ExtractSubset(region, rect)) throw new InvalidOperationException("Screenshot crop failed.");
            }
            using var image = SKImage.FromBitmap(region ?? bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = encoded.ToArray();
            ValidatePng(png);
            return png;
        }
        finally { region?.Dispose(); }
    }

    internal static void ValidatePng(byte[] png)
    {
        using var image = SKBitmap.Decode(png) ?? throw new InvalidDataException("Capture is not a readable PNG.");
        if (image.Width < 64 || image.Height < 64) throw new InvalidDataException("Capture is too small.");
        var colours = new HashSet<uint>();
        long light = 0;
        for (var y = 0; y < image.Height; y += Math.Max(1, image.Height / 100))
            for (var x = 0; x < image.Width; x += Math.Max(1, image.Width / 100))
            {
                var pixel = image.GetPixel(x, y);
                colours.Add((uint)pixel);
                light += pixel.Red + pixel.Green + pixel.Blue;
            }
        if (colours.Count < 24 || light == 0) throw new InvalidDataException("Capture is blank or lacks rendered content.");
    }

    private static SKBitmap Client(TopLevel top)
    {
        var handle = top.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !GetClientRect(handle, out var rect) || rect.Right < 64 || rect.Bottom < 64)
            throw new InvalidOperationException("No captureable native client exists.");
        var hdc = GetDC(handle);
        if (hdc == IntPtr.Zero) throw new InvalidOperationException("The desktop did not provide a capture DC.");
        var memory = CreateCompatibleDC(hdc);
        var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = rect.Right,
            Height = -rect.Bottom, Planes = 1, BitCount = 32 } };
        var dib = CreateDIBSection(hdc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (memory == IntPtr.Zero || dib == IntPtr.Zero)
        {
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(handle, hdc);
            throw new InvalidOperationException("Native screenshot allocation failed.");
        }
        var previous = SelectObject(memory, dib);
        try
        {
            if (!PrintWindow(handle, memory, 3)) throw new InvalidOperationException("Windows PrintWindow failed.");
            var bitmap = new SKBitmap(rect.Right, rect.Bottom, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var bytes = new byte[rect.Right * rect.Bottom * 4];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            // DIB alpha is unspecified; the captured client is opaque.
            for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
            return bitmap;
        }
        finally { SelectObject(memory, previous); DeleteObject(dib); DeleteDC(memory); ReleaseDC(handle, hdc); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colour; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
}
