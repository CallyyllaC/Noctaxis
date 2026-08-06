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
    public LocationSearchDialog(LocationSearchViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.Reset();
    }
    private void SelectResult(object? sender, LocationSearchResult result) => Close(result);
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
    protected override void CancelDialog() => Close(null);
}
