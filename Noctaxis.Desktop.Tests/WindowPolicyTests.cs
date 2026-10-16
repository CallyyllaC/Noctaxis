using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed class WindowPolicyTests
{
    [Fact]
    public void TerrainDebugOverlay_IsOptionalBoundAndCopyable()
    {
        var markup = TestMarkup.ReadMainWindow();
        Assert.DoesNotContain("Terrain cast angular detail", markup);
        Assert.DoesNotContain("SettingsTerrainCastAngularDetailDegrees", markup);
        Assert.Contains("Content=\"Enable terrain diagnostics\"", markup);
        Assert.Contains("IsChecked=\"{Binding SettingsTerrainDebugOverlay}\"", markup);
        Assert.Contains("Profile=\"{Binding EnabledTerrainDiagnosticsProfile}\"", markup);
        Assert.Contains("Copy terrain debug snapshot", markup);
        var document = System.Xml.Linq.XDocument.Parse(markup);
        var tabs = document.Descendants().Where(element => element.Name.LocalName == "TabItem").ToArray();
        var debug = Assert.Single(tabs, element => (string?)element.Attribute("Header") == "Diagnostics");
        var planner = Assert.Single(tabs, element => (string?)element.Attribute("Header") == "Planner");
        var appearance = Assert.Single(tabs, element => (string?)element.Attribute("Header") == "Planner map");
        var data = Assert.Single(tabs, element => (string?)element.Attribute("Header") == "Data");
        Assert.Single(debug.Descendants(), element => element.Name.LocalName == "TerrainDebugMiniMap");
        Assert.DoesNotContain(planner.Descendants(), element => element.Name.LocalName == "TerrainDebugMiniMap");
        Assert.Single(debug.Descendants(), element => (string?)element.Attribute("IsChecked") == "{Binding SettingsTerrainDebugOverlay}");
        Assert.Single(appearance.Descendants(), element => (string?)element.Attribute("IsChecked") == "{Binding SettingsEnableTerrainCalculations}");
        Assert.Single(data.Descendants(), element => (string?)element.Attribute("Command") == "{Binding ClearTerrainCacheCommand}");
        // The retired terrain minimap is gone. In its place the Picture-in-Picture map and the Terrain frame
        // are laid over the main map; neither is a panel.
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "LocalTerrainMap");
        foreach (var name in new[] { "PlannerPip", "PlannerTerrainFrame" })
        {
            var overlay = Assert.Single(planner.Descendants(), element => element.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
            Assert.DoesNotContain(overlay.Ancestors(), element => element.Name.LocalName == "PlannerPanelHost");
        }
        Assert.DoesNotContain(planner.Descendants(), element => (string?)element.Attribute("Text") == "Local terrain");
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

    /// <summary>
    /// The Planner shows where the pin is and does not ask the user to type it: the shell search and the
    /// map move the pin, and there is no name, coordinate or elevation field to edit anywhere in it.
    /// </summary>
    [AvaloniaFact]
    public void ThePlannerShowsThePinsLocationAndHasNoFieldsToTypeItInto()
    {
        var window = new MainWindow();
        Assert.Null(window.FindControl<NumericUpDown>("PlannerGroundElevation"));
        var planner = TestMarkup.LoadMainWindow().Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Planner");
        foreach (var binding in new[] { "{Binding LocationName}", "{Binding Latitude}", "{Binding Longitude}", "{Binding Elevation}" })
            Assert.DoesNotContain(planner.Descendants(), element =>
                element.Name.LocalName is "TextBox" or "NumericUpDown" &&
                element.Attributes().Any(attribute => attribute.Value == binding));
        Assert.DoesNotContain(planner.Descendants(), element => element.Name.LocalName == "LocationSearchControl");

        // The pin's place, coordinates and altitude are one line of text in the Planner's bar, not fields.
        Assert.IsType<Noctaxis.Desktop.Controls.MarqueeText>(window.FindControl<Control>("PlannerLocationText"));
        foreach (var name in new[] { "PlannerLocationName", "PlannerLocationCoordinates", "PlannerLocationAltitude", "PlannerPinPreview" })
            Assert.Null(window.FindControl<Control>(name));
    }

    /// <summary>
    /// The dialog says which of the two things it is doing, in all three places at once. An Add flow
    /// titled "Edit location" tells the user they are about to change something that exists.
    /// </summary>
    [AvaloniaFact]
    public void LocationEditor_NamesItsModeInTheTitleHeadingAndPrimaryAction()
    {
        var location = new SavedLocation(Guid.NewGuid(), "Ridge", new GeoCoordinate(51, -2), "UTC");

        var create = new SavedLocationEditDialog(location, isCreateMode: true);
        var edit = new SavedLocationEditDialog(location);
        string Heading(SavedLocationEditDialog dialog) => dialog.FindControl<TextBlock>("HeadingText")!.Text!;

        Assert.Equal(("Add location", "Add location", "Add location"), (create.Title, Heading(create), create.PrimaryActionLabel));
        Assert.Equal(("Edit location", "Edit location", "Save changes"), (edit.Title, Heading(edit), edit.PrimaryActionLabel));

        // The coordinates come from the Planner pin: shown, selectable, never an editable-looking box.
        foreach (var dialog in new[] { create, edit })
        {
            var coordinates = dialog.FindControl<SelectableTextBlock>("CoordinatesText")!;
            Assert.Equal("51.00000°, -2.00000°", coordinates.Text);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBox>(), box => box.IsReadOnly);
        }
    }
}
