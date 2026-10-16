using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Noctaxis.Core.Domain;
using System.Globalization;
using System.Runtime.InteropServices;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

/// <summary>Temporary depth preview. All reusable camera-space geometry belongs to CameraTerrainDepth.</summary>
public sealed class TerrainFrameView : ChromeDrawingControl
{
    public static ISolidColorBrush NearBrush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(DepthColour(100));
    public static ISolidColorBrush FarBrush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(DepthColour(500000));
    public static ISolidColorBrush SkyBrush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(DepthColour(0));
    /// <summary>Frame centre line: dashed. The legend draws its sample from these.</summary>
    public static Avalonia.Media.Immutable.ImmutableDashStyle CentreLineDash { get; } = new Avalonia.Media.Immutable.ImmutableDashStyle([4, 3], 0);
    public static Avalonia.Media.Immutable.ImmutableSolidColorBrush CentreLineBrush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#709DB2C8"));
    public const double CentreLineThickness = 1;
    private static readonly IPen CentreLinePen = new Avalonia.Media.Immutable.ImmutablePen(CentreLineBrush, CentreLineThickness, CentreLineDash);
    /// <summary>Visibility threshold line: solid and thicker.</summary>
    public static Avalonia.Media.Immutable.ImmutableSolidColorBrush ThresholdLineBrush { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#B0D5B77B"));
    public const double ThresholdLineThickness = 2;
    private static readonly IPen ThresholdLinePen = new Avalonia.Media.Immutable.ImmutablePen(ThresholdLineBrush, ThresholdLineThickness);
    /// <summary>
    /// The frame's shape in the Planner at its default width, 208 × 128. It keeps that shape at whatever width
    /// the Camera frame is given, and the camera's shape inside it: at the default size a 3:2 landscape frame is
    /// drawn about 192 × 128, tall enough to read the skyline across it.
    /// </summary>
    public const double DefaultWidth = 208, DefaultHeight = 128;

    /// <summary>The frame's height at a given width.</summary>
    public static double HeightFor(double width) => width * DefaultHeight / DefaultWidth;
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
        AffectsRender<TerrainFrameView>(DepthProperty, ThresholdProperty, StatusProperty, OutlineProperty);
        AffectsMeasure<TerrainFrameView>(DepthProperty, StatusProperty);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : DefaultWidth;
        return Depth is { Width: > 0 } depth ? Picture(depth, new Size(width, HeightFor(width))).Size : new(width, HeightAt(width));
    }

    /// <summary>
    /// The view's height at a given width. With terrain it is the picture itself, in the camera's shape inside
    /// the frame's, so whatever is laid over the view lies on the picture, never on a band beside it. Without,
    /// the frame's shape, tall enough for its status above the direction control's bottom band.
    /// </summary>
    public double HeightAt(double width) => Depth is { Width: > 0 } depth
        ? Picture(depth, new Size(width, HeightFor(width))).Height
        : Math.Max(HeightFor(width), ChromeText(Status, width - 12 - CameraDirectionPad.EdgeBand).Height + 12 + CameraDirectionPad.EdgeBand + 6);

    /// <summary>The camera's picture, as large as fits in <paramref name="space"/> and centred in it.</summary>
    private static Rect Picture(CameraTerrainDepth depth, Size space)
    {
        var horizontal = depth.Width > 1 ? Angles.NormaliseDegrees(depth.Bearings[^1] - depth.Bearings[0]) : 1;
        var aspect = Math.Tan(horizontal * Math.PI / 360) / Math.Tan((depth.UpperAltitude - depth.LowerAltitude) * Math.PI / 360);
        aspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 1;
        var width = Math.Min(space.Width, space.Height * aspect);
        var height = width / aspect;
        return new Rect((space.Width - width) / 2, (space.Height - height) / 2, width, height);
    }

    /// <summary>
    /// Terrain by its distance, nearest lightest, in a neutral grey that never colours the land; the furthest
    /// terrain stays clearly lighter than the night-blue sky, so a distant skyline still stands out against it.
    /// </summary>
    public static Color DepthColour(double distance)
    {
        if (distance <= 0 || !double.IsFinite(distance)) return Color.Parse("#101D30");
        var shade = (byte)Math.Round(230 - 140 * Math.Clamp(Math.Log10(Math.Max(100, distance) / 100) / Math.Log10(5000), 0, 1));
        return Color.FromRgb(shade, shade, shade);
    }

    /// <summary>
    /// The sky by altitude: the legend's night blue overhead, a little lighter towards the horizon, so the
    /// skyline has a clear edge against it. Presentation only; it says nothing about the weather.
    /// </summary>
    internal static Color SkyColour(double altitudeDegrees)
    {
        var sky = DepthColour(0);
        var low = Color.Parse("#1D3048");
        var t = Math.Clamp(altitudeDegrees / 25, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(b + (a - b) * t);
        return Color.FromRgb(Mix(sky.R, low.R), Mix(sky.G, low.G), Mix(sky.B, low.B));
    }

    /// <summary>The skyline's edge: light and thin, over the terrain's top.</summary>
    public static Avalonia.Media.Immutable.ImmutableSolidColorBrush SkylineBrush { get; } = new(Color.Parse("#CCE4EDF7"));
    private static readonly IPen SkylinePen = new Avalonia.Media.Immutable.ImmutablePen(SkylineBrush, 1.25, lineJoin: PenLineJoin.Round);

    /// <summary>How many pixels across the picture is drawn at before it is scaled to the frame.</summary>
    internal const int PictureColumns = 256;

    public Rect FrameRectangle => Depth is { Width: > 0 } depth ? Picture(depth, Bounds.Size) : new Rect(Bounds.Size);

    /// <summary>The skyline, in the picture's own pixels: per column, the row terrain first starts at (NaN: none).</summary>
    private double[] _skyline = [];

    /// <summary>
    /// The picture as the camera's lens would draw it: each pixel's direction from the camera's bearing, pitch
    /// and field of view (<see cref="CameraFrameProjection.Direction"/>), coloured by the terrain distance the
    /// profile has nearest that direction, or by the sky's tone where there is none. The profile is sampled on an
    /// angular grid; only where it is drawn changes, never what it says.
    /// </summary>
    private WriteableBitmap BuildPicture(CameraTerrainDepth data, out double[] skyline)
    {
        var horizontal = data.Width > 1 ? Angles.NormaliseDegrees(data.Bearings[^1] - data.Bearings[0]) : 1;
        var vertical = data.UpperAltitude - data.LowerAltitude;
        var pitch = (data.UpperAltitude + data.LowerAltitude) / 2;
        var size = Picture(data, new Size(PictureColumns, PictureColumns * 4));
        var (width, height) = (PictureColumns, Math.Clamp((int)Math.Round(size.Height * PictureColumns / size.Width), 16, PictureColumns * 4));
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var tops = new double[width];
        Array.Fill(tops, double.NaN);
        using (var pixels = bitmap.Lock())
        {
            var row = new int[width];
            for (var y = 0; y < height; y++)
            {
                var up = 1 - (y + .5) / height * 2;
                for (var x = 0; x < width; x++)
                {
                    var across = (x + .5) / width * 2 - 1;
                    var (offset, altitude) = CameraFrameProjection.Direction(across, up, horizontal, vertical, pitch);
                    var place = Math.Clamp((offset + horizontal / 2) / horizontal, 0, 1) * (data.Width - 1);
                    var column = (int)Math.Round(place);
                    var line = (int)Math.Floor(Math.Clamp((data.UpperAltitude - altitude) / vertical, 0, .999999) * CameraTerrainDepth.Rows);
                    var distance = data.DistanceAt(column, line);
                    // Terrain or sky by the horizon's exact altitude, between bearings by straight interpolation;
                    // the rows only colour it. Without horizon altitudes, by the rows alone.
                    var horizon = HorizonAt(data, place);
                    var terrain = double.IsFinite(horizon) ? altitude <= horizon : distance > 0 && double.IsFinite(distance);
                    if (terrain && !(distance > 0)) distance = TerrainBelow(data, column, line);
                    if (terrain && double.IsNaN(tops[x])) tops[x] = y;
                    var c = terrain ? DepthColour(distance) : SkyColour(altitude);
                    row[x] = unchecked((int)(0xff000000u | (uint)c.R << 16 | (uint)c.G << 8 | c.B));
                }
                Marshal.Copy(row, 0, pixels.Address + y * pixels.RowBytes, row.Length);
            }
        }
        skyline = tops;
        return bitmap;
    }

    /// <summary>The horizon's altitude at a fractional column, interpolated between its neighbours; NaN without one.</summary>
    private static double HorizonAt(CameraTerrainDepth data, double place)
    {
        var low = (int)Math.Floor(place);
        var high = Math.Min(low + 1, data.Width - 1);
        var (a, b) = (data.HorizonAt(low), data.HorizonAt(high));
        if (double.IsNaN(a)) return b;
        if (double.IsNaN(b)) return a;
        return a + (b - a) * (place - low);
    }

    /// <summary>The nearest terrain distance at or below a row in a column, for colouring the terrain there.</summary>
    private static double TerrainBelow(CameraTerrainDepth data, int column, int line)
    {
        for (var row = line; row < CameraTerrainDepth.Rows; row++)
            if (data.DistanceAt(column, row) is var distance && distance > 0 && double.IsFinite(distance)) return distance;
        return 500000;
    }

    /// <summary>The frame's angles: its horizontal and vertical span, its pitch and its centre bearing.</summary>
    private static (double Horizontal, double Vertical, double Pitch, double Centre) Angular(CameraTerrainDepth data)
    {
        var horizontal = data.Width > 1 ? Angles.NormaliseDegrees(data.Bearings[^1] - data.Bearings[0]) : 1;
        return (horizontal, data.UpperAltitude - data.LowerAltitude, (data.UpperAltitude + data.LowerAltitude) / 2,
            Angles.NormaliseDegrees(data.Bearings[0] + horizontal / 2));
    }

    /// <summary>Where a direction (absolute bearing and altitude) is drawn in the frame, by the one projection.</summary>
    internal Point? PointFor(double bearing, double altitude)
    {
        if (Depth is not { Width: > 0 } data) return null;
        var (horizontal, vertical, pitch, centre) = Angular(data);
        if (CameraFrameProjection.Project(Angles.NormaliseSignedDegrees(bearing - centre), altitude, horizontal, vertical, pitch) is not { } at)
            return null;
        var frame = FrameRectangle;
        return new Point(frame.Center.X + at.Across * frame.Width / 2, frame.Center.Y - at.Up * frame.Height / 2);
    }

    /// <summary>Where an altitude is drawn down the frame, on its centre column; null outside it.</summary>
    internal double? AltitudeY(double altitude)
    {
        if (Depth is not { Width: > 0 } data || PointFor(Angular(data).Centre, altitude) is not { } point) return null;
        var frame = FrameRectangle;
        return point.Y >= frame.Top - .5 && point.Y <= frame.Bottom + .5 ? point.Y : null;
    }

    /// <summary>
    /// The skyline as drawn, in the view's coordinates: each bearing's exact horizon altitude through the one
    /// projection, so it stands exactly where the pitch scale puts that altitude. Empty without horizon altitudes.
    /// </summary>
    internal IReadOnlyList<Point?> SkylinePoints => Depth is { Width: > 0 } data && !data.HorizonAltitudes.IsDefaultOrEmpty
        ? Enumerable.Range(0, data.Width).Select(column => double.IsFinite(data.HorizonAt(column))
            ? PointFor(data.Bearings[column], data.HorizonAt(column)) : null).ToArray()
        : [];

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(ChromeBackground, new Rect(Bounds.Size));
        if (!ReferenceEquals(_source, Depth))
        {
            _bitmap?.Dispose(); _bitmap = null; _skyline = []; _source = Depth;
            if (Depth is { Width: > 0 } data) _bitmap = BuildPicture(data, out _skyline);
        }
        if (_bitmap is null || Depth is not { } depth)
        {
            var text = ChromeText(Status, Bounds.Width - 12 - CameraDirectionPad.EdgeBand);
            context.DrawText(text, new Point(CameraDirectionPad.EdgeBand + 6, 12));
            DrawOutline(context, new Rect(Bounds.Size));
            return;
        }
        var frame = FrameRectangle;
        using (context.PushClip(frame))
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), frame);
            // The skyline's edge, so a low or distant skyline still reads at a glance: at the horizon's exact
            // altitudes when the profile has them, otherwise along the picture's own terrain edge.
            if (SkylinePoints is { Count: > 1 } exact)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var open = false;
                    foreach (var point in exact)
                    {
                        if (point is not { } at) { if (open) g.EndFigure(false); open = false; continue; }
                        if (open) g.LineTo(at);
                        else { g.BeginFigure(at, false); open = true; }
                    }
                    if (open) g.EndFigure(false);
                }
                context.DrawGeometry(null, SkylinePen, geometry);
            }
            else if (_skyline.Length > 1)
            {
                var scale = new Vector(frame.Width / _bitmap.PixelSize.Width, frame.Height / _bitmap.PixelSize.Height);
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    // One stroke per stretch of columns with terrain: none across a gap of open sky.
                    var open = false;
                    for (var x = 0; x < _skyline.Length; x++)
                    {
                        if (double.IsNaN(_skyline[x])) { if (open) g.EndFigure(false); open = false; continue; }
                        var point = new Point(frame.Left + (x + .5) * scale.X, frame.Top + _skyline[x] * scale.Y);
                        if (open) g.LineTo(point);
                        else { g.BeginFigure(point, false); open = true; }
                    }
                    if (open) g.EndFigure(false);
                }
                context.DrawGeometry(null, SkylinePen, geometry);
            }
            // The camera's aim (dashed) and the coverage threshold (solid, thicker), each where its own altitude is
            // drawn, so they differ by pattern and weight as well as colour (legend behind the frame's info button).
            var centre = frame.Center.Y;
            context.DrawLine(CentreLinePen, new Point(frame.Left, centre), new Point(frame.Right, centre));
            var thresholdAltitude = depth.LowerAltitude + (depth.UpperAltitude - depth.LowerAltitude) * Math.Clamp(Threshold, 0, .5);
            if (AltitudeY(thresholdAltitude) is { } threshold)
                context.DrawLine(ThresholdLinePen, new Point(frame.Left, threshold), new Point(frame.Right, threshold));
        }
        DrawOutline(context, frame);
    }

    /// <summary>
    /// The viewport's edge, in the theme's border colour: the night sky is drawn dark, close to the card, so
    /// without it a frame looking mostly at sky has no visible edge.
    /// </summary>
    private void DrawOutline(DrawingContext context, Rect frame)
    {
        if (Outline is { } brush && frame.Width > 1 && frame.Height > 1)
            context.DrawRectangle(null, new Pen(brush, 1), frame.Deflate(.5));
    }

    public static readonly StyledProperty<IBrush?> OutlineProperty = AvaloniaProperty.Register<TerrainFrameView, IBrush?>(nameof(Outline));
    /// <summary>The viewport's edge colour; the theme's border colour unless set.</summary>
    public IBrush? Outline { get => GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }

    public TerrainFrameView() => this.BindThemeDefault(OutlineProperty, "SurfaceBorder");
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _bitmap?.Dispose(); _bitmap = null; _source = null; base.OnDetachedFromVisualTree(e); }
}
