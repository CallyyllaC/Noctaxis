using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls.Docking;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Which dock reaches into each corner, and the one reset that puts the whole layout back. Corner
/// ownership moves the docks around the map; it never moves them into it.
/// </summary>
public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task TheTimelineRunsTheFullWidthUntilTheUserSaysOtherwise()
    {
        var (vm, window, workspace) = await CreatePlannerAsync();
        try
        {
            Assert.Equal(DockCornerOwnership.Default, vm.PlannerDock.Corners);
            var right = Host(window, "inspector");
            var timeline = Host(window, "timeline");
            Assert.Equal(0, right.Bounds.Y, 3);
            // The timeline runs under the Inspector, the whole width, which is the arrangement the Planner ships with.
            Assert.Equal(0, timeline.Bounds.X, 3);
            Assert.Equal(workspace.Bounds.Width, timeline.Bounds.Right, 3);
            Assert.True(right.Bounds.Bottom <= timeline.Bounds.Y + 1, $"the Inspector ends at {right.Bounds.Bottom}");
            NothingOverlaps(window, workspace);
        }
        finally { Close(window); }
    }

    /// <summary>The bottom dock running under the side dock, and then stopping at it again.</summary>
    [AvaloniaFact]
    public async Task ADockRunsAsFarAsTheCornersItOwnsAndTheOthersGiveWay()
    {
        var (vm, window, workspace) = await CreatePlannerAsync();
        try
        {
            var right = Host(window, "inspector");
            var timeline = Host(window, "timeline");

            // Both bottom corners to the timeline: it runs the whole width and the side dock stops above it.
            vm.PlannerDock.SetSpan(PanelDockSide.Bottom, spans: true);
            Settle(window);
            Assert.Equal(0, timeline.Bounds.X, 3);
            Assert.Equal(workspace.Bounds.Width, timeline.Bounds.Right, 3);
            Assert.True(right.Bounds.Bottom <= timeline.Bounds.Y + 1, $"the right dock reaches {right.Bounds.Bottom}");
            Assert.Equal(0, right.Bounds.Y, 3);
            NothingOverlaps(window, workspace);

            // The right dock takes its own bottom corner back: it runs to the bottom, and the timeline stops at it.
            vm.PlannerDock.SetCornerOwner(DockCorner.BottomRight, PanelDockSide.Right);
            Settle(window);
            Assert.Equal(workspace.Bounds.Height, right.Bounds.Bottom, 3);
            Assert.Equal(0, timeline.Bounds.X, 3);
            Assert.True(timeline.Bounds.Right <= right.Bounds.X + 1, $"the timeline ends at {timeline.Bounds.Right}");
            NothingOverlaps(window, workspace);

            // And the top corners were never part of any of it.
            Assert.Equal(PanelDockSide.Left, vm.PlannerDock.Corners.Owner(DockCorner.TopLeft));
            Assert.Equal(PanelDockSide.Right, vm.PlannerDock.Corners.Owner(DockCorner.TopRight));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Every arrangement of the four corners, in the real layout: nothing overlaps, and the map keeps its
    /// room. Two panels fill two docks, so each arrangement is tried with either panel moved to the top.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(PanelDockSide.Left, PanelDockSide.Right, PanelDockSide.Left, PanelDockSide.Right, "inspector")]
    [InlineData(PanelDockSide.Left, PanelDockSide.Right, PanelDockSide.Left, PanelDockSide.Right, "timeline")]
    [InlineData(PanelDockSide.Top, PanelDockSide.Top, PanelDockSide.Bottom, PanelDockSide.Bottom, "inspector")]
    [InlineData(PanelDockSide.Top, PanelDockSide.Top, PanelDockSide.Bottom, PanelDockSide.Bottom, "timeline")]
    [InlineData(PanelDockSide.Left, PanelDockSide.Top, PanelDockSide.Bottom, PanelDockSide.Right, "inspector")]
    [InlineData(PanelDockSide.Top, PanelDockSide.Right, PanelDockSide.Left, PanelDockSide.Bottom, "timeline")]
    public async Task EveryCornerArrangementLeavesTheMapItsRoomAndThePanelsTheirOwn(
        PanelDockSide topLeft, PanelDockSide topRight, PanelDockSide bottomLeft, PanelDockSide bottomRight, string onTop)
    {
        var saved = new PlannerDockLayout(
            [new(onTop, Mode: PanelDockMode.Docked, Side: PanelDockSide.Top, DockedSize: 200)],
            new DockCornerOwnership(topLeft, topRight, bottomLeft, bottomRight));
        var (vm, window, workspace) = await CreatePlannerAsync(new Noctaxis.Core.Domain.AppSettings(PlannerDock: saved));
        try
        {
            Assert.Equal(new DockCornerOwnership(topLeft, topRight, bottomLeft, bottomRight), vm.PlannerDock.Corners);
            // The map's rectangle is decided by how much room the docks take, never by who owns a corner.
            Assert.True(workspace.CentralBounds.Width >= DockWorkspace.MinimumCentralWidth - 1,
                $"the map is {workspace.CentralBounds.Width} wide");
            Assert.True(workspace.CentralBounds.Height >= DockWorkspace.MinimumCentralHeight - 1,
                $"the map is {workspace.CentralBounds.Height} tall");
            NothingOverlaps(window, workspace);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Ownership is chosen by putting a panel in a corner, not by reading a setting: dropping into one
    /// hands it to the dock the panel landed in, and the hint shows that shape before the drop.
    /// </summary>
    [AvaloniaFact]
    public async Task DroppingAPanelIntoACornerGivesThatCornerToTheDockItLandsIn()
    {
        var (vm, window, workspace) = await CreatePlannerAsync();
        try
        {
            // From side docks that run the full height, so the drop is what hands the corner over.
            vm.PlannerDock.SetSpan(PanelDockSide.Bottom, spans: false);
            Settle(window);
            var timeline = vm.PlannerDock["timeline"]!;
            // The very bottom-left of the workspace, closer to the bottom edge than to the left one.
            var corner = new Point(DockWorkspace.EdgeDockDistance - 4, workspace.Bounds.Height - 2);
            var target = workspace.TargetAt(corner);
            Assert.Equal((PanelDockSide.Bottom, DockCorner.BottomLeft), (target!.Value.Side, target.Value.Corner));

            workspace.DropAt(timeline, corner);
            Settle(window);
            Assert.Equal(PanelDockSide.Bottom, vm.PlannerDock.Corners.Owner(DockCorner.BottomLeft));
            Assert.Equal(0, Host(window, "timeline").Bounds.X, 3);
            // Only the corner it was dropped in: the other three are where they were.
            Assert.Equal(PanelDockSide.Right, vm.PlannerDock.Corners.Owner(DockCorner.BottomRight));
            NothingOverlaps(window, workspace);

            // Dropped against the middle of an edge, the panel docks and no corner changes hands.
            var middle = new Point(workspace.Bounds.Width / 2, workspace.Bounds.Height - 2);
            Assert.Null(workspace.TargetAt(middle)!.Value.Corner);
            // ...and the same corner, dropped into the left dock, goes back to the left dock.
            var leftCorner = new Point(2, workspace.Bounds.Height - DockWorkspace.EdgeDockDistance + 4);
            workspace.DropAt(vm.PlannerDock.Inspector, leftCorner);
            Settle(window);
            Assert.Equal(PanelDockSide.Left, vm.PlannerDock.Corners.Owner(DockCorner.BottomLeft));
            NothingOverlaps(window, workspace);
        }
        finally { Close(window); }
    }

    /// <summary>The panel menu says what will happen, not what the setting is called.</summary>
    [AvaloniaFact]
    public async Task ThePanelMenuOffersToRunItsDockTheWholeWayInPlainWords()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var timeline = vm.PlannerDock["timeline"]!;
            var inspector = vm.PlannerDock.Inspector;
            // As shipped, the timeline runs the full width and the Inspector stops above it.
            Assert.Equal("Stop at the side panels", timeline.SpanActionLabel);
            Assert.Equal("Run the full height", inspector.SpanActionLabel);

            timeline.ToggleDockSpanCommand.Execute(null);
            Settle(window);
            Assert.False(vm.PlannerDock.Corners.Spans(PanelDockSide.Bottom));
            Assert.Equal("Run the full width", timeline.SpanActionLabel);
            // The side docks took those corners, so their own menus now offer to give them back.
            Assert.Equal("Stop at the top and bottom panels", inspector.SpanActionLabel);

            timeline.ToggleDockSpanCommand.Execute(null);
            Settle(window);
            Assert.Equal(DockCornerOwnership.Default, vm.PlannerDock.Corners);
        }
        finally { Close(window); }
    }

    // ---- Reset --------------------------------------------------------------------------------------

    /// <summary>
    /// Settings and the Panels menu are two ways into one reset, and it restores everything the docking
    /// framework persists — including which dock owns each corner.
    /// </summary>
    [AvaloniaFact]
    [CoversSettingsInput("PlannerDock.ResetLayoutCommand")]
    public async Task ResettingThePlannerLayoutFromSettingsRestoresEverythingItSaves()
    {
        var store = CreatePlannerStore(new Noctaxis.Core.Domain.AppSettings());
        var (vm, window, workspace) = await CreatePlannerAsync(store: store);
        try
        {
            // Move everything the framework saves away from its default.
            var inspector = vm.PlannerDock.Inspector;
            inspector.FloatCommand.Execute(null);
            inspector.SetFloatingBounds(210, 160, 320, 240);
            inspector.SelectedTabIndex = 2;
            vm.PlannerDock["timeline"]!.DockToCommand.Execute(PanelDockSide.Top);
            vm.PlannerDock["timeline"]!.SetDockedSize(360);
            vm.PlannerDock["timeline"]!.UnpinCommand.Execute(null);
            vm.PlannerDock.SetSpan(PanelDockSide.Bottom, spans: true);
            vm.PlannerDock.SetCornerOwner(DockCorner.TopRight, PanelDockSide.Top);
            Settle(window);
            await vm.WaitForPendingSavesAsync();
            Assert.NotEqual(DockCornerOwnership.Default, store.State.Settings.EffectivePlannerDock.EffectiveCorners);

            // The Settings action is bound to the same command the Panels menu uses.
            var reset = window.FindControl<Button>("ResetPlannerLayoutButton");
            Assert.NotNull(reset);
            Assert.Same(vm.PlannerDock.ResetLayoutCommand, reset.Command);
            reset.Command!.Execute(null);
            Settle(window);
            await vm.WaitForPendingSavesAsync();

            // Everything, from the one shipped definition: panels and corners alike.
            var saved = store.State.Settings.EffectivePlannerDock;
            Assert.Equal(PlannerDockViewModel.ShippedLayout.EffectivePanels, saved.EffectivePanels);
            Assert.Equal(PlannerDockViewModel.ShippedLayout.EffectiveCorners, saved.EffectiveCorners);
            Assert.Equal(DockCornerOwnership.Default, vm.PlannerDock.Corners);
            // ...and the live Planner is already showing it, with no restart.
            Assert.True(vm.PlannerDock["timeline"]!.IsPinned);
            Assert.True(vm.PlannerDock.Inspector.IsPinned);
            Assert.Equal("targets", vm.PlannerDock.Inspector.SelectedTab);
            Assert.Equal(PanelDockSide.Bottom, vm.PlannerDock["timeline"]!.Side);
            Assert.Equal(PanelDockSide.Right, vm.PlannerDock.Inspector.Side);
            Assert.Equal(0, Host(window, "inspector").Bounds.Y, 3);
            Assert.Equal(0, Host(window, "timeline").Bounds.X, 3);
            Assert.Equal(workspace.Bounds.Width, Host(window, "timeline").Bounds.Right, 3);
            Assert.True(Host(window, "inspector").Bounds.Bottom <= Host(window, "timeline").Bounds.Y + 1);
            NothingOverlaps(window, workspace);
        }
        finally { Close(window); }
    }

    /// <summary>The shipped layout is one definition, and the panels the Planner declares come from it.</summary>
    [Fact]
    public void TheShippedLayoutIsTheOneSourceOfTheDefaults()
    {
        Assert.Equal(PlannerDockViewModel.DefaultLayout, PlannerDockViewModel.ShippedLayout.EffectivePanels);
        Assert.Equal(DockCornerOwnership.Default, PlannerDockViewModel.ShippedLayout.EffectiveCorners);
        // A view model built from nothing and one reset to the defaults describe the same layout.
        var moved = new PlannerDockViewModel();
        moved["timeline"]!.UnpinCommand.Execute(null);
        moved.Inspector.SelectedTabIndex = 1;
        moved.SetSpan(PanelDockSide.Bottom, spans: true);
        moved.ResetLayoutCommand.Execute(null);
        var fresh = new PlannerDockViewModel();
        Assert.Equal(fresh.Capture().EffectivePanels, moved.Capture().EffectivePanels);
        Assert.Equal(fresh.Capture().EffectiveCorners, moved.Capture().EffectiveCorners);
        Assert.Equal(PlannerDockViewModel.ShippedLayout.EffectivePanels, moved.Capture().EffectivePanels);
    }

    /// <summary>No two docks may claim the same pixel, whatever the corners say.</summary>
    private static void NothingOverlaps(Window window, DockWorkspace workspace)
    {
        var docked = workspace.Hosts
            .Where(host => host.IsVisible && host.Panel is { IsDocked: true })
            .Select(host => (host.PanelId, Bounds: Placed(host, workspace)))
            .Concat(Enum.GetValues<PanelDockSide>()
                .Select(side => workspace.StripFor(side))
                .Where(strip => strip.IsVisible)
                .Select(strip => (PanelId: "tabs:" + strip.Side, Bounds: Placed(strip, workspace))))
            .Where(item => item.Bounds.Width > 0 && item.Bounds.Height > 0)
            .ToArray();
        for (var i = 0; i < docked.Length; i++)
            for (var j = i + 1; j < docked.Length; j++)
            {
                var overlap = docked[i].Bounds.Intersect(docked[j].Bounds);
                Assert.True(overlap.Width <= 1 || overlap.Height <= 1,
                    $"{docked[i].PanelId} {docked[i].Bounds} overlaps {docked[j].PanelId} {docked[j].Bounds}");
            }
        // The map is still a map, and it is still under everything the docks left it.
        Assert.True(workspace.CentralBounds.Width > 0 && workspace.CentralBounds.Height > 0);
        _ = window;
    }

    private static Rect Placed(Control control, Visual workspace) =>
        new(control.TranslatePoint(default, workspace) ?? default, control.Bounds.Size);
}
