using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.Tests;

/// <summary>A clock the test sets, in a time zone it chooses, so dates and midnight are deterministic.</summary>
internal sealed class ManualTimeProvider(TimeZoneInfo zone, DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override TimeZoneInfo LocalTimeZone => zone;

    /// <summary>Moves the clock to a local wall-clock time (the earlier instant, when a clock change repeats it).</summary>
    public void SetLocal(DateTime local)
    {
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        UtcNow = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUniversalTime();
    }

    public static ManualTimeProvider AtLocal(TimeZoneInfo zone, DateTime local)
    {
        var time = new ManualTimeProvider(zone, DateTimeOffset.UnixEpoch);
        time.SetLocal(local);
        return time;
    }
}

internal static class LogTestZones
{
    /// <summary>An hour ahead of UTC all year, so a local date differs from the UTC date near midnight.</summary>
    public static readonly TimeZoneInfo PlusOne = TimeZoneInfo.CreateCustomTimeZone("Noctaxis/Test+1", TimeSpan.FromHours(1), "Test +1", "Test +1");

    /// <summary>A UK-like zone: summer time from the last Sunday of March to 02:00 on the last Sunday of October.</summary>
    public static readonly TimeZoneInfo UkLike = TimeZoneInfo.CreateCustomTimeZone("Noctaxis/TestUk", TimeSpan.Zero, "Test UK", "Test GMT",
        "Test BST", [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday))]);
}

/// <summary>A logs directory owned by one test, removed afterwards. Never the user's own.</summary>
internal sealed class LogTestDirectory : IDisposable
{
    public LogTestDirectory(string? nameSuffix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noctaxis-log-tests-" + Guid.NewGuid().ToString("N") + (nameSuffix ?? ""), "logs");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public List<string> Reports { get; } = [];

    /// <summary>A provider whose archiving runs inline, so each test observes it finished.</summary>
    public RollingFileLoggerProvider Provider(TimeProvider time, int retainedArchives = RollingFileLoggerProvider.RetainedArchives,
        long maximumFileBytes = RollingFileLoggerProvider.MaximumFileBytes) =>
        new(Path, time, maximumFileBytes, retainedArchives, runMaintenance: work => work());

    public LogArchiver Archiver(TimeProvider time, Func<string?>? active = null, int retained = LogArchiver.DefaultRetainedArchives) =>
        new(Path, time, active ?? (() => null), (message, exception) => Reports.Add(message + (exception is null ? "" : ": " + exception.Message)),
            retained) { LockWait = TimeSpan.FromMilliseconds(200) };

    /// <summary>One session: started at <paramref name="local"/>, one line written, closed.</summary>
    public string Session(ManualTimeProvider time, DateTime local, string message = "entry")
    {
        time.SetLocal(local);
        using var provider = Provider(time);
        provider.CreateLogger("Noctaxis.Test").LogInformation("{Message}", message);
        return provider.FilePath!;
    }

    public string[] Names(string pattern = "*") => Directory.GetFiles(Path, pattern).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    // By the logger's own names: a Windows "*.zip" pattern also matches "x.zip.bak".
    public string[] Archives() => Names().Where(name => LogFileNames.TryParseArchive(name, out _)).ToArray();

    public string[] SessionLogs() => Names().Where(name => LogFileNames.TryParseSession(name, TimeZoneInfo.Utc, out _)).ToArray();

    /// <summary>Reads a log as an export does, sharing it with the logger that may still be writing it.</summary>
    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string[] Entries(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    public static string EntryText(string zipPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(entryName)!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Every file's name and content hash: equal snapshots mean nothing here changed.</summary>
    public Dictionary<string, string> Snapshot() => Directory.GetFiles(Path).ToDictionary(file => System.IO.Path.GetFileName(file),
        file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        });

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
