using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The planning location in the Planner's bar: one line of the existing name, coordinates and altitude, which
/// copies exactly as it reads; which alone gives way when the bar is short, moving only within its own bounds
/// while the controls around it stay put; followed by Save location, Open in Maps and Export. The map no longer
/// carries a location card, and Layers is a tab on the map's right edge.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static MarqueeText LocationText(MainWindow window) => window.FindControl<MarqueeText>("PlannerLocationText")!;

    private static readonly string[] BarFixtures = ["PlannerTimeBar", "PlannerSaveLocation", "PlannerOpenInMaps", "PlannerExportMenu", "PlannerImportMenu"];

    private static Dictionary<string, Rect> BarPositions(MainWindow window) => BarFixtures.ToDictionary(name => name, name =>
    {
        var control = window.FindControl<Control>(name)!;
        return new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    });

    private static async Task<string?> ClipboardText(Window window) =>
        await TopLevel.GetTopLevel(window)!.Clipboard!.TryGetTextAsync();

    /// <summary>The bar reads left to right: time, location, Save location, Open in Maps, Export, on one line.</summary>
    [AvaloniaFact]
    public async Task TheBarIsTimeThenLocationThenItsActionsThenExportOnOneLine()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var at = BarPositions(window);
            var location = window.FindControl<Button>("PlannerLocationCopy")!;
            var locationAt = location.TranslatePoint(default, window)!.Value;
            Assert.True(at["PlannerTimeBar"].Right <= locationAt.X);
            Assert.True(locationAt.X + location.Bounds.Width <= at["PlannerSaveLocation"].X);
            Assert.True(at["PlannerSaveLocation"].Right <= at["PlannerOpenInMaps"].X);
            Assert.True(at["PlannerOpenInMaps"].Right <= at["PlannerExportMenu"].X);
            // One line: every part shares the bar's one row.
            Assert.All(at.Values, rect => Assert.InRange(rect.Center.Y, at["PlannerExportMenu"].Center.Y - 2, at["PlannerExportMenu"].Center.Y + 2));
            Assert.Same(vm.SaveLocationCommand, window.FindControl<Button>("PlannerSaveLocation")!.Command);
            Assert.Same(vm.OpenInMapsCommand, window.FindControl<Button>("PlannerOpenInMaps")!.Command);
            Assert.Equal("Save location", window.FindControl<Button>("PlannerSaveLocation")!.Content);
            Assert.Equal("Open in Maps", window.FindControl<Button>("PlannerOpenInMaps")!.Content);
            // Export carries its glyph, its label and its menu chevron.
            var export = window.FindControl<Button>("PlannerExportMenu")!;
            Assert.Contains("exportAction", export.Classes);
            Assert.Contains(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(export).OfType<Avalonia.Controls.Shapes.Path>(),
                path => ReferenceEquals(path.Data, Application.Current!.FindResource("ExportGlyph")));
            // Nothing of the location is left on the map: its left side is the PiP's and the Camera frame's.
            Assert.Null(window.FindControl<Control>("PlannerPinPreview"));
            Assert.Equal(["PlannerTerrainFrame"], window.FindControl<StackPanel>("PlannerMapBottomLeft")!.Children.Select(child => child.Name));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Clicking or tapping the location, or Enter or Space on it, copies exactly the line it shows and
    /// nothing beside it, and says so for a moment.
    /// </summary>
    [AvaloniaFact]
    public async Task TheLocationCopiesExactlyWhatItShows()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var location = window.FindControl<Button>("PlannerLocationCopy")!;
            Assert.Equal("Copy planning location", Avalonia.Automation.AutomationProperties.GetName(location));
            Assert.Equal(vm.PlannerLocationText, Avalonia.Automation.AutomationProperties.GetHelpText(location));
            var centre = location.TranslatePoint(new Point(location.Bounds.Width / 2, location.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            for (var i = 0; i < 50 && await ClipboardText(window) is null; i++) { Settle(window); await Task.Delay(10); }
            Assert.Equal(vm.PlannerLocationText, await ClipboardText(window));
            Assert.Equal(LocationText(window).Text, await ClipboardText(window));
            Assert.DoesNotContain("Save location", (await ClipboardText(window))!);
            Assert.DoesNotContain("http", (await ClipboardText(window))!);
            Assert.Equal("Location copied", vm.StatusMessage);
            Assert.True(window.FindControl<Control>("PlannerLocationCopied")!.IsVisible);

            // The keyboard: Enter, then Space, on the focused location.
            await TopLevel.GetTopLevel(window)!.Clipboard!.ClearAsync();
            Assert.True(location.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            for (var i = 0; i < 50 && await ClipboardText(window) is null; i++) { Settle(window); await Task.Delay(10); }
            Assert.Equal(vm.PlannerLocationText, await ClipboardText(window));
            await TopLevel.GetTopLevel(window)!.Clipboard!.ClearAsync();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            for (var i = 0; i < 50 && await ClipboardText(window) is null; i++) { Settle(window); await Task.Delay(10); }
            Assert.Equal(vm.PlannerLocationText, await ClipboardText(window));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// A location too long for its room moves back and forth inside its own bounds; the time controls, Save
    /// location, Open in Maps and Export never move. One that fits does not move at all.
    /// </summary>
    [AvaloniaFact]
    public async Task OnlyTheLocationTextMovesWhenItIsTooLong()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var text = LocationText(window);
            text.ReducedMotion = false;
            window.Width = 2000;
            Settle(window);
            Assert.Equal(0, text.Overflow);
            Assert.False(text.IsScrolling);

            vm.LocationName = "A very long place name indeed, on the far side of the long valley beyond the second ridge of the hills";
            window.Width = 1280;
            Settle(window);
            Settle(window);
            Assert.True(text.Overflow > 0);
            Assert.True(text.IsScrolling);
            var still = BarPositions(window);
            var bounds = new Rect(text.TranslatePoint(default, window)!.Value, text.Bounds.Size);
            Assert.True(bounds.Right <= still["PlannerSaveLocation"].X, "the location's room ends before Save location");
            Assert.True(text.ClipToBounds);

            // It rests first, then moves out, and never past its end.
            text.AdvanceForTesting(MarqueeText.Pause - TimeSpan.FromMilliseconds(100));
            Assert.Equal(0, text.OffsetForTesting);
            text.AdvanceForTesting(TimeSpan.FromSeconds(2));
            Settle(window);
            Assert.True(text.OffsetForTesting > 0);
            text.AdvanceForTesting(TimeSpan.FromMinutes(1));
            Assert.InRange(text.OffsetForTesting, 0, text.Overflow);
            Settle(window);
            Assert.Equal(still, BarPositions(window));
            // The whole text is the text of its one line, whatever part is in view.
            Assert.Equal(vm.PlannerLocationText, text.TextBlockForTesting.Text);

            // With reduced motion it stays still, ending in an ellipsis.
            text.ReducedMotion = true;
            Settle(window);
            Assert.False(text.IsScrolling);
            Assert.Equal(TextTrimming.CharacterEllipsis, text.TextBlockForTesting.TextTrimming);
            Assert.Equal(vm.PlannerLocationText, text.TextBlockForTesting.Text);
            Assert.Equal(still, BarPositions(window));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// On a narrow window the location gives up its room first, and nothing wraps: the time controls, Save
    /// location, Open in Maps, Export and Import all stay whole and on screen down to the smallest window.
    /// </summary>
    [AvaloniaFact]
    public async Task ANarrowWindowShrinksTheLocationFirstAndKeepsEverythingElse()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            foreach (var width in new[] { 1100d, 900, 720 })
            {
                window.Width = width;
                Settle(window);
                Settle(window);
                var at = BarPositions(window);
                Assert.All(at, pair => Assert.True(pair.Value.X >= 0 && pair.Value.Right <= window.Bounds.Width - 11.5,
                    $"{pair.Key} {pair.Value} is off a {width} px window"));
                Assert.All(at.Values, rect => Assert.InRange(rect.Center.Y, at["PlannerExportMenu"].Center.Y - 2, at["PlannerExportMenu"].Center.Y + 2));
            }
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Layers is a tab on the map's right edge. A tap opens the layers at once, over the map, beside the tab;
    /// it stays pressed while they are open; the map and the Inspector keep their size; closed, the tab is released.
    /// </summary>
    [AvaloniaFact]
    public async Task LayersIsATabOnTheMapsRightEdge()
    {
        var (_, window, workspace) = await CreatePlannerAsync();
        try
        {
            var map = MainMap(window);
            var tab = Assert.IsType<ToggleButton>(window.FindControl<PlannerLayersControl>("PlannerLayers")!.FindControl<Control>("LayersButton"));
            var tabAt = tab.TranslatePoint(default, map)!.Value;
            Assert.Equal(map.Bounds.Width, tabAt.X + tab.Bounds.Width, 0);
            Assert.False(tab.IsChecked);
            var (mapSize, workspaceSize) = (map.Bounds.Size, workspace.Bounds.Size);

            var centre = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);
            var flyout = Assert.IsType<Flyout>(tab.Flyout);
            Assert.True(flyout.IsOpen, "one tap should open the layers");
            Assert.True(tab.IsChecked);
            Assert.Equal(PlacementMode.LeftEdgeAlignedTop, flyout.Placement);
            Assert.Equal(mapSize, map.Bounds.Size);
            Assert.Equal(workspaceSize, workspace.Bounds.Size);
            // The same layer controls as before.
            Assert.NotNull(window.FindControl<PlannerLayersControl>("PlannerLayers")!.FindControl<CheckBox>("TerrainReliefToggle"));
            Assert.NotNull(window.FindControl<PlannerLayersControl>("PlannerLayers")!.FindControl<CheckBox>("LightPollutionToggle"));

            flyout.Hide();
            Settle(window);
            Assert.False(flyout.IsOpen);
            Assert.False(tab.IsChecked);
        }
        finally { Close(window); }
    }
}
