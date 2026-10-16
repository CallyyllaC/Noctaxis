using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Extensions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;
using MapViewState = Noctaxis.Desktop.Controls.NoctaxisMapView.MapViewState;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    private static void RequestPlannerMapMenu(Window window, NoctaxisMapView map, Point point)
    {
        var control = map.MapControlForTesting;
        var inWindow = control.TranslatePoint(point, window)!.Value;
        window.MouseDown(inWindow, MouseButton.Right);
        window.MouseUp(inWindow, MouseButton.Right);
        // Headless input does not synthesize the platform's ContextRequested event.
        control.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        Assert.True(control.ContextMenu!.IsOpen);
    }

    private static MenuItem OpenMapsItem(NoctaxisMapView map) => Assert.Single(
        map.MapControlForTesting.ContextMenu!.Items.OfType<MenuItem>(),
        item => item.Header?.ToString() == Strings.Get("Planner_Location_OpenMaps_Button"));

    [AvaloniaTheory]
    [InlineData(ExternalMapProvider.OpenStreetMap)]
    [InlineData(ExternalMapProvider.GoogleMaps)]
    [InlineData(ExternalMapProvider.Mapillary)]
    public async Task MapContextMenuUsesClickedCoordinatesAndConfiguredProviderWithoutChangingPlan(ExternalMapProvider provider)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var launcher = new FakeUriLauncher(true, null);
        var place = new SavedLocation(Guid.NewGuid(), "Eryri", new(53.0056, -3.9519), "UTC");
        var session = PlanningSession.Default(Instant.FromUtc(2026, 10, 9, 19, 0), "UTC") with
        { Observer = place.Coordinate, SavedLocationId = place.Id };
        var store = new FakeStore(new(4, new AppSettings(ExternalMapProvider: provider), [place], session, place.Id));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), externalUriLauncher: launcher);
        await vm.InitializeAsync();
        var window = new MainWindow(vm, new DesktopDialogService(new LocationSearchViewModel(
            new FakeLocationSearchProvider(), Microsoft.Extensions.Logging.Abstractions.NullLogger<LocationSearchViewModel>.Instance)))
        { Width = 1440, Height = 1000 };
        var map = window.FindControl<PlannerMapView>("PlannerMap")!;
        foreach (var (_, layer) in map.CompositionForTesting.NativeLayerOrder) layer.Enabled = false;
        TestMaps.OfflinePip(window);
        try
        {
            window.Show();
            vm.ShowPlannerCommand.Execute(null);
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            await vm.SettleSettingsAsync();
            var beforeSession = vm.Session;
            var beforeObserver = vm.Observer;
            var beforePlace = vm.SelectedLocation;
            var beforeSaved = vm.SavedLocations.ToArray();
            var beforeSnapshot = vm.Snapshot;
            var commits = 0;
            map.CoordinateCommitted += (_, _) => commits++;
            map.PreviewCoordinateChanged += (_, _) => commits++;
            foreach (var view in new[] { new MapViewState(place.Coordinate, 45),
                new MapViewState(new(-33.5, -70.25), 1200), new MapViewState(new(0, 179.9), 4000) })
            {
                map.ShowView(view);
                Settle(window);
                foreach (var fraction in new[] { new Point(.60, .30), new Point(.78, .65) })
                {
                    var control = map.MapControlForTesting;
                    var point = new Point(control.Bounds.Width * fraction.X, control.Bounds.Height * fraction.Y);
                    var world = control.Map!.Navigator.Viewport.ScreenToWorld(point.X, point.Y);
                    var expected = WebMercator.ToWgs84(world.X, world.Y).Normalised();
                    RequestPlannerMapMenu(window, map, point);
                    Assert.Equal(new[] { Strings.Get("Planner_Map_SetPinHere_MenuItem"),
                        Strings.Get("Planner_Map_SaveCurrentPin_MenuItem"), Strings.Get("Planner_Location_OpenMaps_Button") },
                        control.ContextMenu!.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));
                    OpenMapsItem(map).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    control.ContextMenu.Close();
                    Settle(window);
                    Assert.Equal(ExternalMapService.CreateUri(expected.Latitude, expected.Longitude, provider), launcher.LastUri);
                    Assert.Equal(beforeSession, vm.Session);
                    Assert.Equal(beforeObserver, vm.Observer);
                    Assert.Same(beforePlace, vm.SelectedLocation);
                    Assert.Equal(beforeSaved, vm.SavedLocations);
                    Assert.Same(beforeSnapshot, vm.Snapshot);
                    Assert.Equal(0, commits);
                }
            }
            await vm.SettleSettingsAsync();
            Assert.Equal(beforeSession, store.State.Session);
            // The real toolbar still opens the planning pin, not the last context-menu coordinate.
            var toolbar = window.FindControl<Button>("PlannerOpenInMaps")!;
            Assert.Equal(OpenMapsItem(map).Header, toolbar.Content);
            var toolbarPoint = toolbar.TranslatePoint(new(toolbar.Bounds.Width / 2, toolbar.Bounds.Height / 2), window)!.Value;
            window.MouseDown(toolbarPoint, MouseButton.Left);
            window.MouseUp(toolbarPoint, MouseButton.Left);
            Settle(window);
            Assert.Equal(ExternalMapService.CreateUri(vm.Latitude, vm.Longitude, provider), launcher.LastUri);
            Assert.Equal(beforeSession, vm.Session);
            // PiP remains a constrained, non-interactive view; do not enable its blocked input here.
            Assert.False(window.PipForTesting!.IsHitTestVisible);
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public void MapContextMenuRetainsCapturedPositionAndExistingPinActions()
    {
        var map = new NoctaxisMapView { Observer = new(53, -1) };
        map.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = 800, Height = 600, Content = map };
        GeoCoordinate? opened = null, committed = null;
        var saves = 0;
        map.OpenInMapsRequested += (_, coordinate) => opened = coordinate;
        map.CoordinateCommitted += (_, coordinate) => committed = coordinate;
        map.SaveCurrentPinRequested += (_, _) => saves++;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var control = map.MapControlForTesting;
            var point = new Point(480, 180);
            var world = control.Map!.Navigator.Viewport.ScreenToWorld(point.X, point.Y);
            var expected = WebMercator.ToWgs84(world.X, world.Y).Normalised();
            RequestPlannerMapMenu(window, map, point);
            map.ShowView(new(new(-30, 150), 1200)); // A changed viewport cannot reinterpret the captured click.
            OpenMapsItem(map).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(expected, opened);
            Assert.Null(committed);
            Assert.Equal(new GeoCoordinate(53, -1), map.Observer);
            var items = control.ContextMenu!.Items.OfType<MenuItem>().ToArray();
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(expected, committed);
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, saves);
            control.ContextMenu.Close();
        }
        finally { window.Close(); }
    }

    [Fact]
    public void MapContextCoordinateCommandRejectsInvalidCoordinatesAndReportsLauncherFailure()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var launcher = new FakeUriLauncher(false, "browser unavailable");
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreateSupporterStore(new AppSettings()),
            new FakeExporter(), externalUriLauncher: launcher);
        foreach (var invalid in new GeoCoordinate?[] { null, new(double.NaN, 0), new(0, double.PositiveInfinity), new(91, 0), new(0, 181) })
        {
            Assert.False(vm.OpenMapCoordinateCommand.CanExecute(invalid));
            vm.OpenMapCoordinateCommand.Execute(invalid);
            Assert.Null(launcher.LastUri);
        }
        vm.OpenMapCoordinateCommand.Execute(new GeoCoordinate(-33.5, -70.25));
        Assert.Equal(ExternalMapService.CreateUri(-33.5, -70.25, vm.Settings.ExternalMapProvider), launcher.LastUri);
        Assert.Equal("Could not open Maps: browser unavailable", vm.StatusMessage);
    }
}
