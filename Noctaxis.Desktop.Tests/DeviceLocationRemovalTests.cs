using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Noctaxis does not use device geolocation: places are chosen by search, on the map or from saved locations.
/// Nothing of the removed "Use device location" feature remains, and the manual ways of choosing a place still
/// drive the plan, its weather and its terrain as before.
/// </summary>
public sealed partial class MainViewModelTests
{
    [Fact]
    public void NothingOfDeviceGeolocationRemains()
    {
        _ = TestMarkup.ReadMainWindow();
        foreach (var assembly in new[] { typeof(GeoCoordinate).Assembly, typeof(MainViewModelTests).Assembly.GetReferencedAssemblies()
                     .Select(System.Reflection.Assembly.Load).Single(item => item.GetName().Name == "Noctaxis.Desktop") })
            Assert.DoesNotContain(assembly.GetTypes(), type =>
                type.Name.Contains("DeviceLocation", StringComparison.Ordinal) || type.Name.Contains("LocationResolver", StringComparison.Ordinal));
        Assert.Equal([LocationResolutionSource.SearchResult], Enum.GetValues<LocationResolutionSource>());
        Assert.DoesNotContain(typeof(Noctaxis.Desktop.ViewModels.LocationsViewModel).GetMembers(), member =>
            member.Name.Contains("Device", StringComparison.Ordinal));
        foreach (var domain in new[] { "Locations", "Places" })
            Assert.DoesNotContain(Strings.Entries(domain), entry =>
                entry.Key.Contains("Device", StringComparison.Ordinal) || entry.Value.Contains("device location", StringComparison.OrdinalIgnoreCase));
        var page = File.ReadAllText(Path.Combine(TestPaths.DesktopProject, "Views", "LocationsPage.axaml"));
        Assert.DoesNotContain("DeviceLocation", page, StringComparison.Ordinal);
    }

    /// <summary>A place chosen on the map and a saved location opened from its card each become the plan's place.</summary>
    [Fact]
    public async Task ManualLocationChoicesStillDriveThePlan()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var saved = new SavedLocation(Guid.NewGuid(), "Kielder Observatory", new GeoCoordinate(55.23, -2.62, 360), "Europe/London");
        var vm = CreateViewModel(planning, catalogue, new FakeStore(new PersistedState(4, new AppSettings(), [saved],
            PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 0), "UTC"), null)), new FakeExporter());
        await vm.InitializeAsync();
        await vm.WaitForPlannerRefreshAsync();

        var onTheMap = new GeoCoordinate(53.6, -0.4, 20);
        var weatherBefore = planning.WeatherRequests;
        vm.MoveObserver(onTheMap);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal((onTheMap.Latitude, onTheMap.Longitude), (vm.Session.Observer.Latitude, vm.Session.Observer.Longitude));
        Assert.Equal(onTheMap.Latitude, planning.LastCalculatedSession!.Observer.Latitude);
        Assert.True(planning.WeatherRequests > weatherBefore, "a new place asks for its weather");

        await Assert.Single(vm.Locations.Saved).OpenCommand.ExecuteAsync(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal((saved.Coordinate.Latitude, saved.Coordinate.Longitude), (vm.Session.Observer.Latitude, vm.Session.Observer.Longitude));
        Assert.Equal(saved.Id, vm.Session.SavedLocationId);
    }
}
