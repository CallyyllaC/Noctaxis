using Avalonia.Controls;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Views;

public partial class LocationSearchDialog : NoctaxisDialogWindow
{
    public LocationSearchDialog()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as LocationSearchViewModel)?.Reset();
    }
    public LocationSearchDialog(LocationSearchViewModel viewModel, string? initialQuery = null) : this()
    {
        DataContext = viewModel;
        viewModel.Reset();
        // Carried over from the shared search field, so the search is already running when this opens.
        if (!string.IsNullOrWhiteSpace(initialQuery)) viewModel.Query = initialQuery;
    }
    private void SelectResult(object? sender, LocationSearchResult result) => Close(result);
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
    protected override void CancelDialog() => Close(null);
}
