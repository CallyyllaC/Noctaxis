using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Controls;

/// <summary>Lets enlarged card text use the artwork area without sharing mutable grid definitions.</summary>
public sealed class LocationCardTextGrid : Grid
{
    public static readonly StyledProperty<bool> FullWidthProperty =
        AvaloniaProperty.Register<LocationCardTextGrid, bool>(nameof(FullWidth));
    public bool FullWidth { get => GetValue(FullWidthProperty); set => SetValue(FullWidthProperty, value); }
    public LocationCardTextGrid() => ColumnDefinitions = new("*,*");
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FullWidthProperty) ColumnDefinitions = new(FullWidth ? "*,0" : "*,*");
    }
}
