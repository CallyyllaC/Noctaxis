using Avalonia;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Where the camera points, as one instrument laid over the Camera frame's picture: a compass tape along
/// its bottom edge and a pitch ladder down its left, each sliding under a fixed mark where the camera
/// aims. Dragging anywhere on the picture is relative, like turning the camera itself: across
/// turns it (bearing), up and down tilts it (pitch). Bearing has no edge and wraps through 0°/360°;
/// pitch stops at <see cref="CameraFramingSettings"/>' limits. The arrow keys nudge it as well, a
/// <see cref="NudgeDegrees"/> step at a time, finer with Shift.
/// </summary>
/// <remarks>
/// <see cref="Bearing"/> and <see cref="Pitch"/> are bound two-way to the camera's own bearing and pitch,
/// so the instrument holds no value of its own: it draws what it is given and writes back changes.
/// </remarks>
public sealed class CameraDirectionPad : ChromeDrawingControl
{
    /// <summary>One nudge: an arrow key, or a tap on one of the arrows beside the instrument.</summary>
    public const double NudgeDegrees = 1;
    /// <summary>How much finer a nudge or a drag is with Shift held.</summary>
    public const double FineFactor = .1;
    /// <summary>How far the camera turns or tilts for each pixel dragged.</summary>
    public const double DegreesPerPixel = .25;
    /// <summary>How many degrees of bearing the tape shows per pixel of width.</summary>
    private const double TapePixelsPerDegree = 2;

    public static readonly StyledProperty<double> BearingProperty = AvaloniaProperty.Register<CameraDirectionPad, double>(
        nameof(Bearing), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> PitchProperty = AvaloniaProperty.Register<CameraDirectionPad, double>(
        nameof(Pitch), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<CameraDirectionPad, IBrush?>(nameof(Accent));

    public double Bearing { get => GetValue(BearingProperty); set => SetValue(BearingProperty, value); }
    public double Pitch { get => GetValue(PitchProperty); set => SetValue(PitchProperty, value); }
    /// <summary>The aim mark's colour.</summary>
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public static readonly StyledProperty<bool> InvertHorizontalDragProperty =
        AvaloniaProperty.Register<CameraDirectionPad, bool>(nameof(InvertHorizontalDrag));
    public static readonly StyledProperty<bool> InvertVerticalDragProperty =
        AvaloniaProperty.Register<CameraDirectionPad, bool>(nameof(InvertVerticalDrag));
    /// <summary>Whether a drag across turns the other way. Only the drag's direction; never the bearing it writes.</summary>
    public bool InvertHorizontalDrag { get => GetValue(InvertHorizontalDragProperty); set => SetValue(InvertHorizontalDragProperty, value); }
    /// <summary>Whether a drag up and down tilts the other way. Only the drag's direction; never the pitch it writes.</summary>
    public bool InvertVerticalDrag { get => GetValue(InvertVerticalDragProperty); set => SetValue(InvertVerticalDragProperty, value); }

    private Point? _dragFrom;

    static CameraDirectionPad()
    {
        AffectsRender<CameraDirectionPad>(BearingProperty, PitchProperty, AccentProperty,
            HorizontalFieldOfViewProperty, VerticalFieldOfViewProperty);
        FocusableProperty.OverrideDefaultValue<CameraDirectionPad>(true);
    }

    public CameraDirectionPad()
    {
        this.BindThemeDefault(AccentProperty, "Accent");
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <summary>A bearing in 0° to 360°, 360° itself being 0°.</summary>
    public static double WrapBearing(double degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>A pitch inside the camera's limits.</summary>
    public static double ClampPitch(double degrees) =>
        Math.Clamp(degrees, CameraFramingSettings.MinimumCameraPitchDegrees, CameraFramingSettings.MaximumCameraPitchDegrees);

    /// <summary>Turns by <paramref name="degrees"/>, through north if need be.</summary>
    public void Turn(double degrees)
    {
        if (degrees != 0 && double.IsFinite(degrees)) SetCurrentValue(BearingProperty, WrapBearing(Math.Round(Bearing + degrees, 4)));
    }

    /// <summary>Tilts by <paramref name="degrees"/>, up to and no further than either limit.</summary>
    public void Tilt(double degrees)
    {
        if (degrees == 0 || !double.IsFinite(degrees)) return;
        var next = Math.Round(ClampPitch(Pitch + degrees), 4);
        if (next != Pitch) SetCurrentValue(PitchProperty, next);
    }

    /// <summary>
    /// How far a drag of <paramref name="pixels"/> turns and tilts the camera. Normally right turns clockwise and
    /// up looks up; each inversion reverses its own axis only. Drags alone: nudges and keys never invert.
    /// </summary>
    public static (double Turn, double Tilt) DragDelta(Vector pixels, bool fine, bool invertHorizontal, bool invertVertical)
    {
        var scale = DegreesPerPixel * (fine ? FineFactor : 1);
        return (pixels.X * scale * (invertHorizontal ? -1 : 1), -pixels.Y * scale * (invertVertical ? -1 : 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        var to = e.GetPosition(this);
        var (turn, tilt) = DragDelta(to - from, (e.KeyModifiers & KeyModifiers.Shift) != 0, InvertHorizontalDrag, InvertVerticalDrag);
        Turn(turn);
        Tilt(tilt);
        _dragFrom = to;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is null) return;
        _dragFrom = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragFrom = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = NudgeDegrees * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? FineFactor : 1);
        switch (e.Key)
        {
            case Key.Left: Turn(-step); break;
            case Key.Right: Turn(step); break;
            case Key.Up: Tilt(step); break;
            case Key.Down: Tilt(-step); break;
            default: return;
        }
        e.Handled = true;
    }

    // Laid over the picture, the instrument takes the picture's size rather than asking for one.
    protected override Size MeasureOverride(Size availableSize) => default;

    /// <summary>
    /// How deep the instrument's two scales are: the bearing tape along the picture's bottom edge and the pitch
    /// ladder down its left, each as deep as the arrows at its ends are wide.
    /// </summary>
    public const double EdgeBand = 28;
    /// <summary>How many pixels of the pitch ladder each degree of pitch takes.</summary>
    private const double LadderPixelsPerDegree = 1;

    /// <summary>
    /// The scales and figures, drawn over the frame's own fixed greyscale picture, so they are fixed too: light
    /// ticks over a narrow dark fade only where the ticks are, whatever the theme. The fade runs from clear
    /// black, never from clear white (<see cref="Colors.Transparent"/>), so its middle darkens the picture
    /// beneath rather than greying it over.
    /// </summary>
    public static IImmutableSolidColorBrush OverlayForeground { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#F2FFFFFF"));
    private static readonly IBrush QuietFigure = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.White, .78);
    private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);
    private static readonly Color Shade = Color.Parse("#8C000000");
    /// <summary>How far in from its edge each scale's fade reaches.</summary>
    internal const double TapeFadeDepth = 26, LadderFadeDepth = 44;
    private static readonly IBrush TapeFade = new Avalonia.Media.Immutable.ImmutableLinearGradientBrush(
        [new Avalonia.Media.Immutable.ImmutableGradientStop(0, Clear), new Avalonia.Media.Immutable.ImmutableGradientStop(1, Shade)],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    private static readonly IBrush LadderFade = new Avalonia.Media.Immutable.ImmutableLinearGradientBrush(
        [new Avalonia.Media.Immutable.ImmutableGradientStop(0, Clear), new Avalonia.Media.Immutable.ImmutableGradientStop(1, Shade)],
        startPoint: new RelativePoint(1, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 0, RelativeUnit.Relative));

    /// <summary>The bearing tape: along the bottom edge, between the turn arrows at its ends.</summary>
    internal Rect TapeBounds => new(2 * EdgeBand, Bounds.Height - EdgeBand, Math.Max(0, Bounds.Width - 3 * EdgeBand), EdgeBand);
    /// <summary>The pitch ladder: down the left edge, between the tilt arrows at its ends.</summary>
    internal Rect LadderBounds => new(0, EdgeBand, EdgeBand, Math.Max(0, Bounds.Height - 2 * EdgeBand));

    public static readonly StyledProperty<double> HorizontalFieldOfViewProperty =
        AvaloniaProperty.Register<CameraDirectionPad, double>(nameof(HorizontalFieldOfView));
    public static readonly StyledProperty<double> VerticalFieldOfViewProperty =
        AvaloniaProperty.Register<CameraDirectionPad, double>(nameof(VerticalFieldOfView));
    /// <summary>
    /// The picture's angular width and height, which the scales are drawn to (<see cref="CameraFrameProjection"/>):
    /// a tick on either scale is where its angle is drawn in the picture beneath. Unknown (0), they fall back to a
    /// fixed spacing.
    /// </summary>
    public double HorizontalFieldOfView { get => GetValue(HorizontalFieldOfViewProperty); set => SetValue(HorizontalFieldOfViewProperty, value); }
    public double VerticalFieldOfView { get => GetValue(VerticalFieldOfViewProperty); set => SetValue(VerticalFieldOfViewProperty, value); }

    private static bool Known(double fieldOfView) => double.IsFinite(fieldOfView) && fieldOfView is > 0 and < 180;

    /// <summary>Where a bearing this far from the camera's own is drawn across the picture.</summary>
    internal double BearingX(double offsetDegrees) => Known(HorizontalFieldOfView) &&
        CameraFrameProjection.Project(offsetDegrees, Pitch, HorizontalFieldOfView, Known(VerticalFieldOfView) ? VerticalFieldOfView : HorizontalFieldOfView, Pitch) is { } at
            ? Bounds.Width / 2 * (1 + at.Across)
            : Bounds.Width / 2 + offsetDegrees * TapePixelsPerDegree;

    /// <summary>
    /// Where an altitude is drawn down the picture, for the camera's current pitch: the same projection as the
    /// picture's skyline (<see cref="CameraFrameProjection.Project"/>), along the camera's own bearing.
    /// </summary>
    internal double AltitudeY(double altitudeDegrees)
    {
        if (!Known(VerticalFieldOfView)) return Bounds.Height / 2 - (altitudeDegrees - Pitch) * LadderPixelsPerDegree;
        var horizontal = Known(HorizontalFieldOfView) ? HorizontalFieldOfView : VerticalFieldOfView;
        return CameraFrameProjection.Project(0, altitudeDegrees, horizontal, VerticalFieldOfView, Pitch) is { } at
            ? Bounds.Height / 2 * (1 - at.Up)
            : altitudeDegrees > Pitch ? double.NegativeInfinity : double.PositiveInfinity;
    }

    /// <summary>
    /// Where the ladder's level (0°) mark is drawn: where the true horizon is in the picture, so through the aim
    /// when the camera is level, and lower as the camera looks up.
    /// </summary>
    internal double HorizonY => AltitudeY(0);

    /// <summary>
    /// The scale's tick steps for its spacing, in pixels per degree at its centre: minor ticks at least 9 px
    /// apart, and labelled major ticks a whole number of minors apart and far enough apart for their figures.
    /// </summary>
    internal static (double Minor, double Major) TickSteps(double pixelsPerDegree, double majorSpacing)
    {
        double[] steps = [1, 2, 5, 10, 15, 30, 45, 90];
        var minor = steps.FirstOrDefault(step => step * pixelsPerDegree >= 9, 90);
        var major = steps.FirstOrDefault(step => step >= minor && step % minor == 0 && step * pixelsPerDegree >= majorSpacing, 90);
        return (minor, major);
    }

    private static double PixelsPerDegree(double extent, double fieldOfView, double fallback) => Known(fieldOfView)
        ? extent / 2 / Math.Tan(fieldOfView / 2 * Math.PI / 180) * Math.PI / 180 : fallback;

    /// <summary>One tick of the pitch scale as laid out: its altitude, where it is, and its figure if it has one.</summary>
    internal sealed record PitchTick(double Altitude, double Y, double Length, bool IsMajor, string? Label, Rect? LabelBounds);

    /// <summary>Where the pitch scale's figures start: just past the level tick, the longest.</summary>
    private const double PitchLabelLeft = 20;
    private const double MinorTickLength = 4, MajorTickLength = 9, LevelTickLength = 14;

    /// <summary>
    /// The pitch scale, laid out for the camera's current pitch and the picture's height. Every tick is placed by
    /// <see cref="AltitudeY"/>, the one projection the skyline uses, over the altitudes the picture actually shows.
    /// Minor ticks every 5° (1° or 2° on a long lens, 10° on a wide one), at least 7 px apart; majors every 10°
    /// (or the next step that is twice a minor and 14 px apart); figures on the majors, every other major when
    /// they would crowd. Figures stay between the tilt arrows, inside the picture, and give way to the camera's
    /// own pitch mark, which the status line reads exactly.
    /// </summary>
    internal IReadOnlyList<PitchTick> PitchScale()
    {
        var ladder = LadderBounds;
        if (ladder.Height <= 0) return [];
        var perDegree = PixelsPerDegree(Bounds.Height, VerticalFieldOfView, LadderPixelsPerDegree);
        double[] minors = [1, 2, 5, 10, 15, 30, 45];
        var minor = minors.FirstOrDefault(step => step * perDegree >= 7, 45);
        double[] majors = [2, 5, 10, 20, 30, 45, 90];
        var major = majors.FirstOrDefault(step => step >= 2 * minor && step % minor == 0 && step * perDegree >= 14, 90);
        var height = ChromeText("0°").Height;
        var labelStep = major * (major * perDegree >= height + 6 ? 1 : 2);

        var reach = Known(VerticalFieldOfView) ? VerticalFieldOfView / 2 + minor : Bounds.Height / 2 / LadderPixelsPerDegree;
        var marker = AltitudeY(Pitch);
        var ticks = new List<PitchTick>();
        for (var tick = Math.Max(-90, Math.Ceiling((Pitch - reach) / minor) * minor); tick <= Math.Min(90, Pitch + reach); tick += minor)
        {
            var y = AltitudeY(tick);
            if (!double.IsFinite(y) || y < ladder.Top || y > ladder.Bottom) continue;
            var level = Math.Abs(tick) < 1e-6;
            var isMajor = level || Math.Abs(Math.IEEERemainder(tick, major)) < 1e-6;
            string? label = null;
            Rect? bounds = null;
            if (isMajor && (level || Math.Abs(Math.IEEERemainder(tick, labelStep)) < 1e-6))
            {
                var text = PitchLabel(tick);
                var figure = ChromeText(text);
                var box = new Rect(ladder.Left + PitchLabelLeft, y - figure.Height / 2, figure.Width, figure.Height);
                // Inside the picture, between the tilt arrows, and clear of the camera's own pitch mark.
                if (box.Top >= ladder.Top && box.Bottom <= ladder.Bottom && Math.Abs(y - marker) >= figure.Height / 2 + 2)
                    (label, bounds) = (text, box);
            }
            ticks.Add(new PitchTick(tick, y, level ? LevelTickLength : isMajor ? MajorTickLength : MinorTickLength, isMajor, label, bounds));
        }
        return ticks;
    }

    /// <summary>A pitch figure: signed, as the status line writes pitch, and plain at level.</summary>
    internal static string PitchLabel(double altitude) => Math.Abs(altitude) < 1e-6 ? "0°"
        : altitude > 0 ? $"+{altitude:0.#}°" : $"-{-altitude:0.#}°";

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        // Transparent, but filled, so the whole picture takes a drag.
        context.FillRectangle(ChromeBackground, bounds);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var clip = context.PushClip(bounds);
        // Only behind the ticks: the rest of the picture is untouched.
        context.FillRectangle(LadderFade, new Rect(0, 0, LadderFadeDepth, bounds.Height));
        context.FillRectangle(TapeFade, new Rect(LadderFadeDepth, bounds.Height - TapeFadeDepth, bounds.Width - LadderFadeDepth, TapeFadeDepth));
        var line = new Pen(ChromeForeground, 1);
        var aim = new Pen(Accent ?? Brushes.Orange, 2);

        // Pitch: a ladder of altitudes, each tick level with that altitude in the picture, the true horizon's (0°)
        // the longest; the camera's own pitch at the fixed mark, level with the frame's centre line.
        var ladder = LadderBounds;
        if (ladder.Height > 0)
        {
            // Figures quieter than the ticks, the level (0°) figure firm and in weight, so the true horizontal is
            // found at a glance without a line across the picture.
            var quiet = QuietFigure;
            // Drawn before the bearing scale, and without a clip of its own: its ticks and figures are already
            // kept inside it. A figure drawn after another figure's clip has closed came out at twice the scale,
            // with everything after it, so no figure here follows a clipped one.
            foreach (var tick in PitchScale())
            {
                var pen = tick.Altitude == 0 ? new Pen(ChromeForeground, 1.6) : line;
                context.DrawLine(pen, new Point(ladder.Left + 3, tick.Y), new Point(ladder.Left + 3 + tick.Length, tick.Y));
                if (tick.Label is not { } label || tick.LabelBounds is not { } box) continue;
                var figure = ChromeText(label);
                if (tick.Altitude == 0) figure.SetFontWeight(FontWeight.SemiBold);
                else figure.SetForegroundBrush(quiet);
                context.DrawText(figure, new Point(box.X, tick.Y - figure.Height / 2));
            }
            var centre = Math.Clamp(AltitudeY(Pitch), ladder.Top, ladder.Bottom);
            context.DrawLine(aim, new Point(ladder.Left + 2, centre), new Point(ladder.Right - 2, centre));
        }

        // Bearing: a compass tape whose ticks stand under the bearings they name in the picture above, with the
        // camera's own bearing at the fixed mark in the middle; minor ticks, and longer labelled majors.
        var tape = TapeBounds;
        if (tape.Width > 0)
        {
            using (context.PushClip(tape))
            {
                var baseline = tape.Bottom - 3;
                var (minor, major) = TickSteps(PixelsPerDegree(bounds.Width, HorizontalFieldOfView, TapePixelsPerDegree), 30);
                var reach = Known(HorizontalFieldOfView) ? HorizontalFieldOfView / 2 : bounds.Width / 2 / TapePixelsPerDegree;
                for (var tick = Math.Ceiling((Bearing - reach) / minor) * minor; tick <= Bearing + reach; tick += minor)
                {
                    var x = BearingX(tick - Bearing);
                    var labelled = Math.Abs(WrapBearing(tick) % major) < 1e-6;
                    context.DrawLine(line, new Point(x, baseline - (labelled ? 6 : 3)), new Point(x, baseline));
                    if (!labelled) continue;
                    var figure = ChromeText($"{WrapBearing(tick):0}°");
                    // A figure under the mark would be cut by it; the status line gives the exact bearing.
                    // Nor is a figure the tape's end would cut drawn.
                    if (Math.Abs(x - BearingX(0)) < figure.Width / 2 + 3 ||
                        x - figure.Width / 2 < tape.Left || x + figure.Width / 2 > tape.Right) continue;
                    context.DrawText(figure, new Point(x - figure.Width / 2, baseline - 7 - figure.Height));
                }
            }
            var mark = BearingX(0);
            context.DrawLine(aim, new Point(mark, tape.Top + 4), new Point(mark, tape.Bottom - 1));
        }
    }
}
