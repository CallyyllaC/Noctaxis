using Avalonia.Controls;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Views;

public partial class SavedLocationEditDialog : NoctaxisDialogWindow
{
    private SavedLocationEditorViewModel ViewModel => (SavedLocationEditorViewModel)DataContext!;
    public SavedLocationEditDialog() => InitializeComponent();
    public SavedLocationEditDialog(SavedLocation location, bool isCreateMode = false) : this()
    {
        DataContext = new SavedLocationEditorViewModel(location);
        PrimaryButton.Content = isCreateMode ? "Add location" : "Save changes";
    }
    public string PrimaryActionLabel => PrimaryButton.Content?.ToString() ?? string.Empty;
    private void SaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var result = ViewModel.ValidateAndCreateResult();
        if (result is not null) Close(result);
    }
    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);
    protected override void CancelDialog() => Close(null);
}
