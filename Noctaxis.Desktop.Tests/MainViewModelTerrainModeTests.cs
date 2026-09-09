using NodaTime;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Time;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    private sealed class FixedClock(Instant instant) : IClock
    {
        public Instant GetCurrentInstant() => instant;
    }

    [Fact]
    [CoversSettingsInput("SettingsEnableTerrainCalculations")]
    public async Task TerrainSetting_SavesAndTogglesFallbackManualAndResolvedGround()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue) { GroundElevationMetres = 345, ChosenGroundElevationMetres = 345 };
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.True(vm.SettingsEnableTerrainCalculations);
        Assert.Equal(345, vm.ResolvedGroundElevationMetres);
        vm.SettingsEnableTerrainCalculations = false;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.False(store.State.Settings.EnableTerrainCalculations);
        Assert.False(vm.Session.EnableTerrainCalculations);
        Assert.Equal(TerrainElevationResolutionState.TerrainDisabledFallback, vm.TerrainElevationResolutionState);
        Assert.Equal(0, vm.ResolvedGroundElevationMetres);
        Assert.Equal(1.7, vm.Snapshot!.Terrain.ObserverAbsoluteElevationMetres!.Value, 8);
        Assert.Empty(vm.Snapshot.Terrain.Samples);
        Assert.Null(vm.TerrainDebugProfile);
        Assert.Null(vm.TerrainDebugMap);
        Assert.NotNull(vm.Snapshot.Weather.Conditions);
        Assert.Equal(20_000, vm.CameraFramingVisibility!.WeatherVisibilityDistanceMetres);
        Assert.Empty(vm.CameraFramingVisibility.EffectiveTerrainObstructions);
        Assert.False(vm.CameraFramingVisibility.IsTargetTerrainObstructed);
        Assert.Contains("disabled", vm.TerrainStatus, StringComparison.OrdinalIgnoreCase);
        vm.Elevation = 120;
        await vm.WaitForPlannerRefreshAsync();
        Assert.True(vm.IsElevationManualOverride);
        Assert.Equal(121.7, vm.Snapshot.Terrain.ObserverAbsoluteElevationMetres!.Value, 8);
        vm.SettingsEnableTerrainCalculations = true;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.True(store.State.Settings.EnableTerrainCalculations);
        Assert.True(vm.Session.EnableTerrainCalculations);
        Assert.Equal(120, vm.ResolvedGroundElevationMetres);
        Assert.NotNull(vm.TerrainDebugProfile);
    }

    // Settings changes and profile commits run on the Avalonia UI context in production.
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TerrainToggle_RejectsLateEnvironmentAndRapidReenableCompletions()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new StagedPlanning(catalogue);
        var vm = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await WaitUntilAsync(() => planning.CoreRequestCount == 1, TimeSpan.FromSeconds(3));
        planning.CompleteCore(0);
        planning.CompleteWeather(0, DataState.Ready);
        await WaitUntilAsync(() => vm.Snapshot is not null, TimeSpan.FromSeconds(3));
        var oldRefresh = vm.WaitForPlannerRefreshAsync();
        var disable = vm.ApplySettingsAsync(vm.Settings with { EnableTerrainCalculations = false });
        Assert.Empty(vm.Snapshot!.Terrain.Samples);
        await WaitUntilAsync(() => planning.CoreRequestCount == 2, TimeSpan.FromSeconds(3));
        Complete(1, 700);
        await disable;
        planning.CompleteEnvironment(0, true, 345); // Deliberately ignores cancellation.
        await oldRefresh;
        AssertDisabled();

        var enable = vm.ApplySettingsAsync(vm.Settings with { EnableTerrainCalculations = true });
        await WaitUntilAsync(() => planning.CoreRequestCount == 3, TimeSpan.FromSeconds(3));
        Assert.Equal(TerrainElevationResolutionState.Unresolved, vm.TerrainElevationResolutionState);
        var disableAgain = vm.ApplySettingsAsync(vm.Settings with { EnableTerrainCalculations = false });
        await WaitUntilAsync(() => planning.CoreRequestCount == 4, TimeSpan.FromSeconds(3));
        Complete(3, 800);
        await disableAgain;
        Complete(2, 400);
        await enable;
        AssertDisabled();

        var finalEnable = vm.ApplySettingsAsync(vm.Settings with { EnableTerrainCalculations = true });
        await WaitUntilAsync(() => planning.CoreRequestCount == 5, TimeSpan.FromSeconds(3));
        Complete(4, 50);
        await finalEnable;
        Assert.Equal(TerrainElevationResolutionState.TerrainResolved, vm.TerrainElevationResolutionState);
        Assert.Equal(50, vm.ResolvedGroundElevationMetres);
        Assert.NotEmpty(vm.Snapshot!.Terrain.Samples);

        void Complete(int index, double ground)
        {
            planning.CompleteCore(index);
            planning.CompleteEnvironment(index, true, ground);
            planning.CompleteWeather(index, DataState.Ready);
        }
        void AssertDisabled()
        {
            Assert.False(vm.Settings.EnableTerrainCalculations);
            Assert.Empty(vm.Snapshot!.Terrain.Samples);
            Assert.Null(vm.TerrainDebugProfile);
            Assert.Equal(0, vm.ResolvedGroundElevationMetres);
        }
    }

    [Fact]
    public async Task TerrainToggle_RejectsDiagnosticMapThatIgnoresCancellation()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var maps = new ControllableTerrainDebugMapService();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(TerrainDebugOverlay: true), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), terrainDebugMaps: maps);
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await WaitUntilAsync(() => maps.RequestCount == 1, TimeSpan.FromSeconds(3));
        var obsolete = vm.WaitForTerrainDebugMapRefreshAsync();
        await vm.ApplySettingsAsync(vm.Settings with { EnableTerrainCalculations = false });
        maps.Complete(0);
        await obsolete;
        Assert.Null(vm.TerrainDebugMap);
        Assert.Equal(1, maps.RequestCount);
        Assert.True(vm.Settings.TerrainDebugOverlay); // The dependent preference is retained.
    }

    [Fact]
    public async Task Startup_UsesInjectedRoundedNowBeforeFirstPlanningRequest()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var clock = new FixedClock(Instant.FromUtc(2026, 12, 31, 23, 41));
        var expected = PlannerStartupTime.RoundedLocalHour(clock, new TimeZoneResolver());
        var vm = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2001, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), clock: clock);
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(expected, vm.Session.Instant);
        Assert.Equal(expected, planning.LastCalculatedSession!.Instant);
        Assert.Equal(expected, vm.Snapshot!.Position.Instant);
    }
}
