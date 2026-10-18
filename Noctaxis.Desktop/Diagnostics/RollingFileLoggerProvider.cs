using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Noctaxis.Desktop.Diagnostics;

/// <summary>
/// Durable diagnostics for the windowed application, which has no console. Each session writes its own uncompressed
/// file, <c>Noctaxis-yyyy-MM-dd_HH-mm-ss[-n].log</c>, created new so no earlier session is ever overwritten. Entries
/// belong to the local date they are written on: the first entry written on a new date, whether after midnight or
/// after a clock change, closes the file and starts another for that date. Earlier days' files are then archived by
/// <see cref="LogArchiver"/> into one ZIP per day, of which the newest ten are kept; it runs in the background at
/// start-up and after each change of date, so logging never waits for it. A file stops growing at
/// <see cref="MaximumFileBytes"/>. Writes are synchronous and serialised, so the lines written immediately before a
/// crash are on disk. Logging failures never propagate to the application.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const long MaximumFileBytes = 5L * 1024 * 1024;
    public const int RetainedArchives = LogArchiver.DefaultRetainedArchives;
    private const string ArchiveCategory = "Noctaxis.Desktop.Diagnostics.LogArchiver";
    private static readonly TimeSpan MaintenanceWaitOnDispose = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly object _maintenanceGate = new();
    private readonly long _maximumFileBytes;
    private readonly TimeProvider _time;
    private readonly Action<Action> _runMaintenance;
    private StreamWriter? _writer;
    private string? _filePath;
    private DateOnly _fileDate;
    private long _bytes;
    private bool _truncated;
    private volatile bool _disposed;
    private bool _maintenanceRunning;
    private bool _maintenanceAgain;
    private TaskCompletionSource? _maintenanceDone;

    /// <param name="time">The clock and time zone; the system's by default.</param>
    /// <param name="runMaintenance">Runs archiving and retention; on the thread pool by default.</param>
    public RollingFileLoggerProvider(string directory, TimeProvider? time = null, long maximumFileBytes = MaximumFileBytes,
        int retainedArchives = RetainedArchives, Action<Action>? runMaintenance = null)
    {
        Directory = directory;
        _time = time ?? TimeProvider.System;
        _maximumFileBytes = maximumFileBytes;
        _runMaintenance = runMaintenance ?? (work => Task.Run(work));
        Archiver = new LogArchiver(directory, _time, () => FilePath, ReportArchiveFailure, retainedArchives);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            lock (_gate) OpenFile(_time.GetLocalNow());
        }
        catch (Exception ex) when (IsSinkFailure(ex))
        {
            // Diagnostics must never stop the application from starting.
            System.Diagnostics.Trace.TraceWarning($"Noctaxis file logging is unavailable: {ex.Message}");
        }
        // Recovers an interrupted archive and archives the days before this one.
        if (FilePath is not null) RequestMaintenance();
    }

    public string Directory { get; }

    /// <summary>The file this session is writing now, or null when file logging could not be started or has stopped.</summary>
    public string? FilePath { get { lock (_gate) return _writer is null ? null : _filePath; } }

    internal LogArchiver Archiver { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Waits until archiving that has been requested is finished (tests, and shutdown within a bound).</summary>
    internal Task MaintenanceCompletion { get { lock (_maintenanceGate) return _maintenanceDone?.Task ?? Task.CompletedTask; } }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }
        // An archive left half written is recovered at the next start, but finishing it now is tidier.
        try { MaintenanceCompletion.Wait(MaintenanceWaitOnDispose); }
        catch (AggregateException) { }
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var utcNow = _time.GetUtcNow();
        var builder = new StringBuilder(128);
        builder.Append(utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviation(level)).Append("] ").Append(category).Append(": ").Append(message);
        if (exception is not null) builder.AppendLine().Append(exception);
        var line = builder.AppendLine().ToString();
        var rolledOver = false;
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                // The entry belongs to the local date it is written on, whichever way the date changed.
                var localNow = TimeZoneInfo.ConvertTime(utcNow, _time.LocalTimeZone);
                if (DateOnly.FromDateTime(localNow.DateTime) != _fileDate)
                {
                    CloseWriter();
                    OpenFile(localNow);
                    rolledOver = true;
                }
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (!_truncated && _bytes + bytes <= _maximumFileBytes)
                {
                    _writer!.Write(line);
                    _bytes += bytes;
                }
                else if (!_truncated)
                {
                    _truncated = true;
                    _writer!.Write("... log size limit reached; further entries for this file are discarded." +
                                   System.Environment.NewLine);
                }
            }
            // The sink itself failed (the disk filled, the file was removed under us). Give up on
            // file logging for this session rather than letting it reach the calling code.
            catch (Exception ex) when (IsSinkFailure(ex))
            {
                System.Diagnostics.Trace.TraceWarning($"Noctaxis file logging stopped: {ex.Message}");
                CloseWriter();
            }
        }
        // Outside the gate: archiving reports its own failures through this method.
        if (rolledOver) RequestMaintenance();
    }

    /// <summary>
    /// Creates this date's file, never opening an existing one: several launches within one second, or a wall-clock
    /// time repeated by a clock change, each get a file of their own. Called with _gate held.
    /// </summary>
    private void OpenFile(DateTimeOffset localNow)
    {
        for (var attempt = 1; ; attempt++)
        {
            var path = Path.Combine(Directory, LogFileNames.Session(localNow, attempt));
            try
            {
                // Shared for reading only: an export can copy it while it grows, and the archiver, which needs it
                // exclusively, leaves it alone while it is written.
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _filePath = path;
                _fileDate = DateOnly.FromDateTime(localNow.DateTime);
                _bytes = 0;
                _truncated = false;
                return;
            }
            catch (IOException) when (attempt < 1000 && File.Exists(path)) { }
        }
    }

    // Called with _gate held, or from Dispose. Never throws.
    private void CloseWriter()
    {
        var writer = _writer;
        _writer = null;
        // Disposing flushes, so it can fail for the same reasons a write can.
        try { writer?.Dispose(); }
        catch (Exception ex) when (IsSinkFailure(ex)) { }
    }

    /// <summary>Starts archiving, or, while it runs, asks it to run once more when it finishes.</summary>
    private void RequestMaintenance()
    {
        lock (_maintenanceGate)
        {
            if (_disposed) return;
            if (_maintenanceRunning) { _maintenanceAgain = true; return; }
            _maintenanceRunning = true;
            _maintenanceDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { _runMaintenance(RunMaintenance); }
        catch (Exception ex)
        {
            lock (_maintenanceGate) _maintenanceAgain = false;
            FinishMaintenance();
            ReportArchiveFailure("Log archiving could not be started", ex);
        }
    }

    private void RunMaintenance()
    {
        while (true)
        {
            // Archiving is housekeeping: whatever fails, the logs it was working on stay where they are and the
            // application carries on, so every failure is reported and none escapes.
            try { Archiver.Run(); }
            catch (Exception ex) { ReportArchiveFailure("Log archiving failed", ex); }
            // Deciding to stop and stopping are one step, so a request made meanwhile is never lost.
            if (FinishMaintenance()) return;
        }
    }

    /// <summary>Marks archiving finished, unless another run was asked for since it last checked.</summary>
    private bool FinishMaintenance()
    {
        TaskCompletionSource? done;
        lock (_maintenanceGate)
        {
            if (_maintenanceAgain && !_disposed) { _maintenanceAgain = false; return false; }
            _maintenanceRunning = false;
            _maintenanceAgain = false;
            done = _maintenanceDone;
        }
        done?.TrySetResult();
        return true;
    }

    private void ReportArchiveFailure(string message, Exception? exception)
    {
        System.Diagnostics.Trace.TraceWarning($"Noctaxis {message}: {exception?.Message}");
        Write(LogLevel.Error, ArchiveCategory, message, exception);
    }

    /// <summary>
    /// True for failures of the log file itself, which are swallowed. Exceptions raised by the
    /// calling code (notably a message formatter) are never caught here.
    /// </summary>
    private static bool IsSinkFailure(Exception exception) => exception
        is IOException or UnauthorizedAccessException or ObjectDisposedException
        or System.Security.SecurityException or NotSupportedException or ArgumentException;

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???"
    };

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && owner._writer is not null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
