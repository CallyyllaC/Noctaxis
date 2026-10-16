using Avalonia.Headless.XUnit;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Timeline's sky cues: sunrise and sunset, astronomical darkness as a faint band with its boundaries
/// marked, and moonrise and moonset while the Moon's own path is hidden. They are drawn from the window's own
/// events, only inside it, each at its exact time, and the chart reads them out with their times.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly Instant CueStart = Instant.FromUtc(2026, 10, 3, 10, 0);
    private static Instant CueAt(double hours) => CueStart + Duration.FromMinutes(hours * 60);

    private static TimelineEvents NightEvents(bool darkAtStart = false) => new([
        new(TimelineEventKind.Sunset, CueAt(8.5)), new(TimelineEventKind.AstronomicalDarknessBegins, CueAt(10.5)),
        new(TimelineEventKind.Moonrise, CueAt(13)), new(TimelineEventKind.AstronomicalDarknessEnds, CueAt(18)),
        new(TimelineEventKind.Sunrise, CueAt(20)), new(TimelineEventKind.Moonset, CueAt(22.5))], darkAtStart);

    /// <summary>The chart focused on something other than the Sun, so the Sun's own rise and set are cues.</summary>
    private static (HorizonGraph Graph, Avalonia.Controls.Window Window) CueChart(double width, TimelineEvents events, params string[] pathIds)
    {
        var (graph, window) = Chart(width, CueStart, CueAt(2));
        var catalogue = new OpenNgcTargetCatalogue();
        var m31 = catalogue.Get("m31");
        graph.Snapshot = graph.Snapshot! with { Position = graph.Snapshot.Position with { Target = m31 } };
        var template = graph.Paths![0].Path;
        graph.Paths = pathIds.Select(id => new TimelinePath(catalogue.Get(id), template)).ToArray();
        graph.Events = events;
        RenderChart(graph);
        return (graph, window);
    }

    private static double CueX(HorizonGraph graph, double hours) =>
        graph.LastLayout!.Plot.Left + graph.LastLayout.Plot.Width * hours / 24;

    /// <summary>
    /// Sunrise and sunset are short ticks on the time axis at their exact times, named with their times where
    /// there is room. Darkness's two boundaries are marked the same way.
    /// </summary>
    [AvaloniaFact]
    public void SunriseAndSunsetAreMarkedAtTheirTimes()
    {
        var (graph, window) = CueChart(720, NightEvents(), "m31", "moon");
        try
        {
            var marks = graph.LastLayout!.EventMarks;
            Assert.Equal(CueX(graph, 8.5), marks.Single(mark => mark.Kind == TimelineEventKind.Sunset).X, 3);
            Assert.Equal(CueX(graph, 20), marks.Single(mark => mark.Kind == TimelineEventKind.Sunrise).X, 3);
            Assert.Equal(CueX(graph, 10.5), marks.Single(mark => mark.Kind == TimelineEventKind.AstronomicalDarknessBegins).X, 3);
            Assert.Equal(CueX(graph, 18), marks.Single(mark => mark.Kind == TimelineEventKind.AstronomicalDarknessEnds).X, 3);
            Assert.Equal("Sunset 18:30", marks.Single(mark => mark.Kind == TimelineEventKind.Sunset).Label);
            Assert.Equal("Astro dark 20:30", marks.Single(mark => mark.Kind == TimelineEventKind.AstronomicalDarknessBegins).Label);
            // Labels never overlap one another, and all stay on the plot.
            var labels = marks.Where(mark => mark.LabelBounds is not null).Select(mark => mark.LabelBounds!.Value).ToArray();
            for (var i = 0; i < labels.Length; i++)
            {
                Assert.True(graph.LastLayout.Plot.Contains(labels[i]));
                for (var j = i + 1; j < labels.Length; j++) Assert.False(labels[i].Intersects(labels[j]));
            }

            // Focused on the Sun, its own rise and set marks are drawn instead, so the cues give way.
            graph.Snapshot = graph.Snapshot! with { Position = graph.Snapshot.Position with { Target = new OpenNgcTargetCatalogue().Get("sun") } };
            RenderChart(graph);
            Assert.DoesNotContain(graph.LastLayout!.EventMarks, mark => mark.Kind is TimelineEventKind.Sunrise or TimelineEventKind.Sunset);
            Assert.Contains(graph.LastLayout.EventMarks, mark => mark.Kind == TimelineEventKind.AstronomicalDarknessBegins);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Astronomical darkness is a band across the plot between its two boundaries; from the window's start when
    /// the window opens dark, to its end when it closes dark, across all of it when it never ends, and absent
    /// when it never begins.
    /// </summary>
    [AvaloniaFact]
    public void TheDarknessBandSpansExactlyTheDarkInterval()
    {
        var (graph, window) = CueChart(720, NightEvents(), "m31");
        try
        {
            var plot = graph.LastLayout!.Plot;
            var band = Assert.Single(graph.LastLayout.DarkBands);
            Assert.Equal(CueX(graph, 10.5), band.Left, 3);
            Assert.Equal(CueX(graph, 18), band.Right, 3);

            graph.Events = new TimelineEvents([new(TimelineEventKind.AstronomicalDarknessEnds, CueAt(5))], true);
            RenderChart(graph);
            band = Assert.Single(graph.LastLayout!.DarkBands);
            Assert.Equal(plot.Left, band.Left, 3);
            Assert.Equal(CueX(graph, 5), band.Right, 3);

            graph.Events = new TimelineEvents([new(TimelineEventKind.AstronomicalDarknessBegins, CueAt(21))], false);
            RenderChart(graph);
            band = Assert.Single(graph.LastLayout!.DarkBands);
            Assert.Equal(CueX(graph, 21), band.Left, 3);
            Assert.Equal(plot.Right, band.Right, 3);

            graph.Events = new TimelineEvents([], true);
            RenderChart(graph);
            band = Assert.Single(graph.LastLayout!.DarkBands);
            Assert.Equal((plot.Left, plot.Right), (band.Left, band.Right));

            graph.Events = new TimelineEvents([], false);
            RenderChart(graph);
            Assert.Empty(graph.LastLayout!.DarkBands);
            Assert.Empty(graph.LastLayout.EventMarks);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Moonrise and moonset are cued only while the Moon's own path is hidden: a shown path already crosses the
    /// horizon there, so the cue would only repeat it.
    /// </summary>
    [AvaloniaFact]
    public void MoonEventsAreCuedOnlyWhileTheMoonPathIsHidden()
    {
        var (graph, window) = CueChart(720, NightEvents(), "m31");
        try
        {
            Assert.Equal(CueX(graph, 13), graph.LastLayout!.EventMarks.Single(mark => mark.Kind == TimelineEventKind.Moonrise).X, 3);
            Assert.Equal(CueX(graph, 22.5), graph.LastLayout.EventMarks.Single(mark => mark.Kind == TimelineEventKind.Moonset).X, 3);

            var catalogue = new OpenNgcTargetCatalogue();
            graph.Paths = [.. graph.Paths!, new TimelinePath(catalogue.Get("moon"), graph.Paths![0].Path)];
            RenderChart(graph);
            Assert.DoesNotContain(graph.LastLayout!.EventMarks, mark => mark.Kind is TimelineEventKind.Moonrise or TimelineEventKind.Moonset);
            Assert.Contains(graph.LastLayout.EventMarks, mark => mark.Kind == TimelineEventKind.Sunset);
        }
        finally { window.Close(); }
    }

    /// <summary>Events outside the window are never drawn, at either end; one exactly at an edge is.</summary>
    [AvaloniaFact]
    public void NoEventOutsideTheWindowIsDrawn()
    {
        var (graph, window) = CueChart(720, new TimelineEvents([
            new(TimelineEventKind.Sunset, CueStart - Duration.FromMinutes(1)), new(TimelineEventKind.Sunrise, CueAt(24) + Duration.FromMinutes(1)),
            new(TimelineEventKind.Moonrise, CueAt(24)), new(TimelineEventKind.Moonset, CueStart)], false), "m31");
        try
        {
            Assert.Equal([TimelineEventKind.Moonrise, TimelineEventKind.Moonset],
                graph.LastLayout!.EventMarks.Select(mark => mark.Kind).Order());
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// At a narrow width the cues stay: each tick is drawn, and a label gives way to the shorter name, then to
    /// none, rather than overlapping another.
    /// </summary>
    [AvaloniaFact]
    public void NarrowChartsShortenOrDropLabelsRatherThanOverlap()
    {
        var crowded = new TimelineEvents([new(TimelineEventKind.Sunset, CueAt(8.5)), new(TimelineEventKind.AstronomicalDarknessBegins, CueAt(9.4)),
            new(TimelineEventKind.Moonrise, CueAt(9.8))], false);
        var (graph, window) = CueChart(320, crowded, "m31");
        try
        {
            var marks = graph.LastLayout!.EventMarks;
            Assert.Equal(3, marks.Count);
            Assert.Equal("Sunset 18:30", marks.Single(mark => mark.Kind == TimelineEventKind.Sunset).Label);
            Assert.Contains(marks, mark => mark.Label is null);
            var labels = marks.Where(mark => mark.LabelBounds is not null).Select(mark => mark.LabelBounds!.Value).ToArray();
            Assert.False(labels.Length > 1 && labels[0].Intersects(labels[1]));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The window's events are calculated with its paths, once per window: scrubbing within it calculates
    /// nothing. The chart reads them out with their times after the chosen moment; the band adds nothing more.
    /// </summary>
    [Fact]
    public async Task TheWindowsEventsAreCalculatedOnceAndReadOutWithTheirTimes()
    {
        var (vm, planning) = await TimelinePlannerAsync();
        planning.TimelineEventsFor = start => new TimelineEvents([
            new(TimelineEventKind.Sunset, start + Duration.FromMinutes(37)),
            new(TimelineEventKind.AstronomicalDarknessBegins, start + Duration.FromMinutes(2 * 60 + 34)),
            new(TimelineEventKind.Moonrise, start + Duration.FromMinutes(3 * 60 + 17))], false);
        // Focused on the Moon, so the Sun's rise and set are cues (the Moon's path is shown, so its rise is not).
        Target(vm, "moon").FocusCommand.Execute(null);
        vm.SetPlannerTime(new TimeSpan(18, 0, 0));
        await Until(() => vm.TimelineEvents.Events.Count == 3 && vm.Snapshot?.Position.Target.Id == "moon", "the window's events should arrive");
        var calculations = planning.TimelineEventCalculations;
        Assert.Equal(vm.TimelineStart + Duration.FromMinutes(37), vm.TimelineEvents.Events[0].Instant);

        vm.TimelineOffsetMinutes = 90;
        vm.TimelineOffsetMinutes = 240;
        Assert.Equal(calculations, planning.TimelineEventCalculations);
        Assert.Equal(Instant.FromUtc(2026, 3, 5, 18, 0), vm.TimelineStart);

        Assert.StartsWith(vm.TimelineSelectionText + ". ", vm.TimelineChartDescription);
        Assert.Contains("Sunset 18:37", vm.TimelineChartDescription);
        Assert.Contains("Astronomical darkness begins 20:34", vm.TimelineChartDescription);
        Assert.DoesNotContain("Moonrise", vm.TimelineChartDescription);
    }
}
