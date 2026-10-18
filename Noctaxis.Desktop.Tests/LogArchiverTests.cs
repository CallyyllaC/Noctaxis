using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.Tests;

/// <summary>Daily archives: one per day logs were written, the newest ten kept, nothing lost on any failure.</summary>
public sealed class LogArchiverTests : IDisposable
{
    private readonly LogTestDirectory _logs = new();
    private readonly ManualTimeProvider _time = ManualTimeProvider.AtLocal(LogTestZones.PlusOne, new DateTime(2026, 10, 18, 9, 0, 0));
    private static readonly DateTime Today = new(2026, 10, 18, 10, 0, 0);

    private void SessionOn(DateTime day, string message = "entry") => _logs.Session(_time, day.Date.AddHours(9), message);

    private RollingFileLoggerProvider LaunchToday()
    {
        _time.SetLocal(Today);
        return _logs.Provider(_time);
    }

    [Fact]
    public void ADayIsArchivedWithEverySessionItHad()
    {
        for (var hour = 8; hour < 13; hour++) _logs.Session(_time, new DateTime(2026, 10, 17, hour, 0, 0), $"Session at {hour}");

        using var provider = LaunchToday();

        var archive = _logs.File("Noctaxis-2026-10-17.zip");
        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
        var entries = LogTestDirectory.Entries(archive);
        Assert.Equal(5, entries.Length);
        for (var hour = 8; hour < 13; hour++)
            Assert.Contains($"Session at {hour}", LogTestDirectory.EntryText(archive, $"Noctaxis-2026-10-17_{hour:00}-00-00.log"));
        Assert.Equal([Path.GetFileName(provider.FilePath!)], _logs.SessionLogs());
    }

    [Fact]
    public void ExactlyTenDailyArchivesAreKeptAndAnEleventhDayEvictsTheOldest()
    {
        for (var day = 1; day <= 10; day++) SessionOn(new DateTime(2026, 10, day));
        // Launched on the 11th: the ten earlier days are archived, and today's log takes no place.
        _time.SetLocal(new DateTime(2026, 10, 11, 9, 0, 0));
        using (var provider = _logs.Provider(_time)) provider.CreateLogger("T").LogInformation("The 11th");
        Assert.Equal(Enumerable.Range(1, 10).Select(day => $"Noctaxis-2026-10-{day:00}.zip"), _logs.Archives());
        Assert.Single(_logs.SessionLogs());

        // The 11th becomes history: the oldest day goes.
        _time.SetLocal(new DateTime(2026, 10, 12, 9, 0, 0));
        using var next = _logs.Provider(_time);
        Assert.Equal(Enumerable.Range(2, 10).Select(day => $"Noctaxis-2026-10-{day:00}.zip"), _logs.Archives());
    }

    [Fact]
    public void RetentionCountsDaysLogsWereWrittenNotCalendarDaysOrAge()
    {
        // Ten days of use spread over most of a year, with long gaps between them.
        var days = new[] { new DateTime(2026, 1, 3), new(2026, 2, 14), new(2026, 3, 1), new(2026, 4, 20), new(2026, 5, 5),
            new(2026, 6, 30), new(2026, 8, 1), new(2026, 9, 9), new(2026, 10, 1), new(2026, 10, 16) };
        foreach (var day in days) SessionOn(day);

        using var provider = LaunchToday();

        Assert.Equal(days.Select(day => $"Noctaxis-{day:yyyy-MM-dd}.zip"), _logs.Archives());
    }

    [Fact]
    public void ManyLaunchesOnOneDayEvictNothing()
    {
        for (var day = 1; day <= 10; day++) SessionOn(new DateTime(2026, 10, day));
        for (var launch = 0; launch < 40; launch++)
        {
            using var provider = LaunchToday();
            provider.CreateLogger("T").LogInformation("Launch {Launch}", launch);
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(day => $"Noctaxis-2026-10-{day:00}.zip"), _logs.Archives());
        Assert.Equal(40, _logs.SessionLogs().Length);
    }

    [Fact]
    public void TodaysLogsNeverTakeOneOfTheTenPlaces()
    {
        for (var day = 1; day <= 10; day++) SessionOn(new DateTime(2026, 10, day));
        using var first = LaunchToday();
        using var second = LaunchToday();

        Assert.Equal(10, _logs.Archives().Length);
        Assert.Equal(2, _logs.SessionLogs().Length);
        Assert.DoesNotContain("Noctaxis-2026-10-18.zip", _logs.Archives());
    }

    [Fact]
    public void AnExistingArchiveTakesInLateLogsWithoutDuplicatesOrChangingWhatItHeld()
    {
        SessionOn(new DateTime(2026, 10, 17), "First");
        using (LaunchToday()) { }
        var archive = _logs.File("Noctaxis-2026-10-17.zip");
        var before = LogTestDirectory.EntryText(archive, "Noctaxis-2026-10-17_09-00-00.log");

        // A log of the 17th found later (copied back, or left by a run that could not archive it then).
        File.WriteAllText(_logs.File("Noctaxis-2026-10-17_22-00-00.log"), "2026-10-17T21:00:00.000Z [INF] T: Late\n");
        using (LaunchToday()) { }

        Assert.Equal(["Noctaxis-2026-10-17_09-00-00.log", "Noctaxis-2026-10-17_22-00-00.log"], LogTestDirectory.Entries(archive));
        Assert.Equal(before, LogTestDirectory.EntryText(archive, "Noctaxis-2026-10-17_09-00-00.log"));
        Assert.Contains("Late", LogTestDirectory.EntryText(archive, "Noctaxis-2026-10-17_22-00-00.log"));
        Assert.DoesNotContain(_logs.SessionLogs(), name => name.StartsWith("Noctaxis-2026-10-17", StringComparison.Ordinal));
    }

    [Fact]
    public void ALogAlreadyArchivedUnderASuffixedNameIsNotAddedAgain()
    {
        var archive = _logs.File("Noctaxis-2026-10-17.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("Noctaxis-2026-10-17_09-00-00.log", "first\n"), ("Noctaxis-2026-10-17_09-00-00~2.log", "second\n") })
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text);
        }
        // The second file again, left behind by a run that archived it as ~2 and stopped before deleting it.
        File.WriteAllText(_logs.File("Noctaxis-2026-10-17_09-00-00.log"), "second\n");
        _time.SetLocal(Today);

        _logs.Archiver(_time).Run();

        Assert.Equal(["Noctaxis-2026-10-17_09-00-00.log", "Noctaxis-2026-10-17_09-00-00~2.log"], LogTestDirectory.Entries(archive));
        Assert.Equal("first\n", LogTestDirectory.EntryText(archive, "Noctaxis-2026-10-17_09-00-00.log"));
        Assert.Empty(_logs.SessionLogs());
    }

    [Fact]
    public void ArchivingIsIdempotent()
    {
        SessionOn(new DateTime(2026, 10, 16));
        SessionOn(new DateTime(2026, 10, 17));
        _time.SetLocal(Today);
        Assert.True(_logs.Archiver(_time).Run());
        var once = _logs.Snapshot();

        for (var run = 0; run < 3; run++) Assert.True(_logs.Archiver(_time).Run());

        Assert.Equal(once, _logs.Snapshot());
        Assert.Empty(_logs.Reports);
    }

    [Fact]
    public void AnArchiveInterruptedBeforeItWasInPlaceIsRecoveredFromItsUntouchedSources()
    {
        SessionOn(new DateTime(2026, 10, 17), "Kept");
        // What a run killed while writing leaves: a partial temporary archive beside the sources.
        File.WriteAllBytes(_logs.File("Noctaxis-2026-10-17.0123456789abcdef0123456789abcdef.zip.tmp"), [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);

        using (LaunchToday()) { }

        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
        Assert.Empty(_logs.Names("*.tmp"));
        Assert.Contains("Kept", LogTestDirectory.EntryText(_logs.File("Noctaxis-2026-10-17.zip"), "Noctaxis-2026-10-17_09-00-00.log"));
    }

    [Fact]
    public void AnArchiveInterruptedAfterItWasInPlaceFinishesWithoutDuplicates()
    {
        SessionOn(new DateTime(2026, 10, 17));
        _time.SetLocal(Today);
        var interrupted = _logs.Archiver(_time);
        interrupted.StageReached = (stage, _) => { if (stage == ArchiveStage.Moved) throw new IOException("stopped before deleting"); };
        interrupted.Run();
        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
        Assert.Contains("Noctaxis-2026-10-17_09-00-00.log", _logs.SessionLogs());

        _logs.Archiver(_time).Run();

        Assert.Equal(["Noctaxis-2026-10-17_09-00-00.log"], LogTestDirectory.Entries(_logs.File("Noctaxis-2026-10-17.zip")));
        Assert.DoesNotContain("Noctaxis-2026-10-17_09-00-00.log", _logs.SessionLogs());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedArchiveDeletesNothingAndIsReported(bool failsOnceValidated)
    {
        var failingStage = failsOnceValidated ? ArchiveStage.Validated : ArchiveStage.Written;
        SessionOn(new DateTime(2026, 10, 17));
        _time.SetLocal(Today);
        var before = _logs.Snapshot();
        var archiver = _logs.Archiver(_time);
        archiver.StageReached = (stage, _) => { if (stage == failingStage) throw new IOException("disk full"); };

        archiver.Run();

        Assert.Equal(before.Keys.Order(), _logs.Snapshot().Keys.Order());
        Assert.Equal(before, _logs.Snapshot());
        Assert.Empty(_logs.Archives());
        Assert.Contains(_logs.Reports, report => report.Contains("could not be archived", StringComparison.Ordinal) && report.Contains("disk full", StringComparison.Ordinal));
        // Retried, and done, by the next run.
        _logs.Archiver(_time).Run();
        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
    }

    [Fact]
    public void ATruncatedArchiveIsDetectedBeforeAnythingIsDeleted()
    {
        SessionOn(new DateTime(2026, 10, 17));
        _time.SetLocal(Today);
        var archiver = _logs.Archiver(_time);
        archiver.StageReached = (stage, path) =>
        {
            if (stage != ArchiveStage.Written) return;
            using var file = new FileStream(path, FileMode.Open);
            file.SetLength(file.Length / 2);
        };

        archiver.Run();

        Assert.Empty(_logs.Archives());
        Assert.Empty(_logs.Names("*.tmp"));
        Assert.Contains("Noctaxis-2026-10-17_09-00-00.log", _logs.SessionLogs());
        Assert.Contains(_logs.Reports, report => report.Contains("did not read back", StringComparison.Ordinal));
    }

    [Fact]
    public void AnArchiveMissingAnEntryIsIncomplete()
    {
        var path = _logs.File("check.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Noctaxis-2026-10-17_09-00-00.log").Open())) writer.Write("one");
        var one = new LogArchiver.Content(3, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("one"u8)));

        Assert.True(LogArchiver.IsComplete(path, new Dictionary<string, LogArchiver.Content> { ["Noctaxis-2026-10-17_09-00-00.log"] = one }));
        Assert.False(LogArchiver.IsComplete(path, new Dictionary<string, LogArchiver.Content>
            { ["Noctaxis-2026-10-17_09-00-00.log"] = one, ["Noctaxis-2026-10-17_10-00-00.log"] = one }));
        Assert.False(LogArchiver.IsComplete(path, new Dictionary<string, LogArchiver.Content>
            { ["Noctaxis-2026-10-17_09-00-00.log"] = one with { Sha256 = new string('0', 64) } }));
    }

    [Fact]
    public void ACorruptArchiveIsNeverOverwrittenAndNeverCounted()
    {
        for (var day = 1; day <= 10; day++) SessionOn(new DateTime(2026, 10, day));
        using (LaunchToday()) { }
        // The newest archive is damaged, and a late log of its day turns up.
        File.WriteAllBytes(_logs.File("Noctaxis-2026-10-10.zip"), [0x50, 0x4B, 0x05, 0x06, 0, 0, 0]);
        File.WriteAllText(_logs.File("Noctaxis-2026-10-10_23-00-00.log"), "late\n");

        using (LaunchToday()) { }

        // The damaged file is kept aside, untouched; a new archive holds the late log; ten readable archives remain.
        var aside = Assert.Single(_logs.Names("Noctaxis-2026-10-10.corrupt-*.zip"));
        Assert.Equal([0x50, 0x4B, 0x05, 0x06, 0, 0, 0], File.ReadAllBytes(_logs.File(aside)));
        Assert.Equal(["Noctaxis-2026-10-10_23-00-00.log"], LogTestDirectory.Entries(_logs.File("Noctaxis-2026-10-10.zip")));
        Assert.Equal(Enumerable.Range(1, 10).Select(day => $"Noctaxis-2026-10-{day:00}.zip"), _logs.Archives());
    }

    [Fact]
    public void AnUnreadableArchiveDoesNotHoldAPlace()
    {
        for (var day = 2; day <= 11; day++) SessionOn(new DateTime(2026, 10, day));
        using (LaunchToday()) { }
        // Truncated by something outside Noctaxis: no longer an archive, so the ten readable ones are all kept.
        File.WriteAllBytes(_logs.File("Noctaxis-2026-10-12.zip"), [1, 2, 3]);

        using (LaunchToday()) { }

        Assert.Equal(Enumerable.Range(2, 10).Select(day => $"Noctaxis-2026-10-{day:00}.zip"), _logs.Archives());
        Assert.Single(_logs.Names("Noctaxis-2026-10-12.corrupt-*.zip"));
    }

    [Fact]
    public void ALogStillBeingWrittenIsNeverArchived()
    {
        // Another process's log of the 17th, open for writing as the logger opens its own.
        var foreign = _logs.File("Noctaxis-2026-10-17_23-00-00.log");
        using (var writer = new FileStream(foreign, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writer.Write("still writing\n"u8);
            using (LaunchToday()) { }
            Assert.True(File.Exists(foreign));
            Assert.Empty(_logs.Archives());
            writer.Write("more\n"u8);
        }

        using (LaunchToday()) { }
        Assert.Equal(["Noctaxis-2026-10-17_23-00-00.log"], LogTestDirectory.Entries(_logs.File("Noctaxis-2026-10-17.zip")));
        Assert.Equal("still writing\nmore\n", LogTestDirectory.EntryText(_logs.File("Noctaxis-2026-10-17.zip"), "Noctaxis-2026-10-17_23-00-00.log"));
    }

    [Fact]
    public void ThisSessionsOwnFileIsNeverArchivedEvenOnceItsDayHasPassed()
    {
        _time.SetLocal(new DateTime(2026, 10, 17, 23, 0, 0));
        using var provider = _logs.Provider(_time);
        provider.CreateLogger("T").LogInformation("Late evening");
        var active = provider.FilePath!;

        // Midnight passes with nothing logged yet; archiving that runs meanwhile leaves the file being written alone.
        _time.SetLocal(Today);
        _logs.Archiver(_time, () => provider.FilePath).Run();

        Assert.True(File.Exists(active));
        Assert.Empty(_logs.Archives());
        Assert.True(provider.CreateLogger("T").IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void ZeroPointOneLogsAreArchivedByTheirLocalDate()
    {
        // 0.1 named files by their UTC start: 23:30 UTC on the 16th is 00:30 on the 17th an hour ahead.
        File.WriteAllText(_logs.File("noctaxis-20261016-233000-4242.log"), "old\n");
        File.WriteAllText(_logs.File("noctaxis-20261015-120000-17.log"), "older\n");

        using (LaunchToday()) { }

        Assert.Equal(["Noctaxis-2026-10-15.zip", "Noctaxis-2026-10-17.zip"], _logs.Archives());
        Assert.Equal(["noctaxis-20261016-233000-4242.log"], LogTestDirectory.Entries(_logs.File("Noctaxis-2026-10-17.zip")));
    }

    [Fact]
    public void OnlyNoctaxisLogFilesAreEverTouched()
    {
        SessionOn(new DateTime(2026, 10, 17));
        string[] unrelated = ["notes.txt", "other.log", "Noctaxis-notes.zip", "Noctaxis-2026-10-17.zip.bak", "noctaxis-crash.dmp",
            "Noctaxis-2026-10-17_09-00.log", "state.json"];
        foreach (var name in unrelated) File.WriteAllText(_logs.File(name), "keep " + name);
        Directory.CreateDirectory(_logs.File("Noctaxis-2026-10-16.zip.d"));

        for (var day = 1; day <= 12; day++) SessionOn(new DateTime(2026, 9, day));
        using (LaunchToday()) { }

        foreach (var name in unrelated) Assert.Equal("keep " + name, File.ReadAllText(_logs.File(name)));
        Assert.True(Directory.Exists(_logs.File("Noctaxis-2026-10-16.zip.d")));
        Assert.Equal(10, _logs.Archives().Length);
    }

    [Fact]
    public void ACompetingRunWaitsForTheLockAndChangesNothing()
    {
        SessionOn(new DateTime(2026, 10, 17));
        _time.SetLocal(Today);
        var before = _logs.Snapshot();

        using (LogArchiver.AcquireLock(_logs.Path, TimeSpan.Zero))
            Assert.False(_logs.Archiver(_time).Run());

        Assert.Equal(before, _logs.Snapshot());
        Assert.True(_logs.Archiver(_time).Run());
        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
    }

    [Fact]
    public async Task ConcurrentRunsProduceOneCompleteArchive()
    {
        for (var hour = 0; hour < 20; hour++) _logs.Session(_time, new DateTime(2026, 10, 17, hour, 0, 0), $"Hour {hour}");
        _time.SetLocal(Today);
        var runs = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            var archiver = _logs.Archiver(_time);
            archiver.LockWait = TimeSpan.FromSeconds(20);
            return archiver.Run();
        })).ToArray();
        await Task.WhenAll(runs);

        Assert.Equal(["Noctaxis-2026-10-17.zip"], _logs.Archives());
        Assert.Equal(20, LogTestDirectory.Entries(_logs.File("Noctaxis-2026-10-17.zip")).Length);
        Assert.Empty(_logs.SessionLogs());
        Assert.Empty(_logs.Names("*.tmp"));
    }

    public void Dispose() => _logs.Dispose();
}
