namespace Noctaxis.Desktop.Views;

public partial class ConfirmationDialog : NoctaxisDialogWindow
{
    public ConfirmationDialog() => InitializeComponent();

    public ConfirmationDialog(string title, string message, string confirmLabel) : this()
    {
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
    private void ConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);
    protected override void CancelDialog() => Close(false);
}
