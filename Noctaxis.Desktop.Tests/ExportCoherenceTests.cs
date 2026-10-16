using Avalonia.Headless.XUnit;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

// U01 (phase-0 audit): a plan image is drawn from one coherent plan. While the Timeline is still calculating the
// paths for a new window or place, the paths it shows are the previous ones; the export must not pair them with
// the new plan's header, window and terrain.
public sealed partial class MainViewModelTests
{
    private static async Task<(MainViewModel ViewModel, FakePlanning Planning, FakeExporter Exporter)> FocusedPlannerAsync()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var exporter = new FakeExporter();
        var vm = CreateViewModel(planning, catalogue, CreatePlannerStore(new AppSettings()), exporter,
            persistenceDelay: (_, _) => Task.CompletedTask);
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        var focused = vm.CelestialObjects.First(item => item.IsVisible);
        focused.FocusCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        await WaitUntilAsync(() => vm.TimelinePaths.Any(path => path.Target.Id == focused.TargetId), TimeSpan.FromSeconds(2));
        return (vm, planning, exporter);
    }

    [AvaloniaFact]
    public async Task ExportWaitsForTheTimelineOfTheWindowItShows()
    {
        var (vm, planning, exporter) = await FocusedPlannerAsync();
        var gate = new TaskCompletionSource();
        planning.TimelineGate = gate.Task;

        // Another day: a new window, whose paths are still being calculated.
        var previousStart = vm.TimelineStart;
        vm.SetPlannerDate(vm.LocalDate!.Value.Date.AddDays(1));
        await vm.WaitForPlannerRefreshAsync();
        Assert.NotEqual(previousStart, vm.TimelineStart);

        var attempt = await Record.ExceptionAsync(() => vm.CreateExportPngAsync(CancellationToken.None));
        if (attempt is null)
        {
            // Had it drawn, the sheet's target must belong to the window it states.
            var sheet = exporter.LastExport!;
            Assert.Equal(sheet.WindowStart, sheet.Target.Samples[0].Time);
        }
        else
        {
            Assert.IsType<ExportNotPossibleException>(attempt);
            Assert.Equal("The focused target is still being calculated. Try again in a moment.", vm.StatusMessage);
        }

        gate.SetResult();
        await WaitUntilAsync(() => vm.TimelinePaths.Count > 0 && vm.TimelinePaths[0].Path.Samples[0].Instant == vm.TimelineStart,
            TimeSpan.FromSeconds(2));
        await vm.CreateExportPngAsync(CancellationToken.None);
        Assert.Equal(vm.TimelineStart, exporter.LastExport!.WindowStart);
        Assert.Equal(exporter.LastExport.WindowStart, exporter.LastExport.Target.Samples[0].Time);
    }

    [AvaloniaFact]
    public async Task ExportWaitsForTheTimelineOfThePlaceItShows()
    {
        var (vm, planning, exporter) = await FocusedPlannerAsync();
        var gate = new TaskCompletionSource();
        planning.TimelineGate = gate.Task;
        var calculations = planning.TimelineCalculations;

        vm.CommitObserverLocation(new GeoCoordinate(40.5, -105.1));
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(40.5, vm.Snapshot!.Session.Observer.Latitude, 6);

        await Assert.ThrowsAsync<ExportNotPossibleException>(() => vm.CreateExportPngAsync(CancellationToken.None));
        Assert.Equal("The focused target is still being calculated. Try again in a moment.", vm.StatusMessage);

        gate.SetResult();
        await WaitUntilAsync(() => planning.TimelineCalculations > calculations, TimeSpan.FromSeconds(2));
        Exception? failure = new TimeoutException();
        for (var attempt = 0; attempt < 100 && failure is not null; attempt++)
        {
            failure = await Record.ExceptionAsync(() => vm.CreateExportPngAsync(CancellationToken.None));
            if (failure is not null) await Task.Delay(20);
        }
        Assert.Null(failure);
        Assert.Equal(40.5, exporter.LastExport!.Planning.Session.Observer.Latitude, 6);
    }
}
