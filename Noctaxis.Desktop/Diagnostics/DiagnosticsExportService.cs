using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Noctaxis.Desktop.Diagnostics;

/// <summary>
/// Settings → Diagnostics → Export Logs: one ZIP for the user to review and send by hand. Nothing is uploaded.
/// <list type="bullet">
/// <item><c>diagnostics.json</c>: the application and runtime, and an inventory of what follows.</item>
/// <item><c>archived/</c>: the retained daily archives, stored as they are.</item>
/// <item><c>sessions/</c>: every uncompressed session log: today's, including a copy of the one being written, and any
/// earlier one still awaiting its archive.</item>
/// </list>
/// Only files the logs directory's naming marks as Noctaxis' own logs are included: no state, settings, saved
/// locations, equipment, caches or anything else from the data folder. Logs are copied through shared reads that
/// never block, truncate or lock the file being written, and nothing in the logs directory is changed: archiving and
/// retention are left to the logger.
/// </summary>
public sealed class DiagnosticsExportService
{
    /// <summary>The most read from any one log; the logger's own limit, with room for its closing notice.</summary>
    internal const long MaximumLogBytes = RollingFileLoggerProvider.MaximumFileBytes + 64 * 1024;
    /// <summary>The most read from any one archive: far beyond any the logger writes.</summary>
    internal const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);
    private readonly string? _logDirectory;
    private readonly Func<string?> _activeLog;
    private readonly TimeProvider _time;

    /// <param name="logDirectory">The logs directory; null exports diagnostics.json alone.</param>
    /// <param name="activeLog">The log this process is writing, copied up to its last complete line.</param>
    public DiagnosticsExportService(string? logDirectory, Func<string?>? activeLog = null, TimeProvider? time = null)
    {
        _logDirectory = logDirectory;
        _activeLog = activeLog ?? (() => null);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The export for this process: its own logs directory, or none in tests and when logging is unavailable.</summary>
    public static DiagnosticsExportService ForCurrentProcess() => Program.FileLogging is { } logging
        ? new DiagnosticsExportService(logging.Directory, () => logging.FilePath)
        : new DiagnosticsExportService(null);

    /// <summary><c>Noctaxis-Diagnostics-2026-10-18-1000.zip</c>, from the local time, in invariant digits.</summary>
    public string SuggestedFileName() => string.Create(CultureInfo.InvariantCulture,
        $"Noctaxis-Diagnostics-{_time.GetLocalNow():yyyy-MM-dd-HHmm}.zip");

    /// <summary>Writes the export to <paramref name="destination"/>. Reads the logs directory only.</summary>
    public DiagnosticsExportSummary WritePackage(Stream destination, CancellationToken cancellationToken = default)
    {
        var inventory = new List<ExportedFile>();
        using (var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (_logDirectory is not null && Directory.Exists(_logDirectory))
            {
                // Archives are not replaced or pruned while they are copied; past the wait, the copy is made without
                // the lock, and an archive replaced meanwhile is simply read as it then is.
                using var held = LogArchiver.AcquireLock(_logDirectory, LockWait);
                var active = _activeLog();
                var zone = _time.LocalTimeZone;
                foreach (var path in Directory.EnumerateFiles(_logDirectory).Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(path);
                    if (LogFileNames.TryParseArchive(name, out var archiveDate))
                        AddCopy(zip, inventory, path, "archived/" + name, ExportedFileKind.DailyArchive, archiveDate,
                            MaximumArchiveBytes, completeLinesOnly: false);
                    else if (LogFileNames.TryParseSession(name, zone, out var sessionDate))
                    {
                        var isActive = LogArchiver.SamePath(path, active);
                        AddCopy(zip, inventory, path, "sessions/" + name,
                            isActive ? ExportedFileKind.ActiveSessionSnapshot : ExportedFileKind.SessionLog, sessionDate,
                            MaximumLogBytes, completeLinesOnly: isActive);
                    }
                }
            }
            var manifest = zip.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
            using var output = manifest.Open();
            WriteManifest(output, inventory);
        }
        return new DiagnosticsExportSummary(inventory.Count(file => file.Kind == ExportedFileKind.DailyArchive),
            inventory.Count(file => file.Kind != ExportedFileKind.DailyArchive));
    }

    /// <summary>
    /// Copies up to <paramref name="limit"/> bytes as they are at this moment. The read shares the file for reading,
    /// writing and deletion, so the logger keeps appending and the archiver can still replace an archive.
    /// </summary>
    private static void AddCopy(ZipArchive zip, List<ExportedFile> inventory, string path, string entryName,
        ExportedFileKind kind, DateOnly date, long limit, bool completeLinesOnly)
    {
        byte[] bytes;
        bool truncated;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = input.Length;
            truncated = length > limit;
            bytes = new byte[Math.Min(length, limit)];
            var read = 0;
            while (read < bytes.Length && input.Read(bytes, read, bytes.Length - read) is var count and > 0) read += count;
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        // Gone between the listing and the copy (archived meanwhile, without the lock): not part of this export.
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        // A line still being written stays out of the copy of the live log.
        if (completeLinesOnly && Array.LastIndexOf(bytes, (byte)'\n') is var lastNewline && lastNewline + 1 < bytes.Length)
            Array.Resize(ref bytes, lastNewline + 1);
        // Archives are already compressed and go in unchanged.
        var entry = zip.CreateEntry(entryName, kind == ExportedFileKind.DailyArchive ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        var lastWrite = File.GetLastWriteTime(path);
        if (lastWrite.Year is >= 1980 and <= 2107) entry.LastWriteTime = lastWrite;
        using (var output = entry.Open()) output.Write(bytes);
        inventory.Add(new ExportedFile(entryName, kind, date, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), truncated));
    }

    /// <summary>
    /// What would help investigate a problem and nothing that identifies the user or the machine: no user, machine
    /// or host names, no hardware or network identifiers, no paths, no settings and no time zone name.
    /// </summary>
    private void WriteManifest(Stream output, IReadOnlyList<ExportedFile> inventory)
    {
        var assembly = typeof(DiagnosticsExportService).Assembly;
        var localNow = _time.GetLocalNow();
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("format", "noctaxis-diagnostics/1");
        json.WriteString("exportedAtUtc", _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        json.WriteString("localUtcOffset", (localNow.Offset < TimeSpan.Zero ? "-" : "+") +
            localNow.Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture));

        json.WriteStartObject("application");
        json.WriteString("name", "Noctaxis");
        json.WriteString("version", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
        json.WriteString("buildConfiguration", assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown");
        json.WriteEndObject();

        json.WriteStartObject("operatingSystem");
        json.WriteString("description", RuntimeInformation.OSDescription);
        json.WriteString("version", System.Environment.OSVersion.Version.ToString());
        json.WriteString("platform", OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux" : "Other");
        json.WriteString("architecture", RuntimeInformation.OSArchitecture.ToString());
        json.WriteEndObject();

        json.WriteStartObject("runtime");
        json.WriteString("framework", RuntimeInformation.FrameworkDescription);
        json.WriteString("version", System.Environment.Version.ToString());
        json.WriteString("runtimeIdentifier", RuntimeInformation.RuntimeIdentifier);
        json.WriteString("processArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
        json.WriteNumber("processorCount", System.Environment.ProcessorCount);
        json.WriteBoolean("serverGarbageCollector", System.Runtime.GCSettings.IsServerGC);
        json.WriteNumber("workingSetBytes", System.Environment.WorkingSet);
        json.WriteNumber("managedHeapBytes", GC.GetTotalMemory(forceFullCollection: false));
        // Not every platform reports a start time; the export goes on without it.
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            json.WriteNumber("uptimeSeconds", Math.Round((DateTime.Now - process.StartTime).TotalSeconds));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
        json.WriteString("culture", CultureInfo.CurrentCulture.Name);
        json.WriteString("uiCulture", CultureInfo.CurrentUICulture.Name);
        json.WriteEndObject();

        json.WriteStartObject("logging");
        json.WriteBoolean("fileLoggingAvailable", _logDirectory is not null);
        json.WriteNumber("retainedDailyArchives", RollingFileLoggerProvider.RetainedArchives);
        json.WriteNumber("maximumLogFileBytes", RollingFileLoggerProvider.MaximumFileBytes);
        json.WriteEndObject();

        json.WriteStartArray("files");
        foreach (var file in inventory)
        {
            json.WriteStartObject();
            json.WriteString("path", file.EntryName);
            json.WriteString("kind", file.Kind switch
            {
                ExportedFileKind.DailyArchive => "dailyArchive",
                ExportedFileKind.ActiveSessionSnapshot => "activeSessionSnapshot",
                _ => "sessionLog"
            });
            json.WriteString("date", file.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            json.WriteNumber("bytes", file.Bytes);
            json.WriteString("sha256", file.Sha256);
            if (file.Truncated) json.WriteBoolean("truncated", true);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private sealed record ExportedFile(string EntryName, ExportedFileKind Kind, DateOnly Date, long Bytes, string Sha256, bool Truncated);

    private enum ExportedFileKind { DailyArchive, SessionLog, ActiveSessionSnapshot }
}

/// <summary>How many daily archives and session logs an export holds.</summary>
public sealed record DiagnosticsExportSummary(int DailyArchives, int SessionLogs);
