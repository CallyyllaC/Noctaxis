using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Picture-in-Picture map: a second view of the same map, fixed at the top-left of the main one, that
/// is looked at rather than worked. It switches with the main map, follows the pin's position, zooms with
/// the wheel over it, and takes no other input. Off, it is not there at all.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static PlannerMapView Pip(MainWindow window) => Assert.IsType<PlannerMapView>(window.PipForTesting);
    private static PlannerMapView MainMap(MainWindow window) => window.FindControl<PlannerMapView>("PlannerMap")!;

    private static GeoCoordinate CentreOf(NoctaxisMapView view) => view.CurrentView!.Value.Centre;
    private static double ZoomOf(NoctaxisMapView view) => view.CurrentView!.Value.Resolution;

    private static void AssertNear(GeoCoordinate expected, GeoCoordinate actual, string what)
    {
        Assert.True(Math.Abs(expected.Latitude - actual.Latitude) < 1e-6 && Math.Abs(expected.Longitude - actual.Longitude) < 1e-6,
            $"{what}: expected {expected}, was {actual}");
    }

    /// <summary>
    /// On by default and following the pin, at the main map's top-left: a map view in its constrained
    /// mode, with the pin and the simplified camera frame and none of the main map's heavier overlays.
    /// </summary>
    [AvaloniaFact]
    public async Task ThePictureInPictureMapIsOnByDefaultAsAConstrainedViewOfTheSameMap()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            Assert.True(vm.IsPlannerPipEnabled);
            Assert.True(vm.PlannerPipFollowPin);
            var pip = Pip(window);
            Assert.True(pip.IsPictureInPicture);
            Assert.True(pip.FollowObserver);
            Assert.False(pip.IsHitTestVisible);
            Assert.False(pip.AttributionStripForTesting.IsVisible);
            // The same registrations as the main map: the normal composition, not a terrain widget.
            Assert.Equal(MainMap(window).CompositionForTesting.Composition.Layers.Select(layer => layer.Id),
                pip.CompositionForTesting.Composition.Layers.Select(layer => layer.Id));
            // The same basemap source, so decoded tiles and downloads are shared.
            Assert.Same(((Mapping.Vector.OpenFreeMapBasemap)MainMap(window).CompositionForTesting.ActiveBasemap).Source,
                ((Mapping.Vector.OpenFreeMapBasemap)pip.CompositionForTesting.ActiveBasemap).Source);
            // The simplified camera frame: the framing outline when the camera overlay is ready, never the
            // environmental shading or the celestial rays.
            var runtime = pip.CompositionForTesting.Runtime;
            Assert.Equal(vm.CameraOverlayReady, runtime["camera-framing"].IsActive);
            Assert.False(runtime["environmental-shading"].IsActive);
            Assert.False(runtime["celestial-rays"].IsActive);
            // Fixed at the top-left of the main map, and not a panel.
            var frame = window.FindControl<Border>("PlannerPip")!;
            var at = frame.TranslatePoint(default, MainMap(window))!.Value;
            Assert.InRange(at.X, 0, 24);
            Assert.InRange(at.Y, 0, 24);
            Assert.InRange(frame.Bounds.Width, MapInstrumentLayout.MinimumPipSide, MainMap(window).Bounds.Width * MapInstrumentLayout.MaximumShare + 1);
            Assert.Equal(frame.Bounds.Width, frame.Bounds.Height, 3);
            Assert.DoesNotContain(frame.GetVisualAncestors(), ancestor => ancestor is Controls.Docking.PlannerPanelHost);
            // Its default layers are the old minimap's: terrain relief, strongly, no light pollution.
            Assert.Equal(new TerrainReliefPreferences(true, 1), pip.TerrainReliefPreferences);
            Assert.False(pip.LightPollutionPreferences.IsVisible);
        }
        finally { Close(window); }
    }

    /// <summary>Turned off, the map is released, not hidden: nothing of it is left to draw or update.</summary>
    [AvaloniaFact]
    [CoversSettingsInput("SettingsPlannerPipEnabled")]
    public async Task TurningThePictureInPictureMapOffReleasesIt()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var first = Pip(window);
            vm.SettingsPlannerPipEnabled = false;
            Settle(window);
            Assert.Null(window.PipForTesting);
            Assert.False(window.FindControl<Border>("PlannerPip")!.IsVisible);
            Assert.Null(window.FindControl<ContentControl>("PlannerPipHost")!.Content);
            Assert.False(first.IsAttachedToVisualTree());
            Assert.Null(MainMap(window).AttributionCompanion);
            await vm.SettleSettingsAsync();

            vm.SettingsPlannerPipEnabled = true;
            Settle(window);
            Assert.NotSame(first, Pip(window));
            Assert.True(Pip(window).IsAttachedToVisualTree());
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Following the pin moves only the centre: the zoom and the layers stay the Picture-in-Picture map's
    /// own. Not following, the centre stays where it was however the pin moves.
    /// </summary>
    [AvaloniaFact]
    public async Task FollowPinMovesOnlyTheCentre()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var pip = Pip(window);
            var zoom = ZoomOf(pip);
            var layers = MapLayerPreferences.Capture(pip.CompositionForTesting.Runtime);
            var moved = new GeoCoordinate(vm.Observer.Latitude + .05, vm.Observer.Longitude - .05);
            vm.CommitObserverLocation(moved);
            Settle(window);
            AssertNear(vm.Observer, CentreOf(pip), "following, the centre is the pin");
            Assert.Equal(zoom, ZoomOf(pip), 6);
            Assert.Equal(layers, MapLayerPreferences.Capture(pip.CompositionForTesting.Runtime));

            // Off: it keeps its centre as the pin moves on, and that centre is what is saved.
            var button = window.FindControl<ToggleButton>("PlannerPipFollowButton")!;
            Assert.True(button.IsChecked);
            button.IsChecked = false;
            Settle(window);
            Assert.False(vm.PlannerPipFollowPin);
            var kept = CentreOf(pip);
            vm.CommitObserverLocation(new GeoCoordinate(moved.Latitude + .1, moved.Longitude + .1));
            Settle(window);
            AssertNear(kept, CentreOf(pip), "not following, the centre stays");
            await vm.WaitForPendingSavesAsync();
            AssertNear(kept, vm.PlannerPip.EffectiveCentre!.Value, "the kept centre is saved");
            Assert.False(vm.PlannerPip.FollowPin);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The wheel over the Picture-in-Picture map zooms it about its own centre and nothing else: the main
    /// map does not move, and the new zoom is saved. It cannot be dragged, and pressing on it never moves
    /// the pin.
    /// </summary>
    [AvaloniaFact]
    public async Task TheWheelZoomsThePictureInPictureMapAndNothingElseReachesIt()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var pip = Pip(window);
            var main = MainMap(window);
            var frame = window.FindControl<Border>("PlannerPip")!;
            var over = frame.TranslatePoint(new Point(frame.Bounds.Width / 3, frame.Bounds.Height / 3), window)!.Value;
            var (pipCentre, pipZoom) = (CentreOf(pip), ZoomOf(pip));
            var (mainCentre, mainZoom) = (CentreOf(main), ZoomOf(main));
            var observer = vm.Observer;

            window.MouseWheel(over, new Vector(0, 1));
            Settle(window);
            Assert.Equal(pipZoom / 2, ZoomOf(pip), 6);
            AssertNear(pipCentre, CentreOf(pip), "zooming keeps the centre");
            Assert.Equal(mainZoom, ZoomOf(main), 6);
            AssertNear(mainCentre, CentreOf(main), "the main map is not moved");
            window.MouseWheel(over, new Vector(0, -2));
            Settle(window);
            Assert.Equal(pipZoom * 2, ZoomOf(pip), 6);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(pipZoom * 2, vm.PlannerPip.EffectiveResolution, 6);

            // Within the map's zoom range, however long the wheel turns: at the limit it stops.
            for (var i = 0; i < 40; i++) window.MouseWheel(over, new Vector(0, -1));
            Settle(window);
            var widest = ZoomOf(pip);
            Assert.True(widest <= NoctaxisMapView.MaximumResolution);
            Assert.False(pip.Zoom(-1));
            Assert.Equal(widest, ZoomOf(pip), 6);

            // No panning and no pin: a drag and a click over it change nothing.
            var before = (CentreOf(pip), CentreOf(main));
            window.MouseDown(over, MouseButton.Left);
            window.MouseMove(over + new Vector(40, 30), RawInputModifiers.LeftMouseButton);
            window.MouseUp(over + new Vector(40, 30), MouseButton.Left);
            window.MouseDown(over, MouseButton.Left);
            window.MouseUp(over, MouseButton.Left);
            Settle(window);
            AssertNear(before.Item1, CentreOf(pip), "the Picture-in-Picture map does not pan");
            AssertNear(before.Item2, CentreOf(main), "the main map does not pan from it");
            Assert.Equal(observer, vm.Observer);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Switch exchanges the two views — centre, zoom and layers — and nothing the Planner shares: the pin,
    /// the time, the focused target and the camera stay exactly as they were.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchExchangesTheViewsAndNothingElse()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var pip = Pip(window);
            var main = MainMap(window);
            vm.PlannerPipFollowPin = false;
            pip.ShowView(new(new GeoCoordinate(vm.Observer.Latitude + .2, vm.Observer.Longitude + .3), 300));
            Settle(window);
            var (mainView, pipView) = (main.CurrentView!.Value, pip.CurrentView!.Value);
            var (mainTerrain, pipTerrain) = (vm.TerrainReliefMapPreferences, vm.PlannerPipTerrainRelief);
            var shared = (vm.Observer, vm.Session.Instant, vm.Session.TargetId, vm.CameraBearingDegrees, vm.CameraPitchDegrees);

            var button = window.FindControl<Button>("PlannerPipSwitchButton")!;
            Assert.Equal("Switch maps", Avalonia.Automation.AutomationProperties.GetName(button));
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(window);

            AssertNear(pipView.Centre, CentreOf(main), "the main map takes the Picture-in-Picture centre");
            Assert.Equal(pipView.Resolution, ZoomOf(main), 6);
            AssertNear(mainView.Centre, CentreOf(pip), "the Picture-in-Picture map takes the main centre");
            Assert.Equal(mainView.Resolution, ZoomOf(pip), 6);
            Assert.Equal(pipTerrain, vm.TerrainReliefMapPreferences);
            Assert.Equal(mainTerrain, vm.PlannerPipTerrainRelief);
            Assert.Equal(mainTerrain, pip.TerrainReliefPreferences);
            Assert.Equal(shared, (vm.Observer, vm.Session.Instant, vm.Session.TargetId, vm.CameraBearingDegrees, vm.CameraPitchDegrees));

            // And back.
            window.SwitchMaps();
            Settle(window);
            Assert.Equal(mainView.Resolution, ZoomOf(main), 6);
            Assert.Equal(pipView.Resolution, ZoomOf(pip), 6);
            Assert.Equal(mainTerrain, vm.TerrainReliefMapPreferences);
            Assert.Equal(pipTerrain, vm.PlannerPipTerrainRelief);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Its two controls are full touch targets with names and help text a screen reader gives, reached by
    /// the keyboard; Follow pin shows its state by staying pressed, not only on hover.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchAndFollowPinAreNamedTouchTargetsTheKeyboardReaches()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var follow = window.FindControl<ToggleButton>("PlannerPipFollowButton")!;
            var swap = window.FindControl<Button>("PlannerPipSwitchButton")!;
            foreach (var control in new Button[] { swap, follow })
            {
                Assert.True(control.Bounds.Width >= 32 && control.Bounds.Height >= 32, $"{control.Name} is {control.Bounds.Size}");
                Assert.True(control.Focusable && control.IsEffectivelyEnabled);
                Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetHelpText(control)));
            }
            Assert.Equal("Follow pin", Avalonia.Automation.AutomationProperties.GetName(follow));
            Assert.Contains("centred on the planning pin", Avalonia.Automation.AutomationProperties.GetHelpText(follow));
            Assert.True(follow.IsChecked);
            Assert.True(follow.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Settle(window);
            Assert.False(follow.IsChecked);
            Assert.False(vm.PlannerPipFollowPin);
        }
        finally { Close(window); }
    }

    /// <summary>The main map's credits include what only the Picture-in-Picture map is drawing.</summary>
    [AvaloniaFact]
    public async Task TheMainMapCreditsWhatThePictureInPictureMapDraws()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var main = MainMap(window);
            var pip = Pip(window);
            main.CompositionForTesting.Runtime.SetVisibility(TerrainReliefMapBinding.LayerId, false);
            pip.CompositionForTesting.Runtime.SetAvailability(TerrainReliefMapBinding.LayerId, MapLayerAvailability.Available);
            Settle(window);
            Assert.DoesNotContain(MapAttributions.TilezenTerrarium, main.CompositionForTesting.Runtime.ActiveAttributions);
            Assert.Contains(MapAttributions.TilezenTerrarium.Label, main.AttributionForTesting);
            // Each credit once, though both maps owe the basemap's.
            Assert.Single(main.StripAttributions, credit => credit.Id == "openstreetmap");
        }
        finally { Close(window); }
    }
    /// <summary>
    /// Roads and names are the Picture-in-Picture map's own setting, off by default: off, its basemap's
    /// reference pass is gated out of its composition while its geography, terrain relief, light pollution
    /// and camera frame stay as they were. The main map keeps its roads and names whatever the setting,
    /// and Switch does not carry the setting across.
    /// </summary>
    [AvaloniaFact]
    [CoversSettingsInput("SettingsPlannerPipShowRoadsAndLabels")]
    public async Task RoadsAndLabelsAreThePictureInPictureMapsOwnAndOffByDefault()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var pip = Pip(window).CompositionForTesting.Runtime;
            var main = MainMap(window).CompositionForTesting.Runtime;
            const string reference = PlannerMapComposition.BasemapReferenceId;
            var toggle = window.FindControl<CheckBox>("PlannerPipRoadsAndLabelsToggle")!;
            Assert.False(vm.SettingsPlannerPipShowRoadsAndLabels);
            Assert.False(toggle.IsChecked);
            Assert.True(toggle.IsEffectivelyEnabled);
            Assert.False(pip[reference].IsActive);
            Assert.True(main[reference].IsActive);
            // Only roads and names: the rest of its view is untouched.
            Assert.True(pip["basemap"].IsRenderable);
            Assert.Equal(vm.PlannerPipTerrainRelief.IsVisible, pip[TerrainReliefMapBinding.LayerId].IsVisible);
            Assert.Equal(vm.PlannerPipTerrainRelief.Opacity, pip[TerrainReliefMapBinding.LayerId].Opacity);
            Assert.Equal(vm.PlannerPipLightPollution.IsVisible, pip[LightPollutionMapBinding.LayerId].IsVisible);
            Assert.Equal(vm.CameraOverlayReady, pip["camera-framing"].IsActive);

            // On: restored in the Picture-in-Picture map, saved with its settings, and the main map unchanged.
            // (Turned straight back off, before the map draws, so the test never reaches the tile server.)
            toggle.IsChecked = true;
            Assert.True(vm.PlannerPipShowRoadsAndLabels);
            Assert.True(vm.Settings.EffectivePlannerPip.ShowRoadsAndLabels);
            Assert.True(pip[reference].IsActive);
            Assert.True(main[reference].IsActive);
            toggle.IsChecked = false;
            Assert.False(pip[reference].IsActive);
            Assert.True(main[reference].IsActive);

            // Switch exchanges the views, not this setting.
            window.SwitchMaps();
            Settle(window);
            Assert.False(pip[reference].IsActive);
            Assert.True(main[reference].IsActive);
            Assert.False(vm.PlannerPipShowRoadsAndLabels);
            await vm.WaitForPendingSavesAsync();
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Following the pin and Switch only move views and set layer state: no layer is rebuilt, no basemap
    /// tile is restyled, and both maps keep drawing terrain relief from the one shared relief source.
    /// </summary>
    [AvaloniaFact]
    public async Task FollowPinAndSwitchRebuildNothing()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var (pip, main) = (Pip(window), MainMap(window));
            object[] Layers(NoctaxisMapView view) => view.CompositionForTesting.NativeLayerOrder.Select(x => (object)x.Layer).ToArray();
            int Restyles(NoctaxisMapView view) => view.CompositionForTesting.Map.Layers
                .OfType<Mapping.Vector.ThemedBasemapTileLayer>().Sum(layer => layer.Invalidations);
            var before = (Layers(pip), Layers(main), Restyles(pip), Restyles(main));
            Assert.Same(main.TerrainReliefForTesting?.Source, pip.TerrainReliefForTesting?.Source);
            Assert.Same(main.LightPollutionForTesting?.Provider, pip.LightPollutionForTesting?.Provider);

            vm.CommitObserverLocation(new GeoCoordinate(vm.Observer.Latitude + .03, vm.Observer.Longitude + .03));
            Settle(window);
            window.SwitchMaps();
            Settle(window);
            window.SwitchMaps();
            Settle(window);

            Assert.Equal(before.Item1, Layers(pip));
            Assert.Equal(before.Item2, Layers(main));
            Assert.Equal((before.Item3, before.Item4), (Restyles(pip), Restyles(main)));
            Assert.Same(Pip(window), pip);
            await vm.WaitForPendingSavesAsync();
        }
        finally { Close(window); }
    }

    /// <summary>Saved as off, the Picture-in-Picture map is never created: there is nothing of it to draw or update.</summary>
    [AvaloniaFact]
    public async Task APictureInPictureMapSavedAsOffIsNeverCreated()
    {
        var (_, window, _) = await CreatePlannerAsync(new AppSettings(PlannerPip: new(IsEnabled: false)));
        try
        {
            Assert.Null(window.PipForTesting);
            Assert.False(window.FindControl<Border>("PlannerPip")!.IsVisible);
            Assert.Null(window.FindControl<ContentControl>("PlannerPipHost")!.Content);
            Assert.Null(MainMap(window).AttributionCompanion);
            Assert.False(window.FindControl<CheckBox>("PlannerPipRoadsAndLabelsToggle")!.IsEffectivelyEnabled);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<NoctaxisMapView>(), view => view.IsPictureInPicture);
        }
        finally { Close(window); }
    }
}
