using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Time;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// A missing rise or set says why: the target stayed above the horizon all day, stayed below it, or
/// crossed it at a time that is outside the day.
/// </summary>
public sealed class DayHorizonTests
{
    private readonly AstronomyEngineService _astronomy = new(new TimeZoneResolver());
    private static readonly GeoCoordinate Wales = new(53.0, -3.95, 270);
    private static readonly LocalDate Date = new(2026, 10, 2);

    private TargetEvents Events(AstralTarget target, GeoCoordinate? observer = null) => _astronomy.Calculate(target, observer ?? Wales,
        Instant.FromUtc(2026, 10, 2, 21, 0), Date, "Europe/London").Events;

    /// <summary>M31 at UK latitudes never sets: no rise, no set, and the model knows it stayed up.</summary>
    [Fact]
    public void AnObjectThatNeverSetsHereIsAboveAllDay()
    {
        var m31 = new AstralTarget("m31", "Andromeda Galaxy", AstralTargetCategory.Galaxy, 0.712, 41.269, "J2000");
        var events = Events(m31);
        Assert.Null(events.Rise);
        Assert.Null(events.Set);
        Assert.NotNull(events.Transit);
        Assert.Equal(DayHorizon.AboveAllDay, events.Horizon);
    }

    [Fact]
    public void AnObjectThatNeverRisesHereIsBelowAllDay()
    {
        var southern = new AstralTarget("southern", "Far southern", AstralTargetCategory.Galaxy, 5.39, -69.75, "J2000");
        var events = Events(southern);
        Assert.Null(events.Rise);
        Assert.Null(events.Set);
        Assert.Equal(DayHorizon.BelowAllDay, events.Horizon);
    }

    [Fact]
    public void AnObjectThatRisesAndSetsCrosses()
    {
        var equatorial = new AstralTarget("equatorial", "Equatorial", AstralTargetCategory.Galaxy, 12, 0, "J2000");
        var events = Events(equatorial);
        Assert.Equal(DayHorizon.Crosses, events.Horizon);
        Assert.True(events.Rise is not null || events.Set is not null);
    }

    /// <summary>The Sun at midsummer inside the Arctic circle: up all day, by the Sun's own event search.</summary>
    [Fact]
    public void TheMidnightSunIsAboveAllDay()
    {
        var sun = new AstralTarget("sun", "Sun", AstralTargetCategory.Solar, null, null, "of-date");
        var events = _astronomy.Calculate(sun, new GeoCoordinate(78.2, 15.6, 10), Instant.FromUtc(2026, 6, 21, 12, 0),
            new LocalDate(2026, 6, 21), "Arctic/Longyearbyen").Events;
        Assert.Null(events.Rise);
        Assert.Null(events.Set);
        Assert.Equal(DayHorizon.AboveAllDay, events.Horizon);
    }
}
