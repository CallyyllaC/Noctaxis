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
        Assert.DoesNotContain("Terrain cast angular detail", markup);
        Assert.DoesNotContain("SettingsTerrainCastAngularDetailDegrees", markup);
        Assert.Contains("Content=\"Enable terrain diagnostics\"", markup);
        Assert.Contains("IsChecked=\"{Binding SettingsTerrainDebugOverlay}\"", markup);
        Assert.Contains("Profile=\"{Binding EnabledTerrainDiagnosticsProfile}\"", markup);
        Assert.Contains("Copy terrain debug snapshot", markup);
        var document = System.Xml.Linq.XDocument.Parse(markup);
        var tabs = document.Descendants().Where(element => element.Name.LocalName == "TabItem").ToArray();
        var debug = Assert.Single(tabs, element => (string?)element.Attribute("Header") == "Debug");
        var planner = Assert.Single(tabs, element => (string?)element.Attribute("Header") == "Planner");
        var appearance = Assert.Single(tabs, element => (string?)element.Attribute("Header") == "Appearance");
        var data = Assert.Single(tabs, element => (string?)element.Attribute("Header") == "Data");
        Assert.Single(debug.Descendants(), element => element.Name.LocalName == "TerrainDebugMiniMap");
        Assert.DoesNotContain(planner.Descendants(), element => element.Name.LocalName == "TerrainDebugMiniMap");
        Assert.Single(debug.Descendants(), element => (string?)element.Attribute("IsChecked") == "{Binding SettingsTerrainDebugOverlay}");
        Assert.Single(appearance.Descendants(), element => (string?)element.Attribute("IsChecked") == "{Binding SettingsEnableTerrainCalculations}");
        Assert.Single(data.Descendants(), element => (string?)element.Attribute("Command") == "{Binding ClearTerrainCacheCommand}");
        var minimap = Assert.Single(planner.Descendants(), element => element.Name.LocalName == "LocalTerrainMap");
        Assert.Equal("208", (string?)minimap.Attribute("Width"));
        Assert.Equal("False", (string?)minimap.Parent!.Parent!.Attribute("IsHitTestVisible"));
        Assert.Equal("Top", (string?)minimap.Parent.Parent.Attribute("VerticalAlignment"));
        Assert.Equal("Left", (string?)minimap.Parent.Parent.Attribute("HorizontalAlignment"));
        Assert.Null(minimap.Parent.Parent.Attribute("IsVisible"));
        Assert.Single(minimap.Parent.Elements(), element => (string?)element.Attribute("Text") == "Terrain");
        Assert.DoesNotContain(planner.Descendants(), element => (string?)element.Attribute("Text") == "Local terrain");
        Assert.Equal("{Binding GroundMetresPerPixel, ElementName=PlannerMap}", (string?)minimap.Attribute("MetresPerPixel"));
        Assert.Single(document.Descendants(), element => element.Name.LocalName == "LocalTerrainMap");
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
