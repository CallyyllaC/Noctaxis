using System.Globalization;
using Microsoft.Extensions.Logging;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.Tests;

/// <summary>The session log itself: one new file per session and per day, written as before.</summary>
public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private readonly LogTestDirectory _logs = new();
    private readonly ManualTimeProvider _time = ManualTimeProvider.AtLocal(LogTestZones.PlusOne, new DateTime(2026, 10, 18, 9, 15, 0));

    [Fact]
    public void WritesEntriesWithLevelCategoryAndException()
    {
        string path;
        using (var provider = _logs.Provider(_time))
        {
            path = Assert.IsType<string>(provider.FilePath);
            var logger = provider.CreateLogger("Noctaxis.Test");
            logger.LogWarning(new IOException("disk full"), "Save failed for {Reason}", "test");
        }

        var text = File.ReadAllText(path);
        Assert.StartsWith(_logs.Path, path);
        // The format is unchanged: a UTC timestamp, the level, the category and the message.
        Assert.Contains("2026-10-18T08:15:00.000Z [WRN] Noctaxis.Test: Save failed for test", text);
        Assert.Contains("System.IO.IOException: disk full", text);
    }

    [Fact]
    public void TheFirstSessionCreatesTodaysLogNamedByItsLocalStart()
    {
        using var provider = _logs.Provider(_time);

        // 08:15 UTC is 09:15 in the test zone: the name is the local date and time.
        Assert.Equal("Noctaxis-2026-10-18_09-15-00.log", Path.GetFileName(provider.FilePath));
        Assert.Equal(["Noctaxis-2026-10-18_09-15-00.log"], _logs.SessionLogs());
        Assert.Empty(_logs.Archives());
    }

    [Fact]
    public void SessionsInTheSameSecondEachGetTheirOwnFileAndNoneIsOverwritten()
    {
        var paths = new List<string>();
        for (var session = 1; session <= 4; session++)
        {
            using var provider = _logs.Provider(_time);
            provider.CreateLogger("Noctaxis.Test").LogInformation("Session {Session}", session);
            paths.Add(provider.FilePath!);
        }

        Assert.Equal(
            ["Noctaxis-2026-10-18_09-15-00-2.log", "Noctaxis-2026-10-18_09-15-00-3.log", "Noctaxis-2026-10-18_09-15-00-4.log",
             "Noctaxis-2026-10-18_09-15-00.log"], _logs.SessionLogs());
        for (var session = 1; session <= 4; session++)
        {
            var text = File.ReadAllText(paths[session - 1]);
            Assert.Contains($"Session {session}", text);
            Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    [Fact]
    public void StopsGrowingAtTheSizeLimit()
    {
        string path;
        using (var provider = _logs.Provider(_time, maximumFileBytes: 512))
        {
            path = provider.FilePath!;
            var logger = provider.CreateLogger("Noctaxis.Test");
            for (var i = 0; i < 100; i++) logger.LogInformation("Entry {Index} with some padding text", i);
        }

        var text = File.ReadAllText(path);
        Assert.Contains("log size limit reached", text);
        Assert.True(new FileInfo(path).Length < 512 + 200);
        Assert.DoesNotContain("Entry 99 ", text);
    }

    [Fact]
    public void EntriesBelongToTheLocalDateTheyAreWrittenOnAndMidnightArchivesTheDayBefore()
    {
        _time.SetLocal(new DateTime(2026, 10, 17, 23, 59, 30));
        using var provider = _logs.Provider(_time);
        var logger = provider.CreateLogger("Noctaxis.Test");
        logger.LogInformation("Before midnight");
        var yesterday = provider.FilePath!;

        _time.SetLocal(new DateTime(2026, 10, 18, 0, 0, 5));
        logger.LogInformation("After midnight");

        // The session goes on, in a new file for the new date, and the previous day is archived at once.
        Assert.Equal("Noctaxis-2026-10-18_00-00-05.log", Path.GetFileName(provider.FilePath));
        Assert.Contains("After midnight", LogTestDirectory.ReadShared(provider.FilePath!));
        Assert.DoesNotContain("Before midnight", LogTestDirectory.ReadShared(provider.FilePath!));
        Assert.False(File.Exists(yesterday));
        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
        var archive = _logs.File("Noctaxis-2026-10-17.zip");
        Assert.Equal([Path.GetFileName(yesterday)], LogTestDirectory.Entries(archive));
        Assert.Contains("Before midnight", LogTestDirectory.EntryText(archive, Path.GetFileName(yesterday)));
        Assert.True(logger.IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void AnApplicationClosedOverMidnightArchivesTheDayBeforeAtTheNextLaunch()
    {
        _logs.Session(_time, new DateTime(2026, 10, 17, 21, 0, 0), "Evening");

        _time.SetLocal(new DateTime(2026, 10, 18, 8, 0, 0));
        using var provider = _logs.Provider(_time);

        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
        Assert.Equal(["Noctaxis-2026-10-18_08-00-00.log"], _logs.SessionLogs());
        Assert.Contains("Evening", LogTestDirectory.EntryText(_logs.File("Noctaxis-2026-10-17.zip"), "Noctaxis-2026-10-17_21-00-00.log"));
    }

    [Fact]
    public void ARepeatedHourAtTheEndOfSummerTimeKeepsBothSessions()
    {
        // 01:30 happens twice on 25 October 2026 in a UK-like zone: once in summer time, once an hour later.
        var time = new ManualTimeProvider(LogTestZones.UkLike, new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero));
        string first, second;
        using (var provider = _logs.Provider(time)) { provider.CreateLogger("T").LogInformation("Summer"); first = provider.FilePath!; }
        time.UtcNow = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        using (var provider = _logs.Provider(time)) { provider.CreateLogger("T").LogInformation("Winter"); second = provider.FilePath!; }

        Assert.Equal("Noctaxis-2026-10-25_01-30-00.log", Path.GetFileName(first));
        Assert.Equal("Noctaxis-2026-10-25_01-30-00-2.log", Path.GetFileName(second));
        Assert.Contains("Summer", File.ReadAllText(first));
        Assert.Contains("Winter", File.ReadAllText(second));
    }

    [Fact]
    public void AClockSetBackAndForwardLosesNothing()
    {
        using var provider = _logs.Provider(_time);
        var logger = provider.CreateLogger("Noctaxis.Test");
        logger.LogInformation("On the 18th");
        // Set back a day: entries now belong to the 17th, in a file of their own; the 18th's file waits for its day.
        _time.SetLocal(new DateTime(2026, 10, 17, 9, 20, 0));
        logger.LogInformation("Back on the 17th");
        Assert.Equal(["Noctaxis-2026-10-17_09-20-00.log", "Noctaxis-2026-10-18_09-15-00.log"], _logs.SessionLogs());
        Assert.Empty(_logs.Archives());
        // Forward again, then on to the 19th: both days are archived, nothing is lost.
        _time.SetLocal(new DateTime(2026, 10, 18, 9, 30, 0));
        logger.LogInformation("Forward to the 18th");
        _time.SetLocal(new DateTime(2026, 10, 19, 9, 0, 0));
        logger.LogInformation("On the 19th");

        Assert.Equal(["Noctaxis-2026-10-17.zip", "Noctaxis-2026-10-18.zip"], _logs.Archives());
        Assert.Equal(["Noctaxis-2026-10-19_09-00-00.log"], _logs.SessionLogs());
        Assert.Contains("Back on the 17th", LogTestDirectory.EntryText(_logs.File("Noctaxis-2026-10-17.zip"), "Noctaxis-2026-10-17_09-20-00.log"));
        Assert.Equal(["Noctaxis-2026-10-18_09-15-00.log", "Noctaxis-2026-10-18_09-30-00.log"],
            LogTestDirectory.Entries(_logs.File("Noctaxis-2026-10-18.zip")));
    }

    [Fact]
    public void NamesUseInvariantDigitsAndTheGregorianCalendarWhateverTheCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // Thai uses the Buddhist calendar (2569) and Arabic (Saudi Arabia) the Hijri one, with its own digits.
            foreach (var name in new[] { "th-TH", "ar-SA", "fa-IR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                _time.SetLocal(new DateTime(2026, 10, 17, 9, 0, 0));
                using (var provider = _logs.Provider(_time)) provider.CreateLogger("T").LogInformation("Written under {Culture}", name);
                _time.SetLocal(new DateTime(2026, 10, 18, 9, 0, 0));
                using (var provider = _logs.Provider(_time))
                    Assert.Matches(@"^Noctaxis-2026-10-18_09-00-00(-[0-9]+)?\.log$", Path.GetFileName(provider.FilePath));
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
        // Each culture's session had the same name; the archive keeps all three, under names of their own.
        Assert.Equal(["Noctaxis-2026-10-17_09-00-00.log", "Noctaxis-2026-10-17_09-00-00~2.log", "Noctaxis-2026-10-17_09-00-00~3.log"],
            LogTestDirectory.Entries(_logs.File("Noctaxis-2026-10-17.zip")));
    }

    [Fact]
    public void WorksInADirectoryWithAUnicodePath()
    {
        using var logs = new LogTestDirectory(" Ünïcødé é 日本 🌌");
        logs.Session(_time, new DateTime(2026, 10, 17, 9, 0, 0), "Ascii and ünïcødé ✓");
        _time.SetLocal(new DateTime(2026, 10, 18, 9, 0, 0));
        using var provider = logs.Provider(_time);

        Assert.Equal(["Noctaxis-2026-10-17.zip"], logs.Archives());
        Assert.Contains("ünïcødé ✓", LogTestDirectory.EntryText(logs.File("Noctaxis-2026-10-17.zip"), "Noctaxis-2026-10-17_09-00-00.log"));
    }

    [Fact]
    public void UnusableDirectoryDisablesFileLoggingWithoutThrowing()
    {
        var blocked = Path.Combine(_logs.Path, "logs");
        File.WriteAllText(blocked, "a file where the logs directory should be");

        using var provider = new RollingFileLoggerProvider(blocked, _time, runMaintenance: work => work());
        var logger = provider.CreateLogger("Noctaxis.Test");
        logger.LogError("Not written anywhere");

        Assert.Null(provider.FilePath);
        Assert.False(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void LoggingAfterDisposeDegradesInsteadOfThrowing()
    {
        var provider = _logs.Provider(_time);
        var logger = provider.CreateLogger("Noctaxis.Test");
        provider.Dispose();

        // Program disposes the provider while other threads may still be logging, even across midnight.
        _time.SetLocal(new DateTime(2026, 10, 19, 0, 0, 1));
        logger.LogCritical("After shutdown");
        provider.Dispose();

        Assert.False(logger.IsEnabled(LogLevel.Error));
        Assert.Equal(["Noctaxis-2026-10-18_09-15-00.log"], _logs.SessionLogs());
    }

    [Fact]
    public void ExceptionsFromTheCallerAreNotSwallowed()
    {
        using var provider = _logs.Provider(_time);
        var logger = provider.CreateLogger("Noctaxis.Test");

        // Only failures of the log file itself are hidden; a broken message formatter is the
        // caller's defect and must surface.
        Assert.Throws<InvalidOperationException>(() => logger.Log<object?>(LogLevel.Error, default, null, null,
            (_, _) => throw new InvalidOperationException("formatter defect")));
    }

    [Fact]
    public async Task BackgroundArchivingFinishesWithoutHoldingUpLogging()
    {
        _logs.Session(_time, new DateTime(2026, 10, 16, 9, 0, 0));
        _logs.Session(_time, new DateTime(2026, 10, 17, 9, 0, 0));
        _time.SetLocal(new DateTime(2026, 10, 18, 9, 0, 0));

        // The production default: archiving on the thread pool.
        using var provider = new RollingFileLoggerProvider(_logs.Path, _time);
        provider.CreateLogger("Noctaxis.Test").LogInformation("Logged while archiving");
        await provider.MaintenanceCompletion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["Noctaxis-2026-10-16.zip", "Noctaxis-2026-10-17.zip"], _logs.Archives());
        Assert.Contains("Logged while archiving", LogTestDirectory.ReadShared(provider.FilePath!));
    }

    public void Dispose() => _logs.Dispose();
}
