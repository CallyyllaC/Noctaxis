using System.Globalization;
using NodaTime;

namespace Noctaxis.Core.Time;

/// <summary>
/// How a resolved time zone is named to the user at a given moment: its short name there and then (GMT or
/// BST for Europe/London, depending on the date) and the zone's identifier for the longer detail. A machine
/// zone with a Windows identifier is named by its IANA equivalent, so the same zone reads the same on every
/// system. A zone with no short name of its own is named by its offset from UTC.
/// </summary>
public static class TimeZoneDisplay
{
    public static (string ShortName, string Identifier, Offset Offset) Describe(DateTimeZone zone, Instant instant)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var named = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone.Id)
            ?? (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(iana) : null)
            ?? zone;
        var offset = named.GetUtcOffset(instant);
        var name = named.GetZoneInterval(instant).Name;
        // TZDB short names are letters (GMT, BST, CEST); a long Windows name or a bare offset ("+03") is not one.
        var isShortName = name.Length is > 0 and <= 6 && name.All(char.IsAsciiLetterUpper);
        return (isShortName ? name : FormatOffset(offset), named.Id, offset);
    }

    /// <summary>An offset from UTC as people write it: UTC, UTC+1, UTC−3:30.</summary>
    public static string FormatOffset(Offset offset)
    {
        if (offset == Offset.Zero) return "UTC";
        var span = offset.ToTimeSpan();
        var sign = span < TimeSpan.Zero ? "−" : "+";
        span = span.Duration();
        return span.Minutes == 0
            ? $"UTC{sign}{span.Hours.ToString(CultureInfo.InvariantCulture)}"
            : $"UTC{sign}{span.Hours.ToString(CultureInfo.InvariantCulture)}:{span.Minutes.ToString("00", CultureInfo.InvariantCulture)}";
    }
}
