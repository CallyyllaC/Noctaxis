using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.Tests;

/// <summary>Export Logs' ZIP: the daily archives as they are, every uncompressed session log, and a manifest.</summary>
public sealed class DiagnosticsExportTests : IDisposable
{
    private readonly LogTestDirectory _logs = new();
    private readonly ManualTimeProvider _time = ManualTimeProvider.AtLocal(LogTestZones.PlusOne, new DateTime(2026, 10, 18, 9, 0, 0));

    private RollingFileLoggerProvider PrepareHistoryAndToday()
    {
        foreach (var day in new[] { 15, 16, 17 }) _logs.Session(_time, new DateTime(2026, 10, day, 20, 0, 0), $"Day {day}");
        _logs.Session(_time, new DateTime(2026, 10, 18, 8, 0, 0), "Earlier today");
        _time.SetLocal(new DateTime(2026, 10, 18, 10, 0, 0));
        var provider = _logs.Provider(_time);
        provider.CreateLogger("Noctaxis.Test").LogInformation("Active session line");
        return provider;
    }

    private DiagnosticsExportService Service(RollingFileLoggerProvider? provider) =>
        new(_logs.Path, provider is null ? null : () => provider.FilePath, _time);

    private static ZipArchive Open(MemoryStream package) => new(new MemoryStream(package.ToArray()), ZipArchiveMode.Read);

    [Fact]
    public void ContainsTheDailyArchivesUnchangedTodaysSessionsAndAValidManifest()
    {
        using var provider = PrepareHistoryAndToday();
        var archiveHashes = _logs.Archives().ToDictionary(name => name, name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_logs.File(name)))));
        using var package = new MemoryStream();

        var summary = Service(provider).WritePackage(package);

        using var zip = Open(package);
        Assert.Equal(
            ["archived/Noctaxis-2026-10-15.zip", "archived/Noctaxis-2026-10-16.zip", "archived/Noctaxis-2026-10-17.zip", "diagnostics.json",
             "sessions/Noctaxis-2026-10-18_08-00-00.log", "sessions/Noctaxis-2026-10-18_10-00-00.log"],
            zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(new DiagnosticsExportSummary(3, 2), summary);
        // The nested archives are byte for byte the retained ones, and still open as archives.
        foreach (var (name, hash) in archiveHashes)
        {
            using var nested = new MemoryStream();
            using (var input = zip.GetEntry("archived/" + name)!.Open()) input.CopyTo(nested);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(nested.ToArray())));
            nested.Position = 0;
            using var inner = new ZipArchive(nested, ZipArchiveMode.Read);
            Assert.Single(inner.Entries);
        }
        Assert.Contains("Earlier today", Text(zip, "sessions/Noctaxis-2026-10-18_08-00-00.log"));
        Assert.Contains("Active session line", Text(zip, "sessions/Noctaxis-2026-10-18_10-00-00.log"));

        using var manifest = JsonDocument.Parse(Text(zip, "diagnostics.json"));
        var root = manifest.RootElement;
        Assert.Equal("noctaxis-diagnostics/1", root.GetProperty("format").GetString());
        Assert.Equal("2026-10-18T09:00:00Z", root.GetProperty("exportedAtUtc").GetString());
        Assert.Equal("+01:00", root.GetProperty("localUtcOffset").GetString());
        Assert.Equal("Noctaxis", root.GetProperty("application").GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("application").GetProperty("version").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("application").GetProperty("buildConfiguration").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("operatingSystem").GetProperty("description").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("operatingSystem").GetProperty("version").GetString()));
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            root.GetProperty("runtime").GetProperty("processArchitecture").GetString());
        Assert.Equal(System.Environment.Version.ToString(), root.GetProperty("runtime").GetProperty("version").GetString());
        var files = root.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(zip.Entries.Select(entry => entry.FullName).Where(name => name != "diagnostics.json").Order(StringComparer.Ordinal),
            files.Select(file => file.GetProperty("path").GetString()).Order(StringComparer.Ordinal));
        foreach (var file in files)
        {
            using var content = new MemoryStream();
            using (var input = zip.GetEntry(file.GetProperty("path").GetString()!)!.Open()) input.CopyTo(content);
            Assert.Equal(content.Length, file.GetProperty("bytes").GetInt64());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content.ToArray())), file.GetProperty("sha256").GetString());
        }
        Assert.Equal("activeSessionSnapshot",
            files.Single(file => file.GetProperty("path").GetString()!.EndsWith("10-00-00.log", StringComparison.Ordinal)).GetProperty("kind").GetString());
        Assert.Equal(3, files.Count(file => file.GetProperty("kind").GetString() == "dailyArchive"));
    }

    [Fact]
    public async Task CopiesTheLiveLogWithoutStoppingLockingOrTruncatingIt()
    {
        using var provider = PrepareHistoryAndToday();
        var logger = provider.CreateLogger("Noctaxis.Test");
        for (var i = 0; i < 200; i++) logger.LogInformation("Line {Index}", i);
        var active = provider.FilePath!;
        var lengthBefore = new FileInfo(active).Length;

        using var package = new MemoryStream();
        // The logger keeps writing while the export runs, from another thread.
        using var writing = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var n = 0; !writing.IsCancellationRequested; n++)
            {
                logger.LogInformation("Concurrent {N}", n);
                Thread.Sleep(1);
            }
        });
        Service(provider).WritePackage(package);
        writing.Cancel();
        await writer;
        logger.LogInformation("After the export");

        Assert.Equal(active, provider.FilePath);
        Assert.True(logger.IsEnabled(LogLevel.Information));
        var live = LogTestDirectory.ReadShared(active);
        Assert.True(new FileInfo(active).Length > lengthBefore);
        Assert.EndsWith("After the export" + System.Environment.NewLine, live);
        using var zip = Open(package);
        var snapshot = Text(zip, "sessions/" + Path.GetFileName(active));
        // A copy of the file as it was, ending at a complete line, and a prefix of what the file now holds.
        Assert.Contains("Line 199", snapshot);
        Assert.EndsWith("\n", snapshot);
        Assert.StartsWith(snapshot, live);
        // Nothing was archived or pruned by the export.
        Assert.Equal(3, _logs.Archives().Length);
        Assert.Equal(2, _logs.SessionLogs().Length);
    }

    [Fact]
    public void TheExportReadsTheLogsDirectoryAndChangesNothingInIt()
    {
        using var provider = PrepareHistoryAndToday();
        var before = _logs.Snapshot();

        Service(provider).WritePackage(new MemoryStream());

        Assert.Equal(before, _logs.Snapshot());
    }

    [Fact]
    public void LogsStillAwaitingTheirArchiveAreIncluded()
    {
        // An earlier day's log the archiver has not taken yet (it was in use, or archiving failed).
        File.WriteAllText(_logs.File("Noctaxis-2026-10-16_12-00-00.log"), "awaiting\n");
        File.WriteAllText(_logs.File("noctaxis-20261014-120000-99.log"), "from 0.1\n");
        using var package = new MemoryStream();

        Service(null).WritePackage(package);

        using var zip = Open(package);
        Assert.Equal("awaiting\n", Text(zip, "sessions/Noctaxis-2026-10-16_12-00-00.log"));
        Assert.Equal("from 0.1\n", Text(zip, "sessions/noctaxis-20261014-120000-99.log"));
    }

    [Fact]
    public void NothingButTheLogsAndTheManifestIsIncluded()
    {
        using var provider = PrepareHistoryAndToday();
        var dataFolder = Path.GetDirectoryName(_logs.Path)!;
        File.WriteAllText(Path.Combine(dataFolder, "state.json"), "{\"secret\":\"licence\"}");
        Directory.CreateDirectory(Path.Combine(dataFolder, "locations"));
        File.WriteAllText(Path.Combine(dataFolder, "locations", "home.png"), "image");
        foreach (var name in new[] { "notes.txt", "state.json", "other.log", "Noctaxis-2026-10-17.zip.bak", "Noctaxis-2026-10-17.corrupt-0123456789abcdef0123456789abcdef.zip",
                     "Noctaxis-2026-10-17.0123456789abcdef0123456789abcdef.zip.tmp" })
            File.WriteAllText(_logs.File(name), "not a log");
        Directory.CreateDirectory(_logs.File("cache"));
        File.WriteAllText(_logs.File(Path.Combine("cache", "Noctaxis-2026-10-17_09-00-00.log")), "nested");
        using var package = new MemoryStream();

        Service(provider).WritePackage(package);

        using var zip = Open(package);
        Assert.All(zip.Entries, entry => Assert.Matches(@"^(diagnostics\.json|archived/Noctaxis-[0-9-]{10}\.zip|sessions/Noctaxis-[0-9_-]+\.log)$", entry.FullName));
        var manifest = Text(zip, "diagnostics.json");
        // No user, machine or host names, and no paths.
        foreach (var identifying in new[] { System.Environment.UserName, System.Environment.MachineName, System.Net.Dns.GetHostName() })
            Assert.DoesNotContain(identifying, manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_logs.Path.Replace("\\", "\\\\"), manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar).Replace("\\", "\\\\"), manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("licence", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutALogsDirectoryOnlyTheManifestIsExported()
    {
        using var package = new MemoryStream();

        var summary = new DiagnosticsExportService(null, time: _time).WritePackage(package);

        using var zip = Open(package);
        Assert.Equal(["diagnostics.json"], zip.Entries.Select(entry => entry.FullName));
        Assert.Equal(new DiagnosticsExportSummary(0, 0), summary);
        using var manifest = JsonDocument.Parse(Text(zip, "diagnostics.json"));
        Assert.False(manifest.RootElement.GetProperty("logging").GetProperty("fileLoggingAvailable").GetBoolean());
        Assert.Empty(manifest.RootElement.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void TheSuggestedNameIsTheLocalDateAndTimeInInvariantDigits()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("th-TH");
            Assert.Equal("Noctaxis-Diagnostics-2026-10-18-1000.zip",
                new DiagnosticsExportService(null, time: ManualTimeProvider.AtLocal(LogTestZones.PlusOne, new DateTime(2026, 10, 18, 10, 0, 0))).SuggestedFileName());
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void ExportsFromADirectoryWithAUnicodePath()
    {
        using var logs = new LogTestDirectory(" Ünïcødé 日本");
        logs.Session(_time, new DateTime(2026, 10, 17, 9, 0, 0), "ünïcødé");
        _time.SetLocal(new DateTime(2026, 10, 18, 9, 0, 0));
        using var provider = logs.Provider(_time);
        using var package = new MemoryStream();

        new DiagnosticsExportService(logs.Path, () => provider.FilePath, _time).WritePackage(package);

        using var zip = Open(package);
        Assert.NotNull(zip.GetEntry("archived/Noctaxis-2026-10-17.zip"));
        Assert.NotNull(zip.GetEntry("sessions/" + Path.GetFileName(provider.FilePath)));
    }

    private static string Text(ZipArchive zip, string entryName)
    {
        using var reader = new StreamReader(zip.GetEntry(entryName)!.Open());
        return reader.ReadToEnd();
    }

    public void Dispose() => _logs.Dispose();
}
