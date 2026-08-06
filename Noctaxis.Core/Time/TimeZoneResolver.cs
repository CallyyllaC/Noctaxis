using NodaTime;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Time;

public interface ITimeZoneResolver
{
    string MachineTimeZoneId { get; }
    string GetEffectiveId(string? requestedId);
    DateTimeZone Resolve(string? requestedId);
    ZonedDateTime InZone(Instant instant, string? requestedId);
    Instant ResolveLocal(LocalDate date, LocalTime time, string? requestedId);
    (Instant Start, Instant End) GetLocalDay(LocalDate date, string? requestedId);
    IReadOnlyList<string> AvailableIds { get; }
}

public sealed class TimeZoneResolver : ITimeZoneResolver
{
    public string MachineTimeZoneId => TimeZoneInfo.Local.Id;

    public IReadOnlyList<string> AvailableIds { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => zone.Id)
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string GetEffectiveId(string? requestedId)
    {
        if (string.IsNullOrWhiteSpace(requestedId) ||
            requestedId.Equals(AppSettings.UseSystemTimeZoneId, StringComparison.OrdinalIgnoreCase))
            return MachineTimeZoneId;

        return TryResolve(requestedId) is null ? MachineTimeZoneId : requestedId;
    }

    public DateTimeZone Resolve(string? requestedId)
    {
        var zone = TryResolve(GetEffectiveId(requestedId));
        if (zone is not null) return zone;

        return DateTimeZoneProviders.Bcl.GetZoneOrNull(MachineTimeZoneId)
               ?? DateTimeZoneProviders.Tzdb.GetSystemDefault();
    }

    private static DateTimeZone? TryResolve(string requestedId) =>
        DateTimeZoneProviders.Bcl.GetZoneOrNull(requestedId)
        ?? DateTimeZoneProviders.Tzdb.GetZoneOrNull(requestedId);

    public ZonedDateTime InZone(Instant instant, string? requestedId) => instant.InZone(Resolve(requestedId));

    public Instant ResolveLocal(LocalDate date, LocalTime time, string? requestedId) =>
        date.At(time).InZoneLeniently(Resolve(requestedId)).ToInstant();

    public (Instant Start, Instant End) GetLocalDay(LocalDate date, string? requestedId)
    {
        var zone = Resolve(requestedId);
        return (zone.AtStartOfDay(date).ToInstant(), zone.AtStartOfDay(date.PlusDays(1)).ToInstant());
    }
}
