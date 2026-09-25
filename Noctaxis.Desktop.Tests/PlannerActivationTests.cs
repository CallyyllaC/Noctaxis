using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Terrain;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlannerPageActivationStartsEnvironmentWithoutPinInteraction(bool selectedBeforeInitialisation)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue) { GroundElevationMetres = 10, IncludeTerrainSightlines = true };
        var maps = new ControllableTerrainDebugMapService();
        var vm = CreateViewModel(planning, catalogue, new FakeStore(new PersistedState(4,
            new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), terrainDebugMaps: maps);
        if (selectedBeforeInitialisation) vm.SelectedPageIndex = 1;
        await vm.InitializeAsync();
        if (!selectedBeforeInitialisation)
        {
            Assert.Equal(0, planning.EnvironmentRequests);
            vm.SelectedPageIndex = 1; // Tab binding, without invoking ShowPlannerCommand.
        }
        Assert.Equal(1, vm.SelectedPageIndex);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(1, planning.EnvironmentRequests);
        Assert.True(await maps.Started.WaitAsync(TimeSpan.FromSeconds(3)));
        maps.Complete(0);
        await vm.WaitForTerrainDebugMapRefreshAsync();
        Assert.Equal(TerrainDebugMapLoadState.Ready, vm.TerrainDebugMapLoadState);
        Assert.NotNull(vm.CameraFramingGuide);
        Assert.True(vm.CameraOverlayReady);
        Assert.NotNull(vm.TerrainFrameDepth);
        vm.SelectedPageIndex = 0; vm.SelectedPageIndex = 1;
        vm.ShowPlannerCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(1, planning.EnvironmentRequests);
    }
}
