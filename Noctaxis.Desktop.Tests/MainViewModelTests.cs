using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Locations;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.ViewModels;
using NodaTime;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task LocationTimeAndTargetChanges_UpdateOneCoherentSession()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var initial = PlanningSession.Default(instant, "Europe/London");
        var store = new FakeStore(new PersistedState(1, new AppSettings(), [], initial, null));
        var catalogue = new EmbeddedTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();

        viewModel.MoveObserver(new GeoCoordinate(55.9533, -3.1883, 75));
        viewModel.MinutesOfDay = 22 * 60 + 30;
        viewModel.AddCelestialObjectCommand.Execute(catalogue.Get("andromeda"));

        Assert.Equal(new GeoCoordinate(55.9533, -3.1883, 75), viewModel.Session.Observer);
        Assert.Equal("openngc:NGC0224", viewModel.Session.TargetId);
        Assert.Equal(new LocalTime(22, 30), viewModel.Session.Instant.InZone(DateTimeZoneProviders.Tzdb["Europe/London"]).TimeOfDay);
        Assert.Same(viewModel.Session, viewModel.Session);
    }

    [Fact]
    public async Task ManualAndExportRefresh_UseForcedWeatherWithoutDiscardingSnapshot()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var initial = PlanningSession.Default(instant, "Europe/London");
        var catalogue = new EmbeddedTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var exporter = new FakeExporter();
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], initial, null)), exporter);
        await viewModel.InitializeAsync();
        viewModel.ShowPlannerCommand.Execute(null);
        await Task.Delay(80);

        await viewModel.RefreshWeatherCommand.ExecuteAsync(null);
        Assert.Equal(1, planning.ForcedRefreshes);
        Assert.NotNull(viewModel.Snapshot);
        var png = await viewModel.CreateExportPngAsync(CancellationToken.None);
        Assert.Equal(2, planning.ForcedRefreshes);
        Assert.Equal([137, 80, 78, 71], png);
        Assert.Same(viewModel.Snapshot, exporter.LastSnapshot);
    }

    [Fact]
    public async Task FailedManualRefreshKeepsWeather_AndFailedExportDoesNotRender()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new EmbeddedTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var exporter = new FakeExporter();
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(instant, "Europe/London"), null)), exporter);
        await viewModel.InitializeAsync();
        viewModel.ShowPlannerCommand.Execute(null);
        await Task.Delay(80);
        var validWeather = viewModel.Snapshot!.Weather;
        planning.FailRefresh = true;

        await viewModel.RefreshWeatherCommand.ExecuteAsync(null);
        Assert.Same(validWeather, viewModel.Snapshot.Weather);
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.CreateExportPngAsync(CancellationToken.None));
        Assert.Equal(0, exporter.RenderCount);
        Assert.Contains("Export cancelled", viewModel.StatusMessage);
    }

    [Fact]
    public async Task PreviewLocation_DoesNotChangeSession_UntilSingleCommit()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new EmbeddedTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(instant, "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        var original = viewModel.Session.Observer;
        var preview = new GeoCoordinate(55.95, -3.19, 80);

        viewModel.PreviewObserverLocation(preview);
        Assert.Equal(original, viewModel.Session.Observer);
        Assert.Equal(preview, viewModel.PreviewObserver);
        viewModel.CommitObserverLocation(preview);
        await Task.Delay(180);
        Assert.Equal(preview, viewModel.Session.Observer);
        Assert.Equal(1, planning.SnapshotCalculations);
    }

    [Fact]
    public void MapViewportMovement_DoesNotMovePlanningPin_WhilePinDragPreviewsThenCommits()
    {
        var original = new GeoCoordinate(51.5, -0.1, 20);
        var interaction = new PlanningPinInteractionState(original);
        interaction.ViewportChanged();
        interaction.ViewportChanged();
        Assert.Equal(original, interaction.CommittedCoordinate);
        Assert.Equal(original, interaction.PreviewCoordinate);

        var dragged = new GeoCoordinate(52.1, -1.3, 20);
        interaction.BeginDrag();
        interaction.UpdateDrag(dragged);
        Assert.Equal(original, interaction.CommittedCoordinate);
        Assert.Equal(dragged, interaction.PreviewCoordinate);
        Assert.Equal(dragged, interaction.CompleteDrag());
        Assert.Equal(dragged, interaction.CommittedCoordinate);
    }

    [Fact]
    public async Task RapidLocationCommits_CancelObsoleteDebounceAndCalculateFinalOnce()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.CommitObserverLocation(new GeoCoordinate(50, 1));
        viewModel.CommitObserverLocation(new GeoCoordinate(51, 2));
        viewModel.CommitObserverLocation(new GeoCoordinate(52, 3));
        await Task.Delay(200);
        Assert.Equal(1, planning.SnapshotCalculations);
        Assert.Equal(new GeoCoordinate(52, 3), planning.LastCalculatedSession!.Observer);
    }

    [Fact]
    public async Task PinMovement_ImmediatelyInvalidatesOldNameThenResolvesCurrentLocality()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var reverse = new ImmediateReverseGeocodingProvider("Edinburgh");
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), reverseGeocoding: reverse);
        await viewModel.InitializeAsync();
        viewModel.LocationName = "Old place";
        var moved = new GeoCoordinate(55.9533, -3.1883, 70);

        viewModel.PreviewObserverLocation(moved);

        Assert.Equal("New location", viewModel.LocationName);
        Assert.Equal(moved, viewModel.PreviewObserver);
        viewModel.CommitObserverLocation(moved);
        await WaitUntilAsync(() => viewModel.LocationName == "Near Edinburgh", TimeSpan.FromSeconds(2));
        Assert.Equal(1, reverse.RequestCount);
    }

    [Fact]
    public async Task StaleReverseGeocodingResponse_CannotOverwriteNewerPin()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var reverse = new ControllableReverseGeocodingProvider();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), reverseGeocoding: reverse);
        await viewModel.InitializeAsync();

        viewModel.CommitObserverLocation(new GeoCoordinate(51, -1));
        await WaitUntilAsync(() => reverse.RequestCount == 1, TimeSpan.FromSeconds(2));
        viewModel.CommitObserverLocation(new GeoCoordinate(52, -2));
        await WaitUntilAsync(() => reverse.RequestCount == 2, TimeSpan.FromSeconds(2));

        reverse.Complete(0, "Old locality");
        await Task.Delay(40);
        Assert.Equal("New location", viewModel.LocationName);
        reverse.Complete(1, "Current locality");
        await WaitUntilAsync(() => viewModel.LocationName == "Near Current locality", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RapidPinCommits_DebounceToOneReverseLookup()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var reverse = new ImmediateReverseGeocodingProvider("Final place");
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), reverseGeocoding: reverse);
        await viewModel.InitializeAsync();

        viewModel.CommitObserverLocation(new GeoCoordinate(50, 1));
        viewModel.CommitObserverLocation(new GeoCoordinate(51, 2));
        viewModel.CommitObserverLocation(new GeoCoordinate(52, 3));

        await WaitUntilAsync(() => viewModel.LocationName == "Near Final place", TimeSpan.FromSeconds(2));
        Assert.Equal(1, reverse.RequestCount);
    }

    [Fact]
    public async Task DateAndTimeSlider_PreviewsThenCommitsAndSynchronisesExactControls()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var initial = PlanningSession.Default(Instant.FromUtc(2024, 1, 15, 20, 0), "UTC");
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(TimeSnapMinutes: 5), [], initial, null)), new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.PreviewMinutesOfDay = 615;
        Assert.Equal("10:15", viewModel.TimeText);
        Assert.Equal(initial.Instant, viewModel.Session.Instant);
        viewModel.PreviewDateOffsetDays = 3;
        Assert.Equal(18, viewModel.LocalDate!.Value.Day);
        viewModel.CommitTemporalPreview();
        Assert.Equal(new LocalDateTime(2024, 1, 18, 10, 15), viewModel.Session.Instant.InUtc().LocalDateTime);
    }

    [Fact]
    public async Task ObsoleteTemporalPreview_CannotOverwriteNewerPreview()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 15, 20, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync(); viewModel.ShowPlannerCommand.Execute(null); await Task.Delay(100);
        planning.CalculateDelayMilliseconds = 150;
        viewModel.PreviewMinutesOfDay = 600;
        await Task.Delay(20);
        viewModel.PreviewMinutesOfDay = 660;
        await Task.Delay(800);
        Assert.Equal(new LocalTime(11, 0), viewModel.Snapshot!.Position.Instant.InUtc().TimeOfDay);
    }

    [Fact]
    public async Task CelestialList_PersistsVisibilityPrimaryAndPreventsDuplicates()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var store = new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();
        var andromeda = catalogue.Get("andromeda");
        viewModel.AddCelestialObjectCommand.Execute(andromeda);
        viewModel.AddCelestialObjectCommand.Execute(andromeda);
        Assert.Single(viewModel.CelestialObjects, item => item.TargetId == "openngc:NGC0224");
        Assert.Equal("openngc:NGC0224", viewModel.Session.TargetId);
        var moon = viewModel.CelestialObjects.Single(item => item.TargetId == "moon");
        moon.IsVisible = false;
        await viewModel.PersistAsync(CancellationToken.None);
        Assert.False(store.State.Session.EffectiveVisibleObjects.Single(item => item.TargetId == "moon").IsVisible);
        Assert.False(store.State.Settings.EffectiveCelestialObjects.EffectiveConfiguredObjects.Single(item => item.TargetId == "moon").IsVisible);
        Assert.Equal("openngc:NGC0224", store.State.Settings.EffectiveCelestialObjects.DefaultPrimaryTargetId);
    }

    [Fact]
    public async Task SavedLocation_CanEditFavouriteAndDelete_AndLastUseIsTrackedForSorting()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "Old name", new GeoCoordinate(51, -1), "UTC");
        var store = new FakeStore(new PersistedState(1, new AppSettings(), [location], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var dialogs = new FakeDialogs { EditResult = new SavedLocationEdit("New name", "Dark ridge") };
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), dialogs);
        await viewModel.InitializeAsync();
        var card = Assert.Single(viewModel.Locations.Saved);
        await card.EditCommand.ExecuteAsync(null);
        Assert.False(dialogs.LastEditorWasCreateMode);
        Assert.Equal("New name", Assert.Single(viewModel.SavedLocations).Name);
        Assert.Equal("Dark ridge", Assert.Single(viewModel.SavedLocations).Notes);
        Assert.Equal(location.Coordinate, Assert.Single(viewModel.SavedLocations).Coordinate);
        await card.ToggleFavouriteCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(viewModel.SavedLocations).IsFavourite);
        card = Assert.Single(viewModel.Locations.Saved);
        await card.OpenCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(viewModel.SavedLocations).LastUsedUtc.HasValue);
        card = Assert.Single(viewModel.Locations.Saved);
        await card.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.SavedLocations);
    }

    [Fact]
    public async Task SavedLocation_DeleteCanBeCancelledWithoutChangingPersistence()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "Keep me", new GeoCoordinate(51, -1), "UTC");
        var store = new FakeStore(new PersistedState(1, new AppSettings(), [location],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var dialogs = new FakeDialogs { ConfirmDeleteResult = false };
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), dialogs);
        await viewModel.InitializeAsync();

        await Assert.Single(viewModel.Locations.Saved).DeleteCommand.ExecuteAsync(null);

        Assert.Equal(location, Assert.Single(viewModel.SavedLocations));
        Assert.Equal(location, Assert.Single(store.State.Locations));
    }

    [Fact]
    public async Task SavedLocation_EditRejectsDuplicateNameAndPreservesBothLocations()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var first = new SavedLocation(Guid.NewGuid(), "Ridge", new GeoCoordinate(51, -1), "UTC");
        var second = new SavedLocation(Guid.NewGuid(), "Valley", new GeoCoordinate(52, -2), "UTC");
        var store = new FakeStore(new PersistedState(1, new AppSettings(), [first, second],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var dialogs = new FakeDialogs { EditResult = new SavedLocationEdit("ridge", "Changed") };
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), dialogs);
        await viewModel.InitializeAsync();

        await viewModel.Locations.Saved.Single(card => card.Id == second.Id).EditCommand.ExecuteAsync(null);

        Assert.Equal("Ridge", viewModel.SavedLocations.Single(location => location.Id == first.Id).Name);
        var unchanged = viewModel.SavedLocations.Single(location => location.Id == second.Id);
        Assert.Equal("Valley", unchanged.Name);
        Assert.Null(unchanged.Notes);
        Assert.Contains("already uses", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpeningSavedLocation_MarksOnlyThatHomepageCardActive()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var first = new SavedLocation(Guid.NewGuid(), "Ridge", new GeoCoordinate(51, -1), "UTC");
        var second = new SavedLocation(Guid.NewGuid(), "Valley", new GeoCoordinate(52, -2), "UTC");
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [first, second],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();

        await viewModel.Locations.Saved.Single(card => card.Id == second.Id).OpenCommand.ExecuteAsync(null);

        Assert.True(viewModel.Locations.Saved.Single(card => card.Id == second.Id).IsSelected);
        Assert.False(viewModel.Locations.Saved.Single(card => card.Id == first.Id).IsSelected);
    }

    [Fact]
    public async Task LocationSort_AlwaysKeepsFavouritesFirstAndSortsWithinEachGroup()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var old = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recent = old.AddDays(10);
        var locations = new[]
        {
            new SavedLocation(Guid.NewGuid(), "Zulu favourite", new(51, -1), "UTC", IsFavourite: true, LastUsedUtc: old, SortOrder: 0, DateAddedUtc: recent),
            new SavedLocation(Guid.NewGuid(), "Alpha normal", new(52, -2), "UTC", LastUsedUtc: recent, SortOrder: 1, DateAddedUtc: old),
            new SavedLocation(Guid.NewGuid(), "Alpha favourite", new(53, -3), "UTC", IsFavourite: true, LastUsedUtc: recent, SortOrder: 2, DateAddedUtc: old),
            new SavedLocation(Guid.NewGuid(), "Zulu normal", new(54, -4), "UTC", LastUsedUtc: old, SortOrder: 3, DateAddedUtc: recent)
        };
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), locations,
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();

        Assert.Equal(["Alpha favourite", "Zulu favourite", "Alpha normal", "Zulu normal"],
            viewModel.Locations.Saved.Select(card => card.Name));
        Assert.IsType<AddLocationCardViewModel>(viewModel.Locations.GridItems[^1]);
        Assert.Equal(viewModel.Locations.Saved,
            viewModel.Locations.GridItems.OfType<LocationCardViewModel>());

        viewModel.Locations.SelectedSortOption = viewModel.Locations.SortOptions.Single(option => option.Mode == LocationSortMode.Name);
        Assert.Equal(["Alpha favourite", "Zulu favourite", "Alpha normal", "Zulu normal"],
            viewModel.Locations.Saved.Select(card => card.Name));
        Assert.IsType<AddLocationCardViewModel>(viewModel.Locations.GridItems[^1]);
        Assert.Equal(viewModel.Locations.Saved,
            viewModel.Locations.GridItems.OfType<LocationCardViewModel>());

        viewModel.Locations.SelectedSortOption = viewModel.Locations.SortOptions.Single(option => option.Mode == LocationSortMode.DateAdded);
        Assert.Equal(["Zulu favourite", "Alpha favourite", "Zulu normal", "Alpha normal"],
            viewModel.Locations.Saved.Select(card => card.Name));
        Assert.IsType<AddLocationCardViewModel>(viewModel.Locations.GridItems[^1]);
        Assert.Equal(viewModel.Locations.Saved,
            viewModel.Locations.GridItems.OfType<LocationCardViewModel>());
    }

    [Fact]
    public async Task SaveCurrentAsNewLocation_UsesEditFlowAndRecordsCreationTime()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var dialogs = new FakeDialogs { EditResult = new SavedLocationEdit("New ridge", "Western view") };
        var thumbnails = new FakeThumbnailService();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), dialogs, thumbnails: thumbnails);
        await viewModel.InitializeAsync();
        viewModel.CommitObserverLocation(new GeoCoordinate(51.2, -2.4, 90));

        await viewModel.SaveCurrentAsNewLocationCommand.ExecuteAsync(null);

        var saved = Assert.Single(viewModel.SavedLocations);
        Assert.True(dialogs.LastEditorWasCreateMode);
        Assert.Equal("New ridge", saved.Name);
        Assert.Equal("Western view", saved.Notes);
        Assert.Equal(new GeoCoordinate(51.2, -2.4, 90), saved.Coordinate);
        Assert.NotNull(saved.DateAddedUtc);
        Assert.Equal(1, thumbnails.ForcedRefreshes);
    }

    [Fact]
    public async Task SavedLocationChanges_UpdateMapImageCommandEnablement()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var dialogs = new FakeDialogs { EditResult = new SavedLocationEdit("New ridge", null) };
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), dialogs, thumbnails: new FakeThumbnailService());
        await viewModel.InitializeAsync();

        Assert.False(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.False(viewModel.RefreshSavedLocationBuildingCachesCommand.CanExecute(null));
        Assert.False(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));

        await viewModel.SaveCurrentAsNewLocationCommand.ExecuteAsync(null);

        Assert.True(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.True(viewModel.RefreshSavedLocationBuildingCachesCommand.CanExecute(null));
        Assert.True(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));

        await Assert.Single(viewModel.Locations.Saved).DeleteCommand.ExecuteAsync(null);

        Assert.False(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.False(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));
    }

    [Fact]
    public async Task PersistedLocations_EnableMapImageCommandsAfterInitialLoad()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "Home", new(53.61, -0.43), "UTC");
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [location],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());

        await viewModel.InitializeAsync();

        Assert.True(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.True(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshSavedLocationThumbnails_ConfirmsAndForcesEachLocationSequentially()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var locations = new[]
        {
            new SavedLocation(Guid.NewGuid(), "Home", new(53.61, -0.43), "UTC"),
            new SavedLocation(Guid.NewGuid(), "Quarry", new(53.60, -0.44), "UTC")
        };
        var thumbnails = new FakeThumbnailService();
        var dialogs = new FakeDialogs();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), locations,
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), dialogs, thumbnails: thumbnails);
        await viewModel.InitializeAsync();

        await viewModel.RefreshSavedLocationThumbnailsCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.RefreshThumbnailConfirmations);
        Assert.Equal(2, thumbnails.ForcedRefreshes);
        Assert.Contains("Raster maps: 2 complete, 0 cached, 0 failed.", viewModel.LocationThumbnailRefreshStatus);
        Assert.Contains("Road and water overlays: 2 complete", viewModel.LocationThumbnailRefreshStatus);
        Assert.Contains("Building stars: 2 complete", viewModel.LocationThumbnailRefreshStatus);
        Assert.True(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.True(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshBuildingCaches_ConfirmsAndDoesNotRequestRasterOrCoreRefresh()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var locations = new[]
        {
            new SavedLocation(Guid.NewGuid(), "Home", new(53.61, -0.43), "UTC"),
            new SavedLocation(Guid.NewGuid(), "Bridge", new(53.56, -0.50), "UTC")
        };
        var thumbnails = new FakeThumbnailService();
        var dialogs = new FakeDialogs();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), locations,
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), dialogs, thumbnails: thumbnails);
        await viewModel.InitializeAsync();

        await viewModel.RefreshSavedLocationBuildingCachesCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.RefreshBuildingConfirmations);
        Assert.Equal(2, thumbnails.BuildingRefreshes);
        Assert.Equal(0, thumbnails.ForcedRefreshes);
        Assert.Contains("Road and water overlays: 0 complete, 2 cached", viewModel.LocationThumbnailRefreshStatus);
        Assert.Contains("Building stars: 2 complete", viewModel.LocationThumbnailRefreshStatus);
    }

    [Fact]
    public async Task ReapplySavedLocationMapStyles_UsesSavedSourcesWithoutRefreshConfirmation()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var locations = new[]
        {
            new SavedLocation(Guid.NewGuid(), "Home", new(53.61, -0.43), "UTC"),
            new SavedLocation(Guid.NewGuid(), "Quarry", new(53.60, -0.44), "UTC")
        };
        var thumbnails = new FakeThumbnailService();
        var dialogs = new FakeDialogs();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), locations,
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), dialogs, thumbnails: thumbnails);
        await viewModel.InitializeAsync();

        await viewModel.ReapplySavedLocationMapStylesCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogs.RefreshThumbnailConfirmations);
        Assert.Equal(0, thumbnails.ForcedRefreshes);
        Assert.Equal(2, thumbnails.StyleReapplications);
        Assert.Equal("2 refreshed, 0 failed.", viewModel.LocationThumbnailRefreshStatus);
    }

    [Fact]
    public void CelestialSearch_ResetFiltersPreservesSearchText()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var search = new CelestialSearchViewModel(new LocalTargetSearchService(catalogue), catalogue)
        {
            Query = "M31",
            SelectedObjectType = new CatalogueTypeOption("Galaxy", AstralTargetCategory.Galaxy),
            SelectedConstellation = "Andromeda",
            SelectedCatalogueFamily = "Messier"
        };

        search.ResetFiltersCommand.Execute(null);

        Assert.Equal("M31", search.Query);
        Assert.Null(search.SelectedObjectType.Value);
        Assert.Equal("All constellations", search.SelectedConstellation);
        Assert.Equal("All catalogues", search.SelectedCatalogueFamily);
        Assert.False(search.HasActiveFilters);
    }

    [Fact]
    public async Task UnsavedCustomLocation_IsCreatedOnlyAfterExplicitSave()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.CommitObserverLocation(new GeoCoordinate(53, -2, 120));
        Assert.Empty(viewModel.SavedLocations);
        viewModel.LocationName = "Scout hill";
        await viewModel.SaveLocationCommand.ExecuteAsync(null);
        var saved = Assert.Single(viewModel.SavedLocations);
        Assert.Equal("Scout hill", saved.Name);
        Assert.Equal(120, saved.Coordinate.ElevationMetres);
    }

    [Fact]
    public async Task SharedLocationSearch_DebouncesAndCancelsObsoleteRequest()
    {
        var provider = new DelayedSearchProvider();
        var search = new LocationSearchViewModel(provider, NullLogger<LocationSearchViewModel>.Instance);
        search.Query = "London";
        await Task.Delay(400);
        search.Query = "Paris";
        await Task.Delay(650);
        Assert.Equal(2, provider.Started);
        Assert.Equal(1, provider.Cancelled);
        Assert.Equal("Paris", Assert.Single(search.Results).DisplayName);
    }

    [Fact]
    public async Task LensPreset_UpdatesFieldOfViewWithoutFullPlanningRefresh()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync(); viewModel.ShowPlannerCommand.Execute(null); await Task.Delay(100);
        var calculations = planning.SnapshotCalculations;
        var oldFov = viewModel.Snapshot!.FieldOfView.HorizontalDegrees;
        viewModel.SelectFocalLengthCommand.Execute(50d);
        Assert.Equal(calculations, planning.SnapshotCalculations);
        Assert.NotEqual(oldFov, viewModel.Snapshot.FieldOfView.HorizontalDegrees);
        viewModel.SelectedOrientation = CameraOrientation.Portrait;
        Assert.True(viewModel.Snapshot.FieldOfView.HorizontalDegrees < viewModel.Snapshot.FieldOfView.VerticalDegrees);
        Assert.NotNull(viewModel.CameraFramingGuide);
        Assert.NotNull(viewModel.CameraFramingVisibility);
        viewModel.ShowFramingVisibilityLimits = false;
        Assert.Null(viewModel.CameraFramingVisibility);
        Assert.Equal("Visibility limits hidden", viewModel.FramingVisibilityStatus);
        viewModel.ShowFramingVisibilityLimits = true;
        viewModel.IsCameraFramingOverlayVisible = false;
        Assert.Null(viewModel.CameraFramingGuide);
        Assert.False(viewModel.Settings.EffectiveCameraFraming.IsOverlayVisible);
        Assert.Equal(calculations, planning.SnapshotCalculations);
    }

    [Fact]
    public void SavedLocationEditor_RejectsEmptyNameWithoutChangingOriginal()
    {
        var original = new SavedLocation(Guid.NewGuid(), "Ridge", new GeoCoordinate(52, -2, 145), "UTC", RegionDescription: "Wales");
        var editor = new SavedLocationEditorViewModel(original) { Name = "   ", Description = "Changed" };
        Assert.Null(editor.ValidateAndCreateResult());
        Assert.Equal("Location name is required.", editor.ValidationMessage);
        Assert.Equal("Ridge", original.Name);
        Assert.Equal(new GeoCoordinate(52, -2, 145), original.Coordinate);
    }

    [Fact]
    public async Task CancelledSavedLocationEdit_DoesNotModifyLocation()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "Original", new GeoCoordinate(51, -1, 80), "UTC", RegionDescription: "Region");
        var store = new FakeStore(new PersistedState(2, new AppSettings(), [location], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), new FakeDialogs());
        await viewModel.InitializeAsync();
        await Assert.Single(viewModel.Locations.Saved).EditCommand.ExecuteAsync(null);
        Assert.Equal(location, Assert.Single(viewModel.SavedLocations));
    }

    [Fact]
    public async Task PlannerSearchSelection_CommitsOnceAndUpdatesPlanningLocation()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        var result = new LocationSearchResult("x", "York", new GeoCoordinate(53.96, -1.08, 20), "England, UK", "UK", "Europe/London", "Test");
        await viewModel.UsePlannerSearchResultCommand.ExecuteAsync(result);
        await Task.Delay(180);
        Assert.Equal(result.Coordinate, viewModel.Session.Observer);
        Assert.Equal(1, planning.SnapshotCalculations);
    }

    [Fact]
    public async Task CelestialVisibility_AllowsEightRejectsNinthAndHiddenDoesNotCount()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        foreach (var target in catalogue.Targets.Where(target => !target.IsSun && !target.IsMoon).Take(7))
            viewModel.AddCelestialObjectCommand.Execute(target);
        Assert.Equal(9, viewModel.CelestialObjects.Count);
        Assert.Equal(8, viewModel.VisibleCelestialCount);
        var hidden = Assert.Single(viewModel.CelestialObjects, item => !item.IsVisible);
        hidden.IsVisible = true;
        Assert.False(hidden.IsVisible);
        Assert.Contains("maximum", viewModel.CelestialLimitMessage!, StringComparison.OrdinalIgnoreCase);
        var visibleNonPrimary = viewModel.CelestialObjects.First(item => item.IsVisible && !item.IsPrimary);
        visibleNonPrimary.IsVisible = false;
        hidden.IsVisible = true;
        Assert.True(hidden.IsVisible);
        Assert.Equal(8, viewModel.VisibleCelestialCount);
    }

    [Fact]
    public async Task HidingPrimary_SelectsVisibleReplacementConsistently()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        var primary = viewModel.CelestialObjects.Single(item => item.IsPrimary);
        primary.IsVisible = false;
        Assert.NotEqual(primary.TargetId, viewModel.Session.TargetId);
        Assert.True(viewModel.CelestialObjects.Single(item => item.TargetId == viewModel.Session.TargetId).IsVisible);
    }

    [Fact]
    public async Task RestoreCelestialDefaults_DoesNotResetUnrelatedSettings()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var settings = new AppSettings("D:\\DEM", "UK", "UTC", TimeSnapMinutes: 15);
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, settings, [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.AddCelestialObjectCommand.Execute(catalogue.Get("andromeda"));
        viewModel.RestoreCelestialDefaultsCommand.Execute(null);
        Assert.Equal("D:\\DEM", viewModel.Settings.DemDirectory);
        Assert.Equal("UK", viewModel.Settings.Units);
        Assert.Equal(15, viewModel.Settings.TimeSnapMinutes);
        Assert.Equal(["sun", "moon"], viewModel.CelestialObjects.Select(item => item.TargetId));
    }

    [Theory]
    [InlineData(DeviceLocationAvailabilityState.Available, true)]
    [InlineData(DeviceLocationAvailabilityState.PermissionRequestable, true)]
    [InlineData(DeviceLocationAvailabilityState.PermissionPermanentlyDenied, false)]
    [InlineData(DeviceLocationAvailabilityState.Unsupported, false)]
    public async Task DeviceLocationControl_ReflectsAvailability(DeviceLocationAvailabilityState state, bool expected)
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter(),
            availability: new FakeAvailability(state));
        await viewModel.InitializeAsync();
        Assert.Equal(expected, viewModel.Locations.CanUseDeviceLocation);
        Assert.Equal(!expected, !string.IsNullOrWhiteSpace(viewModel.Locations.DeviceLocationUnavailableReason));
    }

    [Fact]
    public async Task DeviceLocationFailure_DoesNotChangePlanningLocationOrCrash()
    {
        var catalogue = new EmbeddedTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter(),
            availability: new FakeAvailability(DeviceLocationAvailabilityState.PermissionRequestable));
        await viewModel.InitializeAsync();
        var original = viewModel.Session.Observer;
        await viewModel.Locations.UseDeviceLocationCommand.ExecuteAsync(null);
        Assert.Equal(original, viewModel.Session.Observer);
        Assert.Contains("could not be resolved", viewModel.Locations.ResolutionMessage!, StringComparison.OrdinalIgnoreCase);
    }

    private static MainViewModel CreateViewModel(FakePlanning planning, ITargetCatalogue catalogue, IUserDataStore store, FakeExporter exporter,
        IPlannerDialogService? dialogs = null, IDeviceLocationAvailabilityService? availability = null,
        ILocationMapThumbnailService? thumbnails = null, IReverseGeocodingProvider? reverseGeocoding = null)
    {
        var locationSearch = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var resolver = new LocationResolver(new UnavailableDeviceLocationProvider(), NullLogger<LocationResolver>.Instance);
        return new MainViewModel(planning, catalogue, new TimeZoneResolver(), store, exporter,
            new DemDirectoryProvider(), NullLogger<MainViewModel>.Instance, new LensCalculator(),
            new CameraFramingGuideCalculator(), new FramingVisibilityCalculator(), SystemClock.Instance,
            locationSearch, resolver, availability ?? new UnavailableDeviceLocationProvider(),
            new CelestialSearchViewModel(new LocalTargetSearchService(catalogue), catalogue), dialogs ?? new FakeDialogs(),
            reverseGeocoding ?? new FakeReverseGeocodingProvider(), thumbnails);
    }

    private sealed class FakeLocationSearchProvider : ILocationSearchProvider
    {
        public string Attribution => "Test";
        public Task<IReadOnlyList<LocationSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocationSearchResult>>([]);
    }

    private sealed class FakeReverseGeocodingProvider : IReverseGeocodingProvider
    {
        public Task<ReverseGeocodingResult?> ResolveAsync(GeoCoordinate coordinate, CancellationToken cancellationToken) =>
            Task.FromResult<ReverseGeocodingResult?>(null);
    }

    private sealed class ImmediateReverseGeocodingProvider(string placeName) : IReverseGeocodingProvider
    {
        public int RequestCount { get; private set; }
        public Task<ReverseGeocodingResult?> ResolveAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult<ReverseGeocodingResult?>(new(placeName, null, "Test"));
        }
    }

    private sealed class ControllableReverseGeocodingProvider : IReverseGeocodingProvider
    {
        private readonly List<TaskCompletionSource<ReverseGeocodingResult?>> _requests = [];
        private readonly object _gate = new();
        public int RequestCount { get { lock (_gate) return _requests.Count; } }
        public Task<ReverseGeocodingResult?> ResolveAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<ReverseGeocodingResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _requests.Add(completion);
            return completion.Task; // Deliberately ignores cancellation to exercise generation protection.
        }
        public void Complete(int index, string placeName)
        {
            TaskCompletionSource<ReverseGeocodingResult?> completion;
            lock (_gate) completion = _requests[index];
            completion.SetResult(new ReverseGeocodingResult(placeName, null, "Test"));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert.True(predicate(), "Timed out waiting for asynchronous state change.");
    }

    private sealed class FakePlanning(ITargetCatalogue catalogue) : IPlanningService
    {
        public TargetPosition CalculateCurrent(PlanningSession session)
        {
            if (CalculateDelayMilliseconds > 0) Thread.Sleep(CalculateDelayMilliseconds);
            return Position(session);
        }
        public int ForcedRefreshes { get; private set; }
        public int SnapshotCalculations { get; private set; }
        public PlanningSession? LastCalculatedSession { get; private set; }
        public int CalculateDelayMilliseconds { get; set; }
        public bool FailRefresh { get; set; }
        public Task<PlanningSnapshot> CalculateSnapshotAsync(PlanningSession session, WeatherSettings weatherSettings, CancellationToken cancellationToken)
        {
            SnapshotCalculations++;
            LastCalculatedSession = session;
            var position = Position(session);
            var sample = new AstralPathSample(session.Instant, position.Horizontal);
            var path = new AstralPath(new LocalDate(2024, 1, 15), session.TimeZoneId, Duration.FromMinutes(10), [sample], position.Events, session.Instant);
            var terrain = new TerrainHorizonProfile(session.Observer, Enumerable.Range(0, 8).Select(x => new TerrainHorizonSample(x * 45, 0)).ToArray(), false, "Flat", session.Instant);
            return Task.FromResult(new PlanningSnapshot(session, position, path, new FieldOfView(60, 40, 70), terrain,
                new TerrainCrossings(null, null), ReadyWeather(session.Instant), new AstronomyContext(position, position)));
        }
        public Task<WeatherResult> RefreshWeatherAsync(PlanningSession session, WeatherSettings weatherSettings, CancellationToken cancellationToken)
        {
            ForcedRefreshes++;
            return Task.FromResult(FailRefresh
                ? new WeatherResult(DataState.Error, null, "Synthetic failure")
                : ReadyWeather(session.Instant));
        }
        private static WeatherResult ReadyWeather(Instant instant) => new(DataState.Ready,
            new WeatherConditions(instant, 10, 2, 3, 5, 0, 0, "None", 2, 180, 4, 12, 70, 7, 20, "Clear", instant), "Ready");
        private TargetPosition Position(PlanningSession session) => new(catalogue.Get(session.TargetId), session.Instant, new HorizontalCoordinate(180, 30), new TargetEvents(null, null, null));
    }

    private sealed class FakeStore(PersistedState state) : IUserDataStore
    {
        public PersistedState State { get; private set; } = state;
        public string StorageDirectory => "memory";
        public Task<PersistedState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task SaveAsync(PersistedState value, CancellationToken cancellationToken) { State = value; return Task.CompletedTask; }
    }

    private sealed class DelayedSearchProvider : ILocationSearchProvider
    {
        public int Started { get; private set; }
        public int Cancelled { get; private set; }
        public string Attribution => "Test";
        public async Task<IReadOnlyList<LocationSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Started++;
            try { await Task.Delay(250, cancellationToken); }
            catch (OperationCanceledException) { Cancelled++; throw; }
            return [new LocationSearchResult(query, query, new GeoCoordinate(1, 2), "Region", "Country", "UTC", Attribution)];
        }
    }

    private sealed class FakeExporter : IScoutingCardExporter
    {
        public PlanningSnapshot? LastSnapshot { get; private set; }
        public int RenderCount { get; private set; }
        public Task<byte[]> RenderPngAsync(PlanningSnapshot snapshot, ScoutingCardExportContext context, CancellationToken cancellationToken)
        {
            LastSnapshot = snapshot;
            RenderCount++;
            return Task.FromResult<byte[]>([137, 80, 78, 71]);
        }
    }

    private sealed class FakeDialogs : IPlannerDialogService
    {
        public LocationSearchResult? SearchResult { get; set; }
        public SavedLocationEdit? EditResult { get; set; }
        public bool ConfirmDeleteResult { get; set; } = true;
        public bool LastEditorWasCreateMode { get; private set; }
        public int RefreshThumbnailConfirmations { get; private set; }
        public int RefreshBuildingConfirmations { get; private set; }
        public Task<LocationSearchResult?> ShowLocationSearchAsync(CancellationToken cancellationToken = default) => Task.FromResult(SearchResult);
        public Task<SavedLocationEdit?> ShowSavedLocationEditAsync(SavedLocation location, bool isCreateMode = false,
            CancellationToken cancellationToken = default)
        {
            LastEditorWasCreateMode = isCreateMode;
            return Task.FromResult(EditResult);
        }
        public Task<bool> ConfirmDeleteSavedLocationAsync(SavedLocation location, CancellationToken cancellationToken = default) => Task.FromResult(ConfirmDeleteResult);
        public Task<bool> ConfirmRefreshSavedLocationThumbnailsAsync(int locationCount, CancellationToken cancellationToken = default)
        {
            RefreshThumbnailConfirmations++;
            return Task.FromResult(true);
        }
        public Task<bool> ConfirmRefreshSavedLocationBuildingCachesAsync(int locationCount,
            CancellationToken cancellationToken = default)
        {
            RefreshBuildingConfirmations++;
            return Task.FromResult(true);
        }
        public Task<string?> ChooseDemDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class FakeThumbnailService : ILocationMapThumbnailService
    {
        private readonly string _imagePath;
        public FakeThumbnailService()
        {
            StorageDirectory = Path.Combine(Path.GetTempPath(), "Noctaxis.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StorageDirectory);
            _imagePath = Path.Combine(StorageDirectory, "thumbnail.png");
            using var bitmap = new SKBitmap(8, 8);
            bitmap.Erase(SKColors.Navy);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(_imagePath, data.ToArray());
        }

        public string StorageDirectory { get; }
        public int ForcedRefreshes { get; private set; }
        public int StyleReapplications { get; private set; }
        public int BuildingRefreshes { get; private set; }

        public Task<SavedLocationThumbnailResult?> GetThumbnailAsync(SavedLocation location, bool forceRefresh,
            CancellationToken cancellationToken) => GetThumbnailAsync(location,
            forceRefresh ? SavedLocationMapRefreshMode.RefreshSource : SavedLocationMapRefreshMode.UseCache,
            cancellationToken);

        public Task<SavedLocationThumbnailResult?> GetThumbnailAsync(SavedLocation location,
            SavedLocationMapRefreshMode mode, CancellationToken cancellationToken)
        {
            if (mode == SavedLocationMapRefreshMode.UseCache)
                return Task.FromResult<SavedLocationThumbnailResult?>(null);
            if (mode == SavedLocationMapRefreshMode.RefreshSource) ForcedRefreshes++;
            else if (mode == SavedLocationMapRefreshMode.RefreshBuildings) BuildingRefreshes++;
            else StyleReapplications++;
            var metadata = new SavedLocationThumbnailMetadata(1, location.Id, location.Coordinate.Latitude,
                location.Coordinate.Longitude, 11, 512, 280, "test", "Test maps", "test-style", "test-source",
                "Test attribution", null, null, null, DateTimeOffset.UtcNow, 11,
                location.Coordinate.Latitude, location.Coordinate.Longitude, 620, 340, 2, "hash", "test-v1",
                Path.GetFileName(_imagePath));
            var semantic = new MapFeatureFetchOutcome(MapFeatureFetchStatus.Complete, 1, 0, 0,
                null, null, null, false, false, false, 1, DateTimeOffset.UtcNow);
            var buildings = new BuildingFeatureFetchOutcome(BuildingStarStatus.Complete, 12, null, null,
                null, false, false, 1, DateTimeOffset.UtcNow, false, 0, 1, 1, 0, 0, 1, 1);
            var operation = mode is SavedLocationMapRefreshMode.RefreshSource or SavedLocationMapRefreshMode.RefreshBuildings
                ? new SavedLocationMapRefreshResult(true,
                    mode == SavedLocationMapRefreshMode.RefreshBuildings,
                    mode == SavedLocationMapRefreshMode.RefreshBuildings
                        ? semantic with { Status = MapFeatureFetchStatus.CachedPrevious }
                        : semantic,
                    true, false, null, buildings)
                : null;
            return Task.FromResult<SavedLocationThumbnailResult?>(new SavedLocationThumbnailResult(
                _imagePath, metadata, true, true, false, operation));
        }
    }

    private sealed class FakeAvailability(DeviceLocationAvailabilityState state) : IDeviceLocationAvailabilityService
    {
        public Task<DeviceLocationAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceLocationAvailability(state, state is DeviceLocationAvailabilityState.Available or DeviceLocationAvailabilityState.PermissionRequestable ? null : "Unavailable"));
    }
}
