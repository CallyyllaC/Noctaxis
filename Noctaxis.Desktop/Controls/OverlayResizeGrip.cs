using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Noctaxis.Desktop.Controls;

/// <summary>The corner of an instrument its resize grip sits in.</summary>
public enum OverlayGripCorner { BottomRight, TopRight }

/// <summary>
/// The resize grip of one of the Planner map's fixed instruments. It changes the instrument's width only: a
/// drag (mouse, pen or finger) reports how far the pointer has moved across since it pressed, and the arrow
/// keys step the width while the grip has focus, so resizing needs neither hover nor a drag. It takes the
/// pointer and the wheel it receives, so nothing it handles reaches the map or the instrument beneath.
/// Small to look at, with a 28 px target.
/// </summary>
public sealed class OverlayResizeGrip : Control, ICustomHitTest
{
    /// <summary>One arrow-key step.</summary>
    public const double KeyboardStep = 16;

    public static readonly StyledProperty<OverlayGripCorner> CornerProperty =
        AvaloniaProperty.Register<OverlayResizeGrip, OverlayGripCorner>(nameof(Corner));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<OverlayResizeGrip>();

    public OverlayGripCorner Corner { get => GetValue(CornerProperty); set => SetValue(CornerProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>A drag began.</summary>
    public event EventHandler? ResizeStarted;
    /// <summary>The drag moved: how far the pointer has moved across, in pixels, since it pressed (right is wider).</summary>
    public event EventHandler<double>? Resizing;
    /// <summary>The drag ended, or was lost.</summary>
    public event EventHandler? ResizeCompleted;
    /// <summary>An arrow key asked for this many pixels wider (negative: narrower).</summary>
    public event EventHandler<double>? Stepped;

    private Point? _start;
    private bool _keyboardFocus;

    static OverlayResizeGrip()
    {
        FocusableProperty.OverrideDefaultValue<OverlayResizeGrip>(true);
        AffectsRender<OverlayResizeGrip>(CornerProperty, ForegroundProperty);
    }

    public OverlayResizeGrip()
    {
        Width = Height = 28;
        UpdateCursor();
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CornerProperty) UpdateCursor();
    }

    private void UpdateCursor() => Cursor = new Cursor(Corner == OverlayGripCorner.BottomRight
        ? StandardCursorType.BottomRightCorner : StandardCursorType.TopRightCorner);

    public override void Render(DrawingContext context)
    {
        var pen = new Pen(Foreground ?? Brushes.Gray, 1.4, lineCap: PenLineCap.Round);
        var (w, h) = (Bounds.Width, Bounds.Height);
        // Three short diagonals into the grip's own corner, like a window's size grip.
        for (var i = 0; i < 3; i++)
        {
            var reach = 4 + i * 4;
            if (Corner == OverlayGripCorner.BottomRight)
                context.DrawLine(pen, new Point(w - 5 - reach, h - 5), new Point(w - 5, h - 5 - reach));
            else
                context.DrawLine(pen, new Point(w - 5 - reach, 5), new Point(w - 5, 5 + reach));
        }
        if (_keyboardFocus && IsFocused)
            context.DrawRectangle(null, new Pen(Foreground ?? Brushes.Gray, 1.5), new Rect(1, 1, w - 2, h - 2), 4, 4);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _keyboardFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _keyboardFocus = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || TopLevel.GetTopLevel(this) is not { } top) return;
        e.Pointer.Capture(this);
        _start = e.GetPosition(top);
        ResizeStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_start is not { } start || TopLevel.GetTopLevel(this) is not { } top) return;
        e.Handled = true;
        // Measured against the window, not the grip, which moves as the instrument grows.
        Resizing?.Invoke(this, e.GetPosition(top).X - start.X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Handled = true;
        if (_start is null) return;
        e.Pointer.Capture(null);
        Complete();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Complete();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Right is always wider; up or down is wider towards the corner the grip is in.
        var direction = e.Key switch
        {
            Key.Right => 1,
            Key.Left => -1,
            Key.Down => Corner == OverlayGripCorner.BottomRight ? 1 : -1,
            Key.Up => Corner == OverlayGripCorner.BottomRight ? -1 : 1,
            _ => 0
        };
        if (direction == 0) return;
        e.Handled = true;
        Stepped?.Invoke(this, direction * KeyboardStep);
    }

    private void Complete()
    {
        if (_start is null) return;
        _start = null;
        ResizeCompleted?.Invoke(this, EventArgs.Empty);
    }
}
