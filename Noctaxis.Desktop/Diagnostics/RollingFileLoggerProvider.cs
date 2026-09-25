using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Noctaxis.Desktop.Diagnostics;

/// <summary>
/// Durable diagnostics for the windowed application, which has no console. Each session writes one
/// file (<c>noctaxis-yyyyMMdd-HHmmss-pid.log</c>) in the logs directory; a file stops growing at
/// <see cref="MaximumFileBytes"/> and only the newest <see cref="RetainedFiles"/> files are kept, so
/// disk use is bounded. Writes are synchronous and serialised, so the lines written immediately
/// before a crash are on disk. Logging failures never propagate to the application.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const long MaximumFileBytes = 5L * 1024 * 1024;
    public const int RetainedFiles = 10;
    private const string FilePrefix = "noctaxis-";
    private readonly object _gate = new();
    private readonly long _maximumFileBytes;
    private StreamWriter? _writer;
    private long _bytes;
    private bool _truncated;

    public RollingFileLoggerProvider(string directory, DateTimeOffset? now = null,
        long maximumFileBytes = MaximumFileBytes, int retainedFiles = RetainedFiles)
    {
        Directory = directory;
        _maximumFileBytes = maximumFileBytes;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var started = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
            FilePath = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture,
                $"{FilePrefix}{started:yyyyMMdd-HHmmss}-{System.Environment.ProcessId}.log"));
            // Retention is best effort and swallows its own failures: being unable to delete an old
            // file must not cost this session its log.
            PruneOldFiles(directory, retainedFiles - 1);
            var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (IsSinkFailure(ex))
        {
            // Diagnostics must never stop the application from starting.
            FilePath = null;
            System.Diagnostics.Trace.TraceWarning($"Noctaxis file logging is unavailable: {ex.Message}");
        }
    }

    public string Directory { get; }
    /// <summary>The current session's log file, or null when file logging could not be started.</summary>
    public string? FilePath { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate) CloseWriter();
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var builder = new StringBuilder(128);
        builder.Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviation(level)).Append("] ").Append(category).Append(": ").Append(message);
        if (exception is not null) builder.AppendLine().Append(exception);
        var line = builder.AppendLine().ToString();
        lock (_gate)
        {
            if (_writer is null || _truncated) return;
            try
            {
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (_bytes + bytes > _maximumFileBytes)
                {
                    _truncated = true;
                    _writer.Write("... log size limit reached; further entries for this session are discarded." +
                                  System.Environment.NewLine);
                    return;
                }
                _writer.Write(line);
                _bytes += bytes;
            }
            // The sink itself failed (the disk filled, the file was removed under us). Give up on
            // file logging for this session rather than letting it reach the calling code.
            catch (Exception ex) when (IsSinkFailure(ex))
            {
                System.Diagnostics.Trace.TraceWarning($"Noctaxis file logging stopped: {ex.Message}");
                CloseWriter();
            }
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

    private static void PruneOldFiles(string directory, int keep)
    {
        try
        {
            var old = new DirectoryInfo(directory).GetFiles(FilePrefix + "*.log")
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(Math.Max(0, keep));
            foreach (var file in old)
            {
                try { file.Delete(); }
                catch (Exception ex) when (IsSinkFailure(ex)) { }
            }
        }
        catch (Exception ex) when (IsSinkFailure(ex))
        {
            System.Diagnostics.Trace.TraceWarning($"Noctaxis log retention failed: {ex.Message}");
        }
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
