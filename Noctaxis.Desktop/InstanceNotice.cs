using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Noctaxis.Core.Localisation;

namespace Noctaxis.Desktop;

/// <summary>
/// The whole application for a launch that must not start: one small window that says why, and Close. It loads no
/// state, starts no services and writes nothing, so it can run beside the instance that owns everything.
/// </summary>
internal sealed class InstanceNoticeApp(string message) : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = CreateWindow(message);
        base.OnFrameworkInitializationCompleted();
    }

    internal static Window CreateWindow(string message)
    {
        var window = new Window
        {
            Title = Strings.Get("Shell_Instance_Notice_Title"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var close = new Button { Content = Strings.Get("Shell_Instance_Notice_Close"), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, close }
        };
        AutomationAware(window, message);
        return window;
    }

    private static void AutomationAware(Window window, string message) =>
        Avalonia.Automation.AutomationProperties.SetHelpText(window, message);

    internal static int Show(string message, string[] args) =>
        AppBuilder.Configure(() => new InstanceNoticeApp(message)).UsePlatformDetect().WithInterFont().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
}
