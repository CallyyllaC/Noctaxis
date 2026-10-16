using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Noctaxis.Core.Localisation;

namespace Noctaxis.Desktop.Tests;

// NX-31: a launch that cannot own Noctaxis explains itself in a small window of its own, in the user's language.
public sealed class InstanceNoticeTests
{
    [AvaloniaTheory]
    [InlineData("Shell_Instance_AlreadyRunning")]
    [InlineData("Shell_Instance_RestartIncomplete")]
    public void TheNoticeSaysWhyAndCloses(string key)
    {
        var message = Strings.Get(key);
        Assert.DoesNotContain("⟦", message); // The missing-key marker.
        var window = InstanceNoticeApp.CreateWindow(message);
        window.Show();
        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains(message, texts);
        var close = window.GetLogicalDescendants().OfType<Button>().Single();
        Assert.Equal(Strings.Get("Shell_Instance_Notice_Close"), close.Content);
        close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.IsVisible);
    }

    [Fact]
    public void TheDataFolderNoticeNamesTheFolder()
    {
        // In the application, Program.Main loads the Desktop module (and its resources) before anything else.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(InstanceNoticeApp).Module.ModuleHandle);
        var message = Strings.Format("Shell_Instance_DataFolderInUse_Format", @"C:\Users\someone\AppData\Roaming\Noctaxis");
        Assert.Contains(@"C:\Users\someone\AppData\Roaming\Noctaxis", message);
        Assert.Contains("Nothing was changed", message);
        Assert.DoesNotContain("⟦", message);
    }
}
