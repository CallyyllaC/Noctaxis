using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Controls.Docking;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Planner layout Noctaxis ships, as arranged on a 1920 × 1080 display: the Inspector docked right, the
/// timeline docked under the map and running the full width, and the map's PiP and Camera frame at their
/// tuned shares of the map. New users get it, Reset Planner layout returns to it, and a smaller window keeps
/// it within its own limits.
/// </summary>
public sealed partial class MainViewModelTests
{
    /// <summary>The tuned arrangement, written out once so a change to any of it is a deliberate one.</summary>
    [Fact]
    public void TheShippedLayoutIsTheOneTunedAtFullHd()
    {
        var inspector = PlannerDockViewModel.ShippedLayout.EffectivePanels.Single(panel => panel.PanelId == "inspector");
        var timeline = PlannerDockViewModel.ShippedLayout.EffectivePanels.Single(panel => panel.PanelId == "timeline");
        Assert.Equal((PanelDockMode.Docked, PanelDockSide.Right, 342d, "targets"),
            (inspector.Mode, inspector.Side, inspector.DockedSize, inspector.SelectedTab));
        Assert.Equal((PanelDockMode.Docked, PanelDockSide.Bottom, 275d), (timeline.Mode, timeline.Side, timeline.DockedSize));
        Assert.Equal(new DockCornerOwnership(PanelDockSide.Left, PanelDockSide.Right, PanelDockSide.Bottom, PanelDockSide.Bottom),
            PlannerDockViewModel.ShippedLayout.EffectiveCorners);
        Assert.Equal(new PlannerOverlaySizes(.221, .222), new AppSettings().EffectivePlannerOverlays);
        Assert.Equal(73.2421875, new AppSettings().EffectivePlannerPip.EffectiveResolution);
        // Nothing personal: no centre for the PiP, and it follows the pin.
        Assert.Null(new AppSettings().EffectivePlannerPip.EffectiveCentre);
        Assert.True(new AppSettings().EffectivePlannerPip.FollowPin);
    }

    /// <summary>
    /// A new user's Planner, at full HD (the whole screen and a maximised window under a taskbar), at the
    /// window's own default size and at a narrower one: the panels never overlap, the timeline and the Inspector
    /// are there, the instruments stay on the map and clear of each other, and at full HD both are shown at
    /// their tuned sizes.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1920, 1080)]
    [InlineData(1920, 1009)]
    [InlineData(1440, 900)]
    [InlineData(1024, 700)]
    public async Task ANewUsersPlannerFitsAtEachSupportedSize(int width, int height)
    {
        var (vm, window, workspace) = await CreatePlannerAsync();
        try
        {
            window.Width = width; window.Height = height;
            Settle(window);
            NothingOverlaps(window, workspace);
            var timeline = Host(window, "timeline");
            var inspector = Host(window, "inspector");
            Assert.True(timeline.IsVisible && timeline.Bounds.Height >= PlannerPanelLayout.MinimumDockedSize - 1, $"timeline {timeline.Bounds}");
            Assert.True(inspector.IsVisible && inspector.Bounds.Width >= PlannerPanelLayout.MinimumDockedSize - 1, $"inspector {inspector.Bounds}");
            Assert.Equal(workspace.Bounds.Width, timeline.Bounds.Right, 1);
            Assert.True(inspector.Bounds.Bottom <= timeline.Bounds.Y + 1);
            Assert.True(MainMap(window).Bounds.Width > 0 && MainMap(window).Bounds.Height > 0);
            AssertInstrumentsFit(window);
            var layout = window.OverlayLayoutForTesting;
            if (width == 1920)
                Assert.True(window.FindControl<Border>("PlannerPip")!.IsVisible);
            if ((width, height) == (1920, 1080))
            {
                // The tuned arrangement: the Inspector and the timeline at their sizes, and both instruments shown.
                Assert.Equal(342, inspector.Bounds.Width, 1);
                Assert.Equal(275, timeline.Bounds.Height, 1);
                Assert.True(window.FindControl<Border>("PlannerPip")!.IsVisible);
                Assert.Equal(Math.Round(MainMap(window).Bounds.Width * .222), Math.Round(layout.CameraFrameWidth), 0);
            }
            AppearancePolishTests.Capture(window, $"shipped-layout-{width}x{height}");
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Reset Planner layout returns everything the shipped arrangement defines: the panels and corners, the
    /// instruments' sizes and the PiP's zoom. The PiP's centre, Follow and layers are the user's, and stay.
    /// </summary>
    [AvaloniaFact]
    public async Task ResetPlannerLayoutReturnsTheShippedInstrumentsAndPipZoom()
    {
        var store = CreatePlannerStore(new AppSettings(
            PlannerOverlays: new(.4, .1),
            PlannerPip: new(FollowPin: false, CentreLatitude: 52, CentreLongitude: -3, ResolutionMetresPerPixel: 900,
                ShowRoadsAndLabels: true)));
        var (vm, window, _) = await CreatePlannerAsync(store: store);
        try
        {
            window.Width = 1920; window.Height = 1080;
            Settle(window);
            Assert.Equal(900, ZoomOf(Pip(window)), 6);
            vm.PlannerDock.ResetLayoutCommand.Execute(null);
            Settle(window);
            await vm.WaitForPendingSavesAsync();

            Assert.Equal(new PlannerOverlaySizes(), vm.PlannerOverlaySizes);
            Assert.Equal(new PlannerOverlaySizes(), store.State.Settings.EffectivePlannerOverlays);
            Assert.Equal(PlannerPipSettings.DefaultResolutionMetresPerPixel, ZoomOf(Pip(window)), 6);
            Assert.Equal(PlannerPipSettings.DefaultResolutionMetresPerPixel, store.State.Settings.EffectivePlannerPip.EffectiveResolution);
            // Drawn at the shipped shares at once, with no restart.
            Assert.Equal(Math.Round(MainMap(window).Bounds.Width * PlannerOverlaySizes.DefaultCameraFrameWidthFraction),
                Math.Round(window.OverlayLayoutForTesting.CameraFrameWidth), 0);
            // The user's own: centre, Follow and layers.
            var pip = store.State.Settings.EffectivePlannerPip;
            AssertNear(new GeoCoordinate(52, -3), pip.EffectiveCentre!.Value, "the saved centre stays");
            Assert.False(pip.FollowPin);
            Assert.True(pip.ShowRoadsAndLabels);
            AssertNear(new GeoCoordinate(52, -3), CentreOf(Pip(window)), "the PiP keeps its own centre");
            AssertInstrumentsFit(window);
        }
        finally { Close(window); }
    }

    /// <summary>An existing user's own arrangement is kept: none of the shipped values replaces a saved one.</summary>
    [AvaloniaFact]
    public async Task AnExistingUsersLayoutIsKept()
    {
        var saved = new PlannerDockLayout(
            [new("inspector", Side: PanelDockSide.Left, DockedSize: 500), new("timeline", Side: PanelDockSide.Bottom, DockedSize: 200)],
            DockCornerOwnership.Default.WithSpan(PanelDockSide.Bottom, spans: false));
        var (vm, window, _) = await CreatePlannerAsync(new AppSettings(PlannerDock: saved,
            PlannerOverlays: new(.1, .3), PlannerPip: new(ResolutionMetresPerPixel: 500)));
        try
        {
            Assert.Equal((PanelDockSide.Left, 500d), (vm.PlannerDock.Inspector.Side, vm.PlannerDock.Inspector.DockedSize));
            Assert.Equal(200, vm.PlannerDock["timeline"]!.DockedSize);
            Assert.True(vm.PlannerDock.Corners.Spans(PanelDockSide.Left));
            Assert.Equal(new PlannerOverlaySizes(.1, .3), vm.PlannerOverlaySizes);
            Assert.Equal(500, ZoomOf(Pip(window)), 6);
        }
        finally { Close(window); }
    }
}
