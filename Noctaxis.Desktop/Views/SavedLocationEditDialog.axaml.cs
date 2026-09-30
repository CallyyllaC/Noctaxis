using Noctaxis.Core.Localisation;
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
        // Title, heading and confirm button all say the same thing: adding, or editing.
        Title = Strings.Get(isCreateMode ? "Locations_Edit_AddLocation_Title" : "Locations_Edit_EditLocation_Title");
        HeadingText.Text = Strings.Get(isCreateMode ? "Locations_Edit_AddLocation_Label" : "Locations_Edit_EditLocation_Label");
        PrimaryButton.Content = Strings.Get(isCreateMode ? "Locations_Edit_AddLocation_Button" : "Locations_Edit_SaveChanges_Button");
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
