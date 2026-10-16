using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Controls.Docking;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Planner's dockable panels: the layout they start in, the states they move between, how the user
/// reaches each state without a pointer gesture, the Inspector's tabs, and what is written to the
/// settings file.
/// </summary>
public sealed partial class MainViewModelTests
{
    [Fact]
    public void TheDefaultLayoutIsTheInspectorBesideTheMapAndTheTimelineUnderneath()
    {
        var dock = new PlannerDockViewModel();
        // Two panels. The pin's place, the Picture-in-Picture map and the Terrain frame are on the map
        // itself, not in panels of their own.
        Assert.Equal(["inspector", "timeline"], dock.Panels.Select(panel => panel.PanelId));
        Assert.Null(dock["location"]);
        Assert.Null(dock["terrain"]);

        Assert.Equal((PanelDockSide.Right, PanelDockMode.Docked), (dock["inspector"]!.Side, dock["inspector"]!.Mode));
        Assert.Equal((PanelDockSide.Bottom, PanelDockMode.Docked), (dock["timeline"]!.Side, dock["timeline"]!.Mode));
        // Every panel starts pinned, so nothing waits behind an edge tab on a first run.
        Assert.All(dock.Panels, panel => Assert.True(panel.IsPinned, panel.PanelId));
        Assert.Equal("Inspector", dock.Inspector.Title);
        Assert.All(dock.Panels, panel => Assert.False(string.IsNullOrWhiteSpace(panel.Title), panel.PanelId));
    }

    /// <summary>Every panel the Planner declares is one the layout knows, and the other way round.</summary>
    [Fact]
    public void ThePanelsDeclaredInThePlannerAreExactlyThePanelsTheLayoutKnows()
    {
        var declared = TestMarkup.LoadMainWindow().Descendants()
            .Where(element => element.Name.LocalName == "PlannerPanelHost")
            .Select(element => element.Attribute("PanelId")?.Value).ToArray();
        Assert.Equal(PlannerDockViewModel.DefaultLayout.Select(panel => panel.PanelId), declared);
    }

    // ---- The Inspector ------------------------------------------------------------------------------

    /// <summary>
    /// The planning detail is one panel with one tab per subject, in the order and with the short names
    /// the strip shows, and each tab holds exactly what its region held. Terrain is not one of them: the
    /// Terrain panel owns all of it.
    /// </summary>
    [Fact]
    public void TheInspectorHoldsTargetsWeatherAndEquipmentAsTabsWithShortNames()
    {
        Assert.Equal(["targets", "weather", "equipment"], PlannerDockViewModel.InspectorTabs.Select(tab => tab.Id));
        Assert.Equal(["Targets", "Weather", "Equipment"], PlannerDockViewModel.InspectorTabs.Select(tab => tab.Title));

        var inspector = TestMarkup.LoadMainWindow().Descendants().Single(element =>
            element.Name.LocalName == "PlannerPanelHost" && element.Attribute("PanelId")?.Value == "inspector");
        var tabs = inspector.Descendants().Where(element => element.Name.LocalName == "TabItem").ToArray();
        Assert.Equal(["Targets", "Weather", "Equipment"], tabs.Select(tab => tab.Attribute("Header")?.Value));
        Assert.DoesNotContain("TerrainDatumText", inspector.ToString());
        Assert.DoesNotContain("GroundObstructionText", inspector.ToString());
        // One tab strip, and no panel inside another: the Inspector is one dockable panel.
        Assert.Single(inspector.Descendants(), element => element.Name.LocalName == "TabControl");
        Assert.DoesNotContain(inspector.Descendants(), element => element.Name.LocalName == "PlannerPanelHost");
        // Each tab carries its old region's content, and nothing else's.
        string Markup(int index) => tabs[index].ToString();
        Assert.Contains("CelestialObjects", Markup(0));
        Assert.Contains("Focused target details", Markup(0));
        Assert.Contains("WeatherHeadline", Markup(1));
        Assert.Contains("FocalLengthSlider", Markup(2));
        Assert.DoesNotContain("FocalLengthSlider", Markup(0) + Markup(1));
        // Each tab scrolls under a strip that stays in view.
        Assert.All(tabs, tab => Assert.Equal("ScrollViewer", tab.Elements().Single().Name.LocalName));
        Assert.Contains("selfScrolling", inspector.Attribute("Classes")?.Value);
    }

    [AvaloniaFact]
    public async Task TheInspectorOpensOnTargetsAndSwitchesTabsAtOnceWithoutLosingWhatIsInThem()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var tabs = window.FindControl<TabControl>("PlannerInspectorTabs")!;
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.Equal("targets", vm.PlannerDock.Inspector.SelectedTab);
            var host = Host(window, "inspector");
            // Only the open tab takes the room.
            Assert.DoesNotContain(host.GetVisualDescendants().OfType<Slider>(), slider => slider.Name == "FocalLengthSlider");

            // Switching is immediate: no load, no wait, the content is simply there.
            var slider = window.FindControl<Slider>("FocalLengthSlider")!;
            tabs.SelectedIndex = 2;
            Settle(window);
            Assert.Equal("equipment", vm.PlannerDock.Inspector.SelectedTab);
            Assert.Contains(slider, host.GetVisualDescendants().OfType<Slider>());
            var bearing = vm.CameraBearingDegrees = 123;

            // Away and back: the same controls, holding the same values.
            tabs.SelectedIndex = 1;
            Settle(window);
            Assert.Equal("weather", vm.PlannerDock.Inspector.SelectedTab);
            tabs.SelectedIndex = 2;
            Settle(window);
            Assert.Same(slider, window.FindControl<Slider>("FocalLengthSlider"));
            Assert.Equal(bearing, window.FindControl<CameraDirectionPad>("CameraDirection")!.Bearing, 3);

            // The view model drives the strip as well as following it.
            vm.PlannerDock.Inspector.SelectedTabIndex = 0;
            Settle(window);
            Assert.Equal(0, tabs.SelectedIndex);
            // A position the Inspector does not have, as a strip reports while it rebuilds, changes nothing.
            vm.PlannerDock.Inspector.SelectedTabIndex = -1;
            vm.PlannerDock.Inspector.SelectedTabIndex = 3;
            Assert.Equal("targets", vm.PlannerDock.Inspector.SelectedTab);
        }
        finally { Close(window); }
    }

    /// <summary>Tabs are a normal tab strip: tappable, keyboard-reachable, and plain about which is open.</summary>
    [AvaloniaFact]
    public async Task TheInspectorTabsAreTappableNamedAndShowWhichIsOpen()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var tabs = window.FindControl<TabControl>("PlannerInspectorTabs")!;
            Assert.Equal("Inspector sections", Avalonia.Automation.AutomationProperties.GetName(tabs));
            var items = tabs.Items.OfType<TabItem>().ToArray();
            Assert.Equal(3, items.Length);
            Assert.All(items, item =>
            {
                Assert.True(item.MinHeight >= 36, $"{item.Header} is {item.MinHeight} tall");
                Assert.True(item.Focusable, $"{item.Header} cannot take focus");
            });
            // The open tab is told apart by weight as well as colour.
            Assert.Equal(Avalonia.Media.FontWeight.SemiBold, items[0].FontWeight);
            Assert.NotEqual(Avalonia.Media.FontWeight.SemiBold, items[1].FontWeight);
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public async Task TheOpenInspectorTabIsSavedAndTheInspectorOpensOnItNextTime()
    {
        var store = CreatePlannerStore(new AppSettings());
        var (vm, window, _) = await CreatePlannerAsync(store: store);
        try
        {
            window.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex = 1;
            Settle(window);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal("weather", store.State.Settings.EffectivePlannerDock.EffectivePanels
                .Single(panel => panel.PanelId == "inspector").SelectedTab);
        }
        finally { Close(window); }

        // Next time: straight to the saved tab.
        var (reopened, again, _) = await CreatePlannerAsync(store: store);
        try
        {
            Assert.Equal("weather", reopened.PlannerDock.Inspector.SelectedTab);
            Assert.Equal(1, again.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex);
        }
        finally { Close(again); }
    }

    /// <summary>
    /// A tab id the Inspector does not have opens the first tab: "terrain", which an earlier build
    /// saved before Terrain moved to its own panel, as well as a hand edit or a newer build's id.
    /// </summary>
    [Theory]
    [InlineData("terrain")]
    [InlineData("horizon")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownSavedTabOpensTheFirstTab(string? saved)
    {
        var dock = new PlannerDockViewModel(new PlannerDockLayout([new("inspector", SelectedTab: saved)]));
        Assert.Equal("targets", dock.Inspector.SelectedTab);
        Assert.Equal(0, dock.Inspector.SelectedTabIndex);
        // A panel with one view has no tab at all.
        Assert.Null(dock["timeline"]!.SelectedTab);
        Assert.Equal(-1, dock["timeline"]!.SelectedTabIndex);
    }

    /// <summary>The Inspector is an ordinary panel: it pins, unpins, floats and docks like the others.</summary>
    [AvaloniaFact]
    public async Task TheInspectorPinsUnpinsFloatsAndDocksLikeAnyOtherPanel()
    {
        var (vm, window, workspace) = await CreatePlannerAsync();
        try
        {
            var inspector = vm.PlannerDock.Inspector;
            var host = Host(window, "inspector");
            var map = workspace.CentralBounds;
            var slider = window.FindControl<Slider>("FocalLengthSlider")!;

            // Unpinned: onto the right edge, and the map takes the room it gave back.
            inspector.UnpinCommand.Execute(null);
            Settle(window);
            Assert.True(inspector.IsAutoHidden);
            Assert.False(host.IsVisible);
            Assert.Contains(inspector, workspace.StripFor(PanelDockSide.Right).Items.OfType<PlannerPanelViewModel>());
            Assert.True(workspace.CentralBounds.Width > map.Width);

            // Opened from its tab: over the map, which does not move.
            var opened = workspace.CentralBounds;
            inspector.ToggleRevealCommand.Execute(null);
            Settle(window);
            Assert.True(host.IsVisible && host.Bounds.Width > 0);
            Assert.Equal(opened, workspace.CentralBounds);

            inspector.FloatCommand.Execute(null);
            Settle(window);
            Assert.True(inspector.IsFloating);
            Assert.True(host.ZIndex > Host(window, "timeline").ZIndex);
            Assert.InRange(inspector.FloatingX, 0, workspace.Bounds.Width - inspector.FloatingWidth);

            inspector.DockToCommand.Execute(PanelDockSide.Left);
            Settle(window);
            Assert.Equal((PanelDockSide.Left, true), (inspector.Side, inspector.IsPinned));

            inspector.DockToCommand.Execute(PanelDockSide.Right);
            Settle(window);
            Assert.Equal(map.Width, workspace.CentralBounds.Width, 3);
            // Through all of it, the same content: nothing in the Inspector was rebuilt by moving it.
            Assert.Same(slider, window.FindControl<Slider>("FocalLengthSlider"));
            Assert.Same(host, Host(window, "inspector"));
        }
        finally { Close(window); }
    }

    // ---- No closing ---------------------------------------------------------------------------------

    /// <summary>
    /// The Planner's panels are its permanent tools. There is no close control and no Hide: unpinning
    /// is how a panel is put out of the way, and an unpinned panel is still named and one tap away.
    /// </summary>
    [AvaloniaFact]
    public async Task NoPanelCanBeClosedOrHiddenOnlyPinnedUnpinnedOrFloated()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            foreach (var host in window.GetVisualDescendants().OfType<PlannerPanelHost>().Where(host => host.IsVisible))
            {
                var buttons = host.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Classes.Contains("panelChromeAction")).ToArray();
                // Pin, the unpin that replaces it, and the ••• menu. Nothing that closes.
                Assert.Equal(3, buttons.Length);
                Assert.DoesNotContain(buttons, button => button.Content is TextBlock { Text: "×" });
                Assert.All(buttons, button => Assert.DoesNotContain("Hide",
                    Avalonia.Automation.AutomationProperties.GetName(button) ?? "", StringComparison.OrdinalIgnoreCase));
            }
            // Every panel is somewhere, in every state it can be in.
            foreach (var panel in vm.PlannerDock.Panels)
                Assert.True(panel.IsPinned || panel.IsAutoHidden || panel.IsFloating, panel.PanelId);
            Assert.DoesNotContain(typeof(PlannerPanelViewModel).GetProperties(), property =>
                property.Name is "IsVisible" or "HideCommand" or "ShowCommand" or "ToggleVisibleCommand");
            Assert.DoesNotContain(typeof(PlannerPanelLayout).GetProperties(), property => property.Name == "IsVisible");
        }
        finally { Close(window); }
    }

    /// <summary>The panel menu offers every placement in words, and nothing that closes the panel.</summary>
    [Fact]
    public void ThePanelMenuNamesEveryPlacementAndNoWayToHideAPanel()
    {
        var markup = File.ReadAllText(Path.Combine(TestPaths.DesktopProject, "Controls", "Docking", "DockingStyles.axaml"));
        foreach (var key in new[]
                 {
                     "Planner_Panel_Pin_MenuItem", "Planner_Panel_Unpin_MenuItem", "Planner_Panel_Float_MenuItem",
                     "Planner_Panel_DockLeft_MenuItem", "Planner_Panel_DockRight_MenuItem",
                     "Planner_Panel_DockTop_MenuItem", "Planner_Panel_DockBottom_MenuItem"
                 })
            Assert.Contains("{loc:Text " + key + "}", markup);
        foreach (var side in Enum.GetValues<PanelDockSide>())
            Assert.Contains($"{{x:Static domain:PanelDockSide.{side}}}", markup);
        Assert.DoesNotContain("HideCommand", markup);
        Assert.DoesNotContain("Planner_Panel_Hide", markup);
    }

    /// <summary>
    /// There is no global Panels menu: nothing is left for it to do. Every panel is always somewhere on
    /// screen (docked, unpinned to a tab on its edge, or floating within the workspace), and pins, unpins,
    /// floats and docks itself from its own controls; Reset Planner layout is in Settings.
    /// </summary>
    [AvaloniaFact]
    public async Task EachPanelPlacesItselfAndTheLayoutResetsFromSettings()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var markup = TestMarkup.ReadMainWindow();
            foreach (var retired in new[] { "PlannerPanelsMenu", "PlannerPanelList", "PlannerPanelsReset", "panelListRow", "TogglePinCommand" })
                Assert.DoesNotContain(retired, markup);

            var timeline = vm.PlannerDock["timeline"]!;
            timeline.UnpinCommand.Execute(null);
            Assert.Equal(PanelDockMode.AutoHidden, timeline.Mode);
            timeline.PinCommand.Execute(null);
            Assert.True(timeline.IsPinned);
            var inspector = vm.PlannerDock["inspector"]!;
            inspector.FloatCommand.Execute(null);
            Assert.True(inspector.IsFloating);

            var reset = window.FindControl<Button>("ResetPlannerLayoutButton")!;
            Assert.Same(vm.PlannerDock.ResetLayoutCommand, reset.Command);
            reset.Command!.Execute(null);
            Assert.True(inspector.IsPinned);
            Assert.True(timeline.IsPinned);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// A layout saved by an earlier build names panels this version no longer has: the four the
    /// Inspector replaced, the planning-location panel the map overlay replaced, and the Terrain panel
    /// the Picture-in-Picture map and the Terrain frame overlay replaced. They are dropped,
    /// the Inspector arrives at its default, and the rest of the layout is kept as it was.
    /// </summary>
    [AvaloniaFact]
    public async Task ALayoutNamingRetiredPanelsKeepsEverythingStillTrueAndDropsTheRest()
    {
        var saved = new PlannerDockLayout(
        [
            new("terrain", Mode: PanelDockMode.Floating, FloatingX: 220, FloatingY: 140),
            new("targets", Mode: PanelDockMode.Floating, FloatingX: 600),
            new("camera", Mode: PanelDockMode.AutoHidden, Side: PanelDockSide.Left),
            new("location", Mode: PanelDockMode.Docked, Side: PanelDockSide.Right, DockedSize: 380, DockOrder: 1),
            new("weather", Mode: PanelDockMode.Docked, Side: PanelDockSide.Top),
            new("horizon", Mode: PanelDockMode.AutoHidden),
            new("timeline", Mode: PanelDockMode.Docked, Side: PanelDockSide.Bottom, DockedSize: 260)
        ], DockCornerOwnership.Default.WithSpan(PanelDockSide.Bottom, spans: true));
        var store = CreatePlannerStore(new AppSettings(PlannerDock: saved));
        var (vm, window, workspace) = await CreatePlannerAsync(store: store);
        try
        {
            Assert.Equal(["inspector", "timeline"], vm.PlannerDock.Panels.Select(panel => panel.PanelId));
            // No host, no tab and no edge strip is left behind for the retired location panel.
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<PlannerPanelHost>(), host => host.PanelId is "location" or "terrain");
            Assert.Equal(0, workspace.StripFor(PanelDockSide.Right).ItemCount);
            Assert.Null(vm.PlannerDock["terrain"]);
            Assert.Equal(260, vm.PlannerDock["timeline"]!.DockedSize);
            Assert.True(vm.PlannerDock.Corners.Spans(PanelDockSide.Bottom));
            var inspector = vm.PlannerDock.Inspector;
            Assert.Equal((PanelDockSide.Right, true, "targets"), (inspector.Side, inspector.IsPinned, inspector.SelectedTab));
            Assert.True(Host(window, "inspector").IsVisible);

            // The next save writes only panels that exist, and the retired ones are gone from the file.
            vm.PlannerDock["timeline"]!.SetDockedSize(270);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(["inspector", "timeline"],
                store.State.Settings.EffectivePlannerDock.EffectivePanels.Select(panel => panel.PanelId));
        }
        finally { Close(window); }
    }

    // ---- Layout safeguards --------------------------------------------------------------------------

    /// <summary>
    /// A layout arranged on a large display, opened on a small one. The panel is brought back inside the
    /// workspace, and it is the view that clamps it: the saved layout is not quietly rewritten.
    /// </summary>
    [AvaloniaFact]
    public async Task AFloatingPanelSavedBeyondTheWorkspaceIsBroughtBackInsideIt()
    {
        var saved = new PlannerDockLayout(
        [
            new("inspector", Mode: PanelDockMode.Floating, FloatingX: 3_000, FloatingY: 2_000,
                FloatingWidth: 900, FloatingHeight: 1_400)
        ]);
        var (vm, window, workspace) = await CreatePlannerAsync(new AppSettings(PlannerDock: saved));
        try
        {
            var host = Host(window, "inspector");
            Assert.True(vm.PlannerDock.Inspector.IsFloating);
            Assert.True(host.Bounds.Width > 0 && host.Bounds.Height > 0);
            Assert.True(host.Bounds.Right <= workspace.Bounds.Width + 1, $"{host.Bounds} is outside {workspace.Bounds}");
            Assert.True(host.Bounds.Bottom <= workspace.Bounds.Height + 1, $"{host.Bounds} is outside {workspace.Bounds}");
            // The title bar is the panel's handle; it has to be reachable or the panel cannot be moved.
            Assert.True(host.Bounds.X >= 0 && host.Bounds.Y >= 0);
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public async Task TheDocksAreNeverAllowedToTakeTheWholeMap()
    {
        var (vm, window, workspace) = await CreatePlannerAsync();
        try
        {
            foreach (var panel in vm.PlannerDock.Panels)
            {
                panel.PinCommand.Execute(null);
                panel.SetDockedSize(PlannerPanelLayout.MaximumDockedSize);
            }
            Settle(window);
            Assert.True(workspace.CentralBounds.Width >= DockWorkspace.MinimumCentralWidth,
                $"the map is {workspace.CentralBounds.Width} wide");
            Assert.True(workspace.CentralBounds.Height >= DockWorkspace.MinimumCentralHeight,
                $"the map is {workspace.CentralBounds.Height} tall");
            var map = window.FindControl<PlannerMapView>("PlannerMap")!;
            Assert.True(map.Bounds.Width > 0 && map.Bounds.Height > 0);

            // Dragging a dock wider stops at the same limit, rather than recording a width the layout
            // will not honour; and every panel docked on that side takes it, because a dock has one width.
            var right = vm.PlannerDock.Inspector;
            vm.PlannerDock["timeline"]!.DockToCommand.Execute(PanelDockSide.Right);
            Settle(window);
            workspace.ResizeDock(right, PlannerPanelLayout.MaximumDockedSize);
            Settle(window);
            Assert.True(right.DockedSize < PlannerPanelLayout.MaximumDockedSize, $"a dock reached {right.DockedSize}");
            Assert.Equal(right.DockedSize, vm.PlannerDock["timeline"]!.DockedSize, 3);
            // To the pixel: scaling two docks in proportion leaves the minimum intact.
            Assert.True(workspace.CentralBounds.Width >= DockWorkspace.MinimumCentralWidth - 1);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Pin, unpin and resize are controls on the panel, and placement is a list of named commands.
    /// Nothing here needs a pointer: a tap or the keyboard reaches every state, and none of it is
    /// revealed by hovering.
    /// </summary>
    [AvaloniaFact]
    public async Task EveryPanelStateIsReachableByTappingALabelledControl()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var host = Host(window, "inspector");
            var panel = vm.PlannerDock.Inspector;
            var buttons = host.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("panelChromeAction")).ToArray();
            Assert.All(buttons, button =>
            {
                Assert.True(button.MinWidth >= 32 && button.MinHeight >= 28, $"{button.MinWidth}x{button.MinHeight}");
                Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(button)));
            });
            // Exactly one of pin and unpin is offered, and it is the one that changes the current state.
            var offered = Assert.Single(buttons, button => button.IsEffectivelyVisible &&
                Avalonia.Automation.AutomationProperties.GetName(button)!.Contains("pin", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(panel.UnpinAccessibleName, Avalonia.Automation.AutomationProperties.GetName(offered));

            // Tapped, not hovered: the state changes from the control itself.
            offered.Command!.Execute(offered.CommandParameter);
            Settle(window);
            Assert.True(panel.IsAutoHidden);

            // And its edge tab carries the panel's own name, so an unpinned panel is never anonymous.
            var tab = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("panelEdgeTab") &&
                          Avalonia.Automation.AutomationProperties.GetName(button) == panel.RevealAccessibleName);
            Assert.True(tab.MinHeight >= 34);
            Assert.Contains(panel.Title, tab.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));

            // The grip is small to look at and deliberately not small to hit.
            Assert.True(PlannerPanelHost.GripThickness >= 14);
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public async Task MovingAPanelIsSavedWithTheSettingsAndResettingTheLayoutPutsEveryPanelBack()
    {
        var store = CreatePlannerStore(new AppSettings());
        var (vm, window, _) = await CreatePlannerAsync(store: store);
        try
        {
            Assert.Null(store.State.Settings.PlannerDock);

            var timeline = vm.PlannerDock["timeline"]!;
            timeline.DockToCommand.Execute(PanelDockSide.Right);
            timeline.SetDockedSize(300);
            vm.PlannerDock.Inspector.UnpinCommand.Execute(null);
            vm.PlannerDock.Inspector.SelectedTabIndex = 2;
            Settle(window);
            await vm.WaitForPendingSavesAsync();

            var saved = store.State.Settings.EffectivePlannerDock.EffectivePanels;
            Assert.Equal(PlannerDockViewModel.DefaultLayout.Count, saved.Count);
            var savedTimeline = saved.Single(panel => panel.PanelId == "timeline");
            Assert.Equal((PanelDockSide.Right, 300d), (savedTimeline.Side, savedTimeline.DockedSize));
            Assert.Equal(PanelDockMode.AutoHidden, saved.Single(panel => panel.PanelId == "inspector").Mode);
            Assert.Equal("equipment", saved.Single(panel => panel.PanelId == "inspector").SelectedTab);

            // Reset is one command the user can find, and it restores the layout exactly — the
            // Inspector's open tab included.
            vm.PlannerDock.ResetLayoutCommand.Execute(null);
            Settle(window);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(PlannerDockViewModel.DefaultLayout,
                store.State.Settings.EffectivePlannerDock.EffectivePanels.Select(panel =>
                    panel with { FloatingX = PlannerPanelLayout.DefaultFloatingOffset, FloatingY = PlannerPanelLayout.DefaultFloatingOffset }));
            Assert.Equal("targets", vm.PlannerDock.Inspector.SelectedTab);
            Assert.Equal(0, window.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex);
        }
        finally { Close(window); }
    }

    /// <summary>A saved layout is what the Planner opens with, panel by panel.</summary>
    [AvaloniaFact]
    public async Task ASavedLayoutIsRestoredWhenThePlannerOpens()
    {
        var saved = new PlannerDockLayout(
        [
            new("timeline", Mode: PanelDockMode.AutoHidden, Side: PanelDockSide.Left, DockedSize: 280)
        ]);
        var (vm, window, workspace) = await CreatePlannerAsync(new AppSettings(PlannerDock: saved));
        try
        {
            Assert.True(vm.PlannerDock["timeline"]!.IsAutoHidden);
            Assert.Equal(280, vm.PlannerDock["timeline"]!.DockedSize);
            Assert.Equal(1, workspace.StripFor(PanelDockSide.Left).ItemCount);
            // A panel the file never mentioned still arrives at its default.
            Assert.True(vm.PlannerDock.Inspector.IsPinned);
            Assert.Equal(342, vm.PlannerDock.Inspector.DockedSize);
            Assert.Equal(0, window.FindControl<TabControl>("PlannerInspectorTabs")!.SelectedIndex);
        }
        finally { Close(window); }
    }

    // ---- Harness ------------------------------------------------------------------------------------

    private static PlannerPanelHost Host(Window window, string panelId) => window.GetVisualDescendants()
        .OfType<PlannerPanelHost>().Single(host => host.PanelId == panelId);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Close(Window window)
    {
        window.Close();
        ((App)Application.Current!).Themes!.Apply(new());
    }

    private static FakeStore CreatePlannerStore(AppSettings settings) => new(new PersistedState(4, settings, [],
        PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 0), "UTC"), null));

    private async Task<(MainViewModel ViewModel, MainWindow Window, DockWorkspace Workspace)> CreatePlannerAsync(
        AppSettings? settings = null, FakeStore? store = null, FakeExporter? exporter = null)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            store ?? CreatePlannerStore(settings ?? new AppSettings()), exporter ?? new FakeExporter(),
            persistenceDelay: (_, _) => Task.CompletedTask);
        await vm.InitializeAsync();
        var search = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search)) { Width = 1280, Height = 820 };
        var map = window.FindControl<PlannerMapView>("PlannerMap")!;
        foreach (var (id, layer) in map.CompositionForTesting.NativeLayerOrder)
            if (id is "basemap" or Mapping.PlannerMapComposition.BasemapReferenceId) layer.Enabled = false; // Offline.
        TestMaps.OfflinePip(window);
        window.Show();
        ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 1;
        Settle(window);
        return (vm, window, window.GetVisualDescendants().OfType<DockWorkspace>().Single());
    }
}
