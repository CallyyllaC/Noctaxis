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
using Noctaxis.Core.Environment;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.ViewModels;
using NodaTime;
using SkiaSharp;
using System.Reflection;
using System.Xml.Linq;

namespace Noctaxis.Desktop.Tests;

public sealed class MainViewModelTests
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    private sealed class CoversSettingsInputAttribute(string bindingPath) : Attribute
    {
        public string BindingPath { get; } = bindingPath;
    }

    [Fact]
    public async Task LocationTimeAndTargetChanges_UpdateOneCoherentSession()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var initial = PlanningSession.Default(instant, "Europe/London");
        var store = new FakeStore(new PersistedState(1, new AppSettings(), [], initial, null));
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();

        viewModel.MoveObserver(new GeoCoordinate(55.9533, -3.1883, 75));
        viewModel.MinutesOfDay = 22 * 60 + 30;
        viewModel.AddCelestialObjectCommand.Execute(catalogue.Get("M31"));

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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.CommitObserverLocation(new GeoCoordinate(50, 1));
        var firstGeneration = viewModel.PlannerRefresh.Generation;
        viewModel.CommitObserverLocation(new GeoCoordinate(51, 2));
        Assert.True(viewModel.PlannerRefresh.Generation > firstGeneration);
        viewModel.CommitObserverLocation(new GeoCoordinate(52, 3));
        Assert.Equal(new GeoCoordinate(52, 3), viewModel.Observer);
        await Task.Delay(200);
        Assert.Equal(1, planning.SnapshotCalculations);
        Assert.Equal(new GeoCoordinate(52, 3), planning.LastCalculatedSession!.Observer);
    }

    [Fact]
    public async Task ImmediatelyAvailableEnrichmentCompletesWithoutArtificialPhaseDelay()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();

        viewModel.ShowPlannerCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsPlannerRefreshing && viewModel.Snapshot is not null,
            TimeSpan.FromSeconds(1));

        Assert.Equal(1, viewModel.PlannerRefresh.Progress);
        Assert.Equal(1, planning.SnapshotCalculations);
        Assert.Equal(1, planning.EnvironmentRequests);
    }

    [Fact]
    public async Task ObserverRefresh_CommitsCoreBeforeLateEnvironmentAndWeather()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new StagedPlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();
        var observer = new GeoCoordinate(53.00737, -3.94847, 200);

        viewModel.CommitObserverLocation(observer);

        Assert.Equal(observer, viewModel.Observer);
        Assert.True(viewModel.IsPlannerRefreshing);
        Assert.False(viewModel.CelestialOverlaysReady);
        await WaitUntilAsync(() => planning.CoreRequestCount == 1 && planning.EnvironmentRequestCount == 1 &&
                                   planning.WeatherRequestCount == 1, TimeSpan.FromSeconds(2));
        planning.CompleteCore(0);
        await WaitUntilAsync(() => viewModel.CameraOverlayReady, TimeSpan.FromSeconds(1));

        Assert.True(viewModel.CelestialOverlaysReady);
        Assert.True(viewModel.PlannerRefresh.IsCoreReady);
        await WaitUntilAsync(() => viewModel.PlannerRefresh.CameraTerrainState == PlannerRefreshWorkState.Ready,
            TimeSpan.FromSeconds(1));
        Assert.False(viewModel.Snapshot!.Terrain.IsComplete);
        Assert.True(viewModel.IsPlannerRefreshing);
        Assert.Equal(PlannerPinActivity.EnvironmentLoading, viewModel.PlannerPinActivity);
        Assert.Null(viewModel.Snapshot!.Environment);

        planning.CompleteEnvironment(0, hasTerrain: true);
        await WaitUntilAsync(() => viewModel.Snapshot?.Environment is not null, TimeSpan.FromSeconds(1));
        Assert.True(viewModel.IsPlannerRefreshing);
        planning.CompleteWeather(0, DataState.Ready);
        await WaitUntilAsync(() => !viewModel.IsPlannerRefreshing, TimeSpan.FromSeconds(1));

        Assert.Contains(viewModel.PlannerRefresh.Phase,
            new[] { PlannerRefreshPhase.Ready, PlannerRefreshPhase.Partial });
        Assert.Equal(1, viewModel.PlannerRefresh.Progress);
        Assert.Equal(PlannerPinActivity.None, viewModel.PlannerPinActivity);
    }

    [Fact]
    public async Task StaleEnvironmentCannotEnrichNewerObserver()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new StagedPlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();
        var oldObserver = new GeoCoordinate(50, -1);
        var currentObserver = new GeoCoordinate(52, -3);

        viewModel.CommitObserverLocation(oldObserver);
        await WaitUntilAsync(() => planning.CoreRequestCount == 1, TimeSpan.FromSeconds(2));
        planning.CompleteCore(0);
        viewModel.CommitObserverLocation(currentObserver);
        await WaitUntilAsync(() => planning.CoreRequestCount == 2, TimeSpan.FromSeconds(2));
        planning.CompleteCore(1);
        planning.CompleteEnvironment(1, hasTerrain: true);
        planning.CompleteWeather(1, DataState.Ready);
        await WaitUntilAsync(() => !viewModel.IsPlannerRefreshing, TimeSpan.FromSeconds(1));

        planning.CompleteEnvironment(0, hasTerrain: false);
        planning.CompleteWeather(0, DataState.Ready);
        await Task.Delay(60);

        Assert.Equal(currentObserver, viewModel.Snapshot!.Session.Observer);
        Assert.Equal(currentObserver, viewModel.Snapshot.Environment!.ObserverCoordinate);
        Assert.True(viewModel.Snapshot.Terrain.HasTerrainCoverage);
    }

    [Fact]
    public async Task OptionalProviderFailuresResolveAsPartialInsteadOfLoadingForever()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new StagedPlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();

        viewModel.CommitObserverLocation(new GeoCoordinate(51, -2));
        await WaitUntilAsync(() => planning.CoreRequestCount == 1, TimeSpan.FromSeconds(2));
        planning.CompleteCore(0);
        planning.FailEnvironment(0);
        planning.CompleteWeather(0, DataState.Error);
        await WaitUntilAsync(() => !viewModel.IsPlannerRefreshing, TimeSpan.FromSeconds(1));

        Assert.Equal(PlannerRefreshPhase.Partial, viewModel.PlannerRefresh.Phase);
        Assert.Equal(PlannerRefreshWorkState.Error, viewModel.PlannerRefresh.GroundTerrainState);
        Assert.Equal(PlannerRefreshWorkState.Error, viewModel.PlannerRefresh.WeatherState);
        Assert.Equal(1, viewModel.PlannerRefresh.Progress);
        Assert.True(viewModel.CameraOverlayReady);
    }

    [Fact]
    public async Task CurrentTargetDetailsUseSharedLocalHorizonVisibility()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new StagedPlanning(catalogue) { PositionAltitudeDegrees = 4.3 };
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();

        viewModel.CommitObserverLocation(new GeoCoordinate(53, -3));
        await WaitUntilAsync(() => planning.CoreRequestCount == 1, TimeSpan.FromSeconds(2));
        planning.CompleteCore(0);
        planning.CompleteEnvironment(0, hasTerrain: true);
        planning.CompleteWeather(0, DataState.Ready);
        await WaitUntilAsync(() => !viewModel.IsPlannerRefreshing, TimeSpan.FromSeconds(1));

        Assert.Equal("Terrain blocked", viewModel.HorizonStatus);
        Assert.True(viewModel.HasTargetLocalHorizonDetails);
        Assert.Equal("+5.0°", viewModel.TargetLocalHorizonText);
        Assert.Equal("Blocked by", viewModel.TargetTerrainMarginLabel);
        Assert.Equal("0.7°", viewModel.TargetTerrainMarginText);
    }

    [Fact]
    public async Task DateTimeRefreshReusesStaticEnvironmentAndCameraSettingsDoNotReloadIt()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var equipment = new EquipmentSettings(
            [new CameraProfile("camera", "Full Frame", 36, 24)],
            [new LensProfile("lens", "24-200 mm", 24, 200)]);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(Equipment: equipment), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.ShowPlannerCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsPlannerRefreshing && viewModel.Snapshot is not null,
            TimeSpan.FromSeconds(2));
        var environmentRequests = planning.EnvironmentRequests;

        viewModel.LocalDate = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);
        await WaitUntilAsync(() => !viewModel.IsPlannerRefreshing, TimeSpan.FromSeconds(2));
        viewModel.FocalLength = 85;
        await Task.Delay(80);

        Assert.Equal(environmentRequests, planning.EnvironmentRequests);
        Assert.Equal(85, viewModel.Session.Lens.FocalLengthMillimetres);
    }

    [Fact]
    public async Task StalePlanningResultCannotReplaceNewerObserverWhenProviderIgnoresCancellation()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new ControllablePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();
        var oldObserver = new GeoCoordinate(50, 1);
        var currentObserver = new GeoCoordinate(52, 3);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.CommitObserverLocation(oldObserver);
        await WaitUntilAsync(() => planning.RequestCount == 1, TimeSpan.FromSeconds(2));
        var oldRefresh = viewModel.WaitForPlannerRefreshAsync();
        var oldGeneration = viewModel.TerrainDebugGeneration;
        viewModel.CommitObserverLocation(currentObserver);
        Assert.Contains(nameof(MainViewModel.TerrainDebugProfile), changedProperties);
        Assert.Contains(nameof(MainViewModel.TerrainDebugText), changedProperties);
        Assert.Contains(nameof(MainViewModel.CameraFramingVisibility), changedProperties);
        Assert.Null(viewModel.TerrainDebugProfile);
        Assert.Null(viewModel.CameraFramingVisibility);
        Assert.Contains($"Latitude: {currentObserver.Latitude:F6}", viewModel.TerrainDebugText);
        Assert.Contains("State: Resolving", viewModel.TerrainDebugText);
        Assert.True(viewModel.TerrainDebugGeneration > oldGeneration);
        await WaitUntilAsync(() => planning.RequestCount == 2, TimeSpan.FromSeconds(2));
        planning.Complete(1);
        await WaitUntilAsync(() => viewModel.Snapshot?.Session.Observer == currentObserver,
            TimeSpan.FromSeconds(1));
        Assert.Equal(currentObserver, viewModel.TerrainDebugProfile!.Observer);
        planning.Complete(0);
        await oldRefresh;

        Assert.Equal(currentObserver, viewModel.Snapshot!.Session.Observer);
        Assert.Equal(currentObserver, viewModel.Observer);
        Assert.Equal(currentObserver, viewModel.TerrainDebugProfile!.Observer);
        Assert.Contains($"Latitude: {currentObserver.Latitude:F6}", viewModel.TerrainDebugText);
    }

    [Fact]
    public async Task RepeatedObserverMovementOnlyExposesFinalDebugProfile()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new ControllablePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();
        var observers = new[]
        {
            new GeoCoordinate(50, 1), new GeoCoordinate(51, 2),
            new GeoCoordinate(52, 3), new GeoCoordinate(53, 4)
        };

        var obsoleteRefreshes = new List<Task>();
        foreach (var observer in observers)
        {
            viewModel.CommitObserverLocation(observer);
            await WaitUntilAsync(() => planning.RequestCount == Array.IndexOf(observers, observer) + 1,
                TimeSpan.FromSeconds(2));
            Assert.Null(viewModel.TerrainDebugProfile);
            if (observer != observers[^1]) obsoleteRefreshes.Add(viewModel.WaitForPlannerRefreshAsync());
        }

        planning.Complete(3);
        await WaitUntilAsync(() => viewModel.TerrainDebugProfile?.Observer == observers[3],
            TimeSpan.FromSeconds(1));
        planning.Complete(2);
        planning.Complete(0);
        planning.Complete(1);
        await Task.WhenAll(obsoleteRefreshes);

        Assert.Equal(observers[3], viewModel.Observer);
        Assert.Equal(observers[3], viewModel.TerrainDebugProfile!.Observer);
        Assert.Equal(observers[3], viewModel.Snapshot!.Terrain.Observer);
    }

    [Fact]
    public async Task PinMovement_ImmediatelyInvalidatesOldNameThenResolvesCurrentLocality()
    {
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
    [CoversSettingsInput("IsVisible")]
    public async Task CelestialList_PersistsVisibilityPrimaryAndPreventsDuplicates()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();
        var andromeda = catalogue.Get("M31");
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
        var dialogs = new FakeDialogs { EditResult = new SavedLocationEdit("New ridge", null) };
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(3, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), dialogs, thumbnails: new FakeThumbnailService());
        await viewModel.InitializeAsync();

        Assert.False(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.False(viewModel.RefreshSavedLocationSettlementCachesCommand.CanExecute(null));
        Assert.False(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));

        await viewModel.SaveCurrentAsNewLocationCommand.ExecuteAsync(null);

        Assert.True(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.True(viewModel.RefreshSavedLocationSettlementCachesCommand.CanExecute(null));
        Assert.True(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));

        await Assert.Single(viewModel.Locations.Saved).DeleteCommand.ExecuteAsync(null);

        Assert.False(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.False(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));
    }

    [Fact]
    public async Task PersistedLocations_EnableMapImageCommandsAfterInitialLoad()
    {
        var catalogue = new OpenNgcTargetCatalogue();
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
    [CoversSettingsInput("RefreshSavedLocationThumbnailsCommand")]
    public async Task RefreshSavedLocationThumbnails_ConfirmsAndForcesEachLocationSequentially()
    {
        var catalogue = new OpenNgcTargetCatalogue();
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
        Assert.Contains("WSF settlement layers: 2 complete", viewModel.LocationThumbnailRefreshStatus);
        Assert.True(viewModel.RefreshSavedLocationThumbnailsCommand.CanExecute(null));
        Assert.True(viewModel.ReapplySavedLocationMapStylesCommand.CanExecute(null));
    }

    [Fact]
    [CoversSettingsInput("RefreshSavedLocationSettlementCachesCommand")]
    public async Task RefreshSettlementCaches_ConfirmsAndDoesNotRequestRasterOrCoreRefresh()
    {
        var catalogue = new OpenNgcTargetCatalogue();
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

        await viewModel.RefreshSavedLocationSettlementCachesCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.RefreshSettlementConfirmations);
        Assert.Equal(2, thumbnails.SettlementRefreshes);
        Assert.Equal(0, thumbnails.ForcedRefreshes);
        Assert.Contains("Road and water overlays: 0 complete, 2 cached", viewModel.LocationThumbnailRefreshStatus);
        Assert.Contains("WSF settlement layers: 2 complete", viewModel.LocationThumbnailRefreshStatus);
    }

    [Fact]
    [CoversSettingsInput("ReapplySavedLocationMapStylesCommand")]
    public async Task ReapplySavedLocationMapStyles_UsesSavedSourcesWithoutRefreshConfirmation()
    {
        var catalogue = new OpenNgcTargetCatalogue();
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
    [CoversSettingsInput("SettingsCelestialSearch.Query")]
    [CoversSettingsInput("SettingsCelestialSearch.SelectedObjectType")]
    [CoversSettingsInput("SettingsCelestialSearch.SelectedConstellation")]
    [CoversSettingsInput("SettingsCelestialSearch.SelectedDesignationFamily")]
    [CoversSettingsInput("SettingsCelestialSearch.ResetFiltersCommand")]
    public void CelestialSearch_ResetFiltersPreservesSearchText()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var search = new CelestialSearchViewModel(new LocalTargetSearchService(catalogue), catalogue)
        {
            Query = "M31",
            SelectedObjectType = new ObjectTypeOption("Galaxy", AstralTargetCategory.Galaxy),
            SelectedConstellation = "Andromeda",
            SelectedDesignationFamily = "Messier"
        };

        search.ResetFiltersCommand.Execute(null);

        Assert.Equal("M31", search.Query);
        Assert.Null(search.SelectedObjectType.Value);
        Assert.Equal("All constellations", search.SelectedConstellation);
        Assert.Equal("All designations", search.SelectedDesignationFamily);
        Assert.False(search.HasActiveFilters);
    }

    [Fact]
    public async Task SettingsCelestialSearch_DoesNotMutateSelectedTargetsUntilAdd()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();

        var selectedTargetIds = viewModel.CelestialObjects.Select(item => item.TargetId).ToArray();
        viewModel.SettingsCelestialSearch.Query = "B33";
        viewModel.SettingsCelestialSearch.SelectedDesignationFamily = "Barnard";

        Assert.Equal("B33", viewModel.SettingsCelestialSearch.Query);
        Assert.Equal("Barnard", viewModel.SettingsCelestialSearch.SelectedDesignationFamily);
        Assert.Equal(selectedTargetIds, viewModel.CelestialObjects.Select(item => item.TargetId));
    }

    [Fact]
    public async Task TerrainGroundManualOverrideCameraHeightAndReset_FlowThroughPlannerState()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue) { GroundElevationMetres = 100 };
        var settings = new AppSettings(CameraHeightAboveGroundMetres: 2);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, settings, [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();

        viewModel.ShowPlannerCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Snapshot?.Terrain.ObserverAbsoluteElevationMetres == 102,
            TimeSpan.FromSeconds(2));
        Assert.Equal(100, viewModel.Elevation);
        Assert.False(viewModel.IsElevationManualOverride);
        Assert.Equal(2, planning.LastCameraHeightAboveGroundMetres);

        viewModel.Elevation = 250;
        planning.GroundElevationMetres = 120;
        await viewModel.ApplySettingsAsync(viewModel.Settings);
        Assert.Equal(250, viewModel.Elevation);
        Assert.True(viewModel.IsElevationManualOverride);
        Assert.Equal(252, viewModel.Snapshot!.Terrain.ObserverAbsoluteElevationMetres);

        viewModel.ResetGroundElevationCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsElevationManualOverride && viewModel.Elevation == 120 &&
                                   viewModel.Snapshot?.Terrain.ObserverAbsoluteElevationMetres == 122,
            TimeSpan.FromSeconds(2));
        Assert.Equal(122, viewModel.Snapshot!.Terrain.ObserverAbsoluteElevationMetres);
    }

    [Fact]
    public async Task ResolvedOceanSurfaceIsCommittedInsteadOfRawMapzenBathymetry()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue)
        {
            GroundElevationMetres = -3_685,
            ChosenGroundElevationMetres = 0
        };
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());

        await viewModel.InitializeAsync();
        viewModel.ShowPlannerCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Snapshot?.Terrain.ChosenObserverGroundElevationMetres == 0,
            TimeSpan.FromSeconds(2));

        Assert.Equal(0, viewModel.Elevation);
        Assert.Equal(0, viewModel.Session.Observer.ElevationMetres);
        Assert.NotEqual(-500, viewModel.Session.Observer.ElevationMetres);
    }

    [Fact]
    public async Task EquipmentSelection_ClampsZoomAndFixesPrimeFocalLength()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var equipment = new EquipmentSettings(
            [new CameraProfile("camera", "Full Frame", 36, 24)],
            [new LensProfile("prime", "24 mm", 24, 24),
             new LensProfile("zoom", "70-200 mm", 70, 200)]);
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(Equipment: equipment), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsFocalLengthEditable);
        Assert.Equal(24, viewModel.FocalLength);

        viewModel.SelectedLens = viewModel.Lenses.Single(item => item.Id == "zoom");
        Assert.True(viewModel.IsFocalLengthEditable);
        Assert.Equal(70, viewModel.FocalLength);
        viewModel.FocalLength = 300;
        Assert.Equal(200, viewModel.FocalLength);

        viewModel.SelectedLens = viewModel.Lenses.Single(item => item.Id == "prime");
        Assert.False(viewModel.IsFocalLengthEditable);
        Assert.Equal(24, viewModel.FocalLength);
    }

    [Fact]
    public async Task ConfiguredCelestialTargets_RecoverUniquelyAndDiscardUnresolvedEntries()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var configured = new CelestialObjectSettings(
            [new("HIP 17465", true, 0), new("not-a-real-target", true, 1)], "HIP 17465");
        var settings = new AppSettings(CelestialObjects: configured);
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(1, settings, [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());

        await viewModel.InitializeAsync();

        Assert.Contains(viewModel.CelestialObjects, item => item.TargetId == "openngc:IC0348");
        Assert.DoesNotContain(viewModel.CelestialObjects, item => item.TargetId == "not-a-real-target");
        Assert.Equal("openngc:IC0348", viewModel.Session.TargetId);
        Assert.DoesNotContain(viewModel.Settings.EffectiveCelestialObjects.EffectiveConfiguredObjects,
            item => item.TargetId == "not-a-real-target");
    }

    [Fact]
    [CoversSettingsInput("IsEnabled")]
    public async Task WeatherVisualGrouping_PreservesEnabledWeatherValuesAndPersistedKeys()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        WeatherField[] enabled = [WeatherField.TotalCloudCover, WeatherField.Moonrise];
        var store = new FakeStore(new PersistedState(1,
            new AppSettings(Weather: new WeatherSettings(enabled, 7.5)), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();

        Assert.Equal(["Weather", "Sun & darkness", "Moon"], viewModel.WeatherFieldGroups.Select(group => group.Label));
        Assert.Equal(Enum.GetValues<WeatherField>(), viewModel.WeatherFieldGroups.SelectMany(group => group.Fields).Select(item => item.Field));
        Assert.Equal(enabled, viewModel.WeatherFieldOptions.Where(item => item.IsEnabled).Select(item => item.Field));

        var changed = Enum.GetValues<WeatherField>().Where((_, index) => index % 2 == 0).ToArray();
        foreach (var option in viewModel.WeatherFieldOptions)
            option.IsEnabled = changed.Contains(option.Field);

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(changed, store.State.Settings.EffectiveWeather.EffectiveFields);
        Assert.Equal(7.5, store.State.Settings.EffectiveWeather.CacheDistanceKilometres);

        foreach (var option in viewModel.WeatherFieldOptions)
            option.IsEnabled = !option.IsEnabled;
        viewModel.ResetSettingsEditorCommand.Execute(null);
        Assert.Equal(changed, viewModel.WeatherFieldOptions.Where(item => item.IsEnabled).Select(item => item.Field));
    }

    [Fact]
    [CoversSettingsInput("SettingsUnits")]
    [CoversSettingsInput("SettingsTimeZoneId")]
    [CoversSettingsInput("SettingsTimeSnapMinutes")]
    [CoversSettingsInput("SettingsCameraFramingOverlayVisible")]
    [CoversSettingsInput("SettingsShowFramingVisibilityLimits")]
    [CoversSettingsInput("SettingsFramingShadingOpacityPercent")]
    [CoversSettingsInput("SettingsFramingLineThickness")]
    [CoversSettingsInput("SettingsTerrainCastAngularDetailDegrees")]
    [CoversSettingsInput("SettingsTerrainDebugOverlay")]
    [CoversSettingsInput("SettingsWeatherCacheDistance")]
    [CoversSettingsInput("ResetSettingsEditorCommand")]
    [CoversSettingsInput("SaveSettingsCommand")]
    public async Task ScalarSettings_LoadResetSaveAndPreviewEveryValue()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var initialFraming = new CameraFramingSettings(
            IsOverlayVisible: false,
            ShowVisibilityLimits: false,
            ShadingOpacityPercent: 22,
            LineThickness: .5,
            TerrainCastAngularDetailDegrees: 7);
        var store = new FakeStore(new PersistedState(1,
            new AppSettings(
                Units: "UK",
                SelectedTimeZoneId: "UTC",
                Weather: new WeatherSettings(CacheDistanceKilometres: 7.5),
                TimeSnapMinutes: 15,
                CameraFraming: initialFraming), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();

        AssertScalarSettingsEditors(viewModel, "UK", "UTC", 15, 7.5, false, false, 22, .5);
        Assert.Equal(7, viewModel.SettingsTerrainCastAngularDetailDegrees);
        Assert.False(viewModel.SettingsTerrainDebugOverlay);

        SetScalarSettingsEditors(viewModel, "Imperial", "Europe/London", 27, 12.5, true, true, 37, 5);
        viewModel.SettingsTerrainCastAngularDetailDegrees = 25;
        viewModel.SettingsTerrainDebugOverlay = true;
        Assert.Equal(5, viewModel.CameraFramingMapSettings.LineThickness);

        viewModel.ResetSettingsEditorCommand.Execute(null);
        AssertScalarSettingsEditors(viewModel, "UK", "UTC", 15, 7.5, false, false, 22, .5);
        Assert.Equal(7, viewModel.SettingsTerrainCastAngularDetailDegrees);
        Assert.False(viewModel.SettingsTerrainDebugOverlay);
        Assert.Equal(.5, viewModel.CameraFramingMapSettings.LineThickness);

        SetScalarSettingsEditors(viewModel, "Imperial", "Europe/London", 27, 12.5, true, true, 37, 5);
        viewModel.SettingsTerrainCastAngularDetailDegrees = 25;
        viewModel.SettingsTerrainDebugOverlay = true;
        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(5, viewModel.CameraFramingMapSettings.LineThickness);
        Assert.Equal("Imperial", store.State.Settings.Units);
        Assert.Equal("Europe/London", store.State.Settings.SelectedTimeZoneId);
        Assert.Equal(27, store.State.Settings.TimeSnapMinutes);
        Assert.Equal(12.5, store.State.Settings.EffectiveWeather.CacheDistanceKilometres);
        Assert.True(store.State.Settings.EffectiveCameraFraming.IsOverlayVisible);
        Assert.True(store.State.Settings.EffectiveCameraFraming.ShowVisibilityLimits);
        Assert.Equal(37, store.State.Settings.EffectiveCameraFraming.ShadingOpacityPercent);
        Assert.Equal(5, store.State.Settings.EffectiveCameraFraming.LineThickness);
        Assert.Equal(25, store.State.Settings.EffectiveCameraFraming.TerrainCastAngularDetailDegrees);
        Assert.True(store.State.Settings.TerrainDebugOverlay);
    }

    [Fact]
    [CoversSettingsInput("DataContext.AddSettingsCelestialObjectCommand")]
    [CoversSettingsInput("MoveUpCommand")]
    [CoversSettingsInput("MoveDownCommand")]
    [CoversSettingsInput("MakePrimaryCommand")]
    [CoversSettingsInput("RemoveCommand")]
    public async Task CelestialSettingsCommands_AddReorderMakePrimaryAndRemove()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.SettingsCelestialSearch.Query = "M31";

        viewModel.AddSettingsCelestialObjectCommand.Execute(catalogue.Get("M31"));

        var andromeda = viewModel.CelestialObjects.Single(item => item.TargetId == "openngc:NGC0224");
        Assert.Equal(string.Empty, viewModel.SettingsCelestialSearch.Query);
        Assert.True(andromeda.IsPrimary);
        Assert.Equal(2, viewModel.CelestialObjects.IndexOf(andromeda));

        andromeda.MoveUpCommand.Execute(null);
        Assert.Equal(1, viewModel.CelestialObjects.IndexOf(andromeda));
        andromeda.MoveDownCommand.Execute(null);
        Assert.Equal(2, viewModel.CelestialObjects.IndexOf(andromeda));

        var moon = viewModel.CelestialObjects.Single(item => item.TargetId == "moon");
        moon.MakePrimaryCommand.Execute(null);
        Assert.True(moon.IsPrimary);
        Assert.Equal("moon", viewModel.Settings.EffectiveCelestialObjects.DefaultPrimaryTargetId);

        andromeda.RemoveCommand.Execute(null);
        Assert.DoesNotContain(viewModel.CelestialObjects, item => item.TargetId == andromeda.TargetId);
        Assert.DoesNotContain(viewModel.Settings.EffectiveCelestialObjects.EffectiveConfiguredObjects,
            item => item.TargetId == andromeda.TargetId);
    }

    [Fact]
    [CoversSettingsInput("SettingsCameraHeightAboveGroundMetres")]
    [CoversSettingsInput("AddCameraProfileCommand")]
    [CoversSettingsInput("AddLensProfileCommand")]
    [CoversSettingsInput("DisplayName")]
    [CoversSettingsInput("SensorWidthMillimetres")]
    [CoversSettingsInput("SensorHeightMillimetres")]
    [CoversSettingsInput("MinimumFocalLengthMillimetres")]
    [CoversSettingsInput("MaximumFocalLengthMillimetres")]
    [CoversSettingsInput("Manufacturer")]
    [CoversSettingsInput("Model")]
    public async Task EquipmentSettings_AddValidateAndPersistProfilesAndCameraHeight()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store,
            new FakeExporter());
        await viewModel.InitializeAsync();

        viewModel.AddCameraProfileCommand.Execute(null);
        var camera = viewModel.EquipmentCameraEditors[^1];
        camera.DisplayName = "Astro camera";
        camera.SensorWidthMillimetres = 23.5;
        camera.SensorHeightMillimetres = 15.6;
        camera.Manufacturer = "Test";
        camera.Model = "A1";
        viewModel.AddLensProfileCommand.Execute(null);
        var lens = viewModel.EquipmentLensEditors[^1];
        lens.DisplayName = "70-200 mm";
        lens.MinimumFocalLengthMillimetres = 70;
        lens.MaximumFocalLengthMillimetres = 200;
        viewModel.SettingsCameraHeightAboveGroundMetres = 1.6;

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1.6, store.State.Settings.EffectiveCameraHeightAboveGroundMetres);
        Assert.Contains(store.State.Settings.Equipment!.Cameras!, item => item.DisplayName == "Astro camera");
        Assert.Contains(store.State.Settings.Equipment!.Lenses!, item => item.DisplayName == "70-200 mm");
    }

    [Fact]
    public void PlannerSidebarAndSettings_HaveTheRequestedSingleOwnershipStructure()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Noctaxis.Desktop", "Views", "MainWindow.axaml"));
        var document = XDocument.Load(sourcePath);
        var planner = document.Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Planner");
        var expanders = planner.Descendants().Where(element => element.Name.LocalName == "Expander")
            .Select(element => element.Attribute("Header")?.Value).OfType<string>().ToArray();
        Assert.Equal(["Planning location", "Selected targets", "Current target details",
            "Camera and framing", "Weather conditions", "Terrain horizon"], expanders);

        var planningLocation = planner.Descendants().Single(element =>
            element.Name.LocalName == "Expander" && element.Attribute("Header")?.Value == "Planning location");
        Assert.DoesNotContain("Time zone", planningLocation.ToString(), StringComparison.Ordinal);
        var selectedTargets = planner.Descendants().Single(element =>
            element.Name.LocalName == "Expander" && element.Attribute("Header")?.Value == "Selected targets");
        Assert.DoesNotContain("PlannerCelestialSearch", selectedTargets.ToString(), StringComparison.Ordinal);
        var camera = planner.Descendants().Single(element =>
            element.Name.LocalName == "Expander" && element.Attribute("Header")?.Value == "Camera and framing");
        var cameraMarkup = camera.ToString();
        Assert.DoesNotContain("Advanced sensor dimensions", cameraMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("Show camera framing on map", cameraMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("FocalLengthPresets", cameraMarkup, StringComparison.Ordinal);
        var focalLengthSlider = Assert.Single(camera.Descendants(), element =>
            element.Name.LocalName == "Slider" &&
            element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "FocalLengthSlider");
        Assert.Equal("{Binding FocalLength}", focalLengthSlider.Attribute("Value")?.Value);
        Assert.Equal("{Binding FocalLengthMinimum}", focalLengthSlider.Attribute("Minimum")?.Value);
        Assert.Equal("{Binding FocalLengthMaximum}", focalLengthSlider.Attribute("Maximum")?.Value);
        Assert.Equal("{Binding IsFocalLengthEditable}", focalLengthSlider.Attribute("IsEnabled")?.Value);
        Assert.DoesNotContain(camera.Descendants(), element => element.Name.LocalName == "NumericUpDown");
        Assert.DoesNotContain(camera.Descendants(), element => element.Name.LocalName == "ComboBox" &&
            element.Attribute("SelectedItem")?.Value == "{Binding SelectedOrientation}");
        var orientationButtons = camera.Descendants().Where(element => element.Name.LocalName == "RadioButton")
            .ToArray();
        Assert.Equal(2, orientationButtons.Length);
        Assert.Equal(["Landscape", "Portrait"], orientationButtons.Select(element =>
            element.Attribute("Content")?.Value).OfType<string>().ToArray());
        Assert.All(orientationButtons, element => Assert.Equal("CameraOrientation",
            element.Attribute("GroupName")?.Value));
        Assert.Equal(["{Binding IsLandscapeOrientation}", "{Binding IsPortraitOrientation}"],
            orientationButtons.Select(element => element.Attribute("IsChecked")?.Value)
                .OfType<string>().ToArray());
        Assert.Contains("FieldOfViewText", cameraMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("Value=\"{Binding FieldOfView", cameraMarkup, StringComparison.Ordinal);

        var settings = document.Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Settings");
        var equipment = settings.Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Equipment");
        Assert.Contains("SettingsCameraHeightAboveGroundMetres", equipment.ToString(), StringComparison.Ordinal);
        Assert.Contains("EquipmentCameraEditors", equipment.ToString(), StringComparison.Ordinal);
        Assert.Contains("EquipmentLensEditors", equipment.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrientationRadioProperties_SelectAndReportTheSessionOrientation()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsLandscapeOrientation);
        Assert.False(viewModel.IsPortraitOrientation);

        viewModel.IsPortraitOrientation = true;

        Assert.Equal(CameraOrientation.Portrait, viewModel.SelectedOrientation);
        Assert.Equal(CameraOrientation.Portrait, viewModel.Session.Lens.Orientation);
        Assert.False(viewModel.IsLandscapeOrientation);
        Assert.True(viewModel.IsPortraitOrientation);

        viewModel.IsLandscapeOrientation = true;

        Assert.Equal(CameraOrientation.Landscape, viewModel.SelectedOrientation);
        Assert.Equal(CameraOrientation.Landscape, viewModel.Session.Lens.Orientation);
    }

    [Fact]
    public void GeneralSettings_ContainsOneDisabledKofiPlaceholderWithoutBehavior()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Noctaxis.Desktop", "Views", "MainWindow.axaml"));
        var document = XDocument.Load(sourcePath);
        var settings = document.Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Settings");
        var general = settings.Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "General");

        var supportButton = Assert.Single(general.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Content")?.Value == "Support on Ko-fi");
        Assert.Equal("False", supportButton.Attribute("IsEnabled")?.Value);
        Assert.Null(supportButton.Attribute("Command"));
        Assert.Null(supportButton.Attribute("CommandParameter"));
        Assert.Null(supportButton.Attribute("Click"));
        Assert.Null(supportButton.Attribute("Classes"));

        Assert.Contains(general.Descendants(), element =>
            element.Name.LocalName == "TextBlock" && element.Attribute("Text")?.Value == "Support");
        Assert.Contains(general.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            element.Attribute("Text")?.Value == "Supporter features coming later.");

        var generalInputs = general.Descendants()
            .Where(element => element.Name.LocalName is "TextBox" or "ComboBox" or "NumericUpDown" or "CheckBox" or "Button")
            .ToArray();
        Assert.Equal(["ComboBox", "Button"], generalInputs.Select(element => element.Name.LocalName));
        Assert.Equal("{Binding UnitsOptions}", generalInputs[0].Attribute("ItemsSource")?.Value);
        Assert.Equal("{Binding SettingsUnits}", generalInputs[0].Attribute("SelectedItem")?.Value);
    }

    [Fact]
    public void EverySettingsInput_HasDeclaredBehavioralTestCoverage()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Noctaxis.Desktop", "Views", "MainWindow.axaml"));
        var document = XDocument.Load(sourcePath);
        var settingsTab = document.Descendants().Single(element =>
            element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Settings");
        var bindingAttributes = new Dictionary<string, string>
        {
            ["TextBox"] = "Text",
            ["ComboBox"] = "SelectedItem",
            ["NumericUpDown"] = "Value",
            ["CheckBox"] = "IsChecked",
            ["Button"] = "Command"
        };
        var settingsInputs = settingsTab.Descendants()
            .Where(element => bindingAttributes.ContainsKey(element.Name.LocalName))
            .Select(element => element.Attributes().SingleOrDefault(attribute =>
                attribute.Name.LocalName == bindingAttributes[element.Name.LocalName])?.Value)
            .Where(value => value is not null)
            .Select(value => ParseBindingPath(value!))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var coveredInputs = typeof(MainViewModelTests).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<CoversSettingsInputAttribute>())
            .Select(attribute => attribute.BindingPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(settingsInputs, coveredInputs);
    }

    [Fact]
    public async Task UnsavedCustomLocation_IsCreatedOnlyAfterExplicitSave()
    {
        var catalogue = new OpenNgcTargetCatalogue();
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
    public async Task ZoomLens_UpdatesFieldOfViewWithoutFullPlanningRefresh()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var equipment = new EquipmentSettings(
            [new CameraProfile("camera", "Full Frame", 36, 24),
             new CameraProfile("aps-c", "APS-C", 23.6, 15.7)],
            [new LensProfile("lens", "24-70 mm", 24, 70)]);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(Equipment: equipment), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync(); viewModel.ShowPlannerCommand.Execute(null); await Task.Delay(100);
        var calculations = planning.SnapshotCalculations;
        var oldFov = viewModel.Snapshot!.FieldOfView.HorizontalDegrees;
        viewModel.FocalLength = 50;
        Assert.Equal(calculations, planning.SnapshotCalculations);
        Assert.NotEqual(oldFov, viewModel.Snapshot.FieldOfView.HorizontalDegrees);
        var fullFrameFov = viewModel.Snapshot.FieldOfView.HorizontalDegrees;
        viewModel.SelectedCamera = viewModel.Cameras.Single(item => item.Id == "aps-c");
        Assert.NotEqual(fullFrameFov, viewModel.Snapshot.FieldOfView.HorizontalDegrees);
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        var primary = viewModel.CelestialObjects.Single(item => item.IsPrimary);
        primary.IsVisible = false;
        Assert.NotEqual(primary.TargetId, viewModel.Session.TargetId);
        Assert.True(viewModel.CelestialObjects.Single(item => item.TargetId == viewModel.Session.TargetId).IsVisible);
    }

    [Fact]
    [CoversSettingsInput("RestoreCelestialDefaultsCommand")]
    public async Task RestoreCelestialDefaults_DoesNotResetUnrelatedSettings()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var settings = new AppSettings("UK", "UTC", TimeSnapMinutes: 15);
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, settings, [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.AddCelestialObjectCommand.Execute(catalogue.Get("M31"));
        viewModel.RestoreCelestialDefaultsCommand.Execute(null);
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
        var catalogue = new OpenNgcTargetCatalogue();
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
        var catalogue = new OpenNgcTargetCatalogue();
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(2, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter(),
            availability: new FakeAvailability(DeviceLocationAvailabilityState.PermissionRequestable));
        await viewModel.InitializeAsync();
        var original = viewModel.Session.Observer;
        await viewModel.Locations.UseDeviceLocationCommand.ExecuteAsync(null);
        Assert.Equal(original, viewModel.Session.Observer);
        Assert.Contains("could not be resolved", viewModel.Locations.ResolutionMessage!, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetScalarSettingsEditors(
        MainViewModel viewModel,
        string units,
        string timeZoneId,
        int timeSnapMinutes,
        double weatherCacheDistance,
        bool framingOverlayVisible,
        bool visibilityLimitsVisible,
        double shadingOpacity,
        double lineThickness)
    {
        viewModel.SettingsUnits = units;
        viewModel.SettingsTimeZoneId = timeZoneId;
        viewModel.SettingsTimeSnapMinutes = timeSnapMinutes;
        viewModel.SettingsWeatherCacheDistance = weatherCacheDistance;
        viewModel.SettingsCameraFramingOverlayVisible = framingOverlayVisible;
        viewModel.SettingsShowFramingVisibilityLimits = visibilityLimitsVisible;
        viewModel.SettingsFramingShadingOpacityPercent = shadingOpacity;
        viewModel.SettingsFramingLineThickness = lineThickness;
    }

    private static void AssertScalarSettingsEditors(
        MainViewModel viewModel,
        string units,
        string timeZoneId,
        int timeSnapMinutes,
        double weatherCacheDistance,
        bool framingOverlayVisible,
        bool visibilityLimitsVisible,
        double shadingOpacity,
        double lineThickness)
    {
        Assert.Equal(units, viewModel.SettingsUnits);
        Assert.Equal(timeZoneId, viewModel.SettingsTimeZoneId);
        Assert.Equal(timeSnapMinutes, viewModel.SettingsTimeSnapMinutes);
        Assert.Equal(weatherCacheDistance, viewModel.SettingsWeatherCacheDistance);
        Assert.Equal(framingOverlayVisible, viewModel.SettingsCameraFramingOverlayVisible);
        Assert.Equal(visibilityLimitsVisible, viewModel.SettingsShowFramingVisibilityLimits);
        Assert.Equal(shadingOpacity, viewModel.SettingsFramingShadingOpacityPercent);
        Assert.Equal(lineThickness, viewModel.SettingsFramingLineThickness);
    }

    private static string ParseBindingPath(string binding)
    {
        const string prefix = "{Binding ";
        Assert.StartsWith(prefix, binding, StringComparison.Ordinal);
        return binding[prefix.Length..].TrimEnd('}').Split(',', 2)[0].Trim();
    }

    private static MainViewModel CreateViewModel(IPlanningService planning, ITargetCatalogue catalogue, IUserDataStore store, FakeExporter exporter,
        IPlannerDialogService? dialogs = null, IDeviceLocationAvailabilityService? availability = null,
        ILocationMapThumbnailService? thumbnails = null, IReverseGeocodingProvider? reverseGeocoding = null)
    {
        var locationSearch = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var resolver = new LocationResolver(new UnavailableDeviceLocationProvider(), NullLogger<LocationResolver>.Instance);
        var localHorizon = new LocalHorizonCalculator();
        return new MainViewModel(planning, catalogue, new TimeZoneResolver(), store, exporter,
            NullLogger<MainViewModel>.Instance, new LensCalculator(),
            new CameraFramingGuideCalculator(), new FramingVisibilityCalculator(localHorizon), localHorizon,
            SystemClock.Instance,
            locationSearch, resolver, availability ?? new UnavailableDeviceLocationProvider(),
            new LocalTargetSearchService(catalogue), dialogs ?? new FakeDialogs(),
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
        public int EnvironmentRequests { get; private set; }
        public double? LastCameraHeightAboveGroundMetres { get; private set; }
        public double? GroundElevationMetres { get; set; }
        public double? ChosenGroundElevationMetres { get; set; }
        public PlanningSession? LastCalculatedSession { get; private set; }
        public int CalculateDelayMilliseconds { get; set; }
        public bool FailRefresh { get; set; }
        public Task<PlanningSnapshot> CalculateCoreSnapshotAsync(PlanningSession session, CancellationToken cancellationToken)
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
        public PlanningRefreshWork StartRefresh(PlanningSession session, WeatherSettings weatherSettings,
            CancellationToken cancellationToken) => StartRefresh(session, weatherSettings,
            AppSettings.DefaultCameraHeightAboveGroundMetres, cancellationToken);
        public PlanningRefreshWork StartRefresh(PlanningSession session, WeatherSettings weatherSettings,
            double cameraHeightAboveGroundMetres, CancellationToken cancellationToken)
        {
            LastCameraHeightAboveGroundMetres = cameraHeightAboveGroundMetres;
            return new PlanningRefreshWork(CalculateCoreSnapshotAsync(session, cancellationToken),
                LoadEnvironmentAsync(session, cancellationToken),
                LoadWeatherAsync(session, weatherSettings, cancellationToken));
        }
        public Task<PlanningSnapshot> CalculateSnapshotAsync(PlanningSession session, WeatherSettings weatherSettings,
            CancellationToken cancellationToken) => CalculateCoreSnapshotAsync(session, cancellationToken);
        public Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
            CancellationToken cancellationToken)
        {
            EnvironmentRequests++;
            return Task.FromResult(EnvironmentFor(session));
        }
        public Task<WeatherResult> LoadWeatherAsync(PlanningSession session, WeatherSettings weatherSettings,
            CancellationToken cancellationToken) => Task.FromResult(ReadyWeather(session.Instant));
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
        private PlannerEnvironmentSnapshot EnvironmentFor(PlanningSession session)
        {
            var ground = GroundElevationMetres is double elevation
                ? new EnvironmentalValue<double>(EnvironmentalDataState.Available, elevation,
                    "mapzen-terrarium", "test", "Ready")
                : EnvironmentalValue<double>.Unavailable("mapzen-terrarium", "test", "Unavailable");
            var unavailableCover = EnvironmentalValue<LandCoverClass>.Unavailable("worldcover", "test", "Unavailable");
            var unavailableSettlement = EnvironmentalValue<SettlementRaster>.Unavailable("wsf", "test", "Unavailable");
            var resolvedGround = session.EffectiveObserverElevation.ManualGroundElevationOverrideAslMetres ??
                                 ChosenGroundElevationMetres ?? GroundElevationMetres ??
                                 session.Observer.ElevationMetres;
            var cameraHeight = LastCameraHeightAboveGroundMetres ??
                               AppSettings.DefaultCameraHeightAboveGroundMetres;
            var horizon = new TerrainHorizonProfile(session.Observer, [], GroundElevationMetres.HasValue,
                GroundElevationMetres.HasValue ? "Ready" : "Unavailable", session.Instant,
                TerrainElevationAtObserver: ground,
                ObserverHeightAboveGroundMetres: cameraHeight,
                ChosenObserverGroundElevationMetres: resolvedGround,
                ObserverAbsoluteElevationMetres: resolvedGround + cameraHeight);
            return new PlannerEnvironmentSnapshot(session.Observer, ground,
                unavailableCover, unavailableSettlement, horizon, session.Instant);
        }
    }

    private sealed class ControllablePlanning(ITargetCatalogue catalogue) : IPlanningService
    {
        private readonly List<(PlanningSession Session, TaskCompletionSource<PlanningSnapshot> Completion)> _requests = [];
        private readonly object _gate = new();
        public int RequestCount { get { lock (_gate) return _requests.Count; } }

        public TargetPosition CalculateCurrent(PlanningSession session) => Position(session);

        public Task<PlanningSnapshot> CalculateCoreSnapshotAsync(PlanningSession session,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<PlanningSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _requests.Add((session, completion));
            return completion.Task; // Deliberately ignores cancellation to exercise Planner generation protection.
        }

        public PlanningRefreshWork StartRefresh(PlanningSession session, WeatherSettings weatherSettings,
            CancellationToken cancellationToken) => new(CalculateCoreSnapshotAsync(session, cancellationToken),
            LoadEnvironmentAsync(session, cancellationToken), LoadWeatherAsync(session, weatherSettings, cancellationToken));

        public Task<PlanningSnapshot> CalculateSnapshotAsync(PlanningSession session,
            WeatherSettings weatherSettings, CancellationToken cancellationToken) =>
            CalculateCoreSnapshotAsync(session, cancellationToken);

        public Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
            CancellationToken cancellationToken) => Task.FromResult(FakeEnvironment(session));

        public Task<WeatherResult> LoadWeatherAsync(PlanningSession session, WeatherSettings weatherSettings,
            CancellationToken cancellationToken) => Task.FromResult(ReadyWeather(session.Instant));

        public Task<WeatherResult> RefreshWeatherAsync(PlanningSession session,
            WeatherSettings weatherSettings, CancellationToken cancellationToken) =>
            Task.FromResult(ReadyWeather(session.Instant));

        public void Complete(int index)
        {
            (PlanningSession Session, TaskCompletionSource<PlanningSnapshot> Completion) request;
            lock (_gate) request = _requests[index];
            var position = Position(request.Session);
            var path = new AstralPath(new LocalDate(2024, 1, 1), request.Session.TimeZoneId,
                Duration.FromMinutes(10), [new AstralPathSample(request.Session.Instant, position.Horizontal)],
                position.Events, request.Session.Instant);
            var terrain = new TerrainHorizonProfile(request.Session.Observer,
                Enumerable.Range(0, 8).Select(point => new TerrainHorizonSample(point * 45, 0)).ToArray(),
                false, "Unavailable", request.Session.Instant);
            request.Completion.SetResult(new PlanningSnapshot(request.Session, position, path,
                new FieldOfView(60, 40, 70), terrain, new TerrainCrossings(null, null),
                ReadyWeather(request.Session.Instant), new AstronomyContext(position, position)));
        }

        private TargetPosition Position(PlanningSession session) => new(catalogue.Get(session.TargetId),
            session.Instant, new HorizontalCoordinate(180, 30), new TargetEvents(null, null, null));

        private static WeatherResult ReadyWeather(Instant instant) => new(DataState.Ready,
            new WeatherConditions(instant, 10, 2, 3, 5, 0, 0, "None", 2, 180, 4, 12, 70, 7, 20,
                "Clear", instant), "Ready");
        private static PlannerEnvironmentSnapshot FakeEnvironment(PlanningSession session)
        {
            var horizon = new TerrainHorizonProfile(session.Observer, [], false, "Unavailable", session.Instant);
            return new PlannerEnvironmentSnapshot(session.Observer,
                EnvironmentalValue<double>.Unavailable("mapzen-terrarium", "test", "Unavailable"),
                EnvironmentalValue<LandCoverClass>.Unavailable("worldcover", "test", "Unavailable"),
                EnvironmentalValue<SettlementRaster>.Unavailable("wsf", "test", "Unavailable"),
                horizon, session.Instant);
        }
    }

    private sealed class StagedPlanning(ITargetCatalogue catalogue) : IPlanningService
    {
        private readonly List<(PlanningSession Session, TaskCompletionSource<PlanningSnapshot> Completion)> _core = [];
        private readonly List<(PlanningSession Session, TaskCompletionSource<PlannerEnvironmentSnapshot> Completion)> _environment = [];
        private readonly List<(PlanningSession Session, TaskCompletionSource<WeatherResult> Completion)> _weather = [];
        private readonly object _gate = new();

        public int CoreRequestCount { get { lock (_gate) return _core.Count; } }
        public int EnvironmentRequestCount { get { lock (_gate) return _environment.Count; } }
        public int WeatherRequestCount { get { lock (_gate) return _weather.Count; } }
        public double PositionAltitudeDegrees { get; init; } = 30;

        public TargetPosition CalculateCurrent(PlanningSession session) => Position(session);

        public Task<PlanningSnapshot> CalculateCoreSnapshotAsync(PlanningSession session,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<PlanningSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _core.Add((session, completion));
            return completion.Task;
        }

        public Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<PlannerEnvironmentSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _environment.Add((session, completion));
            return completion.Task;
        }

        public Task<WeatherResult> LoadWeatherAsync(PlanningSession session, WeatherSettings weatherSettings,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<WeatherResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _weather.Add((session, completion));
            return completion.Task;
        }

        public PlanningRefreshWork StartRefresh(PlanningSession session, WeatherSettings weatherSettings,
            CancellationToken cancellationToken) => new(CalculateCoreSnapshotAsync(session, cancellationToken),
            LoadEnvironmentAsync(session, cancellationToken), LoadWeatherAsync(session, weatherSettings, cancellationToken),
            (bearings, token) => Task.FromResult(PriorityHorizon(session, bearings.Count)));

        public async Task<PlanningSnapshot> CalculateSnapshotAsync(PlanningSession session,
            WeatherSettings weatherSettings, CancellationToken cancellationToken)
        {
            var work = StartRefresh(session, weatherSettings, cancellationToken);
            await Task.WhenAll(work.Core, work.Environment, work.Weather);
            return work.Core.Result with
            {
                Terrain = work.Environment.Result.HorizonProfile,
                Environment = work.Environment.Result,
                Weather = work.Weather.Result
            };
        }

        public Task<WeatherResult> RefreshWeatherAsync(PlanningSession session, WeatherSettings weatherSettings,
            CancellationToken cancellationToken) => Task.FromResult(Weather(session.Instant, DataState.Ready));

        public void CompleteCore(int index)
        {
            (PlanningSession Session, TaskCompletionSource<PlanningSnapshot> Completion) request;
            lock (_gate) request = _core[index];
            var position = Position(request.Session);
            var path = new AstralPath(new LocalDate(2024, 1, 1), request.Session.TimeZoneId,
                Duration.FromMinutes(10), [new AstralPathSample(request.Session.Instant, position.Horizontal)],
                position.Events, request.Session.Instant);
            var pending = new TerrainHorizonProfile(request.Session.Observer, [], false, "Loading", request.Session.Instant);
            request.Completion.SetResult(new PlanningSnapshot(request.Session, position, path,
                new FieldOfView(60, 40, 70), pending, new TerrainCrossings(null, null),
                new WeatherResult(DataState.Loading, null, "Loading"), new AstronomyContext(position, position)));
        }

        public void CompleteEnvironment(int index, bool hasTerrain)
        {
            (PlanningSession Session, TaskCompletionSource<PlannerEnvironmentSnapshot> Completion) request;
            lock (_gate) request = _environment[index];
            var samples = Enumerable.Range(0, 8).Select(point => new TerrainHorizonSample(point * 45,
                hasTerrain ? 5 : null, hasTerrain ? 450 : null)).ToArray();
            var horizon = new TerrainHorizonProfile(request.Session.Observer, samples, hasTerrain, "Synthetic",
                request.Session.Instant,
                HorizonState: hasTerrain ? EnvironmentalDataState.Available : EnvironmentalDataState.Unavailable);
            request.Completion.SetResult(new PlannerEnvironmentSnapshot(request.Session.Observer,
                hasTerrain
                    ? new EnvironmentalValue<double>(EnvironmentalDataState.Available, 200, "mapzen-terrarium", "test", "Ready")
                    : EnvironmentalValue<double>.Unavailable("mapzen-terrarium", "test", "Unavailable"),
                new EnvironmentalValue<LandCoverClass>(EnvironmentalDataState.Available,
                    LandCoverClass.BareOrSparseVegetation, "worldcover", "test", "Ready"),
                new EnvironmentalValue<SettlementRaster>(EnvironmentalDataState.Available,
                    new SettlementRaster("wsf", "test", new GeoRasterRequest(
                        new GeoBounds(request.Session.Observer.Latitude - .001, request.Session.Observer.Longitude - .001,
                            request.Session.Observer.Latitude + .001, request.Session.Observer.Longitude + .001), 1, 1),
                        [0], [0]), "wsf", "test", "Ready"),
                horizon, request.Session.Instant));
        }

        private static TerrainHorizonProfile PriorityHorizon(PlanningSession session, int completedBearings)
        {
            var samples = Enumerable.Range(0, 360).Select(index => new TerrainHorizonSample(index,
                5, 450)).ToArray();
            return new TerrainHorizonProfile(session.Observer, samples, true,
                "Current camera terrain ready; full horizon refining", session.Instant,
                HorizonState: EnvironmentalDataState.Available,
                IsComplete: false, CompletedBearingCount: completedBearings);
        }

        public void FailEnvironment(int index)
        {
            TaskCompletionSource<PlannerEnvironmentSnapshot> completion;
            lock (_gate) completion = _environment[index].Completion;
            completion.SetException(new IOException("Synthetic environment failure"));
        }

        public void CompleteWeather(int index, DataState state)
        {
            (PlanningSession Session, TaskCompletionSource<WeatherResult> Completion) request;
            lock (_gate) request = _weather[index];
            request.Completion.SetResult(Weather(request.Session.Instant, state));
        }

        private TargetPosition Position(PlanningSession session) => new(catalogue.Get(session.TargetId),
            session.Instant, new HorizontalCoordinate(180, PositionAltitudeDegrees), new TargetEvents(null, null, null));

        private static WeatherResult Weather(Instant instant, DataState state) => state == DataState.Ready
            ? new WeatherResult(DataState.Ready,
                new WeatherConditions(instant, 10, 2, 3, 5, 0, 0, "None", 2, 180, 4, 12, 70, 7, 20,
                    "Clear", instant), "Ready")
            : new WeatherResult(state, null, "Unavailable");
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
        public int RefreshSettlementConfirmations { get; private set; }
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
        public Task<bool> ConfirmRefreshSavedLocationSettlementCachesAsync(int locationCount,
            CancellationToken cancellationToken = default)
        {
            RefreshSettlementConfirmations++;
            return Task.FromResult(true);
        }
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
        public int SettlementRefreshes { get; private set; }

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
            else if (mode == SavedLocationMapRefreshMode.RefreshSettlement) SettlementRefreshes++;
            else StyleReapplications++;
            var metadata = new SavedLocationThumbnailMetadata(1, location.Id, location.Coordinate.Latitude,
                location.Coordinate.Longitude, 11, 512, 280, "test", "Test maps", "test-style", "test-source",
                "Test attribution", null, null, null, DateTimeOffset.UtcNow, 11,
                location.Coordinate.Latitude, location.Coordinate.Longitude, 620, 340, 2, "hash", "test-v1",
                Path.GetFileName(_imagePath));
            var semantic = new MapFeatureFetchOutcome(MapFeatureFetchStatus.Complete, 1, 0,
                null, null, null, false, false, false, 1, DateTimeOffset.UtcNow);
            var operation = mode is SavedLocationMapRefreshMode.RefreshSource or SavedLocationMapRefreshMode.RefreshSettlement
                ? new SavedLocationMapRefreshResult(true,
                    mode == SavedLocationMapRefreshMode.RefreshSettlement,
                    mode == SavedLocationMapRefreshMode.RefreshSettlement
                        ? semantic with { Status = MapFeatureFetchStatus.CachedPrevious }
                        : semantic,
                    true, false, null, Noctaxis.Core.Environment.EnvironmentalDataState.Available)
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
