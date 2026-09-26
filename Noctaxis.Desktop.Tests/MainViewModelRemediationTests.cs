using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Planning;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

// Regression tests for the first review remediation batch. Kept in a separate partial file so
// the shared fakes in MainViewModelTests remain the single source of test doubles.
public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task Startup_UnexpectedFailureIsShownAndLeavesPlannerInactive()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new ThrowingStore(new InvalidOperationException("synthetic defect")), new FakeExporter());

        await viewModel.StartAsync();

        Assert.True(viewModel.HasStartupError);
        Assert.Contains("synthetic defect", viewModel.StartupError);
        viewModel.ShowPlannerCommand.Execute(null);
        Assert.Equal(1, viewModel.SelectedPageIndex);
        Assert.False(viewModel.IsPlannerRefreshing);
        await viewModel.WaitForPlannerRefreshAsync();
        Assert.Equal(0, planning.SnapshotCalculations);
    }

    [Fact]
    public async Task Startup_SuccessfulInitialisationReportsNoStartupError()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());

        await viewModel.StartAsync();

        Assert.False(viewModel.HasStartupError);
        Assert.Null(viewModel.StartupError);
    }

    [AvaloniaFact]
    public async Task WindowClose_PersistsUnsavedSessionStateBeforeCleanupCompletes()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.StartAsync();
        // Orientation edits recalculate only the field of view; nothing saves them until close.
        viewModel.SelectedOrientation = CameraOrientation.Portrait;
        Assert.Equal(CameraOrientation.Landscape, store.State.Session.Lens.Orientation);
        var window = new MainWindow(viewModel, new DesktopDialogService(new LocationSearchViewModel(
            new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance)));

        window.Show();
        window.Close();
        await window.CloseCleanup.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CameraOrientation.Portrait, store.State.Session.Lens.Orientation);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task WindowClose_AfterFailedStartupDoesNotOverwriteSavedState()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new ThrowingStore(new InvalidOperationException("synthetic defect"));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.StartAsync();
        var window = new MainWindow(viewModel, new DesktopDialogService(new LocationSearchViewModel(
            new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance)));

        window.Show();
        window.Close();
        await window.CloseCleanup.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, store.Saves);
        Assert.False(window.IsVisible);
    }

    [Fact]
    public async Task SavedLocationCardDelete_ClearsSelectionSoSaveCannotResurrectIt()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "Dark ridge", new GeoCoordinate(51, -1), "UTC");
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [location],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();
        await Assert.Single(viewModel.Locations.Saved).OpenCommand.ExecuteAsync(null);
        await viewModel.WaitForPlannerRefreshAsync();
        Assert.Equal(location.Id, viewModel.SelectedLocation?.Id);

        await Assert.Single(viewModel.Locations.Saved).DeleteCommand.ExecuteAsync(null);

        Assert.Null(viewModel.SelectedLocation);
        Assert.Null(store.State.MostRecentLocationId);
        Assert.False(viewModel.DeleteLocationCommand.CanExecute(null));
        await viewModel.SaveLocationCommand.ExecuteAsync(null);
        var saved = Assert.Single(viewModel.SavedLocations);
        Assert.NotEqual(location.Id, saved.Id);
        Assert.DoesNotContain(store.State.Locations, item => item.Id == location.Id);
    }

    [Fact]
    public async Task PinCommitAtCurrentObserver_KeepsSavedLocationStateAndStartsNoWork()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var location = new SavedLocation(Guid.NewGuid(), "Dark ridge", new GeoCoordinate(51, -1), "UTC");
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [location],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();
        await Assert.Single(viewModel.Locations.Saved).OpenCommand.ExecuteAsync(null);
        await viewModel.WaitForPlannerRefreshAsync();
        var calculations = planning.SnapshotCalculations;

        // What the map raises when the pin is released where it already was.
        viewModel.CommitUnresolvedObserverLocation(viewModel.Observer with { ElevationMetres = 0 });
        await viewModel.WaitForPlannerRefreshAsync();

        Assert.Equal(location.Id, viewModel.SelectedLocation?.Id);
        Assert.Equal(location.Id, viewModel.Session.SavedLocationId);
        Assert.Equal("Dark ridge", viewModel.LocationName);
        Assert.Equal(calculations, planning.SnapshotCalculations);
    }

    [Fact]
    public async Task SaveFailure_DoesNotEscapeCommandsAndIsShownUntilASaveSucceeds()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FlakyStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();
        store.Fail = true;

        // AsyncRelayCommand rethrows command failures; completing normally is the regression check.
        await viewModel.SaveLocationCommand.ExecuteAsync(null);
        Assert.Equal("Location could not be saved", viewModel.StatusMessage);
        Assert.True(viewModel.HasOperationalNotice);
        Assert.Contains("could not be saved", viewModel.OperationalNotice);
        Assert.Empty(store.State.Locations);
        await viewModel.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal("Settings applied, but they could not be saved", viewModel.StatusMessage);

        store.Fail = false;
        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Settings saved", viewModel.StatusMessage);
        Assert.False(viewModel.HasOperationalNotice);
        // The retry persisted the newest state, including the location whose own save failed.
        Assert.Single(store.State.Locations);
    }

    [Fact]
    public async Task OperationalNotice_CanBeDismissed()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FlakyStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)) { Fail = true };
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();
        Assert.False(await viewModel.FlushAsync());
        Assert.True(viewModel.HasOperationalNotice);

        viewModel.DismissOperationalNoticeCommand.Execute(null);

        Assert.False(viewModel.HasOperationalNotice);
        Assert.Null(viewModel.OperationalNotice);
    }

    [Fact]
    public async Task RefreshCompletionSaveFailure_IsNotReportedAsAPlannerFailure()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FlakyStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await viewModel.InitializeAsync();
        store.Fail = true;

        viewModel.ShowPlannerCommand.Execute(null);
        await viewModel.WaitForPlannerRefreshAsync();
        await viewModel.WaitForPendingSavesAsync();

        Assert.Contains(viewModel.PlannerRefresh.Phase, new[] { PlannerRefreshPhase.Ready, PlannerRefreshPhase.Partial });
        Assert.True(store.FailedSaves > 0);
        Assert.True(viewModel.HasOperationalNotice);
    }

    [AvaloniaFact]
    public async Task WindowClose_FlushesDebouncedChangesThatHaveNotBeenWritten()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FlakyStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        // A debounce that never elapses on its own: only a flush can write the pending change.
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(),
            persistenceDelay: (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
        await viewModel.InitializeAsync();
        var initial = viewModel.IsCameraFramingOverlayVisible;
        var saves = store.Saves;

        viewModel.IsCameraFramingOverlayVisible = !initial;
        viewModel.IsCameraFramingOverlayVisible = initial;
        viewModel.IsCameraFramingOverlayVisible = !initial;
        Assert.Equal(saves, store.Saves);

        var window = new MainWindow(viewModel, new DesktopDialogService(new LocationSearchViewModel(
            new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance)));
        window.Show();
        window.Close();
        await window.CloseCleanup.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(!initial, store.State.Settings.EffectiveCameraFraming.IsOverlayVisible);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task DebouncedSave_WritesTheSnapshotTakenWhenTheChangeWasMade()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new SnapshotProbeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        // The debounce ends only when the test releases it, from a worker thread.
        var debounce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(),
            persistenceDelay: (_, _) => debounce.Task);
        await viewModel.InitializeAsync();

        viewModel.CameraPitchDegrees = 10;
        viewModel.CameraPitchDegrees = 20;
        Assert.Empty(store.WrittenPitches);

        store.BlockWrites = true;
        await Task.Run(() => debounce.SetResult());
        await store.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        // The view model keeps changing while that write is in flight. The snapshot was taken on the
        // UI thread when the change requested the save, so the write cannot observe this mutation.
        viewModel.CameraPitchDegrees = 30;
        store.ReleaseWrites();
        await viewModel.WaitForPendingSavesAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([20, 30], store.WrittenPitches);
    }

    [Fact]
    public async Task ManualWeatherRefreshForASupersededObserverIsDiscardedSilently()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = await PlannerAtObserverAsync(planning, catalogue,
            new GeoCoordinate(51, -1), instant);
        var committed = viewModel.Snapshot!.Weather;
        planning.WeatherGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = viewModel.RefreshWeatherCommand.ExecuteAsync(null);
        viewModel.MoveObserver(new GeoCoordinate(48, 7));
        await viewModel.WaitForPlannerRefreshAsync();
        planning.WeatherGate.SetResult(true);
        await refresh;

        // The answer described the place the planner had left, so nothing on screen took it.
        Assert.Equal(51, planning.LastForcedWeatherObserver!.Value.Latitude);
        Assert.NotEqual(51, viewModel.Snapshot!.Weather.Conditions!.TemperatureCelsius);
        Assert.NotSame(committed, viewModel.Snapshot.Weather);
        Assert.DoesNotContain("failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsWeatherRefreshing);
    }

    [Fact]
    public async Task ManualWeatherRefreshForTheCurrentObserverStillCommits()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = await PlannerAtObserverAsync(planning, catalogue,
            new GeoCoordinate(51, -1), instant);

        await viewModel.RefreshWeatherCommand.ExecuteAsync(null);

        Assert.Equal(51, viewModel.Snapshot!.Weather.Conditions!.TemperatureCelsius);
        Assert.Equal("Weather refreshed", viewModel.StatusMessage);
    }

    [Fact]
    public async Task FailedWeatherRefreshForASupersededObserverReportsNothing()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = await PlannerAtObserverAsync(planning, catalogue,
            new GeoCoordinate(51, -1), instant);
        planning.FailRefresh = true;
        planning.WeatherGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = viewModel.RefreshWeatherCommand.ExecuteAsync(null);
        viewModel.MoveObserver(new GeoCoordinate(48, 7));
        await viewModel.WaitForPlannerRefreshAsync();
        planning.WeatherGate.SetResult(true);
        await refresh;

        // A failure the user has already walked away from is not news.
        Assert.DoesNotContain("failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportRendersTheObserverItsWeatherWasFetchedFor()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var exporter = new FakeExporter();
        var viewModel = await PlannerAtObserverAsync(planning, catalogue,
            new GeoCoordinate(51, -1), instant, exporter);
        planning.WeatherGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var export = viewModel.CreateExportPngAsync(CancellationToken.None);
        viewModel.MoveObserver(new GeoCoordinate(48, 7));
        await viewModel.WaitForPlannerRefreshAsync();
        planning.WeatherGate.SetResult(true);
        await export;

        var rendered = exporter.LastSnapshot!;
        Assert.Equal(51, rendered.Session.Observer.Latitude);
        Assert.Equal(51, rendered.Weather.Conditions!.TemperatureCelsius);
        // The plan on screen belongs to the new observer and never received the old one's weather.
        Assert.Equal(48, viewModel.Snapshot!.Session.Observer.Latitude);
        Assert.NotEqual(51, viewModel.Snapshot.Weather.Conditions!.TemperatureCelsius);
    }

    [Fact]
    public async Task ExportStillCommitsAndRendersTheLivePlanWhenTheObserverHasNotMoved()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var exporter = new FakeExporter();
        var viewModel = await PlannerAtObserverAsync(planning, catalogue,
            new GeoCoordinate(51, -1), instant, exporter);

        await viewModel.CreateExportPngAsync(CancellationToken.None);

        Assert.Same(viewModel.Snapshot, exporter.LastSnapshot);
        Assert.Equal(51, viewModel.Snapshot!.Weather.Conditions!.TemperatureCelsius);
        Assert.Equal("Scouting card ready", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ExportCancellationStillPropagatesAndLeavesThePlanAlone()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var exporter = new FakeExporter();
        var viewModel = await PlannerAtObserverAsync(planning, catalogue,
            new GeoCoordinate(51, -1), instant, exporter);
        var committed = viewModel.Snapshot!.Weather;
        planning.WeatherGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var export = viewModel.CreateExportPngAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.Equal(0, exporter.RenderCount);
        Assert.Same(committed, viewModel.Snapshot!.Weather);
    }

    [Fact]
    public async Task ManualWeatherRefreshForASupersededPlannerTimeIsDiscardedSilently()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var viewModel = await PlannerAtObserverAsync(planning, catalogue,
            new GeoCoordinate(51, -1), instant);
        var requestedInstant = viewModel.Session.Instant;
        planning.WeatherGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = viewModel.RefreshWeatherCommand.ExecuteAsync(null);
        // Same observer, different planner time: weather is time-dependent, so this is a new request.
        viewModel.MinutesOfDay = 300;
        await viewModel.WaitForPlannerRefreshAsync();
        var currentInstant = viewModel.Session.Instant;
        Assert.NotEqual(requestedInstant, currentInstant);
        planning.WeatherGate.SetResult(true);
        await refresh;

        Assert.Equal(currentInstant, viewModel.Snapshot!.Weather.Conditions!.ForecastInstant);
        Assert.DoesNotContain("failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsWeatherRefreshing);
    }

    [Fact]
    public async Task ExportRendersThePlannerTimeItsWeatherWasFetchedFor()
    {
        var instant = Instant.FromUtc(2024, 1, 15, 20, 0);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var exporter = new FakeExporter();
        var viewModel = await PlannerAtObserverAsync(planning, catalogue,
            new GeoCoordinate(51, -1), instant, exporter);
        var exportedInstant = viewModel.Session.Instant;
        planning.WeatherGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var export = viewModel.CreateExportPngAsync(CancellationToken.None);
        viewModel.MinutesOfDay = 300;
        await viewModel.WaitForPlannerRefreshAsync();
        var currentInstant = viewModel.Session.Instant;
        planning.WeatherGate.SetResult(true);
        await export;

        var rendered = exporter.LastSnapshot!;
        Assert.Equal(exportedInstant, rendered.Session.Instant);
        Assert.Equal(exportedInstant, rendered.Weather.Conditions!.ForecastInstant);
        // The plan on screen moved on in time and kept the weather of its own moment.
        Assert.Equal(currentInstant, viewModel.Snapshot!.Session.Instant);
        Assert.Equal(currentInstant, viewModel.Snapshot.Weather.Conditions!.ForecastInstant);
    }

    private async Task<MainViewModel> PlannerAtObserverAsync(FakePlanning planning, ITargetCatalogue catalogue,
        GeoCoordinate observer, Instant instant, FakeExporter? exporter = null)
    {
        var session = PlanningSession.Default(instant, "Europe/London") with { Observer = observer };
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [], session, null)),
            exporter ?? new FakeExporter());
        await viewModel.InitializeAsync();
        viewModel.ShowPlannerCommand.Execute(null);
        await viewModel.WaitForPlannerRefreshAsync();
        return viewModel;
    }

    private sealed class SnapshotProbeStore(PersistedState state) : IUserDataStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PersistedState State { get; private set; } = state;
        public bool BlockWrites { get; set; }
        public List<double> WrittenPitches { get; } = [];
        public Task FirstWriteStarted => _firstWrite.Task;
        public string StorageDirectory => "memory";
        public Task<PersistedState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public void ReleaseWrites() => _release.TrySetResult();
        public async Task SaveAsync(PersistedState value, CancellationToken cancellationToken)
        {
            _firstWrite.TrySetResult();
            if (BlockWrites) await _release.Task;
            State = value;
            WrittenPitches.Add(value.Settings.EffectiveCameraFraming.CameraPitchDegrees);
        }
    }

    private sealed class FlakyStore(PersistedState state) : IUserDataStore
    {
        public PersistedState State { get; private set; } = state;
        public bool Fail { get; set; }
        public int Saves { get; private set; }
        public int FailedSaves { get; private set; }
        public string StorageDirectory => "memory";
        public Task<PersistedState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task SaveAsync(PersistedState value, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                FailedSaves++;
                return Task.FromException(new IOException("The disk is full."));
            }
            Saves++;
            State = value;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStore(Exception failure) : IUserDataStore
    {
        public int Saves { get; private set; }
        public string StorageDirectory => "memory";
        public Task<PersistedState> LoadAsync(CancellationToken cancellationToken) => Task.FromException<PersistedState>(failure);
        public Task SaveAsync(PersistedState state, CancellationToken cancellationToken) { Saves++; return Task.CompletedTask; }
    }
}
