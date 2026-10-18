using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Noctaxis.Desktop.Diagnostics;

/// <summary>
/// Turns the session logs of earlier days into one ZIP per day and keeps the newest <see cref="DefaultRetainedArchives"/>
/// of them. Each run, under <see cref="LogFileNames.LockFile"/>:
/// <list type="number">
/// <item>Temporary archives left by an interrupted run are removed. Their sources were never deleted.</item>
/// <item>Each earlier date's session logs that nothing is writing are written, with that date's existing archive if
/// there is one, to a temporary ZIP. It is read back and every entry compared with what was written, then moved over
/// the date's archive. Only then are the sources deleted. A log an archive already holds unchanged is not added again,
/// so an interrupted run repeats safely.</item>
/// <item>Archives are kept by the dates they represent, newest first. Today's date never takes a place.</item>
/// </list>
/// Failures leave every source where it was, are reported, and are retried by the next run. Only files named as
/// <see cref="LogFileNames"/> describes are ever touched.
/// </summary>
internal sealed class LogArchiver
{
    public const int DefaultRetainedArchives = 10;
    private static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(10);
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Func<string?> _activeFile;
    private readonly Action<string, Exception?> _report;
    private readonly int _retainedArchives;

    /// <param name="activeFile">The file this process is writing, which is never archived.</param>
    /// <param name="report">Receives each failure, which is otherwise swallowed.</param>
    public LogArchiver(string directory, TimeProvider time, Func<string?> activeFile, Action<string, Exception?> report,
        int retainedArchives = DefaultRetainedArchives)
    {
        _directory = directory;
        _time = time;
        _activeFile = activeFile;
        _report = report;
        _retainedArchives = Math.Max(1, retainedArchives);
    }

    /// <summary>Test seam: called at each stage of writing one archive, with the temporary archive's path.</summary>
    internal Action<ArchiveStage, string>? StageReached { get; set; }

    internal TimeSpan LockWait { get; set; } = DefaultLockWait;

    public DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>Recovers, archives and prunes once. Returns false when another run held the lock throughout.</summary>
    public bool Run()
    {
        using var held = AcquireLock(_directory, LockWait);
        if (held is null) return false;
        var today = Today;
        RemoveTemporaryArchives();
        foreach (var (date, sources) in PendingSessions(today)) ArchiveDate(date, sources);
        EnforceRetention(today);
        return true;
    }

    /// <summary>
    /// The lock that serialises everything that writes or replaces archives, across threads and processes: an
    /// exclusively opened file (an advisory lock on Linux and macOS). Null when it is not free within <paramref name="wait"/>.
    /// </summary>
    public static IDisposable? AcquireLock(string directory, TimeSpan wait)
    {
        var path = Path.Combine(directory, LogFileNames.LockFile);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    // ---- Discovery -----------------------------------------------------------------------------------------------

    private IEnumerable<(DateOnly Date, List<string> Sources)> PendingSessions(DateOnly today)
    {
        var active = _activeFile();
        var zone = _time.LocalTimeZone;
        return Files()
            .Select(path => (Path: path, Ok: LogFileNames.TryParseSession(Path.GetFileName(path), zone, out var date), Date: date))
            // Only earlier days: today's logs, and any dated ahead of a clock that was set back, wait for their day.
            .Where(file => file.Ok && file.Date < today && !SamePath(file.Path, active))
            .GroupBy(file => file.Date)
            .OrderBy(group => group.Key)
            .Select(group => (group.Key, group.Select(file => file.Path).Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }

    private IEnumerable<string> Files()
    {
        try { return Directory.EnumerateFiles(_directory).ToList(); }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            _report("Logs directory could not be listed", ex);
            return [];
        }
    }

    internal static bool SamePath(string path, string? other) => other is not null &&
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(other),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- One day's archive ---------------------------------------------------------------------------------------

    private void ArchiveDate(DateOnly date, List<string> sourcePaths)
    {
        var archivePath = Path.Combine(_directory, LogFileNames.Archive(date));
        var temporaryPath = Path.Combine(_directory, LogFileNames.Temporary(date));
        var sources = new List<(string Path, FileStream Stream)>();
        try
        {
            // Opening a log exclusively proves that nothing is writing it: the writer shares it for reading only, so
            // this fails on Windows, and its advisory lock makes it fail on Linux and macOS. Held until the archive
            // is in place, so nothing can start writing it meanwhile. A log in use waits for a later run.
            foreach (var path in sourcePaths)
            {
                try { sources.Add((path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))); }
                catch (Exception ex) when (IsFileSystemFailure(ex)) { }
            }
            if (sources.Count == 0) return;

            var hasArchive = HasReadableArchive(archivePath, date);
            var expected = new Dictionary<string, Content>(StringComparer.Ordinal);
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    if (hasArchive)
                    {
                        using var previous = ZipFile.OpenRead(archivePath);
                        foreach (var entry in previous.Entries)
                        {
                            using var input = entry.Open();
                            expected[entry.FullName] = Copy(input, zip, entry.FullName, entry.LastWriteTime);
                        }
                    }
                    foreach (var (path, stream) in sources)
                    {
                        var fileName = Path.GetFileName(path);
                        var content = Hash(stream);
                        var name = fileName;
                        var archived = false;
                        // Archived, under its own name or a ~n one, by a run that stopped before deleting it: nothing
                        // to add, and deleted below. A different file of the same name goes in under the next free ~n.
                        for (var attempt = 2; expected.TryGetValue(name, out var held); attempt++)
                        {
                            if (held == content) { archived = true; break; }
                            name = LogFileNames.Disambiguated(fileName, attempt);
                        }
                        if (archived) continue;
                        stream.Position = 0;
                        expected[name] = Copy(stream, zip, name, File.GetLastWriteTime(path));
                    }
                }
                output.Flush(flushToDisk: true);
            }
            StageReached?.Invoke(ArchiveStage.Written, temporaryPath);

            if (!IsComplete(temporaryPath, expected))
            {
                _report(string.Create(CultureInfo.InvariantCulture,
                    $"Log archive for {date:yyyy-MM-dd} did not read back as written; its {sources.Count} log(s) were kept"), null);
                TryDelete(temporaryPath);
                return;
            }
            StageReached?.Invoke(ArchiveStage.Validated, temporaryPath);
            // An existing archive is replaced only by one holding everything it held, which was just checked.
            File.Move(temporaryPath, archivePath, overwrite: true);
            StageReached?.Invoke(ArchiveStage.Moved, archivePath);

            foreach (var (_, stream) in sources) stream.Dispose();
            foreach (var (path, _) in sources)
            {
                try { File.Delete(path); }
                catch (Exception ex) when (IsFileSystemFailure(ex))
                {
                    // Archived, so the next run finds it there unchanged and deletes it then.
                    _report("Archived log could not be deleted: " + Path.GetFileName(path), ex);
                }
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex) || ex is InvalidDataException)
        {
            _report(string.Create(CultureInfo.InvariantCulture, $"Logs for {date:yyyy-MM-dd} could not be archived; they were kept"), ex);
            TryDelete(temporaryPath);
        }
        finally
        {
            foreach (var (_, stream) in sources) stream.Dispose();
        }
    }

    /// <summary>
    /// The date's archive, if there is one and it reads completely. One that does not is renamed aside (never
    /// overwritten or deleted while it is still within retention) and no longer counts as that date's archive.
    /// </summary>
    private bool HasReadableArchive(string archivePath, DateOnly date)
    {
        if (!File.Exists(archivePath)) return false;
        if (TryListEntries(archivePath, verifyContent: true) is not null) return true;
        var aside = Path.Combine(_directory, LogFileNames.SetAside(date));
        File.Move(archivePath, aside);
        _report(string.Create(CultureInfo.InvariantCulture,
            $"Log archive for {date:yyyy-MM-dd} was unreadable; it was kept as {Path.GetFileName(aside)} and a new archive started"), null);
        return false;
    }

    private static Content Copy(Stream input, ZipArchive zip, string name, DateTimeOffset lastWrite)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        // ZIP stores 1980–2107 local times only; a file dated outside that range keeps the format's earliest time.
        if (lastWrite.Year is >= 1980 and <= 2107) entry.LastWriteTime = lastWrite;
        using var output = entry.Open();
        return CopyHashing(input, output);
    }

    private static Content Hash(Stream input) => CopyHashing(input, Stream.Null);

    private static Content CopyHashing(Stream input, Stream output)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            length += read;
        }
        return new Content(length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    /// <summary>Whether the archive at <paramref name="path"/> holds exactly <paramref name="expected"/>, byte for byte.</summary>
    internal static bool IsComplete(string path, IReadOnlyDictionary<string, Content> expected)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count != expected.Count) return false;
            foreach (var entry in zip.Entries)
            {
                if (!expected.TryGetValue(entry.FullName, out var content)) return false;
                using var input = entry.Open();
                if (Hash(input) != content) return false;
            }
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex) || ex is InvalidDataException) { return false; }
    }

    /// <summary>
    /// The entry names of a well-formed daily archive, or null when it is not one: unreadable, truncated, empty, or
    /// holding anything but session logs. <paramref name="verifyContent"/> also reads every entry through its checksum.
    /// </summary>
    internal static IReadOnlyCollection<string>? TryListEntries(string path, bool verifyContent)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = new List<string>(zip.Entries.Count);
            foreach (var entry in zip.Entries)
            {
                if (!LogFileNames.IsArchiveEntry(entry.FullName)) return null;
                if (verifyContent)
                {
                    using var input = entry.Open();
                    if (Hash(input).Length != entry.Length) return null;
                }
                names.Add(entry.FullName);
            }
            return names.Count == 0 ? null : names;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex) || ex is InvalidDataException) { return null; }
    }

    // ---- Recovery and retention ----------------------------------------------------------------------------------

    /// <summary>Under the lock, any temporary archive belongs to a run that ended early; its sources are untouched.</summary>
    private void RemoveTemporaryArchives()
    {
        foreach (var path in Files())
            if (LogFileNames.TryParseTemporary(Path.GetFileName(path), out _)) TryDelete(path);
    }

    private void EnforceRetention(DateOnly today)
    {
        var archives = new List<(DateOnly Date, string Path)>();
        var setAside = new List<(DateOnly Date, string Path)>();
        foreach (var path in Files())
        {
            var name = Path.GetFileName(path);
            if (LogFileNames.TryParseArchive(name, out var date))
            {
                // A ZIP that does not read is not an archive, and never holds one of the places.
                if (TryListEntries(path, verifyContent: false) is not null) archives.Add((date, path));
                else
                {
                    var aside = Path.Combine(_directory, LogFileNames.SetAside(date));
                    if (TryMove(path, aside)) setAside.Add((date, aside));
                    _report("Unreadable log archive set aside: " + name, null);
                }
            }
            else if (LogFileNames.TryParseSetAside(name, out var asideDate)) setAside.Add((asideDate, path));
        }

        // Retention is by the dates archives represent. Today's never counts or goes (it exists only after the clock
        // went back), and neither do days nothing was logged.
        var counted = archives.Where(archive => archive.Date != today).OrderByDescending(archive => archive.Date).ToList();
        foreach (var (_, path) in counted.Skip(_retainedArchives)) TryDelete(path);
        if (counted.Count < _retainedArchives) return;
        // A set-aside archive goes once its date would no longer have been kept.
        var oldestKept = counted[_retainedArchives - 1].Date;
        foreach (var (date, path) in setAside)
            if (date < oldestKept) TryDelete(path);
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (IsFileSystemFailure(ex)) { _report("Log file could not be removed: " + Path.GetFileName(path), ex); }
    }

    private bool TryMove(string from, string to)
    {
        try { File.Move(from, to); return true; }
        catch (Exception ex) when (IsFileSystemFailure(ex)) { _report("Log file could not be moved: " + Path.GetFileName(from), ex); return false; }
    }

    internal static bool IsFileSystemFailure(Exception exception) => exception
        is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;

    internal readonly record struct Content(long Length, string Sha256);
}

internal enum ArchiveStage
{
    /// <summary>The temporary archive is written and closed, not yet checked.</summary>
    Written,
    /// <summary>The temporary archive read back as written and is about to replace the date's archive.</summary>
    Validated,
    /// <summary>The date's archive is in place; its sources are about to be deleted.</summary>
    Moved,
}
