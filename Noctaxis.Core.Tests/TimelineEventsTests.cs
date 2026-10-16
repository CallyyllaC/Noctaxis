using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Time;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// The Timeline's Sun and Moon events are the plan's own per-day astronomy, kept to the 24-hour window, and its
/// astronomical darkness is the intervals those events bound.
/// </summary>
public sealed class TimelineEventsTests
{
    private static readonly Instant Start = Instant.FromUtc(2026, 10, 3, 10, 0);
    private static Instant At(double hours) => Start + Duration.FromMinutes(hours * 60);
    private static Instant End => Start + IPlanningService.TimelineLength;

    // Only the astronomy is used: the window's events need no terrain, weather or lens.
    private static PlanningService Service(IAstronomyService astronomy) =>
        new(new OpenNgcTargetCatalogue(), astronomy, new LensCalculator(), null!, null!, new TimeZoneResolver());

    /// <summary>
    /// Every event is inside the window and in time order, each kind at most once a day, and each is exactly the
    /// time the plan's own day calculation gives: sunrise, sunset and the astronomical dusk and dawn from the
    /// Sun's twilight, moonrise and moonset from the Moon's events.
    /// </summary>
    [Fact]
    public async Task TheEventsAreTheDaysOwnAstronomyInsideTheWindow()
    {
        var astronomy = new AstronomyEngineService(new TimeZoneResolver());
        var session = PlanningSession.Default(Start, "Europe/London") with { Observer = new GeoCoordinate(53.5, -0.5) };
        var result = await Service(astronomy).CalculateTimelineEventsAsync(session, Start, CancellationToken.None);

        Assert.NotEmpty(result.Events);
        Assert.All(result.Events, item => Assert.InRange(item.Instant, Start, End));
        Assert.Equal(result.Events.OrderBy(item => item.Instant), result.Events);
        var catalogue = new OpenNgcTargetCatalogue();
        var expected = new List<TimelineEvent>();
        foreach (var day in new[] { new LocalDate(2026, 10, 3), new LocalDate(2026, 10, 4) })
        {
            var twilight = astronomy.Calculate(catalogue.Get("sun"), session.Observer, Start, day, "Europe/London").Twilight!;
            var moon = astronomy.Calculate(catalogue.Get("moon"), session.Observer, Start, day, "Europe/London").Events;
            foreach (var (kind, instant) in new (TimelineEventKind, Instant?)[]
            {
                (TimelineEventKind.Sunrise, twilight.Sunrise), (TimelineEventKind.Sunset, twilight.Sunset),
                (TimelineEventKind.AstronomicalDarknessBegins, twilight.AstronomicalDusk),
                (TimelineEventKind.AstronomicalDarknessEnds, twilight.AstronomicalDawn),
                (TimelineEventKind.Moonrise, moon.Rise), (TimelineEventKind.Moonset, moon.Set)
            })
                if (instant is { } at && at >= Start && at <= End) expected.Add(new(kind, at));
        }
        Assert.Equal(expected.OrderBy(item => item.Instant).ToHashSet(), result.Events.ToHashSet());
        // An October night at 53.5°N: a sunset, a sunrise and one dark interval, between dusk and dawn.
        Assert.Single(result.Events, item => item.Kind == TimelineEventKind.Sunset);
        Assert.Single(result.Events, item => item.Kind == TimelineEventKind.Sunrise);
        Assert.False(result.DarkAtStart);
        var dark = Assert.Single(result.DarknessIntervals(Start, End));
        Assert.Equal(result.Events.Single(item => item.Kind == TimelineEventKind.AstronomicalDarknessBegins).Instant, dark.Start);
        Assert.Equal(result.Events.Single(item => item.Kind == TimelineEventKind.AstronomicalDarknessEnds).Instant, dark.End);
    }

    /// <summary>A window that opens in darkness starts dark: the Sun is then more than 18° below the horizon.</summary>
    [Fact]
    public async Task AWindowOpeningAtMidnightOpensDark()
    {
        var midnight = Instant.FromUtc(2026, 1, 10, 0, 0);
        var session = PlanningSession.Default(midnight, "UTC") with { Observer = new GeoCoordinate(53.5, -0.5) };
        var result = await Service(new AstronomyEngineService(new TimeZoneResolver())).CalculateTimelineEventsAsync(session, midnight, CancellationToken.None);
        Assert.True(result.DarkAtStart);
        var intervals = result.DarknessIntervals(midnight, midnight + IPlanningService.TimelineLength);
        Assert.Equal(midnight, intervals[0].Start);
    }

    /// <summary>
    /// The dark intervals inside a window: from its start when it opens dark, to its end when it closes dark, the
    /// whole window when it is dark throughout, none when it never gets dark, and each night on its own.
    /// </summary>
    [Fact]
    public void DarknessIntervalsAreBoundedByTheWindow()
    {
        TimelineEvent Begins(double hours) => new(TimelineEventKind.AstronomicalDarknessBegins, At(hours));
        TimelineEvent Ends(double hours) => new(TimelineEventKind.AstronomicalDarknessEnds, At(hours));

        Assert.Equal([(At(10), At(18))], new TimelineEvents([Begins(10), Ends(18)], false).DarknessIntervals(Start, End));
        Assert.Equal([(Start, At(5))], new TimelineEvents([Ends(5)], true).DarknessIntervals(Start, End));
        Assert.Equal([(At(20), End)], new TimelineEvents([Begins(20)], false).DarknessIntervals(Start, End));
        Assert.Equal([(Start, End)], new TimelineEvents([], true).DarknessIntervals(Start, End));
        Assert.Empty(new TimelineEvents([], false).DarknessIntervals(Start, End));
        Assert.Equal([(Start, At(2)), (At(22), End)], new TimelineEvents([Ends(2), Begins(22)], true).DarknessIntervals(Start, End));
        // Rise and set make no difference to darkness, and anything outside the window is ignored.
        Assert.Equal([(At(10), At(18))], new TimelineEvents([new(TimelineEventKind.Sunset, At(8)), Begins(10), Ends(18),
            new(TimelineEventKind.AstronomicalDarknessBegins, End + Duration.FromHours(1))], false).DarknessIntervals(Start, End));
    }
}
