using NodaTime;
using NodaTime.TimeZones;
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

    /// <summary>
    /// The system's zones, always including the machine's own: macOS can report the machine's zone by an alias
    /// ("Universal") that its list of zones leaves out.
    /// </summary>
    public IReadOnlyList<string> AvailableIds { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => zone.Id)
        .Append(TimeZoneInfo.Local.Id)
        .Distinct(StringComparer.OrdinalIgnoreCase)
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
        ?? ResolveWithoutSystemZones(requestedId);

    /// <summary>
    /// Resolves an identifier from NodaTime's own data only. A zone saved on Windows carries a Windows identifier
    /// ("GMT Standard Time"); on a system whose time-zone support cannot read it (Linux without ICU, for example)
    /// it is mapped through the CLDR Windows mapping bundled with NodaTime, so a saved location keeps its zone
    /// instead of silently taking the machine's.
    /// </summary>
    internal static DateTimeZone? ResolveWithoutSystemZones(string requestedId) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(requestedId)
        ?? (TzdbDateTimeZoneSource.Default.WindowsMapping.PrimaryMapping.TryGetValue(requestedId, out var iana)
            ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(iana)
            : null);

    public ZonedDateTime InZone(Instant instant, string? requestedId) => instant.InZone(Resolve(requestedId));

    public Instant ResolveLocal(LocalDate date, LocalTime time, string? requestedId) =>
        date.At(time).InZoneLeniently(Resolve(requestedId)).ToInstant();

    public (Instant Start, Instant End) GetLocalDay(LocalDate date, string? requestedId)
    {
        var zone = Resolve(requestedId);
        return (zone.AtStartOfDay(date).ToInstant(), zone.AtStartOfDay(date.PlusDays(1)).ToInstant());
    }
}
