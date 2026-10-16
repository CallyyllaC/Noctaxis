using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Planning;
using Noctaxis.Desktop.ViewModels;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

// D01 (phase-0 audit): an astronomy refresh (a time change) that supersedes an observer refresh whose
// terrain is still loading must carry that observer's environment work on, not drop it and report Ready.
// In production both environment requests join one shared build in PlannerEnvironmentService (see
// TerrainResultCacheTests.CancellingOnlyWaiterDoesNotLeaveCompletedBuildInActiveDictionary); this fake
// keeps them apart so each refresh's commit can be driven separately.
public sealed partial class MainViewModelTests
{
    private static readonly Instant RefreshStart = Instant.FromUtc(2026, 10, 14, 10, 0);

    private static async Task<(MainViewModel ViewModel, StagedPlanning Planning)> StagedPlannerAsync()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new StagedPlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(RefreshStart, "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();
        return (viewModel, planning);
    }

    [Fact]
    public async Task TimeChangeWhileTerrainIsLoading_StillCommitsTheObserversFullTerrain()
    {
        var (viewModel, planning) = await StagedPlannerAsync();
        var observer = new GeoCoordinate(53, -1);
        viewModel.CommitObserverLocation(observer);
        await WaitUntilAsync(() => planning.CoreRequestCount == 1, TimeSpan.FromSeconds(2));
        planning.CompleteCore(0);
        planning.CompleteWeather(0, DataState.Ready);
        await WaitUntilAsync(() => viewModel.Snapshot is { Terrain.HasTerrainCoverage: true },
            TimeSpan.FromSeconds(2));
        var observerRefresh = viewModel.WaitForPlannerRefreshAsync();

        viewModel.StepTime(30);
        await WaitUntilAsync(() => planning.CoreRequestCount == 2, TimeSpan.FromSeconds(2));
        planning.CompleteCore(1);
        planning.CompleteWeather(1, DataState.Ready);
        await WaitUntilAsync(() => viewModel.PlannerRefresh.IsCoreReady &&
                                   viewModel.Snapshot!.Session.Instant == RefreshStart + Duration.FromMinutes(30),
            TimeSpan.FromSeconds(2));

        // Core first: the new time is shown with the terrain loaded so far, and the refresh is not Ready.
        Assert.True(viewModel.Snapshot!.Terrain.HasTerrainCoverage);
        Assert.False(viewModel.Snapshot.Terrain.IsComplete);
        Assert.Equal(PlannerRefreshScope.Astronomy, viewModel.PlannerRefresh.Scope);
        Assert.Equal(PlannerRefreshWorkState.Running, viewModel.PlannerRefresh.GroundTerrainState);
        Assert.True(viewModel.IsPlannerRefreshing);
        Assert.Equal(2, planning.EnvironmentRequestCount);
        Assert.Equal(observer, planning.EnvironmentSession(1).Observer);

        // The superseded refresh's own completion is still rejected.
        planning.CompleteEnvironment(0, hasTerrain: true, ground: 10);
        await observerRefresh;
        Assert.Null(viewModel.Snapshot.Environment);
        Assert.True(viewModel.IsPlannerRefreshing);

        var timeRefresh = viewModel.WaitForPlannerRefreshAsync();
        planning.CompleteEnvironment(1, hasTerrain: true, ground: 10);
        await timeRefresh;

        var snapshot = viewModel.Snapshot!;
        Assert.Equal(observer, snapshot.Environment!.ObserverCoordinate);
        Assert.True(snapshot.Terrain.IsComplete);
        Assert.True(snapshot.Terrain.HasTerrainCoverage);
        Assert.Equal(RefreshStart + Duration.FromMinutes(30), snapshot.Session.Instant);
        Assert.Equal(PlannerRefreshPhase.Ready, viewModel.PlannerRefresh.Phase);
        Assert.Equal(PlannerRefreshWorkState.Ready, viewModel.PlannerRefresh.GroundTerrainState);
        Assert.Equal(PlannerRefreshWorkState.Ready, viewModel.PlannerRefresh.EnvironmentMetadataState);
    }

    [Fact]
    public async Task TimeChangeBeforeTheObserversFirstResults_StillLoadsItsTerrain()
    {
        var (viewModel, planning) = await StagedPlannerAsync();
        var observer = new GeoCoordinate(53, -1);
        viewModel.CommitObserverLocation(observer);
        await WaitUntilAsync(() => planning.CoreRequestCount == 1, TimeSpan.FromSeconds(2));

        viewModel.StepTime(30);
        await WaitUntilAsync(() => planning.CoreRequestCount == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, planning.EnvironmentRequestCount);
        var timeRefresh = viewModel.WaitForPlannerRefreshAsync();
        planning.CompleteCore(1);
        planning.CompleteWeather(1, DataState.Ready);
        planning.CompleteEnvironment(1, hasTerrain: true);
        await timeRefresh;

        Assert.Equal(observer, viewModel.Snapshot!.Session.Observer);
        Assert.Equal(observer, viewModel.Snapshot.Environment!.ObserverCoordinate);
        Assert.True(viewModel.Snapshot.Terrain.IsComplete);
        Assert.Equal(PlannerRefreshPhase.Ready, viewModel.PlannerRefresh.Phase);
    }

    [Fact]
    public async Task TimeChangeAfterTheObserversTerrainCompleted_ReusesItWithoutAnotherRequest()
    {
        var (viewModel, planning) = await StagedPlannerAsync();
        viewModel.CommitObserverLocation(new GeoCoordinate(53, -1));
        await WaitUntilAsync(() => planning.CoreRequestCount == 1, TimeSpan.FromSeconds(2));
        var observerRefresh = viewModel.WaitForPlannerRefreshAsync();
        planning.CompleteCore(0);
        planning.CompleteWeather(0, DataState.Ready);
        planning.CompleteEnvironment(0, hasTerrain: true);
        await observerRefresh;
        var environment = viewModel.Snapshot!.Environment;
        Assert.NotNull(environment);

        viewModel.StepTime(30);
        await WaitUntilAsync(() => planning.CoreRequestCount == 2, TimeSpan.FromSeconds(2));
        var timeRefresh = viewModel.WaitForPlannerRefreshAsync();
        planning.CompleteCore(1);
        planning.CompleteWeather(1, DataState.Ready);
        await timeRefresh;

        Assert.Equal(1, planning.EnvironmentRequestCount);
        Assert.Same(environment, viewModel.Snapshot!.Environment);
        Assert.Equal(PlannerRefreshWorkState.NotRequired, viewModel.PlannerRefresh.GroundTerrainState);
        Assert.Equal(PlannerRefreshPhase.Ready, viewModel.PlannerRefresh.Phase);
    }

    [Fact]
    public async Task CarriedTerrainOfAnObserverLeftBehindCannotEnrichTheNewObserver()
    {
        var (viewModel, planning) = await StagedPlannerAsync();
        var first = new GeoCoordinate(53, -1);
        var second = new GeoCoordinate(51, -3);
        viewModel.CommitObserverLocation(first);
        await WaitUntilAsync(() => planning.CoreRequestCount == 1, TimeSpan.FromSeconds(2));
        planning.CompleteCore(0);
        viewModel.StepTime(30);
        await WaitUntilAsync(() => planning.CoreRequestCount == 2, TimeSpan.FromSeconds(2));
        planning.CompleteCore(1);

        viewModel.CommitObserverLocation(second);
        await WaitUntilAsync(() => planning.CoreRequestCount == 3, TimeSpan.FromSeconds(2));
        var secondRefresh = viewModel.WaitForPlannerRefreshAsync();
        planning.CompleteCore(2);
        planning.CompleteWeather(2, DataState.Ready);
        planning.CompleteEnvironment(1, hasTerrain: false);
        planning.CompleteEnvironment(0, hasTerrain: false);
        await WaitUntilAsync(() => viewModel.Snapshot?.Session.Observer == second, TimeSpan.FromSeconds(2));
        Assert.Null(viewModel.Snapshot!.Environment);

        planning.CompleteEnvironment(2, hasTerrain: true);
        await secondRefresh;
        Assert.Equal(second, viewModel.Snapshot!.Environment!.ObserverCoordinate);
        Assert.True(viewModel.Snapshot.Terrain.HasTerrainCoverage);
    }
}
