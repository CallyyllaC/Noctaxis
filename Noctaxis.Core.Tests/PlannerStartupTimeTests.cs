using NodaTime;
using NodaTime.Text;
using Noctaxis.Core.Time;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Tests;

public sealed class PlannerStartupTimeTests
{
    [Theory]
    [InlineData("2026-10-25T01:10:00Z", "2026-10-25T01:00:00Z")]
    [InlineData("2026-10-25T00:40:00Z", "2026-10-25T01:00:00Z")]
    [InlineData("2026-03-29T00:40:00Z", "2026-03-29T01:00:00Z")]
    public void StartupUsesNearestActualHourAcrossClockChanges(string input, string expected)
    {
        var zones = new FixedZones(DateTimeZoneProviders.Tzdb["Europe/London"]);
        var actual = PlannerStartupTime.RoundedLocalHour(
            new FixedClock(InstantPattern.ExtendedIso.Parse(input).Value), zones);
        Assert.Equal(InstantPattern.ExtendedIso.Parse(expected).Value, actual);
    }

    [Theory]
    [InlineData("2026-09-06T10:29:59", "2026-09-06T10:00:00")]
    [InlineData("2026-09-06T10:30:00", "2026-09-06T11:00:00")]
    [InlineData("2026-09-06T10:31:00", "2026-09-06T11:00:00")]
    [InlineData("2026-09-06T10:00:00", "2026-09-06T10:00:00")]
    [InlineData("2026-09-30T23:30:00", "2026-10-01T00:00:00")]
    [InlineData("2026-12-31T23:59:59", "2027-01-01T00:00:00")]
    [InlineData("2028-02-28T23:30:00", "2028-02-29T00:00:00")]
    public void StartupRoundsMachineLocalHour(string input, string expected)
    {
        // Non-integral UTC offset proves rounding occurs in local time, not UTC.
        var zones = new FixedZones(DateTimeZone.ForOffset(Offset.FromHoursAndMinutes(5, 45)));
        var local = LocalDateTimePattern.ExtendedIso.Parse(input).Value;
        var clock = new FixedClock(local.InZoneLeniently(zones.Zone).ToInstant());
        var actual = PlannerStartupTime.RoundedLocalHour(clock, zones);
        Assert.Equal(LocalDateTimePattern.ExtendedIso.Parse(expected).Value,
            actual.InZone(zones.Zone).LocalDateTime);
    }

    [Fact]
    public void DisabledFallbackIsDistinctFromMeasuredZeroAndPreservesManualOverride()
    {
        var measured = new ObserverElevationState(0);
        Assert.Equal(TerrainElevationResolutionState.TerrainResolved, measured.ResolutionState);
        var disabled = new ObserverElevationState(345) { TerrainCalculationsEnabled = false };
        Assert.Equal(TerrainElevationResolutionState.TerrainDisabledFallback, disabled.ResolutionState);
        Assert.Equal(0, disabled.ResolvedGroundElevationAslMetres);
        Assert.Equal(1.7, disabled.EffectiveObserverAltitudeAsl(345, 1.7), 8);
        var manual = disabled.WithManualOverride(120);
        Assert.True(manual.IsManualOverride);
        Assert.Equal(121.7, manual.EffectiveObserverAltitudeAsl(345, 1.7), 8);
        Assert.Equal(0, manual.ResetManualOverride().ResolvedGroundElevationAslMetres);
    }

    private sealed class FixedClock(Instant instant) : IClock
    {
        public Instant GetCurrentInstant() => instant;
    }
    private sealed class FixedZones(DateTimeZone zone) : ITimeZoneResolver
    {
        public DateTimeZone Zone => zone;
        public string MachineTimeZoneId => zone.Id;
        public IReadOnlyList<string> AvailableIds => [zone.Id];
        public string GetEffectiveId(string? requestedId) => zone.Id;
        public DateTimeZone Resolve(string? requestedId) => zone;
        public ZonedDateTime InZone(Instant instant, string? requestedId) => instant.InZone(zone);
        public Instant ResolveLocal(LocalDate date, LocalTime time, string? requestedId) => date.At(time).InZoneLeniently(zone).ToInstant();
        public (Instant Start, Instant End) GetLocalDay(LocalDate date, string? requestedId) =>
            (zone.AtStartOfDay(date).ToInstant(), zone.AtStartOfDay(date.PlusDays(1)).ToInstant());
    }
}
