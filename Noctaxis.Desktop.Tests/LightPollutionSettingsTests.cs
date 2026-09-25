using Noctaxis.Core.Domain;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.LightPollution;
using Noctaxis.Core.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Time;
using Noctaxis.Core.Locations;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task DatasetCommandsReportBusyCancelAndRemoveWithoutBlockingCaller()
    {
        await using var paths = new AtlasTestData();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new LightPollutionInstallationTests.Handler(async (_, ct) =>
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(System.Net.HttpStatusCode.OK); }));
        var dataset = new LorenzInstallation(paths, http);
        await paths.SeedAsync(dataset);
        var catalogue = new OpenNgcTargetCatalogue();
        var store = CreateSupporterStore(new AppSettings());
        var vm = new MainViewModel(new FakePlanning(catalogue), catalogue, new TimeZoneResolver(), store,
            new FakeExporter(), NullLogger<MainViewModel>.Instance, new LensCalculator(),
            new CameraFramingGuideCalculator(), new FramingVisibilityCalculator(), new LocalHorizonCalculator(),
            new FixedClock(store.State.Session.Instant),
            new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance),
            new LocationResolver(new UnavailableDeviceLocationProvider(), NullLogger<LocationResolver>.Instance),
            new UnavailableDeviceLocationProvider(), new LocalTargetSearchService(catalogue), new FakeDialogs(),
            new FakeReverseGeocodingProvider(), lightPollutionInstallation: dataset);
        await vm.InitializeAsync();
        Assert.Equal("Installed", vm.LightPollutionStatus);
        var installing = vm.InstallLightPollutionCommand.ExecuteAsync(null);
        Assert.True(vm.IsManagingLightPollution); Assert.False(vm.CanManageLightPollution);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelLightPollutionInstallCommand.Execute(null);
        await installing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsManagingLightPollution); Assert.True(dataset.IsAvailable);
        Assert.Contains("previous installation retained", vm.LightPollutionStatus);
        await vm.RemoveLightPollutionCommand.ExecuteAsync(null);
        Assert.False(dataset.IsAvailable); Assert.Equal("Not installed", vm.LightPollutionStatus);
        vm.CancelEnvironmentalInstall();
    }

    [Fact]
    [CoversSettingsInput("OpenLightPollutionSourceCommand")]
    public async Task LightPollutionPreferencesUseSaveResetAndSourceIsAlwaysAvailable()
    {
        var launcher = new FakeUriLauncher(true, null);
        var catalogue = new OpenNgcTargetCatalogue();
        var store = CreateSupporterStore(new AppSettings());
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), externalUriLauncher: launcher);
        await vm.InitializeAsync();
        Assert.False(vm.LightPollutionMapPreferences.IsVisible); Assert.Equal(.5, vm.LightPollutionMapPreferences.Opacity);
        vm.PreviewPlannerLayers(true, 25);
        vm.ResetSettingsEditorCommand.Execute(null); Assert.True(vm.LightPollutionMapPreferences.IsVisible);
        await vm.CommitPlannerLayersAsync();
        Assert.Equal(new LightPollutionPreferences(true, .25), store.State.Settings.LightPollution);
        Assert.Equal(store.State.Settings.LightPollution, vm.LightPollutionMapPreferences);
        vm.OpenLightPollutionSourceCommand.Execute(null);
        Assert.Equal(LorenzAtlas.InformationUrl, launcher.LastUri!.AbsoluteUri);
        Assert.Equal(LorenzAtlas.Name, vm.LightPollutionSourceName);
        await using var paths = new AtlasTestData();
        var disk = new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance);
        await disk.SaveAsync(store.State, CancellationToken.None);
        Assert.Equal(store.State.Settings.LightPollution, (await disk.LoadAsync(CancellationToken.None)).Settings.LightPollution);
    }

    [Fact]
    [CoversSettingsInput("InstallLightPollutionCommand")]
    [CoversSettingsInput("CancelLightPollutionInstallCommand")]
    [CoversSettingsInput("RemoveLightPollutionCommand")]
    public async Task DatasetCommandsAreSafeWhenNoInstallationServiceExists()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = CreateSupporterStore(new AppSettings());
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        Assert.False(vm.CanManageLightPollution);
        await vm.InstallLightPollutionCommand.ExecuteAsync(null);
        vm.CancelLightPollutionInstallCommand.Execute(null);
        await vm.RemoveLightPollutionCommand.ExecuteAsync(null);
        Assert.False(vm.IsManagingLightPollution);
        Assert.Equal("Not installed", vm.LightPollutionStatus);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task MainWindowCloseWaitsForActiveNumericalReadAndReleasesDatasetFiles()
    {
        await using var paths = new AtlasTestData();
        using var http = new HttpClient(new LightPollutionInstallationTests.Handler((_, _) =>
            throw new InvalidOperationException("Window shutdown must not access the network.")));
        var dataset = new LorenzInstallation(paths, http);
        var directory = await paths.SeedAsync(dataset);
        var catalogue = new OpenNgcTargetCatalogue();
        var store = CreateSupporterStore(new AppSettings());
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(),
            lightPollutionInstallation: dataset);
        await vm.InitializeAsync();
        var search = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search));
        await using var reader = new LightPollutionDataProvider(dataset);
        var lease = await reader.OpenAsync();
        Assert.NotNull(lease);

        window.Show();
        window.Close();
        window.Close(); // A duplicate request must share the same pending cleanup.
        Assert.False(window.CloseCleanup.IsCompleted);

        lease.Dispose();
        await window.CloseCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(window.IsVisible);
        await dataset.RemoveAsync();
        Assert.False(Directory.Exists(directory));
    }
}
