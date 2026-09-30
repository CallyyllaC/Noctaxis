using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Noctaxis.Desktop.Views;

public partial class LocationsPage : UserControl
{
    public LocationsPage() => InitializeComponent();

    /// <summary>
    /// A right-click anywhere on a saved-location card opens that card's own ••• menu, at the pointer.
    /// It is the same flyout instance the button opens, so a right-click is only ever a shortcut to
    /// what is already visible and no action exists behind the right button alone.
    /// </summary>
    private void CardContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control card) return;
        var actions = card.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Name == "CardActions" && button.Flyout is not null);
        if (actions?.Flyout is not MenuFlyout flyout) return;
        // At the pointer when a pointer asked, and anchored to the button when the keyboard did, for this
        // opening only: the ••• button keeps its own placement.
        var fromPointer = e.TryGetPosition(card, out _);
        var placement = flyout.Placement;
        flyout.Placement = fromPointer ? PlacementMode.Pointer : placement;
        void Restore(object? _, EventArgs __) { flyout.Placement = placement; flyout.Closed -= Restore; }
        flyout.Closed += Restore;
        flyout.ShowAt(fromPointer ? (Control)card : actions);
        e.Handled = true;
    }

    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 780;
        PageContent.Margin = isNarrow ? new Thickness(18) : new Thickness(32, 28, 32, 32);
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
