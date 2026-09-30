using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task TerrainReliefLayerPreferencesAreSavedFromTheLayersPanel()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = CreateSupporterStore(new AppSettings());
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        Assert.Equal(new TerrainReliefPreferences(true, TerrainReliefPreferences.DefaultOpacity), vm.TerrainReliefMapPreferences);
        Assert.Null(store.State.Settings.TerrainRelief); // Nothing stored until the user changes it.

        vm.PreviewTerrainRelief(false, 35);
        await vm.CommitPlannerLayersAsync();
        Assert.Equal(new TerrainReliefPreferences(false, .35), store.State.Settings.TerrainRelief);
        Assert.Equal(store.State.Settings.TerrainRelief, vm.TerrainReliefMapPreferences);
        // Out-of-range input is clamped rather than stored.
        vm.PreviewTerrainRelief(true, 250);
        Assert.Equal(1, vm.TerrainReliefMapPreferences.Opacity);
    }

    [Fact]
    public async Task StoredLayerPreferencesReachAMapBoundBeforeSettingsLoad()
    {
        // The app binds its window, and so the map's layer preferences, before settings are loaded:
        // loading must announce both, or the map keeps the defaults it was bound with.
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreateSupporterStore(new AppSettings(
            LightPollution: new LightPollutionPreferences(true, .35), TerrainRelief: new TerrainReliefPreferences(true, .3))),
            new FakeExporter());
        var announced = new List<(string?, object)>();
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(vm.TerrainReliefMapPreferences)) announced.Add((change.PropertyName, vm.TerrainReliefMapPreferences));
            if (change.PropertyName == nameof(vm.LightPollutionMapPreferences)) announced.Add((change.PropertyName, vm.LightPollutionMapPreferences));
        };
        await vm.InitializeAsync();
        Assert.Contains((nameof(vm.TerrainReliefMapPreferences), (object)new TerrainReliefPreferences(true, .3)), announced);
        Assert.Contains((nameof(vm.LightPollutionMapPreferences), (object)new LightPollutionPreferences(true, .35)), announced);
    }

    [Fact]
    public async Task LightPollutionDefaultsToTenPercentButAStoredOpacityIsKept()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var fresh = CreateViewModel(new FakePlanning(catalogue), catalogue, CreateSupporterStore(new AppSettings()), new FakeExporter());
        await fresh.InitializeAsync();
        Assert.Equal(.1, fresh.LightPollutionMapPreferences.Opacity);

        var existing = CreateViewModel(new FakePlanning(catalogue), catalogue,
            CreateSupporterStore(new AppSettings(LightPollution: new LightPollutionPreferences(true, .5))), new FakeExporter());
        await existing.InitializeAsync();
        Assert.Equal(.5, existing.LightPollutionMapPreferences.Opacity);
        Assert.True(existing.LightPollutionMapPreferences.IsVisible);
    }
}
