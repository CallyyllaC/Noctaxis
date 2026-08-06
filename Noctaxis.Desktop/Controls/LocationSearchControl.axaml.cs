using Avalonia.Controls;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Controls;

public partial class LocationSearchControl : UserControl
{
    public LocationSearchControl() => InitializeComponent();
    public event EventHandler<LocationSearchResult>? ResultSelected;
    private void ResultClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: LocationSearchResult result }) ResultSelected?.Invoke(this, result);
    }
}
