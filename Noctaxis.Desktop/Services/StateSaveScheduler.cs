using Microsoft.Extensions.Logging;
using Noctaxis.Core.Persistence;

namespace Noctaxis.Desktop.Services;

/// <summary>Outcome of one state-file write attempt.</summary>
internal sealed record StateSaveResult(bool Succeeded, Exception? Failure = null);

/// <summary>
/// The single path from application state to <see cref="IUserDataStore"/>. Requests carry an
/// already-captured snapshot, so the newest snapshot always wins; writes never overlap; rapid
/// requests coalesce. Expected filesystem failures are reported through <see cref="Completed"/> and
/// leave the state dirty so the next request retries. Any other exception is a programming error and
/// is never swallowed: <see cref="FlushAsync"/> throws it to its caller, and a background (debounced)
/// save hands it to <see cref="UnexpectedBackgroundFailure"/>.
/// Nothing is written until <see cref="Enable"/> is called, i.e. until persisted state has been
/// fully loaded; otherwise a failed or incomplete startup could overwrite the user's saved file.
/// <para>
/// Threading: this class never reads live view-model state. The caller captures the snapshot on its
/// own thread and passes the immutable <see cref="PersistedState"/> in, so the debounce timer, the
/// write gate and the write itself can complete on any thread without racing UI-thread mutation of
/// observable state. Callers must therefore request a save from the thread that owns that state
/// (the UI thread), which is where every property setter and command already runs.
/// </para>
/// </summary>
internal sealed class StateSaveScheduler
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    private readonly Func<PersistedState, CancellationToken, Task> _write;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    /// <summary>The newest snapshot that has not been written yet, or null when nothing is pending.</summary>
    private PersistedState? _pending;
    private bool _enabled;
    private bool _lastWriteFailed;
    private CancellationTokenSource? _debounceCancellation;
    private int _busy;
    private TaskCompletionSource _idle = CompletedSource();

    public StateSaveScheduler(Func<PersistedState, CancellationToken, Task> write,
        ILogger logger, TimeSpan? debounce = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _write = write;
        _logger = logger;
        _debounce = debounce ?? DefaultDebounce;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Raised after every write attempt, on the context that performed it.</summary>
    public event Action<StateSaveResult>? Completed;

    /// <summary>Receives non-filesystem exceptions from debounced saves, which have no awaiting caller.</summary>
    public Action<Exception>? UnexpectedBackgroundFailure { get; set; }

    public bool IsEnabled { get { lock (_gate) return _enabled; } }

    public void Enable() { lock (_gate) _enabled = true; }

    /// <summary>
    /// Schedules a debounced write of <paramref name="state"/>, replacing any snapshot that has not
    /// been written yet. Never throws for filesystem failures.
    /// </summary>
    public void RequestSave(PersistedState state)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (!_enabled) return;
            _pending = state;
            // A pending debounce will pick this snapshot up when it fires.
            if (_debounceCancellation is not null) return;
            _debounceCancellation = new CancellationTokenSource();
            token = _debounceCancellation.Token;
            EnterBusy();
        }
        _ = DebounceThenSaveAsync(token);
    }

    /// <summary>
    /// Writes <paramref name="state"/> now: completes any pending debounce, waits for an in-flight
    /// write and then writes the newest snapshot. Returns false when the write failed for an expected
    /// filesystem reason (already logged and reported).
    /// </summary>
    public async Task<bool> FlushAsync(PersistedState state)
    {
        lock (_gate)
        {
            if (!_enabled) return true;
            // A flush always writes, including for changes that never requested a save.
            _pending = state;
            CancelDebounce();
            EnterBusy();
        }
        try { return await SaveLatestAsync(); }
        finally { ExitBusy(); }
    }

    /// <summary>Completes when no debounce is pending and no write is running.</summary>
    public Task WhenIdleAsync() { lock (_gate) return _idle.Task; }

    private async Task DebounceThenSaveAsync(CancellationToken token)
    {
        try
        {
            try { await _delay(_debounce, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; } // A flush took over.
            lock (_gate)
            {
                if (_debounceCancellation?.Token != token) return;
                _debounceCancellation.Dispose();
                _debounceCancellation = null;
            }
            await SaveLatestAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unexpected failure while saving application state");
            if (UnexpectedBackgroundFailure is { } report) report(ex);
            else throw;
        }
        finally { ExitBusy(); }
    }

    private async Task<bool> SaveLatestAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            while (true)
            {
                PersistedState state;
                lock (_gate)
                {
                    // Nothing pending means the newest requested snapshot is already durable.
                    if (_pending is null) return true;
                    state = _pending;
                    _pending = null;
                }
                try
                {
                    await _write(state, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Application state could not be saved; it will be retried on the next change");
                    lock (_gate) _lastWriteFailed = true;
                    Completed?.Invoke(new StateSaveResult(false, ex));
                    return false;
                }
                bool recovered;
                lock (_gate)
                {
                    recovered = _lastWriteFailed;
                    _lastWriteFailed = false;
                }
                if (recovered) _logger.LogInformation("Application state saved after an earlier failure");
                Completed?.Invoke(new StateSaveResult(true));
            }
        }
        finally { _writeGate.Release(); }
    }

    private void CancelDebounce()
    {
        if (_debounceCancellation is null) return;
        _debounceCancellation.Cancel();
        _debounceCancellation.Dispose();
        _debounceCancellation = null;
    }

    // Called with _gate held.
    private void EnterBusy()
    {
        if (_busy++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ExitBusy()
    {
        TaskCompletionSource? idle = null;
        lock (_gate) if (--_busy == 0) idle = _idle;
        idle?.TrySetResult();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}
