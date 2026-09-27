using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctaxis.Desktop.Mapping.Preview;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Shows a saved location's preview, rendered for the current theme (<see cref="ThemeService.LocationPreviewStyleKey"/>)
/// at the control's pixel size. Rendering runs off the UI thread and only when the scene, theme or
/// size changes; scrolling and hover reuse the finished bitmap.
/// </summary>
public sealed class LocationPreviewImage : Control
{
    public static readonly StyledProperty<LocationPreviewScene?> SceneProperty =
        AvaloniaProperty.Register<LocationPreviewImage, LocationPreviewScene?>(nameof(Scene));
    public static readonly StyledProperty<LocationPreviewStyle?> PreviewStyleProperty =
        AvaloniaProperty.Register<LocationPreviewImage, LocationPreviewStyle?>(nameof(PreviewStyle));

    public LocationPreviewScene? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public LocationPreviewStyle? PreviewStyle { get => GetValue(PreviewStyleProperty); set => SetValue(PreviewStyleProperty, value); }

    private static readonly LocationPreviewScene Placeholder = LocationPreviewScene.Empty(Guid.Empty);
    private WriteableBitmap? _bitmap;
    private string? _bitmapKey;
    private string? _pendingKey;
    private int _generation;

    static LocationPreviewImage() => AffectsRender<LocationPreviewImage>(SceneProperty, PreviewStyleProperty);

    public LocationPreviewImage() => this.Bind(PreviewStyleProperty, new DynamicResourceExtension(ThemeService.LocationPreviewStyleKey));

    /// <summary>The render in flight, for tests; completed when the displayed bitmap is current.</summary>
    internal Task PendingRender { get; private set; } = Task.CompletedTask;
    internal string? DisplayedKey => _bitmapKey;
    internal static string Key(LocationPreviewScene scene, LocationPreviewStyle style, PixelSize size) =>
        $"{scene.Fingerprint}|{style.Key}|{size.Width}x{size.Height}";

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 1 || Bounds.Height < 1 || PreviewStyle is not { } style) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling)), Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling)));
        var scene = Scene ?? Placeholder;
        var key = Key(scene, style, size);
        if (_bitmap is not null)
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), new Rect(Bounds.Size));
        else
            context.FillRectangle(new SolidColorBrush(Color.FromUInt32((uint)style.Land)), new Rect(Bounds.Size));
        if (key != _bitmapKey && key != _pendingKey) StartRender(scene, style, size, scaling, key);
    }

    private void StartRender(LocationPreviewScene scene, LocationPreviewStyle style, PixelSize size, double scaling, string key)
    {
        _pendingKey = key;
        var generation = ++_generation;
        PendingRender = Task.Run(() =>
        {
            using var rendered = LocationPreviewRenderer.Render(scene, style, size.Width, size.Height);
            var pixels = new byte[rendered.ByteCount];
            Marshal.Copy(rendered.GetPixels(), pixels, 0, pixels.Length);
            return (pixels, rendered.RowBytes);
        }).ContinueWith(task =>
        {
            if (task.IsFaulted || generation != _generation) return;
            var (pixels, rowBytes) = task.Result;
            var bitmap = new WriteableBitmap(size, new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var buffer = bitmap.Lock())
                for (var y = 0; y < size.Height; y++)
                    Marshal.Copy(pixels, y * rowBytes, buffer.Address + y * buffer.RowBytes, Math.Min(rowBytes, buffer.RowBytes));
            var previous = _bitmap;
            _bitmap = bitmap;
            _bitmapKey = key;
            _pendingKey = null;
            previous?.Dispose();
            InvalidateVisual();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _generation++;
        _pendingKey = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _bitmapKey = null;
        base.OnDetachedFromVisualTree(e);
    }
}
