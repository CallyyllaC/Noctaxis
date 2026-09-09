using NodaTime;

namespace Noctaxis.Core.Time;

public static class PlannerStartupTime
{
    public static Instant RoundedLocalHour(IClock clock, ITimeZoneResolver zones)
    {
        var now = clock.GetCurrentInstant();
        var zone = zones.Resolve(zones.MachineTimeZoneId);
        var local = now.InZone(zone).LocalDateTime;
        var floor = local.Date.At(new LocalTime(local.Hour, 0));
        // Consider both occurrences of an overlapping hour and omit nonexistent hours.
        // Resolving a rounded wall time leniently can otherwise move launch time by over an hour.
        Instant? nearest = null;
        var distance = double.PositiveInfinity;
        for (var hour = -2; hour <= 2; hour++)
        {
            var mapping = zone.MapLocal(floor.PlusHours(hour));
            if (mapping.Count == 0) continue;
            Consider(mapping.First().ToInstant());
            if (mapping.Count == 2) Consider(mapping.Last().ToInstant());
        }
        return nearest ?? now;

        void Consider(Instant candidate)
        {
            var difference = Math.Abs((candidate - now).TotalSeconds);
            if (difference < distance || (difference == distance && candidate > nearest!.Value))
            {
                nearest = candidate;
                distance = difference;
            }
        }
    }
}
