using System.Globalization;
using System.Text.RegularExpressions;

namespace Noctaxis.Desktop.Diagnostics;

/// <summary>
/// The names of the files Noctaxis manages in its logs directory. Anything else there is never read, moved or
/// deleted. Every date and time in a name is the local calendar date and wall-clock time, written with the invariant
/// culture, so names sort by date and parse the same under every regional format.
/// </summary>
internal static partial class LogFileNames
{
    /// <summary>The cross-process lock held while archives are written, merged, pruned or exported.</summary>
    public const string LockFile = "Noctaxis-logs.lock";

    // Noctaxis-2026-10-18_09-15-00.log, or -2, -3 … when that second already has a session.
    [GeneratedRegex(@"^Noctaxis-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})_[0-9]{2}-[0-9]{2}-[0-9]{2}(?:-[0-9]{1,4})?(?:~[0-9]{1,4})?\.log$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SessionPattern();

    // Noctaxis 0.1 named each session by its UTC start and process id: noctaxis-20261017-081500-1234.log.
    [GeneratedRegex(@"^noctaxis-(?<stamp>[0-9]{8}-[0-9]{6})-[0-9]+(?:~[0-9]{1,4})?\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacySessionPattern();

    [GeneratedRegex(@"^Noctaxis-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchivePattern();

    [GeneratedRegex(@"^Noctaxis-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})\.[0-9a-f]{32}\.zip\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryPattern();

    [GeneratedRegex(@"^Noctaxis-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})\.corrupt-[0-9a-f]{32}\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex SetAsidePattern();

    /// <summary>A session file started at <paramref name="localStart"/>; <paramref name="attempt"/> above 1 adds a suffix.</summary>
    public static string Session(DateTimeOffset localStart, int attempt) => attempt <= 1
        ? string.Create(CultureInfo.InvariantCulture, $"Noctaxis-{localStart:yyyy-MM-dd_HH-mm-ss}.log")
        : string.Create(CultureInfo.InvariantCulture, $"Noctaxis-{localStart:yyyy-MM-dd_HH-mm-ss}-{attempt}.log");

    public static string Archive(DateOnly date) => string.Create(CultureInfo.InvariantCulture, $"Noctaxis-{date:yyyy-MM-dd}.zip");

    public static string Temporary(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"Noctaxis-{date:yyyy-MM-dd}.{Guid.NewGuid():N}.zip.tmp");

    public static string SetAside(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"Noctaxis-{date:yyyy-MM-dd}.corrupt-{Guid.NewGuid():N}.zip");

    /// <summary>
    /// The local date a session file's entries belong to. A 0.1 file is dated by its UTC start, converted to
    /// <paramref name="zone"/>; a 0.2 file never spans midnight, because the writer starts a new file each day.
    /// </summary>
    public static bool TryParseSession(string fileName, TimeZoneInfo zone, out DateOnly date)
    {
        if (SessionPattern().Match(fileName) is { Success: true } current) return TryParseDate(current.Groups["date"].Value, out date);
        if (LegacySessionPattern().Match(fileName) is { Success: true } legacy && DateTime.TryParseExact(legacy.Groups["stamp"].Value,
                "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
            return true;
        }
        date = default;
        return false;
    }

    public static bool TryParseArchive(string fileName, out DateOnly date) => TryParse(ArchivePattern(), fileName, out date);
    public static bool TryParseTemporary(string fileName, out DateOnly date) => TryParse(TemporaryPattern(), fileName, out date);
    public static bool TryParseSetAside(string fileName, out DateOnly date) => TryParse(SetAsidePattern(), fileName, out date);

    /// <summary>An entry name an archive may hold: a session file name, without any directory.</summary>
    public static bool IsArchiveEntry(string entryName) =>
        SessionPattern().IsMatch(entryName) || LegacySessionPattern().IsMatch(entryName);

    /// <summary>A name for a second, different file under an entry name an archive already holds.</summary>
    public static string Disambiguated(string fileName, int attempt) =>
        string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(fileName)}~{attempt}.log");

    private static bool TryParse(Regex pattern, string fileName, out DateOnly date)
    {
        if (pattern.Match(fileName) is { Success: true } match) return TryParseDate(match.Groups["date"].Value, out date);
        date = default;
        return false;
    }

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
