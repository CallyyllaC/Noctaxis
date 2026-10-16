using System.Reflection;
using Avalonia;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop;

internal static class Program
{
    /// <summary>Durable log for this process; null in tests and when the logs directory is unusable.</summary>
    internal static RollingFileLoggerProvider? FileLogging { get; private set; }

    /// <summary>The command-line arguments this instance was started with, as a restart passes them on.</summary>
    internal static IReadOnlyList<string> LaunchArguments { get; private set; } = [];

    /// <summary>
    /// This process's ownership of the user's Noctaxis instance and of the data folder (NX-31); null in tests, where
    /// no Program runs. Held from before anything is read or written until Main returns.
    /// </summary>
    internal static ApplicationInstanceOwnership? Ownership { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        LaunchArguments = Services.ApplicationRestartService.WithoutWaitArgument(args, out var previousInstance);
        args = [.. LaunchArguments];
        // One Noctaxis per operating-system user, and one owner of the data folder, before anything (even the log)
        // is written. A restarted instance waits, within a bound, for the instance it replaces to save and exit.
        var dataDirectory = new PlatformUserDataPathProvider().GetApplicationDataDirectory();
        var scope = ApplicationInstanceOwnership.CurrentUserScope();
        var (outcome, ownership) = ApplicationInstanceOwnership.Acquire(new InstanceOwnershipRequest(dataDirectory, scope,
            ApplicationInstanceOwnership.DefaultUserLockDirectory(), previousInstance));
        if (outcome != InstanceOwnershipOutcome.Acquired) return RefuseLaunch(outcome, scope, dataDirectory, args);
        using var owned = ownership!;
        Ownership = owned;
        owned.ActivationRequested += () => Avalonia.Threading.Dispatcher.UIThread.Post(ActivateMainWindow);
        FileLogging = new RollingFileLoggerProvider(Path.Combine(dataDirectory, "logs"));
        var log = FileLogging.CreateLogger("Noctaxis.Desktop.Program");
        // Regenerable caches leave the (possibly roaming) data folder for the platform cache folder; earlier caches
        // move by rename only, before any cache is opened (NX-38).
        var caches = CacheStorage.Prepare(dataDirectory, CacheStorage.ResolveForCurrentPlatform(),
            FileLogging.CreateLogger("Noctaxis.Core.Persistence.CacheStorage"));
        PlatformUserDataPathProvider.UseCacheDirectory(caches);
        log.LogInformation("Caches in {CacheDirectory}", caches);
        // Last-resort diagnostics only. Neither handler marks anything handled or recovers: an
        // exception escaping the UI thread still terminates the process after it has been logged.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.LogCritical(e.ExceptionObject as Exception,
                "Unhandled exception; Noctaxis is terminating (IsTerminating={IsTerminating})", e.IsTerminating);
            Console.Error.WriteLine($"Noctaxis terminated after an unexpected error. Details: {FileLogging?.FilePath}");
        };
        // .NET does not terminate on unobserved task exceptions; record them so lost failures are visible.
        TaskScheduler.UnobservedTaskException += (_, e) =>
            log.LogError(e.Exception, "Unobserved task exception");

        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        log.LogInformation("Noctaxis {Version} starting on {Os} ({Architecture})", version,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        try
        {
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.LogInformation("Noctaxis exited with code {ExitCode}", exitCode);
            return exitCode;
        }
        // The filter logs before the finally block closes the log, without catching the exception.
        catch (Exception ex) when (LogFatal(log, ex)) { throw; }
        finally
        {
            FileLogging.Dispose();
        }
    }

    /// <summary>
    /// A launch that does not own Noctaxis starts nothing writable. A second launch brings the running window forward
    /// where the platform allows it; otherwise, and for a data folder in use or an unfinished restart, it says why.
    /// </summary>
    private static int RefuseLaunch(InstanceOwnershipOutcome outcome, string scope, string dataDirectory, string[] args)
    {
        if (outcome == InstanceOwnershipOutcome.HeldByAnotherInstance)
        {
            // This process is in the foreground (it was just launched): let the running instance take the focus.
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AllowAnyProcess);
            if (ApplicationInstanceOwnership.RequestActivation(scope)) return 0;
        }
        // Reading the saved language is a read only; the owner's state is never written from here.
        AppLanguages.Apply(AppLanguages.ReadSaved(Path.Combine(dataDirectory, "state.json")));
        var message = outcome switch
        {
            InstanceOwnershipOutcome.HeldByAnotherInstance => Strings.Get("Shell_Instance_AlreadyRunning"),
            InstanceOwnershipOutcome.DataFolderInUse => Strings.Format("Shell_Instance_DataFolderInUse_Format", dataDirectory),
            _ => Strings.Get("Shell_Instance_RestartIncomplete")
        };
        InstanceNoticeApp.Show(message, args);
        return outcome == InstanceOwnershipOutcome.HeldByAnotherInstance ? 0 : 1;
    }

    /// <summary>A second launch asked this instance to show itself.</summary>
    private static void ActivateMainWindow()
    {
        if ((Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow is not { } window) return;
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private const int AllowAnyProcess = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static bool LogFatal(ILogger log, Exception exception)
    {
        log.LogCritical(exception, "Unhandled exception on the UI thread; Noctaxis is terminating");
        return false;
    }

    /// <summary>
    /// The X11 window class, which Linux desktops match against the menu entry's StartupWMClass
    /// (packaging/linux/noctaxis.desktop) to give the window its icon and group it with its launcher.
    /// </summary>
    internal const string LinuxWindowClass = "Noctaxis";

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = LinuxWindowClass })
        .WithInterFont()
        .LogToTrace();
}
