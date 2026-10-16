using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Controls;

public partial class PlannerLayersControl : UserControl
{
    /// <summary>How far one notch of the mouse wheel moves an opacity slider, in percentage points.</summary>
    public const double OpacityPerWheelNotch = 5;
    private double _wheelRemainder;
    private bool _wheelEditPending;

    public PlannerLayersControl()
    {
        InitializeComponent();
        LightPollutionToggle.Click += CommitEdit;
        // Register once, including handled thumb events; reattachment adds no handlers.
        OpacitySlider.AddHandler(PointerReleasedEvent, CommitEdit, RoutingStrategies.Bubble, true);
        OpacitySlider.AddHandler(KeyUpEvent, CommitEdit, RoutingStrategies.Bubble, true);
        OpacitySlider.LostFocus += CommitEdit;
        // A wheel turn has no release to commit on, so it commits when the pointer leaves the slider
        // (the flyout closing commits as well).
        OpacitySlider.PointerExited += (sender, args) =>
        {
            if (!_wheelEditPending) return;
            _wheelEditPending = false;
            CommitEdit(sender, args);
        };
        foreach (var slider in new[] { TerrainReliefOpacitySlider, OpacitySlider })
            slider.PointerWheelChanged += WheelAdjustsOpacity;
        DetachedFromVisualTree += (_, _) => LayersButton.Flyout?.Hide();
        // How long the flyout takes to appear, from the press to its first drawn frame.
        LayersButton.AddHandler(PointerPressedEvent, (_, _) => _pressedAt = System.Diagnostics.Stopwatch.GetTimestamp(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        LayersButton.Flyout!.Opened += (_, _) => MeasureOpening();
        // The tab stays pressed exactly while the layers are open, however they were opened or closed.
        LayersButton.Flyout.Opened += (_, _) => LayersButton.IsChecked = true;
        LayersButton.Flyout.Closed += (_, _) => LayersButton.IsChecked = false;
    }

    /// <summary>An opening slower than this is written to the application log, with where the time went.</summary>
    public static readonly TimeSpan SlowOpening = TimeSpan.FromMilliseconds(150);
    private long _pressedAt;

    /// <summary>The last opening's time from the press to the flyout opening, and to its first frame.</summary>
    internal (TimeSpan Opened, TimeSpan FirstFrame)? LastOpening { get; private set; }
    internal event EventHandler? OpeningMeasured;

    /// <summary>
    /// Opening the flyout recalculates nothing: no layer, attribution, map or saved state is touched, and
    /// the same content is shown each time. What remains is Avalonia showing a popup and drawing its
    /// first frame, so that is what is measured, and only a slow opening is logged.
    /// </summary>
    private void MeasureOpening()
    {
        var pressed = _pressedAt;
        _pressedAt = 0;
        if (pressed == 0) return;
        var opened = System.Diagnostics.Stopwatch.GetElapsedTime(pressed);
        if (TopLevel.GetTopLevel(LayerRows) is not { } popup) return;
        popup.RequestAnimationFrame(_ =>
        {
            var firstFrame = System.Diagnostics.Stopwatch.GetElapsedTime(pressed);
            LastOpening = (opened, firstFrame);
            OpeningMeasured?.Invoke(this, EventArgs.Empty);
            if (firstFrame >= SlowOpening && Program.FileLogging?.CreateLogger(typeof(PlannerLayersControl).FullName!) is { } log)
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log,
                    "Layers flyout took {FirstFrameMs:F0} ms from press to first frame ({OpenedMs:F0} ms to open)",
                    firstFrame.TotalMilliseconds, opened.TotalMilliseconds);
        });
    }

    /// <summary>
    /// The wheel over an opacity slider, and only there, moves it: away from the user raises it, a notch
    /// at a time, with trackpad deltas gathered into whole notches, within the slider's own range. A
    /// slider whose layer is off is left alone, and at either end the wheel is not taken, so it still
    /// scrolls the flyout. Dragging, tapping and the keyboard work as before.
    /// </summary>
    private void WheelAdjustsOpacity(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Slider { IsEffectivelyEnabled: true } slider || e.Delta.Y == 0) return;
        var raising = e.Delta.Y > 0;
        if (raising ? slider.Value >= slider.Maximum : slider.Value <= slider.Minimum)
        {
            _wheelRemainder = 0;
            return;
        }
        e.Handled = true;
        _wheelRemainder += e.Delta.Y;
        var notches = (int)Math.Truncate(_wheelRemainder);
        if (notches == 0) return;
        _wheelRemainder -= notches;
        slider.Value = Math.Clamp(Math.Round(slider.Value) + notches * OpacityPerWheelNotch, slider.Minimum, slider.Maximum);
        if (ReferenceEquals(slider, OpacitySlider)) _wheelEditPending = true;
    }

    private async void CommitEdit(object? sender, EventArgs e)
    {
        if (DataContext is PlannerLayersViewModel layers) await layers.CommitSafelyAsync();
    }
}
