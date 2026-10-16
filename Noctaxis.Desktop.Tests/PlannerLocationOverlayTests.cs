using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Planner's location context after the Planning location panel: the pin's place, coordinates and
/// camera altitude on the map, Open in Maps and Save location in the toolbar, and nothing that edits
/// or deletes a saved location — that is the Locations page's job.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly GeoCoordinate Brigg = new(53.55619, -0.48535);

    /// <summary>A session whose ground elevation does not wait on terrain data: 6.3 m at Brigg.</summary>
    private static FakeStore BriggStore(AppSettings? settings = null, params SavedLocation[] locations) =>
        new(new PersistedState(4, settings ?? new AppSettings(), locations,
            PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 0), "UTC") with
            {
                Observer = Brigg with { ElevationMetres = 6.3 },
                ObserverElevation = new ObserverElevationState(ManualGroundElevationOverrideAslMetres: 6.3)
            }, null));

    // ---- The location in the bar ---------------------------------------------------------------------

    private static string LocationLine(MainWindow window) =>
        window.FindControl<Noctaxis.Desktop.Controls.MarqueeText>("PlannerLocationText")!.Text!;

    /// <summary>
    /// The bar names the pin with the Planner's one name for it — the same value, with the same fallbacks —
    /// not a second lookup of its own, followed by its coordinates and the camera's altitude.
    /// </summary>
    [AvaloniaFact]
    public async Task TheBarNamesThePinWithTheSameNameThePlannerAlreadyHasForIt()
    {
        var saved = new SavedLocation(Guid.NewGuid(), "Brigg", Brigg, "Europe/London");
        var (vm, window, _) = await CreatePlannerAsync(store: BriggStore(locations: saved));
        try
        {
            // The fallback before anything is chosen is the Planner's own placeholder.
            Assert.StartsWith("Current location · ", LocationLine(window));
            Assert.Equal(vm.PlannerLocationText, LocationLine(window));

            // Opened from Locations: the saved name, in the bar at once.
            await vm.Locations.Saved.Single().OpenCommand.ExecuteAsync(null);
            Settle(window);
            Assert.Equal("Brigg", vm.LocationName);
            Assert.StartsWith("Brigg · ", LocationLine(window));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// One line: name · coordinates in Noctaxis' own form · altitude above sea level in the measurement
    /// profile's unit. Its tooltip only adds what the altitude is made of; it never repeats the line.
    /// </summary>
    [AvaloniaFact]
    public async Task TheBarShowsCoordinatesAndAltitudeAndItsTooltipOnlyAddsDetail()
    {
        var (vm, window, _) = await CreatePlannerAsync(store: BriggStore());
        try
        {
            // Ground 6.3 m plus the default 1.7 m camera: 8.0 m above sea level.
            Assert.Equal(8.0, vm.EffectiveCameraAltitudeMetres!.Value, 6);
            Assert.Equal($"{vm.LocationName} · {Brigg} · {8.0.ToString("F1", CultureInfo.CurrentCulture)} m ASL", LocationLine(window));
            var detail = (string)ToolTip.GetTip(window.FindControl<Button>("PlannerLocationCopy")!)!;
            Assert.Contains($"ground + {1.7.ToString("F1", CultureInfo.CurrentCulture)} m camera height", detail);
            Assert.Contains($"Ground elevation: {6.3.ToString("F1", CultureInfo.CurrentCulture)} m ASL", detail);
            Assert.Contains(vm.GroundElevationSourceText, detail);
            Assert.DoesNotContain(LocationLine(window), detail);

            // The measurement profile's unit, through the same state.
            vm.SettingsUnits = "Imperial";
            Settle(window);
            Assert.EndsWith(" ft ASL", LocationLine(window));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// A manual ground elevation — every location from an older state file carries one — can still be
    /// handed back to terrain from the Planner. The action is on the overlay, and only while it applies.
    /// </summary>
    [AvaloniaFact]
    public async Task AManualGroundElevationCanStillBeHandedBackToTerrain()
    {
        var (vm, window, _) = await CreatePlannerAsync(store: BriggStore());
        try
        {
            var reset = window.FindControl<Button>("PlannerResetGroundElevation")!;
            Assert.True(vm.IsElevationManualOverride);
            Assert.True(reset.IsVisible);
            Assert.Same(vm.ResetGroundElevationCommand, reset.Command);
        }
        finally { Close(window); }

        var (plain, again, _) = await CreatePlannerAsync();
        try
        {
            Assert.False(plain.IsElevationManualOverride);
            Assert.False(again.FindControl<Button>("PlannerResetGroundElevation")!.IsVisible);
        }
        finally { Close(again); }
    }

    // ---- Saving --------------------------------------------------------------------------------------

    /// <summary>
    /// Save always makes a new saved location. Twice at the same spot is two locations; the second one
    /// is numbered because names are how saved locations are told apart, never refused or merged.
    /// </summary>
    [Fact]
    public async Task SaveLocationAlwaysCreatesANewLocationEvenAtTheSameCoordinates()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = BriggStore();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();

        await vm.SaveLocationCommand.ExecuteAsync(null);
        await vm.SaveLocationCommand.ExecuteAsync(null);
        await vm.SaveLocationCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.SavedLocations.Count);
        Assert.Equal(3, vm.SavedLocations.Select(location => location.Id).Distinct().Count());
        Assert.All(vm.SavedLocations, location => Assert.Equal(Brigg.Latitude, location.Coordinate.Latitude, 9));
        // The placeholder name becomes "Location n", and repeats are numbered rather than refused.
        Assert.Equal(["Location 1", "Location 1 (2)", "Location 1 (3)"], vm.SavedLocations.Select(location => location.Name));
        Assert.Equal(3, store.State.Locations.Count);
        Assert.Equal("Location saved", vm.StatusMessage);
    }

    /// <summary>
    /// Saving while the pin stands on a saved location — or a breath away from one — leaves that location
    /// exactly as it was. There is no update of the current or the nearest location from the Planner.
    /// </summary>
    [Fact]
    public async Task SavingAtOrNearASavedLocationNeverChangesThatLocation()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var existing = new SavedLocation(Guid.NewGuid(), "Brigg", Brigg, "Europe/London", Notes: "Car park by the river");
        var store = BriggStore(locations: existing);
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        await vm.Locations.Saved.Single().OpenCommand.ExecuteAsync(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(existing.Id, vm.SelectedLocation?.Id);

        // At the saved location itself...
        await vm.SaveLocationCommand.ExecuteAsync(null);
        // ...and a metre or so away from it.
        vm.CommitObserverLocation(new GeoCoordinate(Brigg.Latitude + 0.00001, Brigg.Longitude, 6.3));
        await vm.SaveLocationCommand.ExecuteAsync(null);

        var original = Assert.Single(vm.SavedLocations, location => location.Id == existing.Id);
        Assert.Equal(("Brigg", existing.Coordinate, "Car park by the river"), (original.Name, original.Coordinate, original.Notes));
        Assert.Equal(3, vm.SavedLocations.Count);
        Assert.Equal(["Brigg", "Brigg (2)"], vm.SavedLocations.Select(location => location.Name).Take(2));
        Assert.Equal(store.State.Locations.Single(location => location.Id == existing.Id), original);
    }

    // ---- What the Planner no longer does -------------------------------------------------------------

    /// <summary>
    /// Updating and deleting saved locations belong to the Locations page. The Planner has neither, and
    /// the Locations page keeps both.
    /// </summary>
    [Fact]
    public void ThePlannerHasNoUpdateOrDeleteForSavedLocationsAndLocationsKeepsThem()
    {
        var commands = typeof(MainViewModel).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("DeleteLocationCommand", commands);
        Assert.DoesNotContain("UsePlannerSearchResultCommand", commands);
        Assert.Contains("SaveLocationCommand", commands);
        Assert.Contains("OpenInMapsCommand", commands);

        var planner = TestMarkup.LoadMainWindow().Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Planner");
        var markup = planner.ToString();
        Assert.DoesNotContain("DeleteLocation", markup);
        Assert.DoesNotContain("Save or update", markup);
        Assert.DoesNotContain("Delete saved location", markup);
        Assert.DoesNotContain(planner.Descendants(), element =>
            element.Name.LocalName == "PlannerPanelHost" && element.Attribute("PanelId")?.Value == "location");

        // The shared parts stay where Locations uses them.
        var card = typeof(LocationCardViewModel).GetProperties().Select(property => property.Name).ToArray();
        Assert.Contains("DeleteCommand", card);
        Assert.Contains("EditCommand", card);
    }

    /// <summary>
    /// The pin's two actions follow its location in the bar, once each, bound to the commands that already
    /// existed: Save location first, beside the place it saves, then Open in Maps, then Export at the end.
    /// Neither is inside the location's own region, so neither moves with its text.
    /// </summary>
    [Fact]
    public void SaveLocationThenOpenInMapsFollowTheLocationInTheBar()
    {
        var document = TestMarkup.LoadMainWindow();
        System.Xml.Linq.XElement Named(string name) => document.Descendants().Single(element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == name));
        var open = Named("PlannerOpenInMaps");
        var save = Named("PlannerSaveLocation");
        Assert.Equal(("Open in Maps", "{Binding OpenInMapsCommand}"), (open.Attribute("Content")?.Value, open.Attribute("Command")?.Value));
        Assert.Equal(("Save location", "{Binding SaveLocationCommand}"), (save.Attribute("Content")?.Value, save.Attribute("Command")?.Value));
        var bar = Named("PlannerTopBar");
        string Column(System.Xml.Linq.XElement element) => element.Attributes().Single(attribute => attribute.Name.LocalName == "Grid.Column").Value;
        Assert.All(new[] { open, save }, button => Assert.Same(bar, button.Parent));
        Assert.Equal(("1", "2", "3", "5", "6"), (Column(Named("PlannerLocationCopy")), Column(save), Column(open), Column(Named("PlannerExportMenu")),
            Column(Named("PlannerImportMenu"))));
        Assert.Equal("Auto,*,Auto,Auto,Auto,Auto,Auto", bar.Attribute("ColumnDefinitions")?.Value);
        Assert.DoesNotContain(Named("PlannerLocationCopy").Descendants(), element => element.Name.LocalName == "Button");
        var markup = TestMarkup.ReadMainWindow();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markup, @"{Binding OpenInMapsCommand}"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(markup, @"{Binding SaveLocationCommand}"));
    }

    /// <summary>Open in Maps is the existing service: the configured provider, at the pin.</summary>
    [Theory]
    [InlineData(ExternalMapProvider.OpenStreetMap, "openstreetmap.org")]
    [InlineData(ExternalMapProvider.GoogleMaps, "google.")]
    public async Task OpenInMapsOpensThePinInTheConfiguredProvider(ExternalMapProvider provider, string host)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var launcher = new FakeUriLauncher(true, null);
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            BriggStore(new AppSettings(ExternalMapProvider: provider)), new FakeExporter(), externalUriLauncher: launcher);
        await vm.InitializeAsync();

        vm.OpenInMapsCommand.Execute(null);

        Assert.True(launcher.Opened);
        Assert.Equal(Noctaxis.Desktop.Services.ExternalMapService.CreateUri(vm.Latitude, vm.Longitude, provider), launcher.LastUri);
        Assert.Contains(host, launcher.LastUri!.Host + launcher.LastUri.AbsoluteUri, StringComparison.Ordinal);
    }
}
