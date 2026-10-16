using Avalonia.Headless.XUnit;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Export;
using Noctaxis.Desktop.ViewModels;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Plan images do not depend on weather: fresh weather when it can be had, the cached forecast (said to be cached,
/// and how old) when it cannot, and an explicit "Weather unavailable" with no values when there is none. Only
/// cancellation and the plan's own requirements stop an export; errors that are not about weather still surface.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly Instant ExportMoment = Instant.FromUtc(2024, 1, 15, 20, 0);

    private static WeatherResult ReadyAt(Instant instant, Instant retrieved, double cloud = 10) => new(DataState.Ready,
        new WeatherConditions(instant, cloud, 2, 3, 5, 0, 0, "None", 2, 180, 4, 12, 70, 7, 20, "Clear", retrieved), "Ready");

    private static async Task<(MainViewModel ViewModel, FakePlanning Planning, FakeExporter Exporter)> ExportablePlanAsync()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var exporter = new FakeExporter();
        var vm = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(1, new AppSettings(), [], PlanningSession.Default(ExportMoment, "Europe/London"), null)), exporter);
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        for (var attempt = 0; attempt < 100 && vm.Snapshot is null; attempt++) await Task.Delay(10);
        await vm.WaitForPlannerRefreshAsync();
        Assert.NotNull(vm.Snapshot);
        // The Timeline's paths are what the sheet draws the focused target from.
        for (var attempt = 0; attempt < 50 && vm.TimelinePaths.Count == 0; attempt++) await Task.Delay(10);
        Assert.NotEmpty(vm.TimelinePaths);
        return (vm, planning, exporter);
    }

    /// <summary>Fresh weather: the sheet uses it, its panel has every row and no note, and the plan on screen takes it too.</summary>
    [Fact]
    public async Task ExportWithFreshWeatherUsesItWithoutANote()
    {
        var (vm, planning, exporter) = await ExportablePlanAsync();
        var fresh = ReadyAt(ExportMoment, ExportMoment, cloud: 3);
        planning.RefreshFor = (_, _) => Task.FromResult(fresh);
        var png = await vm.CreateExportPngAsync(CancellationToken.None);
        var sheet = exporter.LastExport!;
        Assert.Same(fresh, sheet.Planning.Weather);
        Assert.Same(fresh, vm.Snapshot!.Weather);
        Assert.Equal(MainViewModel.ExportWeatherRows, sheet.Weather.Count);
        Assert.Null(sheet.WeatherNote);
        Assert.Equal("Plan image ready", vm.StatusMessage);
        Assert.Equal(vm.CurrentPlan(), NoctaxisPlanImage.Read(png).Plan);
    }

    /// <summary>
    /// The refresh fails but the forecast is cached: the sheet is drawn from the cache, keeps the plan's own weather,
    /// and its note says the forecast is cached and when it was retrieved, in the plan's zone.
    /// </summary>
    [Fact]
    public async Task ExportWithOnlyCachedWeatherSaysSoAndHowOld()
    {
        var (vm, planning, exporter) = await ExportablePlanAsync();
        var retrieved = ExportMoment - Duration.FromHours(5);
        planning.WeatherFor = (session, _) => Task.FromResult(ReadyAt(session.Instant, retrieved));
        planning.RefreshFor = (_, _) => Task.FromResult(new WeatherResult(DataState.Error, null, "Open-Meteo returned HTTP 503."));
        var shown = vm.Snapshot!.Weather;
        var png = await vm.CreateExportPngAsync(CancellationToken.None);
        var sheet = exporter.LastExport!;
        Assert.Same(shown, sheet.Planning.Weather);
        Assert.Same(shown, vm.Snapshot!.Weather);
        Assert.Equal(MainViewModel.ExportWeatherRows, sheet.Weather.Count);
        var local = retrieved.InZone(vm.TimelineZone).ToDateTimeUnspecified();
        Assert.Equal($"Fresh weather was unavailable. Showing the cached forecast retrieved {local:d MMM HH:mm}.", sheet.WeatherNote);
        Assert.Equal("Plan image ready. Fresh weather was unavailable; the sheet notes what it shows.", vm.StatusMessage);
        Assert.Equal(vm.CurrentPlan(), NoctaxisPlanImage.Read(png).Plan);
    }

    /// <summary>
    /// No weather at all, whether the service answers with an error, throws, or never answers: the sheet is drawn
    /// with no weather values and an explicit "Weather unavailable" and why; the plan is still embedded and reopens.
    /// </summary>
    [Theory]
    [InlineData("error")]
    [InlineData("throws")]
    [InlineData("silent")]
    [InlineData("deaf")]
    public async Task ExportWithNoUsableWeatherSaysItIsUnavailable(string failure)
    {
        var (vm, planning, exporter) = await ExportablePlanAsync();
        vm.ExportWeatherTimeout = TimeSpan.FromMilliseconds(200);
        Func<PlanningSession, CancellationToken, Task<WeatherResult>> answer = failure switch
        {
            "error" => (_, _) => Task.FromResult(new WeatherResult(DataState.Error, null, "Open-Meteo returned HTTP 503.")),
            "throws" => (_, _) => Task.FromException<WeatherResult>(new HttpRequestException("No route to host")),
            // NX-40: a service that ignores cancellation altogether still cannot hold the export open.
            "deaf" => (_, _) => new TaskCompletionSource<WeatherResult>().Task,
            _ => async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null!; }
        };
        planning.RefreshFor = answer;
        planning.WeatherFor = answer;
        var png = await vm.CreateExportPngAsync(CancellationToken.None);
        var sheet = exporter.LastExport!;
        Assert.Empty(sheet.Weather);
        Assert.Equal(failure switch
        {
            "error" => "Weather unavailable: Open-Meteo returned HTTP 503.",
            "throws" => "Weather unavailable: No route to host",
            _ => "Weather unavailable: the weather service did not respond in time"
        }, sheet.WeatherNote);
        // Missing weather is never presented as conditions, and the plan the sheet carries reopens.
        Assert.DoesNotContain(sheet.Weather, row => row.Cloud.Length > 0);
        var plan = NoctaxisPlanImage.Read(png).Plan!;
        Assert.Equal(vm.CurrentPlan(), plan);
        var reopened = vm.ImportPlanImage(PlanImageSource.Of(png));
        Assert.True(reopened.Imported, reopened.Message);
    }

    /// <summary>Cancelling an export while weather is in flight cancels it: nothing is drawn and nothing is reported as failed.</summary>
    [Fact]
    public async Task CancellingAnExportDuringWeatherCancelsIt()
    {
        var (vm, planning, exporter) = await ExportablePlanAsync();
        using var cancel = new CancellationTokenSource();
        planning.RefreshFor = async (_, token) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return null!;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.CreateExportPngAsync(cancel.Token));
        Assert.Equal(0, exporter.RenderCount);
    }

    /// <summary>Weather fallbacks swallow nothing else: a failure to draw the sheet still fails the export and is reported.</summary>
    [Fact]
    public async Task AnErrorThatIsNotAboutWeatherStillFailsTheExport()
    {
        var (vm, planning, exporter) = await ExportablePlanAsync();
        planning.FailRefresh = true;
        exporter.Throw = new InvalidOperationException("The sheet could not be drawn.");
        // An unexpected failure is not the quiet expected outcome: it propagates as itself, and the status line says the
        // export failed in the user's language while the details go to the log (NX-04, NX-08).
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => vm.CreateExportPngAsync(CancellationToken.None));
        Assert.Equal("The sheet could not be drawn.", error.Message);
        Assert.Equal("The plan image could not be made. The details are in the Noctaxis log.", vm.StatusMessage);
    }

    /// <summary>The plan's own requirement is unchanged: with nothing focused there is still no sheet, whatever the weather.</summary>
    [AvaloniaFact]
    public async Task WeatherFallbackDoesNotRelaxTheFocusedTargetRequirement()
    {
        var exporter = new FakeExporter();
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        try
        {
            vm.ImportPlanImage(PlanImageSource.Of(PlanPng(vm.CurrentPlan() with { Focus = null })));
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            await Assert.ThrowsAsync<ExportNotPossibleException>(() => vm.CreateExportPngAsync(CancellationToken.None));
            Assert.Equal("Focus a target to export a plan.", vm.StatusMessage);
            Assert.Equal(0, exporter.RenderCount);
        }
        finally { Close(window); }
    }

    /// <summary>Save PNG and Copy PNG deliver the one image: with weather unavailable both get the same bytes and plan.</summary>
    [AvaloniaFact]
    public async Task SaveAndCopyGetTheSamePlanImageWhenWeatherIsUnavailable()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue)
        {
            RefreshFor = (_, _) => Task.FromResult(new WeatherResult(DataState.Error, null, "Open-Meteo returned HTTP 503.")),
            WeatherFor = (_, _) => Task.FromResult(new WeatherResult(DataState.Error, null, "Open-Meteo returned HTTP 503."))
        };
        var exporter = new FakeExporter();
        var vm = CreateViewModel(planning, catalogue, CreatePlannerStore(new AppSettings()), exporter);
        await vm.InitializeAsync();
        var search = new Noctaxis.Desktop.ViewModels.LocationSearchViewModel(new FakeLocationSearchProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Noctaxis.Desktop.ViewModels.LocationSearchViewModel>.Instance);
        var window = new Noctaxis.Desktop.Views.MainWindow(vm, new Noctaxis.Desktop.Views.DesktopDialogService(search)) { Width = 1280, Height = 820 };
        TestMaps.OfflinePip(window);
        window.Show();
        try
        {
            await vm.WaitForPlannerRefreshAsync();
            vm.CelestialObjects.First(item => item.IsVisible).FocusCommand.Execute(null);
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            for (var attempt = 0; attempt < 50 && vm.TimelinePaths.Count == 0; attempt++) { await Task.Delay(10); Settle(window); }
            // Save PNG and Copy PNG both call this one method (PlanImageTests pins that); twice, as each would.
            var forSave = await window.CreatePlanImageAsync(CancellationToken.None);
            var forCopy = await window.CreatePlanImageAsync(CancellationToken.None);
            Assert.Equal(forSave, forCopy);
            Assert.Equal(NoctaxisPlanImage.Read(forSave).Plan, NoctaxisPlanImage.Read(forCopy).Plan);
            Assert.Equal(vm.CurrentPlan(), NoctaxisPlanImage.Read(forSave).Plan);
            Assert.Equal("Weather unavailable: Open-Meteo returned HTTP 503.", exporter.LastExport!.WeatherNote);
            Assert.Equal(2, exporter.RenderCount);
        }
        finally { Close(window); }
    }
}
