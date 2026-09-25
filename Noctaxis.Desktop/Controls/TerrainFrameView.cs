using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Noctaxis.Core.Domain;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Noctaxis.Desktop.Controls;

/// <summary>Temporary depth preview. All reusable camera-space geometry belongs to CameraTerrainDepth.</summary>
public sealed class TerrainFrameView : ChromeDrawingControl
{
    public static ISolidColorBrush NearBrush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(DepthColour(100));
    public static ISolidColorBrush FarBrush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(DepthColour(500000));
    public static ISolidColorBrush SkyBrush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(DepthColour(0));
    public static readonly StyledProperty<CameraTerrainDepth?> DepthProperty = AvaloniaProperty.Register<TerrainFrameView, CameraTerrainDepth?>(nameof(Depth));
    public static readonly StyledProperty<double> ThresholdProperty = AvaloniaProperty.Register<TerrainFrameView, double>(nameof(Threshold), .05);
    public static readonly StyledProperty<string> StatusProperty = AvaloniaProperty.Register<TerrainFrameView, string>(nameof(Status), "Resolving terrain…");
    public CameraTerrainDepth? Depth { get => GetValue(DepthProperty); set => SetValue(DepthProperty, value); }
    public double Threshold { get => GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public string Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    private CameraTerrainDepth? _source;
    private WriteableBitmap? _bitmap;
    static TerrainFrameView()
    {
        AffectsRender<TerrainFrameView>(DepthProperty, ThresholdProperty, StatusProperty);
        AffectsMeasure<TerrainFrameView>(DepthProperty, StatusProperty);
    }
    protected override Size MeasureOverride(Size availableSize) => new(208,
        Depth is { Width: > 0 } ? 160 : ChromeText(Status, 196).Height + 24);

    public static Color DepthColour(double distance)
    {
        if (distance <= 0 || !double.IsFinite(distance)) return Color.Parse("#101D30");
        var shade = (byte)Math.Round(230 - 180 * Math.Clamp(Math.Log10(Math.Max(100, distance) / 100) / Math.Log10(5000), 0, 1));
        return Color.FromRgb(shade, shade, shade);
    }

    public Rect FrameRectangle
    {
        get
        {
            if (Depth is not { Width: > 0 } depth) return new Rect(Bounds.Size);
            var horizontal = depth.Width > 1 ? Angles.NormaliseDegrees(depth.Bearings[^1] - depth.Bearings[0]) : 1;
            var aspect = Math.Tan(horizontal * Math.PI / 360) / Math.Tan((depth.UpperAltitude - depth.LowerAltitude) * Math.PI / 360);
            aspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 1;
            var width = Math.Min(Bounds.Width, Bounds.Height * aspect);
            var height = width / aspect;
            return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(ChromeBackground, new Rect(Bounds.Size));
        if (!ReferenceEquals(_source, Depth))
        {
            _bitmap?.Dispose(); _bitmap = null; _source = Depth;
            if (Depth is { Width: > 0 } data)
            {
                _bitmap = new WriteableBitmap(new PixelSize(data.Width, CameraTerrainDepth.Rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using var pixels = _bitmap.Lock();
                var row = new int[data.Width];
                for (var y = 0; y < CameraTerrainDepth.Rows; y++)
                {
                    for (var x = 0; x < data.Width; x++)
                    {
                        var c = DepthColour(data.DistanceAt(x, y));
                        row[x] = unchecked((int)(0xff000000u | (uint)c.R << 16 | (uint)c.G << 8 | c.B));
                    }
                    Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length);
                }
            }
        }
        if (_bitmap is null)
        {
            var text = ChromeText(Status, Bounds.Width - 12);
            context.DrawText(text, new Point(6, 12)); return;
        }
        var frame = FrameRectangle;
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), frame);
        var centre = frame.Y + frame.Height / 2;
        var threshold = frame.Bottom - frame.Height * Math.Clamp(Threshold, 0, .5);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#709DB2C8")), 1), new Point(frame.Left, centre), new Point(frame.Right, centre));
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#B0D5B77B")), 1), new Point(frame.Left, threshold), new Point(frame.Right, threshold));
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _bitmap?.Dispose(); _bitmap = null; _source = null; base.OnDetachedFromVisualTree(e); }
}
