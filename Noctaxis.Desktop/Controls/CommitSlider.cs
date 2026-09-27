using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// A slider for settings that are costly to apply: <see cref="RangeBase.Value"/> follows the user while
/// they drag, and <see cref="CommittedValue"/> (the bound setting) changes once the interaction finishes.
/// A pointer drag (mouse, touch or pen, on the thumb or the track) commits on release or capture loss;
/// keyboard steps, automation (screen readers) and other discrete changes commit at once.
/// </summary>
public sealed class CommitSlider : Slider
{
    public static readonly StyledProperty<double> CommittedValueProperty =
        AvaloniaProperty.Register<CommitSlider, double>(nameof(CommittedValue), defaultBindingMode: BindingMode.TwoWay);

    private bool _pointerInteraction;
    private bool _synchronising;

    protected override Type StyleKeyOverride => typeof(Slider);

    public CommitSlider()
    {
        // Tunnel and handled-too: the slider's own thumb and track handling marks these handled.
        AddHandler(PointerPressedEvent, (_, _) => _pointerInteraction = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => EndPointerInteraction(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndPointerInteraction(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public double CommittedValue
    {
        get => GetValue(CommittedValueProperty);
        set => SetValue(CommittedValueProperty, value);
    }

    /// <summary>True while a pointer interaction is in progress and the value is not yet committed.</summary>
    internal bool IsInteracting => _pointerInteraction;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_synchronising) return;
        if (change.Property == CommittedValueProperty)
        {
            _synchronising = true;
            try { SetCurrentValue(ValueProperty, CommittedValue); }
            finally { _synchronising = false; }
        }
        else if (change.Property == ValueProperty && !_pointerInteraction) Commit();
    }

    private void EndPointerInteraction()
    {
        if (!_pointerInteraction) return;
        _pointerInteraction = false;
        Commit();
    }

    private void Commit()
    {
        if (CommittedValue.Equals(Value)) return;
        _synchronising = true;
        try { SetCurrentValue(CommittedValueProperty, Value); }
        finally { _synchronising = false; }
    }
}
