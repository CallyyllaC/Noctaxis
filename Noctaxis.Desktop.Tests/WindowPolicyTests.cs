using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed class WindowPolicyTests
{
    [Fact]
    public void NoctaxisSecondaryWindows_UseDialogWindowPolicy()
    {
        var violations = typeof(MainWindow).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(Window).IsAssignableFrom(type))
            .Where(type => type != typeof(MainWindow))
            .Where(type => !typeof(NoctaxisDialogWindow).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .OrderBy(name => name)
            .ToArray();

        Assert.True(violations.Length == 0,
            "Noctaxis-owned secondary Window types must inherit NoctaxisDialogWindow: " +
            string.Join(", ", violations));
    }

    [AvaloniaFact]
    public void NoctaxisDialogWindow_UsesBorderlessTopmostPolicy()
    {
        var dialog = new NoctaxisDialogWindow();

        Assert.Equal(WindowDecorations.None, dialog.WindowDecorations);
        Assert.False(dialog.ShowInTaskbar);
        Assert.True(dialog.Topmost);
        Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
        Assert.False(dialog.CanResize);
    }

    [AvaloniaFact]
    public void LocationEditor_UsesNeutralTitleAndModeSpecificPrimaryAction()
    {
        var location = new SavedLocation(Guid.NewGuid(), "Ridge", new GeoCoordinate(51, -2), "UTC");

        var create = new SavedLocationEditDialog(location, isCreateMode: true);
        var edit = new SavedLocationEditDialog(location);

        Assert.Equal("Edit location", create.Title);
        Assert.Equal("Edit location", edit.Title);
        Assert.Equal("Add location", create.PrimaryActionLabel);
        Assert.Equal("Save changes", edit.PrimaryActionLabel);
    }
}
