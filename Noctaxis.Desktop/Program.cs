using System.Reflection;
using Avalonia;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop;

internal static class Program
{
    /// <summary>Durable log for this process; null in tests and when the logs directory is unusable.</summary>
    internal static RollingFileLoggerProvider? FileLogging { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        FileLogging = new RollingFileLoggerProvider(
            Path.Combine(new PlatformUserDataPathProvider().GetApplicationDataDirectory(), "logs"));
        var log = FileLogging.CreateLogger("Noctaxis.Desktop.Program");
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

    private static bool LogFatal(ILogger log, Exception exception)
    {
        log.LogCritical(exception, "Unhandled exception on the UI thread; Noctaxis is terminating");
        return false;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
