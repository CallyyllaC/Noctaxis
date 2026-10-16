using Noctaxis.Core.Localisation;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Noctaxis.Desktop.Services;

public sealed record ApplicationRestartResult(bool Succeeded, string? Failure = null)
{
    public static ApplicationRestartResult Started { get; } = new(true);
    public static ApplicationRestartResult Failed(string reason) => new(false, reason);
}

/// <summary>Starts a new Noctaxis process and closes this one, in that order.</summary>
public interface IApplicationRestartService
{
    /// <summary>
    /// Starts the replacement process; only once it has started is this one asked to close (through the
    /// normal window close, which saves state and releases resources). On failure nothing closes.
    /// The caller saves settings first.
    /// </summary>
    ApplicationRestartResult Restart();
}

/// <summary>Used where no application lifetime exists (tests, design time).</summary>
public sealed class UnavailableApplicationRestartService : IApplicationRestartService
{
    public ApplicationRestartResult Restart() => ApplicationRestartResult.Failed(Strings.Get("Settings_Restart_Unavailable"));
}

/// <summary>
/// Relaunches the same executable with the same arguments, on Windows and Linux alike. The new process is
/// told this one's id (<see cref="WaitForExitArgument"/>) and, before it loads anything, waits for this one to
/// release the Noctaxis instance and data-folder ownership (<see cref="Noctaxis.Core.Persistence.ApplicationInstanceOwnership"/>).
/// It never proceeds without that ownership: if this instance has not exited within the bound, the replacement
/// says so and exits, and Noctaxis can simply be started again.
/// </summary>
public sealed class ApplicationRestartService(
    Func<ProcessStartInfo, Process?> start,
    Action requestShutdown,
    ILogger<ApplicationRestartService> logger,
    string? processPath = null,
    string? entryAssemblyPath = null,
    IReadOnlyList<string>? arguments = null,
    int? processId = null) : IApplicationRestartService
{
    public const string WaitForExitArgument = "--noctaxis-restart-after";

    public ApplicationRestartService(Action requestShutdown, ILogger<ApplicationRestartService> logger)
        : this(Process.Start, requestShutdown, logger) { }

    public ApplicationRestartResult Restart()
    {
        ProcessStartInfo info;
        try
        {
            info = CreateStartInfo(processPath ?? Environment.ProcessPath,
                entryAssemblyPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location,
                arguments ?? Program.LaunchArguments, processId ?? Environment.ProcessId);
        }
        catch (InvalidOperationException ex) { return ApplicationRestartResult.Failed(ex.Message); }
        try
        {
            using var process = start(info);
            if (process is null) return ApplicationRestartResult.Failed(Strings.Get("Settings_Restart_ProcessNotStarted"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Noctaxis could not start its replacement process");
            return ApplicationRestartResult.Failed(ex.Message);
        }
        logger.LogInformation("Replacement Noctaxis process started; closing this instance");
        requestShutdown();
        return ApplicationRestartResult.Started;
    }

    /// <summary>
    /// The command that starts this application again. A framework-dependent launch through the
    /// <c>dotnet</c> host passes the entry assembly first; an apphost (Noctaxis.Desktop.exe or the Linux
    /// executable) is started directly.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string? processPath, string? entryAssemblyPath,
        IReadOnlyList<string> arguments, int processId)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("the Noctaxis executable could not be located");
        var info = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        var host = Path.GetFileNameWithoutExtension(processPath);
        if (host.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
                throw new InvalidOperationException("the Noctaxis assembly could not be located");
            info.ArgumentList.Add(entryAssemblyPath);
        }
        foreach (var argument in WithoutWaitArgument(arguments)) info.ArgumentList.Add(argument);
        info.ArgumentList.Add(WaitForExitArgument);
        info.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return info;
    }

    internal static IReadOnlyList<string> WithoutWaitArgument(IReadOnlyList<string> arguments) =>
        WithoutWaitArgument(arguments, out _);

    /// <summary>
    /// Removes a <see cref="WaitForExitArgument"/> pair from <paramref name="arguments"/>, returning the
    /// process id it named, if any.
    /// </summary>
    internal static IReadOnlyList<string> WithoutWaitArgument(IReadOnlyList<string> arguments, out int? waitForProcessId)
    {
        waitForProcessId = null;
        var result = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == WaitForExitArgument)
            {
                if (i + 1 < arguments.Count && int.TryParse(arguments[i + 1], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var id))
                    waitForProcessId = id;
                i++;
                continue;
            }
            result.Add(arguments[i]);
        }
        return result;
    }
}
