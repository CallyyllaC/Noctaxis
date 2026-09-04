using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed class WindowPolicyTests
{
    [Fact]
    public void TerrainDebugOverlay_IsOptionalBoundAndCopyable()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Noctaxis.Desktop", "Views", "MainWindow.axaml"));
        var markup = File.ReadAllText(sourcePath);
        Assert.Contains("Content=\"Terrain debug overlay\"", markup);
        Assert.Contains("IsChecked=\"{Binding SettingsTerrainDebugOverlay}\"", markup);
        Assert.Contains("ShowTerrainDebug=\"{Binding ShowTerrainDebugOverlay}\"", markup);
        Assert.Contains("Copy terrain debug snapshot", markup);
    }
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
    public void MainWindow_CloseButton_UsesClientInputInsteadOfNativeCaptionHitTesting()
    {
        var window = new MainWindow();
        var closeButton = window.FindControl<Button>("CloseWindowButton");

        Assert.NotNull(closeButton);
        Assert.Equal(
            WindowDecorationsElementRole.User,
            WindowDecorationProperties.GetElementRole(closeButton));
    }

    [AvaloniaFact]
    public void PlannerRefreshProgress_IsANonBlockingOverlayInsideTheMapArea()
    {
        var window = new MainWindow();
        var map = window.FindControl<Control>("PlannerMap");
        var strip = window.FindControl<Border>("PlannerRefreshStrip");
        var progress = window.FindControl<ProgressBar>("PlannerRefreshProgressBar");

        Assert.NotNull(map);
        Assert.NotNull(strip);
        Assert.NotNull(progress);
        Assert.False(strip.IsHitTestVisible);
        Assert.Equal(0, progress.Minimum);
        Assert.Equal(1, progress.Maximum);
    }

    [AvaloniaFact]
    public void PlannerGroundElevation_IsReadOnlyAndHasNoSpinner()
    {
        var window = new MainWindow();
        var elevation = window.FindControl<NumericUpDown>("PlannerGroundElevation");

        Assert.NotNull(elevation);
        Assert.True(elevation.IsReadOnly);
        Assert.False(elevation.ShowButtonSpinner);
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
