using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// One line of text that never wraps and never pushes its neighbours. When it fits it simply sits there. When
/// it does not, it moves slowly to its end and back inside its own bounds, pausing at each end, so all of it
/// can be read; everything outside its bounds stays still. With reduced motion it does not move: it ends in an
/// ellipsis instead. The whole text is always the text of its one <see cref="TextBlock"/>, for assistive
/// technology, whatever part is in view.
/// </summary>
public sealed class MarqueeText : Control
{
    /// <summary>How fast the text moves while it scrolls, and how long it rests at each end.</summary>
    public const double PixelsPerSecond = 28;
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1.6);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarqueeText, string?>(nameof(Text));
    /// <summary>Whether movement is reduced: by default, the operating system's own setting.</summary>
    public static readonly StyledProperty<bool> ReducedMotionProperty =
        AvaloniaProperty.Register<MarqueeText, bool>(nameof(ReducedMotion), MotionPreference.IsReduced);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool ReducedMotion { get => GetValue(ReducedMotionProperty); set => SetValue(ReducedMotionProperty, value); }

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.NoWrap };
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = new();
    private double _overflow, _offset;
    private bool _attached;

    public MarqueeText()
    {
        ClipToBounds = true;
        LogicalChildren.Add(_text);
        VisualChildren.Add(_text);
        _timer.Tick += (_, _) => Advance();
        AttachedToVisualTree += (_, _) => { _attached = true; UpdateMotion(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; UpdateMotion(); };
    }

    /// <summary>The text's one line, which assistive technology reads in full.</summary>
    internal TextBlock TextBlockForTesting => _text;
    /// <summary>How far the text is wider than the room it has; zero when it fits.</summary>
    public double Overflow => _overflow;
    /// <summary>Whether the text is moving now.</summary>
    public bool IsScrolling => _timer.IsEnabled;
    internal double OffsetForTesting => _offset;
    /// <summary>Moves the scroll on by <paramref name="elapsed"/> as if that much time had passed.</summary>
    internal void AdvanceForTesting(TimeSpan elapsed) => Step(elapsed.TotalSeconds);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _text.Text = Text;
            Restart();
            InvalidateMeasure();
        }
        else if (change.Property == ReducedMotionProperty)
        {
            Restart();
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _text.TextTrimming = TextTrimming.None;
        _text.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var natural = _text.DesiredSize;
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(natural.Width, availableSize.Width) : natural.Width, natural.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var natural = _text.DesiredSize.Width;
        var overflow = Math.Max(0, natural - finalSize.Width);
        if (Math.Abs(overflow - _overflow) > .5) { _overflow = overflow; Restart(); }
        _overflow = overflow;
        if (overflow > 0 && ReducedMotion)
        {
            // Still: as much as fits, ending in an ellipsis.
            _text.TextTrimming = TextTrimming.CharacterEllipsis;
            _text.Measure(finalSize);
            _text.Arrange(new Rect(finalSize));
        }
        else
        {
            _text.Arrange(new Rect(-Math.Min(_offset, overflow), 0, Math.Max(natural, finalSize.Width), finalSize.Height));
        }
        UpdateMotion();
        return finalSize;
    }

    // Phase of the back-and-forth: rest at the start, travel out, rest at the end, travel back.
    private enum Phase { RestStart, Out, RestEnd, Back }
    private Phase _phase;
    private double _rested;

    private void Restart()
    {
        _offset = 0;
        _phase = Phase.RestStart;
        _rested = 0;
        _clock.Restart();
        InvalidateArrange();
    }

    private void UpdateMotion()
    {
        var move = _attached && _overflow > 0 && !ReducedMotion && IsEffectivelyVisible;
        if (move == _timer.IsEnabled) return;
        if (move) { _clock.Restart(); _timer.Start(); }
        else { _timer.Stop(); _offset = 0; _phase = Phase.RestStart; _rested = 0; }
    }

    private void Advance()
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        Step(Math.Min(seconds, .25));
    }

    private void Step(double seconds)
    {
        if (_overflow <= 0) return;
        while (seconds > 0)
        {
            switch (_phase)
            {
                case Phase.RestStart or Phase.RestEnd:
                    var rest = Math.Min(seconds, Pause.TotalSeconds - _rested);
                    _rested += rest;
                    seconds -= rest;
                    if (_rested >= Pause.TotalSeconds - 1e-9) { _rested = 0; _phase = _phase == Phase.RestStart ? Phase.Out : Phase.Back; }
                    break;
                case Phase.Out:
                    var outward = Math.Min(seconds * PixelsPerSecond, _overflow - _offset);
                    _offset += outward;
                    seconds -= outward / PixelsPerSecond;
                    if (_offset >= _overflow - 1e-9) _phase = Phase.RestEnd;
                    break;
                case Phase.Back:
                    var back = Math.Min(seconds * PixelsPerSecond, _offset);
                    _offset -= back;
                    seconds -= back / PixelsPerSecond;
                    if (_offset <= 1e-9) _phase = Phase.RestStart;
                    break;
            }
        }
        InvalidateArrange();
    }
}

/// <summary>Whether the user has asked the operating system for less motion.</summary>
public static class MotionPreference
{
    /// <summary>On Windows, "Show animations in Windows" turned off; elsewhere, not known, so false.</summary>
    public static bool IsReduced => OperatingSystem.IsWindows() && NativeMethods.ClientAreaAnimationOff();

    private static class NativeMethods
    {
        private const uint GetClientAreaAnimation = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);

        public static bool ClientAreaAnimationOff()
        {
            try { return SystemParametersInfo(GetClientAreaAnimation, 0, out var on, 0) && on == 0; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }
}
