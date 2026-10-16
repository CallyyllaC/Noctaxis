using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Picture-in-Picture map's zoom is kept between runs. The app builds its window, and the map with it,
/// before the saved settings load, so the map takes its saved view once they have: its zoom, and its centre
/// unless it follows the pin. Following the pin and Switch keep the zoom what it is.
/// </summary>
public sealed partial class MainViewModelTests
{
    /// <summary>Starts as the app does: the window first, then the saved state.</summary>
    private async Task<(MainViewModel ViewModel, MainWindow Window)> StartLikeTheAppAsync(FakeStore store)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(),
            persistenceDelay: (_, _) => Task.CompletedTask);
        var search = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search)) { Width = 1280, Height = 820 };
        foreach (var (id, layer) in MainMap(window).CompositionForTesting.NativeLayerOrder)
            if (id is "basemap" or Mapping.PlannerMapComposition.BasemapReferenceId) layer.Enabled = false; // Offline.
        TestMaps.OfflinePip(window);
        window.Show();
        ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 1;
        Settle(window);
        await vm.InitializeAsync();
        Settle(window);
        return (vm, window);
    }

    /// <summary>A zoom changed with the wheel is saved, and the next run opens the map at it.</summary>
    [AvaloniaFact]
    public async Task ThePictureInPictureZoomIsRestoredAfterARestart()
    {
        var store = CreatePlannerStore(new AppSettings());
        double zoom;
        var (vm, window, _) = await CreatePlannerAsync(store: store);
        try
        {
            var frame = window.FindControl<Border>("PlannerPip")!;
            var over = frame.TranslatePoint(new Point(frame.Bounds.Width / 3, frame.Bounds.Height / 3), window)!.Value;
            window.MouseWheel(over, new Vector(0, 2));
            Settle(window);
            zoom = ZoomOf(Pip(window));
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(zoom, vm.PlannerPip.EffectiveResolution, 6);
            Assert.Equal(zoom, store.State.Settings.EffectivePlannerPip.EffectiveResolution, 6);
        }
        finally { Close(window); }

        var (restarted, again) = await StartLikeTheAppAsync(store);
        try
        {
            Assert.Equal(zoom, ZoomOf(Pip(again)), 6);
            Assert.Equal(zoom, restarted.PlannerPip.EffectiveResolution, 6);
            AssertNear(restarted.Observer, CentreOf(Pip(again)), "following, it opens on the pin");
        }
        finally { Close(again); }
    }

    /// <summary>Not following the pin, the next run opens on its saved centre as well as its zoom.</summary>
    [AvaloniaFact]
    public async Task APictureInPictureMapNotFollowingThePinOpensOnItsSavedCentreAndZoom()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        var centre = new GeoCoordinate(vm.Observer.Latitude + .3, vm.Observer.Longitude - .2);
        Close(window);
        var store = CreatePlannerStore(new AppSettings(PlannerPip: new(FollowPin: false,
            CentreLatitude: centre.Latitude, CentreLongitude: centre.Longitude, ResolutionMetresPerPixel: 123)));
        var (_, again) = await StartLikeTheAppAsync(store);
        try
        {
            Assert.Equal(123, ZoomOf(Pip(again)), 6);
            AssertNear(centre, CentreOf(Pip(again)), "the saved centre");
        }
        finally { Close(again); }
    }

    /// <summary>
    /// Settings saved before the zoom was (or with none valid) open at the shipped zoom, the same as a map
    /// that was never saved.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    public async Task SettingsWithoutAZoomOpenAtTheDefault(double? saved)
    {
        var (_, fresh) = await StartLikeTheAppAsync(CreatePlannerStore(new AppSettings()));
        double expected;
        try { expected = ZoomOf(Pip(fresh)); }
        finally { Close(fresh); }

        var (vm, window) = await StartLikeTheAppAsync(CreatePlannerStore(new AppSettings(
            PlannerPip: new(ResolutionMetresPerPixel: saved))));
        try
        {
            Assert.Equal(PlannerPipSettings.DefaultResolutionMetresPerPixel, vm.PlannerPip.EffectiveResolution);
            Assert.Equal(PlannerPipSettings.DefaultResolutionMetresPerPixel, expected, 6);
            Assert.Equal(expected, ZoomOf(Pip(window)), 6);
            Assert.InRange(ZoomOf(Pip(window)), NoctaxisMapView.MinimumResolution, NoctaxisMapView.MaximumResolution);
        }
        finally { Close(window); }
    }

    /// <summary>Following the pin moves only the restored map's centre; the saved zoom is never overwritten.</summary>
    [AvaloniaFact]
    public async Task FollowingThePinNeverOverwritesTheSavedZoom()
    {
        var store = CreatePlannerStore(new AppSettings(PlannerPip: new(ResolutionMetresPerPixel: 250)));
        var (vm, window) = await StartLikeTheAppAsync(store);
        try
        {
            var pip = Pip(window);
            Assert.Equal(250, ZoomOf(pip), 6);
            vm.CommitObserverLocation(new GeoCoordinate(vm.Observer.Latitude + .05, vm.Observer.Longitude + .05));
            Settle(window);
            AssertNear(vm.Observer, CentreOf(pip), "the centre follows the pin");
            Assert.Equal(250, ZoomOf(pip), 6);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(250, store.State.Settings.EffectivePlannerPip.EffectiveResolution, 6);

            // Turning Follow off saves the view as it is: the same zoom.
            vm.PlannerPipFollowPin = false;
            Settle(window);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(250, store.State.Settings.EffectivePlannerPip.EffectiveResolution, 6);
            vm.PlannerPipFollowPin = true;
            Settle(window);
            Assert.Equal(250, ZoomOf(pip), 6);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Switch exchanges the restored zoom with the main map's and saves the map's new one; switching back
    /// returns the restored zoom, and that is what is saved, never the default.
    /// </summary>
    [AvaloniaFact]
    public async Task SwitchKeepsTheRestoredZoom()
    {
        var store = CreatePlannerStore(new AppSettings(PlannerPip: new(ResolutionMetresPerPixel: 250)));
        var (vm, window) = await StartLikeTheAppAsync(store);
        try
        {
            var (pip, main) = (Pip(window), MainMap(window));
            var mainZoom = ZoomOf(main);
            Assert.NotEqual(250, mainZoom, 3);
            window.SwitchMaps();
            Settle(window);
            Assert.Equal(250, ZoomOf(main), 6);
            Assert.Equal(mainZoom, ZoomOf(pip), 6);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(mainZoom, store.State.Settings.EffectivePlannerPip.EffectiveResolution, 6);

            window.SwitchMaps();
            Settle(window);
            Assert.Equal(250, ZoomOf(pip), 6);
            Assert.Equal(mainZoom, ZoomOf(main), 6);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(250, store.State.Settings.EffectivePlannerPip.EffectiveResolution, 6);
        }
        finally { Close(window); }
    }
}
