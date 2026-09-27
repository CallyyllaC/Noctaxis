using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace Noctaxis.Desktop.Views;

/// <summary>
/// Invariant presentation policy for every Noctaxis-owned secondary window.
/// Modal result types receive their default value when Escape or a platform close request closes
/// the window, so unfinished form state is never applied implicitly.
/// </summary>
public class NoctaxisDialogWindow : Window
{
    // The same application icon as the main window (Alt+Tab shows it even though dialogs skip the taskbar).
    private static readonly Lazy<WindowIcon> ApplicationIcon = new(() =>
        new WindowIcon(AssetLoader.Open(new Uri("avares://Noctaxis.Desktop/Assets/noctaxis.ico"))));

    public NoctaxisDialogWindow()
    {
        Icon = ApplicationIcon.Value;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Classes.Add("noctaxisDialog");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelDialog();
            return;
        }
        base.OnKeyDown(e);
    }

    protected virtual void CancelDialog() => Close();
}
