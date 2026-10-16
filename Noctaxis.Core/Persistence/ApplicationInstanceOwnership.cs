using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Noctaxis.Core.Persistence;

/// <summary>What happened when this process asked to become the one Noctaxis instance.</summary>
public enum InstanceOwnershipOutcome
{
    /// <summary>This process owns the user's instance and the data folder until it is disposed or exits.</summary>
    Acquired,
    /// <summary>Another Noctaxis of this operating-system user is running; this process must not start.</summary>
    HeldByAnotherInstance,
    /// <summary>The data folder is owned by another process (another user, or a writer outside this lock).</summary>
    DataFolderInUse,
    /// <summary>A restart was asked to wait for the instance it replaces, which still owns everything.</summary>
    PreviousInstanceStillRunning
}

/// <summary>Identifies one instance's ownership claim.</summary>
/// <param name="DataDirectory">The persistent data folder this instance will read and write.</param>
/// <param name="UserScope">
/// The operating-system user the single-instance rule is scoped to (<see cref="ApplicationInstanceOwnership.CurrentUserScope"/>
/// in production; tests use their own scope so they never meet a running Noctaxis).
/// </param>
/// <param name="UserLockDirectory">Where the per-user lock file lives on platforms without named mutexes.</param>
/// <param name="PreviousProcessId">For a restart: the instance being replaced, which is waited for.</param>
/// <param name="RestartWait">How long a restart waits for the previous instance to release ownership.</param>
public sealed record InstanceOwnershipRequest(
    string DataDirectory,
    string UserScope,
    string UserLockDirectory,
    int? PreviousProcessId = null,
    TimeSpan? RestartWait = null);

/// <summary>
/// Makes one Noctaxis process per operating-system user the only reader and writer of its persistent state.
/// Two independent locks are held for the life of the process:
/// <list type="bullet">
/// <item>the user's instance lock: a named mutex on Windows (Global namespace, so every session of the same user
/// shares it, and named after a hash of the user's SID, so other users never meet it), and an exclusive file lock
/// under the user's local data folder elsewhere;</item>
/// <item>the data folder lock: an exclusive lock on <c>.noctaxis-instance.lock</c> inside the data folder, so a
/// second writer of that folder is refused whatever user or instance rule it ran under.</item>
/// </list>
/// Both are released by the operating system when the process ends, however it ends, so a crash never leaves a
/// stale lock that prevents the next launch. Nothing here reads or writes application state.
/// </summary>
public sealed class ApplicationInstanceOwnership : IDisposable
{
    public const string DataLockFileName = ".noctaxis-instance.lock";
    /// <summary>How long a restarted instance waits for the one it replaces to save and exit.</summary>
    public static readonly TimeSpan DefaultRestartWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(200);

    private readonly IDisposable _userLock;
    private readonly FileStream _dataLock;
    private readonly EventWaitHandle? _activation;
    private readonly RegisteredWaitHandle? _activationWait;
    private int _disposed;

    private ApplicationInstanceOwnership(string dataDirectory, IDisposable userLock, FileStream dataLock, EventWaitHandle? activation)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        _userLock = userLock;
        _dataLock = dataLock;
        _activation = activation;
        if (activation is not null)
            _activationWait = ThreadPool.RegisterWaitForSingleObject(activation,
                (_, _) => { if (!IsDisposed) ActivationRequested?.Invoke(); }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>The data folder this instance owns.</summary>
    public string DataDirectory { get; }

    /// <summary>Raised on a thread-pool thread when a second launch asks this instance to show itself.</summary>
    public event Action? ActivationRequested;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Whether this process still owns <paramref name="directory"/>.</summary>
    public bool Owns(string directory) =>
        !IsDisposed && string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            DataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The current operating-system user, as a stable scope for the single-instance rule: the SID on Windows, and
    /// the user name with the home folder elsewhere.
    /// </summary>
    public static string CurrentUserScope()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity.User?.Value is { Length: > 0 } sid) return "sid:" + sid;
        }
        return "user:" + System.Environment.UserName + "|" + System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Where the per-user lock file lives on platforms without named mutexes: the user's local data folder.</summary>
    public static string DefaultUserLockDirectory() =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Noctaxis");

    /// <summary>A short, file- and object-name-safe identity for a scope.</summary>
    public static string ScopeKey(string scope) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32];

    /// <summary>
    /// Tries to become the owner. A restart (<see cref="InstanceOwnershipRequest.PreviousProcessId"/>) waits, up to
    /// <see cref="InstanceOwnershipRequest.RestartWait"/>, while the instance it replaces is still running; any other
    /// launch decides at once. Never waits without bound.
    /// </summary>
    public static (InstanceOwnershipOutcome Outcome, ApplicationInstanceOwnership? Ownership) Acquire(InstanceOwnershipRequest request)
    {
        var deadline = Stopwatch.StartNew();
        var wait = request.RestartWait ?? DefaultRestartWait;
        while (true)
        {
            var outcome = TryAcquireOnce(request, out var ownership);
            if (outcome == InstanceOwnershipOutcome.Acquired) return (outcome, ownership);
            var previousRunning = request.PreviousProcessId is { } previous && IsRunning(previous);
            if (!previousRunning)
            {
                // The instance being replaced is gone: one more attempt, since its locks are released as it exits.
                if (request.PreviousProcessId is not null && deadline.Elapsed < wait)
                {
                    outcome = TryAcquireOnce(request, out ownership);
                    if (outcome == InstanceOwnershipOutcome.Acquired) return (outcome, ownership);
                }
                return (outcome, null);
            }
            if (deadline.Elapsed >= wait) return (InstanceOwnershipOutcome.PreviousInstanceStillRunning, null);
            Thread.Sleep(RetryInterval);
        }
    }

    /// <summary>
    /// Asks the running instance of <paramref name="userScope"/> to bring its window forward. False when that is not
    /// possible here (no instance listening, or a platform without named events), so the caller explains instead.
    /// </summary>
    public static bool RequestActivation(string userScope)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivationName(userScope), out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private static InstanceOwnershipOutcome TryAcquireOnce(InstanceOwnershipRequest request, out ApplicationInstanceOwnership? ownership)
    {
        ownership = null;
        var userLock = TryAcquireUserLock(request);
        if (userLock is null) return InstanceOwnershipOutcome.HeldByAnotherInstance;
        var dataLock = TryAcquireDataLock(request.DataDirectory);
        if (dataLock is null)
        {
            userLock.Dispose();
            return InstanceOwnershipOutcome.DataFolderInUse;
        }
        EventWaitHandle? activation = null;
        if (OperatingSystem.IsWindows())
        {
            try { activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationName(request.UserScope)); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                activation = null; // Activation is a convenience; ownership stands without it.
            }
        }
        ownership = new ApplicationInstanceOwnership(request.DataDirectory, userLock, dataLock, activation);
        return InstanceOwnershipOutcome.Acquired;
    }

    private static string MutexName(string scope) => @"Global\Noctaxis.Instance." + ScopeKey(scope);
    private static string ActivationName(string scope) => @"Global\Noctaxis.Activate." + ScopeKey(scope);

    private static IDisposable? TryAcquireUserLock(InstanceOwnershipRequest request)
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(false, MutexName(request.UserScope));
            try
            {
                if (mutex.WaitOne(0)) return new MutexLock(mutex);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner ended without releasing it (a crash): the mutex is now ours.
                return new MutexLock(mutex);
            }
            mutex.Dispose();
            return null;
        }
        Directory.CreateDirectory(request.UserLockDirectory);
        return TryLockFile(Path.Combine(request.UserLockDirectory, "instance-" + ScopeKey(request.UserScope) + ".lock"));
    }

    private static FileStream? TryAcquireDataLock(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        return TryLockFile(Path.Combine(dataDirectory, DataLockFileName));
    }

    /// <summary>
    /// An exclusive lock on <paramref name="path"/>: a share-nothing handle on Windows, an advisory flock elsewhere
    /// (.NET maps FileShare.None to flock). Either is released when the process ends. The file holds this process's
    /// id for diagnosis only; nothing trusts its contents.
    /// </summary>
    private static FileStream? TryLockFile(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            try
            {
                stream.SetLength(0);
                var text = Encoding.UTF8.GetBytes(System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                stream.Write(text);
                stream.Flush();
            }
            catch (IOException) { }
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _activationWait?.Unregister(null);
        _activation?.Dispose();
        _dataLock.Dispose();
        _userLock.Dispose();
    }

    /// <summary>A held mutex, released by the thread that holds it, or by the operating system when the process ends.</summary>
    private sealed class MutexLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { } // Not the owning thread: the handle's closure (or process exit) releases it.
            mutex.Dispose();
        }
    }
}

/// <summary>Lets the state store confirm, before every save, that this process owns the data folder it writes.</summary>
public interface IDataDirectoryOwnership
{
    bool Owns(string directory);
}

/// <summary>Adapts <see cref="ApplicationInstanceOwnership"/> for the store.</summary>
public sealed class InstanceDataDirectoryOwnership(ApplicationInstanceOwnership ownership) : IDataDirectoryOwnership
{
    public bool Owns(string directory) => ownership.Owns(directory);
}
