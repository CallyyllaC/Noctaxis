using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Views;

public partial class LocationsPage : UserControl
{
    public LocationsPage() => InitializeComponent();

    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 780;
        PageContent.Margin = new Thickness(isNarrow ? 18 : 32);
        ConfigureResponsiveHeader(CurrentLocationLayout, CurrentLocationActions, isNarrow);
        ConfigureResponsiveHeader(SavedLocationsHeader, SavedLocationsSort, isNarrow);
    }

    private static void ConfigureResponsiveHeader(Grid grid, Control trailingContent, bool isNarrow)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        if (isNarrow)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(trailingContent, 0);
            Grid.SetRow(trailingContent, 1);
            trailingContent.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            trailingContent.Margin = new Thickness(0, 14, 0, 0);
            return;
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(trailingContent, 1);
        Grid.SetRow(trailingContent, 0);
        trailingContent.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        trailingContent.Margin = default;
    }
}
