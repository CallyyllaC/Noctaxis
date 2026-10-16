using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Export;
using Noctaxis.Desktop.ViewModels;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

// D02 (phase-0 audit): a known absolute moment (an imported plan's, a Timeline position) is kept exact. The time
// bar's controls hold only a local date and minute; they display such a moment but never rebuild it, so seconds
// survive and an autumn fold keeps its own occurrence of the repeated hour. Typed wall-clock edits still resolve
// through the plan's zone as before.
public sealed partial class MainViewModelTests
{
    private static async Task<MainViewModel> PlannerInZoneAsync(string zoneId)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var viewModel = CreateViewModel(new StagedPlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(SelectedTimeZoneId: zoneId), [],
                PlanningSession.Default(Instant.FromUtc(2026, 6, 1, 12, 0), zoneId), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();
        Assert.Equal(zoneId, viewModel.TimeZoneId);
        return viewModel;
    }

    private static NoctaxisPlan PlanAt(Instant instant) => new(instant, 52.59302, -3.64697, 293.3, 7.2, null);

    [Theory]
    // Autumn fold, Europe/London: 01:30 happens at 00:30Z (BST) and again at 01:30Z (GMT). Both are kept.
    [InlineData("Europe/London", "2026-10-25T01:30:45Z", "01:30")]
    [InlineData("Europe/London", "2026-10-25T00:30:45Z", "01:30")]
    // Autumn fold, America/New_York: 01:30 EDT (05:30Z) and 01:30 EST (06:30Z).
    [InlineData("America/New_York", "2026-11-01T06:30:20Z", "01:30")]
    [InlineData("America/New_York", "2026-11-01T05:30:20Z", "01:30")]
    // Either side of the spring gap, Europe/London (local 01:00-02:00 does not exist on 29 March 2026).
    [InlineData("Europe/London", "2026-03-29T00:59:30Z", "00:59")]
    [InlineData("Europe/London", "2026-03-29T01:00:30Z", "02:00")]
    // Ordinary dates, with seconds, in and out of summer time and in a zone far from the machine's.
    [InlineData("Europe/London", "2026-07-01T21:15:30Z", "22:15")]
    [InlineData("Europe/London", "2026-01-15T21:15:30Z", "21:15")]
    [InlineData("Australia/Sydney", "2026-04-04T15:30:10Z", "02:30")]
    public async Task ImportedPlanKeepsItsExactInstant(string zoneId, string utc, string shownTime)
    {
        var viewModel = await PlannerInZoneAsync(zoneId);
        var instant = NodaTime.Text.InstantPattern.General.Parse(utc).Value;

        Assert.True(viewModel.ImportPlanImage(PlanImageSource.Of(PlanPng(PlanAt(instant)))).Imported);

        Assert.Equal(instant, viewModel.Session.Instant);
        Assert.Equal(shownTime, viewModel.TimeText);
        Assert.Equal(instant, viewModel.TimelineSelectedInstant);
        Assert.Equal(instant, viewModel.TimelineStart);
        Assert.Equal(instant, viewModel.CurrentPlan().TimeUtc);
        // Releasing the scrubber without moving it, or the debounced commit, does not rebuild it from the clock.
        viewModel.CommitTemporalPreview();
        Assert.Equal(instant, viewModel.Session.Instant);
    }

    [Fact]
    public async Task ImportedFoldInstantSurvivesAZoneThatShowsTheSameClockTime()
    {
        var viewModel = await PlannerInZoneAsync("Europe/London");
        var later = Instant.FromUtc(2026, 10, 25, 1, 30, 45);
        Assert.True(viewModel.ImportPlanImage(PlanImageSource.Of(PlanPng(PlanAt(later)))).Imported);

        // Lisbon reads the same clock time as London at this moment: the exact moment is kept.
        viewModel.TimeZoneId = "Europe/Lisbon";
        Assert.Equal(later, viewModel.Session.Instant);
        Assert.Equal("Europe/Lisbon", viewModel.Session.TimeZoneId);
    }

    [Fact]
    public async Task AWallClockEditStillResolvesThroughThePlansZone()
    {
        var viewModel = await PlannerInZoneAsync("Europe/London");
        Assert.True(viewModel.ImportPlanImage(PlanImageSource.Of(PlanPng(PlanAt(Instant.FromUtc(2026, 7, 1, 21, 15, 30))))).Imported);

        // A deliberate local edit: the bar's step moves the clock and the minute it shows is the moment.
        viewModel.StepTime(30);
        Assert.Equal("22:45", viewModel.TimeText);
        Assert.Equal(Instant.FromUtc(2026, 7, 1, 21, 45), viewModel.Session.Instant);

        // Typing a time that is ambiguous resolves to its earlier occurrence, as before.
        viewModel.SetPlannerDate(new DateTime(2026, 10, 25));
        viewModel.TimeText = "01:30";
        Assert.Equal(Instant.FromUtc(2026, 10, 25, 0, 30), viewModel.Session.Instant);
    }

    [Fact]
    public async Task ScrubbingThroughAnAutumnFoldKeepsTheSecondOccurrence()
    {
        var viewModel = await PlannerInZoneAsync("Europe/London");
        // Window from 00:00 BST on 25 October 2026 (23:00Z the day before).
        var start = Instant.FromUtc(2026, 10, 24, 23, 0);
        Assert.True(viewModel.ImportPlanImage(PlanImageSource.Of(PlanPng(PlanAt(start)))).Imported);
        Assert.Equal(start, viewModel.TimelineStart);

        // 150 minutes on is 01:30Z, the second 01:30 (GMT).
        viewModel.TimelineOffsetMinutes = 150;
        Assert.Equal("01:30", viewModel.TimeText);
        Assert.Equal(Instant.FromUtc(2026, 10, 25, 1, 30), viewModel.TimelineSelectedInstant);
        Assert.Equal(150, viewModel.TimelineOffsetMinutes, 6);
        viewModel.CommitTemporalPreview();
        Assert.Equal(Instant.FromUtc(2026, 10, 25, 1, 30), viewModel.Session.Instant);

        // 90 minutes on is 00:30Z, the first 01:30 (BST).
        viewModel.TimelineOffsetMinutes = 90;
        Assert.Equal("01:30", viewModel.TimeText);
        Assert.Equal(Instant.FromUtc(2026, 10, 25, 0, 30), viewModel.TimelineSelectedInstant);
        viewModel.CommitTemporalPreview();
        Assert.Equal(Instant.FromUtc(2026, 10, 25, 0, 30), viewModel.Session.Instant);
    }

    [Fact]
    public async Task ImportedPlanAtANewPlaceLoadsThatPlacesTerrain()
    {
        // Import moves the observer and then the time; the refresh that runs must still load the new place's
        // terrain (the D01 carry-on), not only its astronomy.
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new StagedPlanning(catalogue);
        var viewModel = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2026, 6, 1, 12, 0), "UTC"), null)),
            new FakeExporter());
        await viewModel.InitializeAsync();
        var plan = PlanAt(Instant.FromUtc(2026, 10, 7, 21, 0));

        Assert.True(viewModel.ImportPlanImage(PlanImageSource.Of(PlanPng(plan))).Imported);
        await WaitUntilAsync(() => planning.EnvironmentRequestCount > 0, TimeSpan.FromSeconds(2));
        var index = planning.EnvironmentRequestCount - 1;
        Assert.Equal(plan.Latitude, planning.EnvironmentSession(index).Observer.Latitude, 6);
        var refresh = viewModel.WaitForPlannerRefreshAsync();
        await WaitUntilAsync(() => planning.CoreRequestCount > 0 && planning.WeatherRequestCount > 0, TimeSpan.FromSeconds(2));
        planning.CompleteCore(planning.CoreRequestCount - 1);
        planning.CompleteWeather(planning.WeatherRequestCount - 1, DataState.Ready);
        planning.CompleteEnvironment(index, hasTerrain: true);
        await refresh;
        Assert.Equal(plan.Latitude, viewModel.Snapshot!.Environment!.ObserverCoordinate.Latitude, 6);
        Assert.Equal(plan.TimeUtc, viewModel.Snapshot.Session.Instant);
    }
}
