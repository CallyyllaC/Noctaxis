using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Timeline: the 24 hours on from where the plan's moment was last set in the bar, one scrubber that moves
/// that moment within them (across midnight, with the bar following), and a chart with a clear horizon, a few
/// altitude and hour guides, midnight dated, the selected moment as the one strong cursor and the real present
/// moment drawn faintly, only when it falls inside the window.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly Instant TimelineAnchor = Instant.FromUtc(2026, 3, 5, 21, 0);

    private static async Task<(Noctaxis.Desktop.ViewModels.MainViewModel ViewModel, FakePlanning Planning)> TimelinePlannerAsync(Instant? now = null)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var vm = CreateViewModel(planning, catalogue, CreatePlannerStore(new AppSettings(SelectedTimeZoneId: "UTC", TimeSnapMinutes: 5)),
            new FakeExporter(), clock: new FixedClock(now ?? TimelineAnchor));
        await vm.InitializeAsync();
        return (vm, planning);
    }

    /// <summary>The window is exactly 24 hours, from where the moment was set; it may cross midnight.</summary>
    [Fact]
    public async Task TheWindowRunsTwentyFourHoursOnFromTheMoment()
    {
        var (vm, planning) = await TimelinePlannerAsync();
        Assert.Equal(TimelineAnchor, vm.TimelineStart);
        Assert.Equal(Duration.FromHours(24), vm.TimelineEnd - vm.TimelineStart);
        Assert.Equal(0, vm.TimelineOffsetMinutes);
        await Until(() => vm.TimelinePaths.Count > 0, "the window's paths should arrive");
        Assert.Equal(TimelineAnchor, planning.LastTimelineStart);
        Assert.All(vm.TimelinePaths, path =>
        {
            Assert.Equal(TimelineAnchor, path.Path.Samples[0].Instant);
            Assert.Equal(TimelineAnchor + Duration.FromHours(24), path.Path.Samples[^1].Instant);
        });
    }

    /// <summary>Every direct change from the bar — a date, a time, a step, a day, Now — re-anchors the window there.</summary>
    [Fact]
    public async Task EveryChangeFromTheBarReanchorsTheWindow()
    {
        var (vm, planning) = await TimelinePlannerAsync(now: Instant.FromUtc(2026, 10, 3, 10, 7));
        vm.SetPlannerTime(new TimeSpan(14, 0, 0));
        Assert.Equal(Instant.FromUtc(2026, 10, 3, 14, 0), vm.TimelineStart);
        vm.StepTime(30);
        Assert.Equal(Instant.FromUtc(2026, 10, 3, 14, 30), vm.TimelineStart);
        vm.NextDayCommand.Execute(null);
        Assert.Equal(Instant.FromUtc(2026, 10, 4, 14, 30), vm.TimelineStart);
        vm.SetPlannerDate(new DateTime(2026, 4, 1));
        Assert.Equal(Instant.FromUtc(2026, 4, 1, 14, 30), vm.TimelineStart);
        vm.NowCommand.Execute(null);
        Assert.Equal(Instant.FromUtc(2026, 10, 3, 10, 7), vm.TimelineStart);
        Assert.All(new[] { vm.TimelineOffsetMinutes }, offset => Assert.Equal(0, offset));
        await Until(() => planning.LastTimelineStart == vm.TimelineStart, "the new window's paths should be calculated");
    }

    /// <summary>
    /// Scrubbing moves the Planner's one moment within the window — across midnight, the bar's date following —
    /// and never moves the window, while it moves or after it is let go; nor does it recalculate the paths.
    /// </summary>
    [Fact]
    public async Task ScrubbingMovesTheMomentAcrossMidnightAndNeverTheWindow()
    {
        var (vm, planning) = await TimelinePlannerAsync();
        await Until(() => vm.TimelinePaths.Count > 0, "the window's paths should arrive");
        var calculations = planning.TimelineCalculations;

        vm.TimelineOffsetMinutes = 4 * 60 + 2; // 01:02 the next morning, on the 5-minute snap: 01:00.
        Assert.Equal("01:00", vm.TimeText);
        Assert.Equal(new DateTime(2026, 3, 6), vm.LocalDate!.Value.Date);
        Assert.Equal("06 Mar 2026", vm.PlannerDateText);
        Assert.Equal(240, vm.TimelineOffsetMinutes);
        Assert.Equal(TimelineAnchor, vm.TimelineStart);
        Assert.Equal("06 Mar 01:00", vm.TimelineSelectionText);

        vm.CommitTemporalPreview();
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(Instant.FromUtc(2026, 3, 6, 1, 0), vm.Session.Instant);
        Assert.Equal(TimelineAnchor, vm.TimelineStart);
        vm.TimelineOffsetMinutes = 23 * 60;
        vm.CommitTemporalPreview();
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal(TimelineAnchor, vm.TimelineStart);
        Assert.Equal(calculations, planning.TimelineCalculations);

        // Never past either end.
        vm.TimelineOffsetMinutes = 99999;
        Assert.Equal(24 * 60, vm.TimelineOffsetMinutes);
        Assert.Equal("21:00", vm.TimeText);
    }

    // ---- The chart --------------------------------------------------------------------------------------

    private static PlanningSnapshot ChartSnapshot(Instant selected)
    {
        var target = new OpenNgcTargetCatalogue().Get("sun");
        var position = new TargetPosition(target, selected, new HorizontalCoordinate(180, 20), new TargetEvents(null, null, null));
        var session = PlanningSession.Default(selected, "UTC");
        var path = new AstralPath(new LocalDate(2026, 10, 3), "UTC", Duration.FromMinutes(10),
            [new AstralPathSample(selected, position.Horizontal)], position.Events, selected);
        return new PlanningSnapshot(session, position, path, new FieldOfView(60, 40, 70),
            new TerrainHorizonProfile(session.Observer, [], false, "Flat", selected), new TerrainCrossings(null, null),
            new WeatherResult(DataState.Loading, null, ""), new AstronomyContext(position, position));
    }

    private static TimelinePath ChartPath(Instant start)
    {
        var target = new OpenNgcTargetCatalogue().Get("sun");
        var samples = Enumerable.Range(0, 145).Select(step => new AstralPathSample(start + Duration.FromMinutes(10 * step),
            new HorizontalCoordinate(step, 50 * Math.Sin(step / 144d * 2 * Math.PI)))).ToArray();
        return new TimelinePath(target, new AstralPath(new LocalDate(2026, 10, 3), "UTC", Duration.FromMinutes(10), samples,
            new TargetEvents(null, null, null), start));
    }

    private static (HorizonGraph Graph, Window Window) Chart(double width, Instant start, Instant selected, Instant? now = null)
    {
        var graph = new HorizonGraph
        {
            Width = width, Height = 140, Snapshot = ChartSnapshot(selected), Paths = [ChartPath(start)],
            WindowStart = start, WindowEnd = start + Duration.FromHours(24), Zone = DateTimeZone.Utc, RealNow = now
        };
        var window = new Window { Width = width + 40, Height = 220, Content = graph };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        // Render a frame, so the chart records what it drew.
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)width, 140));
        bitmap.Render(graph);
        return (graph, window);
    }

    /// <summary>
    /// The hour scale: labels on whole hours with room between them, fewer as the chart narrows, and midnight a
    /// firmer divider labelled with its date at every width.
    /// </summary>
    [AvaloniaFact]
    public void TheHourScaleDatesMidnightAndNeverOverlaps()
    {
        var start = Instant.FromUtc(2026, 10, 3, 10, 0);
        var counts = new List<int>();
        foreach (var width in new[] { 900d, 520, 300 })
        {
            var (graph, window) = Chart(width, start, start + Duration.FromHours(8));
            try
            {
                var labels = graph.LastLayout!.TimeLabels;
                var midnight = Assert.Single(labels, label => label.IsMidnight);
                Assert.Equal("04 Oct", midnight.Text);
                // Midnight is 14 hours into the window.
                Assert.Equal(graph.LastLayout.Plot.Left + graph.LastLayout.Plot.Width * 14 / 24, midnight.Bounds.Center.X, 0);
                for (var i = 0; i < labels.Count; i++)
                for (var j = i + 1; j < labels.Count; j++)
                    Assert.False(labels[i].Bounds.Intersects(labels[j].Bounds), $"{labels[i].Text} overlaps {labels[j].Text} at {width}");
                Assert.All(labels.Where(label => !label.IsMidnight), label => Assert.Matches("^[0-9]{2}:00$", label.Text));
                counts.Add(labels.Count);
            }
            finally { window.Close(); }
        }
        Assert.True(counts[0] >= counts[1] && counts[1] >= counts[2] && counts[0] > counts[2], $"labels by width: {string.Join(", ", counts)}");
    }

    /// <summary>
    /// The selected moment's cursor stands where the moment is in the window. The real present moment is marked
    /// only when it falls inside the window.
    /// </summary>
    [AvaloniaFact]
    public void TheCursorMarksTheMomentAndRealNowOnlyInsideTheWindow()
    {
        var start = Instant.FromUtc(2026, 10, 3, 10, 0);
        var (graph, window) = Chart(720, start, start + Duration.FromMinutes(8 * 60 + 30), now: start + Duration.FromHours(2));
        try
        {
            var layout = graph.LastLayout!;
            Assert.Equal(layout.Plot.Left + layout.Plot.Width * 8.5 / 24, layout.CursorX!.Value, 3);
            Assert.Equal(layout.Plot.Left + layout.Plot.Width * 2 / 24, layout.RealNowX!.Value, 3);
            graph.RealNow = start - Duration.FromHours(1);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(720, 140));
            bitmap.Render(graph);
            Assert.Null(graph.LastLayout!.RealNowX);
            Assert.NotNull(graph.LastLayout.CursorX);
        }
        finally { window.Close(); }
    }

    /// <summary>The 0° horizon and the 30° and 60° guides, each where its altitude is; the horizon below the others.</summary>
    [AvaloniaFact]
    public void TheHorizonAndAltitudeGuidesAreWhereTheirAltitudesAre()
    {
        var start = Instant.FromUtc(2026, 10, 3, 10, 0);
        var (graph, window) = Chart(720, start, start);
        try
        {
            var layout = graph.LastLayout!;
            Assert.Equal(new[] { 0d, 30, 60 }, layout.Guides.Select(guide => guide.Altitude));
            double Y(double altitude) => layout.Plot.Bottom - (altitude - HorizonGraph.LowestAltitude) /
                (HorizonGraph.HighestAltitude - HorizonGraph.LowestAltitude) * layout.Plot.Height;
            Assert.All(layout.Guides, guide => Assert.Equal(Y(guide.Altitude), guide.Y, 3));
            Assert.Equal(Y(0), layout.HorizonY, 3);
            Assert.True(layout.Guides[0].Y > layout.Guides[1].Y && layout.Guides[1].Y > layout.Guides[2].Y);
        }
        finally { window.Close(); }
    }

    // ---- The panel: the chart is the scrubber ------------------------------------------------------------

    private static HorizonGraph TimelineChart(Window window) => window.FindControl<HorizonGraph>("TimelineChart")!;

    private static void RenderChart(HorizonGraph chart)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)chart.Bounds.Width, (int)chart.Bounds.Height));
        bitmap.Render(chart);
    }

    /// <summary>A point on the chart, in the window, at a share of the plot's width and a share of its height.</summary>
    private static Point OnPlot(Window window, HorizonGraph chart, double across, double down)
    {
        var plot = chart.PlotRectangle;
        return chart.TranslatePoint(new Point(plot.Left + plot.Width * across, plot.Top + plot.Height * down), window)!.Value;
    }

    /// <summary>
    /// The panel is the chart alone: no slider, no date or time of its own (the bar has them), and compact. The
    /// chart takes the keyboard, is named, and says its moment in words for assistive technology.
    /// </summary>
    [AvaloniaFact]
    public async Task ThePanelIsTheChartAlone()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var markup = TestMarkup.ReadMainWindow();
            foreach (var retired in new[] { "Nearby date", "PreviewDateOffsetDays", "PreviewMinutesOfDay", "TimelineScrubber", "TimeSliderReleased" })
                Assert.DoesNotContain(retired, markup);
            var host = window.GetVisualDescendants().OfType<Controls.Docking.PlannerPanelHost>().Single(panel => panel.PanelId == "timeline");
            Assert.Empty(host.GetVisualDescendants().OfType<Slider>());
            Assert.DoesNotContain(host.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.IsEffectivelyVisible && (text.Text == vm.PlannerDateText || text.Text == vm.TimeText));
            Assert.Equal(275, vm.PlannerDock["timeline"]!.DockedSize);
            var chart = TimelineChart(window);
            Assert.True(chart.Focusable);
            Assert.Equal("Time on the timeline", Avalonia.Automation.AutomationProperties.GetName(chart));
            Assert.Equal(vm.TimelineSelectionText, Avalonia.Automation.AutomationProperties.GetHelpText(chart));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// A tap or click anywhere on the plot moves the moment to that time across it (its height makes no
    /// difference) and commits it, leaving the window where it was. The cursor stands where the moment is.
    /// </summary>
    [AvaloniaFact]
    public async Task ATapSeeksByTimeAcrossTheChartAlone()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var chart = TimelineChart(window);
            var anchor = vm.TimelineStart;
            foreach (var down in new[] { .1, .5, .95 })
            {
                var at = OnPlot(window, chart, .25, down);
                window.MouseDown(at, Avalonia.Input.MouseButton.Left);
                window.MouseUp(at, Avalonia.Input.MouseButton.Left);
                Settle(window);
                Assert.Equal(6 * 60, vm.TimelineOffsetMinutes, 0);
            }
            await vm.WaitForPlannerRefreshAsync();
            Assert.Equal(anchor + Duration.FromHours(6), vm.Session.Instant);
            Assert.Equal(anchor, vm.TimelineStart);
            RenderChart(chart);
            Assert.Equal(chart.PlotRectangle.Left + chart.PlotRectangle.Width / 4, chart.LastLayout!.CursorX!.Value, 0);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// A drag scrubs continuously by X, across midnight (the bar's date follows) and no further than the window's
    /// end, however far it goes; the window never moves.
    /// </summary>
    [AvaloniaFact]
    public async Task ADragScrubsAcrossMidnightAndStopsAtTheWindowsEnd()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var chart = TimelineChart(window);
            var anchor = vm.TimelineStart;
            var startDate = vm.LocalDate!.Value.Date;
            var from = OnPlot(window, chart, .05, .5);
            window.MouseDown(from, Avalonia.Input.MouseButton.Left);
            var previous = vm.TimelineOffsetMinutes;
            for (var step = 1; step <= 6; step++)
            {
                window.MouseMove(OnPlot(window, chart, .05 + step * .15, step % 2 == 0 ? .1 : .9), Avalonia.Input.RawInputModifiers.LeftMouseButton);
                Settle(window);
                Assert.True(vm.TimelineOffsetMinutes > previous, $"step {step} should move the moment on");
                previous = vm.TimelineOffsetMinutes;
                Assert.Equal(anchor, vm.TimelineStart);
            }
            // Past the end: held at the window's end.
            window.MouseMove(OnPlot(window, chart, 1.4, .5), Avalonia.Input.RawInputModifiers.LeftMouseButton);
            Settle(window);
            Assert.Equal(24 * 60, vm.TimelineOffsetMinutes);
            window.MouseUp(OnPlot(window, chart, 1.4, .5), Avalonia.Input.MouseButton.Left);
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            Assert.Equal(anchor + Duration.FromHours(24), vm.Session.Instant);
            Assert.Equal(startDate.AddDays(1), vm.LocalDate!.Value.Date);
            Assert.Equal(anchor, vm.TimelineStart);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The keyboard: the chart takes focus with Tab; Left and Right move the moment one time snap, Page Up and
    /// Page Down an hour. The cursor's tab is on the cursor, and its time stays on the plot at both ends.
    /// </summary>
    [AvaloniaFact]
    public async Task TheCursorsTabTakesTheKeyboardAndItsTimeStaysOnThePlot()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var chart = TimelineChart(window);
            Assert.True(chart.Focus(Avalonia.Input.NavigationMethod.Tab));
            void Press(Avalonia.Input.PhysicalKey key)
            {
                window.KeyPressQwerty(key, Avalonia.Input.RawInputModifiers.None);
                window.KeyReleaseQwerty(key, Avalonia.Input.RawInputModifiers.None);
                Settle(window);
            }
            Press(Avalonia.Input.PhysicalKey.ArrowRight);
            Assert.Equal(vm.TimelineStepMinutes, vm.TimelineOffsetMinutes);
            Press(Avalonia.Input.PhysicalKey.PageUp);
            Assert.Equal(vm.TimelineStepMinutes + 60, vm.TimelineOffsetMinutes);
            Press(Avalonia.Input.PhysicalKey.ArrowLeft);
            Assert.Equal(60, vm.TimelineOffsetMinutes);
            Press(Avalonia.Input.PhysicalKey.PageDown);
            Assert.Equal(0, vm.TimelineOffsetMinutes);

            foreach (var end in new[] { 0d, 24 * 60 })
            {
                vm.TimelineOffsetMinutes = end;
                Settle(window);
                RenderChart(chart);
                var layout = chart.LastLayout!;
                Assert.NotNull(layout.Handle);
                Assert.Equal(layout.CursorX!.Value, layout.Handle!.Value.Center.X, 3);
                Assert.True(layout.CursorLabel!.Value.Left >= layout.Plot.Left - .5 && layout.CursorLabel.Value.Right <= layout.Plot.Right + .5,
                    $"the time {layout.CursorLabel} should stay on the plot {layout.Plot}");
                Assert.False(layout.CursorLabel.Value.Intersects(layout.Handle.Value));
            }
        }
        finally { Close(window); }
    }

    // ---- The hour grid ----------------------------------------------------------------------------------

    /// <summary>
    /// A major guide every 3 hours, a minor one on each other whole hour, never both at one hour, and midnight's
    /// divider in place of either; narrow, the minors go and the majors and midnight stay.
    /// </summary>
    [AvaloniaFact]
    public void MinorHourlyGuidesFillBetweenTheMajorsUntilTheChartIsTooNarrow()
    {
        var start = Instant.FromUtc(2026, 10, 3, 10, 0);
        var (graph, window) = Chart(900, start, start);
        try
        {
            var layout = graph.LastLayout!;
            double XAt(int hoursIn) => layout.Plot.Left + layout.Plot.Width * hoursIn / 24;
            // The window opens at 10:00; the whole hours in it are 11:00 to 09:00 the next day.
            var majors = Enumerable.Range(1, 23).Where(h => (10 + h) % 3 == 0 && (10 + h) % 24 != 0).Select(XAt).ToArray();
            var minors = Enumerable.Range(1, 23).Where(h => (10 + h) % 3 != 0).Select(XAt).ToArray();
            Assert.Equal(majors, layout.MajorGuideXs, new Tolerance(.01));
            Assert.Equal(minors, layout.MinorGuideXs, new Tolerance(.01));
            Assert.Equal(new[] { XAt(14) }, layout.MidnightXs, new Tolerance(.01));
            Assert.Empty(layout.MajorGuideXs.Intersect(layout.MinorGuideXs));
            Assert.DoesNotContain(layout.MidnightXs[0], layout.MajorGuideXs.Concat(layout.MinorGuideXs));
        }
        finally { window.Close(); }

        var (narrow, narrowWindow) = Chart(200, start, start);
        try
        {
            var layout = narrow.LastLayout!;
            Assert.True(layout.Plot.Width / 24 < HorizonGraph.MinimumMinorGuideSpacing);
            Assert.Empty(layout.MinorGuideXs);
            Assert.Equal(7, layout.MajorGuideXs.Count);
            Assert.Single(layout.MidnightXs);
        }
        finally { narrowWindow.Close(); }
    }

    private sealed class Tolerance(double within) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= within;
        public int GetHashCode(double value) => 0;
    }
}
