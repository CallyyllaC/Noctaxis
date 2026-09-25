using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Controls;

public partial class PlannerLayersControl : UserControl
{
    public PlannerLayersControl()
    {
        InitializeComponent();
        LightPollutionToggle.Click += CommitEdit;
        // Register once, including handled thumb events; reattachment adds no handlers.
        OpacitySlider.AddHandler(PointerReleasedEvent, CommitEdit, RoutingStrategies.Bubble, true);
        OpacitySlider.AddHandler(KeyUpEvent, CommitEdit, RoutingStrategies.Bubble, true);
        OpacitySlider.LostFocus += CommitEdit;
        DetachedFromVisualTree += (_, _) => LayersButton.Flyout?.Hide();
    }

    private async void CommitEdit(object? sender, EventArgs e)
    {
        if (DataContext is PlannerLayersViewModel layers) await layers.CommitSafelyAsync();
    }
}
