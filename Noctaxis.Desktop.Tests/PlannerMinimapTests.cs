using NodaTime;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentMinimap_UnavailableAndErrorEndResolving(bool fail)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var maps = new ControllableTerrainDebugMapService();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), terrainDebugMaps: maps);
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        Assert.True(await maps.Started.WaitAsync(TimeSpan.FromSeconds(3)));
        if (fail) maps.Fail(0); else maps.Complete(0, available: false);
        await vm.WaitForTerrainDebugMapRefreshAsync();
        Assert.Equal(TerrainDebugMapLoadState.Unavailable, vm.TerrainDebugMapLoadState);
        Assert.Null(vm.TerrainDebugMap);
    }

    [Fact]
    public async Task PermanentMinimap_IsIndependentOfDiagnosticsAndReusesRasterForPresentation()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var maps = new ControllableTerrainDebugMapService();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), terrainDebugMaps: maps);
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        Assert.True(await maps.Started.WaitAsync(TimeSpan.FromSeconds(3)));
        maps.Complete(0);
        await vm.WaitForTerrainDebugMapRefreshAsync();
        await vm.WaitForPlannerRefreshAsync();
        var raster = vm.TerrainDebugMap;
        Assert.NotNull(raster);
        Assert.Null(vm.EnabledTerrainDiagnosticsProfile);
        Assert.Equal("Terrain diagnostics disabled", vm.TerrainDebugText);
        await vm.ApplySettingsAsync(vm.Settings with { TerrainDebugOverlay = true });
        Assert.NotNull(vm.EnabledTerrainDiagnosticsProfile);
        Assert.Contains("Observer:", vm.TerrainDiagnosticsStatus);
        Assert.Same(raster, vm.TerrainDebugMap);
        await vm.ApplySettingsAsync(vm.Settings with { TerrainDebugOverlay = false });
        Assert.Null(vm.EnabledTerrainDiagnosticsProfile);
        Assert.Same(raster, vm.TerrainDebugMap);
        var fov = vm.TerrainDebugHorizontalFieldOfView;
        vm.FocalLength = 85;
        await vm.WaitForPlannerRefreshAsync();
        Assert.NotEqual(fov, vm.TerrainDebugHorizontalFieldOfView);
        vm.MinutesOfDay = 600;
        await vm.WaitForPlannerRefreshAsync();
        vm.AddCelestialObjectCommand.Execute(catalogue.Get("M31"));
        await vm.WaitForPlannerRefreshAsync();
        await vm.RefreshWeatherCommand.ExecuteAsync(null);
        await vm.ApplySettingsAsync(vm.Settings with
        {
            CameraFraming = vm.Settings.EffectiveCameraFraming with { ManualBearingDegrees = 90 }
        });
        Assert.Same(raster, vm.TerrainDebugMap);
        Assert.Equal(1, maps.RequestCount);
        await vm.ApplySettingsAsync(vm.Settings with { EnableTerrainCalculations = false });
        Assert.Null(vm.TerrainDebugMap);
        Assert.Equal(TerrainDebugMapLoadState.Disabled, vm.TerrainDebugMapLoadState);
        Assert.Equal("Terrain calculations disabled", vm.TerrainDiagnosticsStatus);
        Assert.Equal(1, maps.RequestCount);
        Assert.NotNull(vm.Snapshot!.Weather.Conditions);
    }

    [Fact]
    public async Task PermanentMinimap_RejectsLateObserverWithoutDiagnosticMode()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var maps = new ControllableTerrainDebugMapService();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), terrainDebugMaps: maps);
        await vm.InitializeAsync();
        vm.CommitObserverLocation(new GeoCoordinate(51, -1));
        Assert.True(await maps.Started.WaitAsync(TimeSpan.FromSeconds(3)));
        var first = vm.WaitForTerrainDebugMapRefreshAsync();
        vm.CommitObserverLocation(new GeoCoordinate(53, 1));
        Assert.Null(vm.TerrainDebugMap);
        Assert.Equal(TerrainDebugMapLoadState.Resolving, vm.TerrainDebugMapLoadState);
        Assert.True(await maps.Started.WaitAsync(TimeSpan.FromSeconds(3)));
        maps.Complete(1);
        await vm.WaitForTerrainDebugMapRefreshAsync();
        var current = vm.TerrainDebugMap;
        maps.Complete(0);
        await first;
        Assert.Same(current, vm.TerrainDebugMap);
        Assert.Equal(53, vm.TerrainDebugMap!.Observer.Latitude);
        Assert.False(vm.Settings.TerrainDebugOverlay);
    }
}
