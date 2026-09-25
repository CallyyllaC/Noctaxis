using Microsoft.Extensions.Logging;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.Tests;

public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-log-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WritesEntriesWithLevelCategoryAndException()
    {
        string path;
        using (var provider = new RollingFileLoggerProvider(_directory))
        {
            path = Assert.IsType<string>(provider.FilePath);
            var logger = provider.CreateLogger("Noctaxis.Test");
            logger.LogWarning(new IOException("disk full"), "Save failed for {Reason}", "test");
        }

        var text = File.ReadAllText(path);
        Assert.StartsWith(_directory, path);
        Assert.Contains("[WRN] Noctaxis.Test: Save failed for test", text);
        Assert.Contains("System.IO.IOException: disk full", text);
    }

    [Fact]
    public void StopsGrowingAtTheSizeLimit()
    {
        string path;
        using (var provider = new RollingFileLoggerProvider(_directory, maximumFileBytes: 512))
        {
            path = provider.FilePath!;
            var logger = provider.CreateLogger("Noctaxis.Test");
            for (var i = 0; i < 100; i++) logger.LogInformation("Entry {Index} with some padding text", i);
        }

        var text = File.ReadAllText(path);
        Assert.Contains("log size limit reached", text);
        Assert.True(new FileInfo(path).Length < 512 + 200);
        Assert.DoesNotContain("Entry 99 ", text);
    }

    [Fact]
    public void KeepsOnlyTheNewestSessionFiles()
    {
        Directory.CreateDirectory(_directory);
        for (var day = 1; day <= 5; day++)
            File.WriteAllText(Path.Combine(_directory, $"noctaxis-2020010{day}-000000-1.log"), "old");
        var unrelated = Path.Combine(_directory, "notes.txt");
        File.WriteAllText(unrelated, "keep");

        using var provider = new RollingFileLoggerProvider(_directory,
            now: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), retainedFiles: 3);

        var remaining = Directory.GetFiles(_directory, "noctaxis-*.log").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(3, remaining.Length);
        Assert.Equal(["noctaxis-20200104-000000-1.log", "noctaxis-20200105-000000-1.log"], remaining[..2]);
        Assert.Equal(Path.GetFileName(provider.FilePath), remaining[2]);
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void UnusableDirectoryDisablesFileLoggingWithoutThrowing()
    {
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "logs");
        File.WriteAllText(blocked, "a file where the logs directory should be");

        using var provider = new RollingFileLoggerProvider(blocked);
        var logger = provider.CreateLogger("Noctaxis.Test");
        logger.LogError("Not written anywhere");

        Assert.Null(provider.FilePath);
        Assert.False(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void LoggingAfterDisposeDegradesInsteadOfThrowing()
    {
        var provider = new RollingFileLoggerProvider(_directory);
        var logger = provider.CreateLogger("Noctaxis.Test");
        provider.Dispose();

        // Program disposes the provider while other threads may still be logging.
        logger.LogCritical("After shutdown");
        provider.Dispose();

        Assert.False(logger.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void ExceptionsFromTheCallerAreNotSwallowed()
    {
        using var provider = new RollingFileLoggerProvider(_directory);
        var logger = provider.CreateLogger("Noctaxis.Test");

        // Only failures of the log file itself are hidden; a broken message formatter is the
        // caller's defect and must surface.
        Assert.Throws<InvalidOperationException>(() => logger.Log<object?>(LogLevel.Error, default, null, null,
            (_, _) => throw new InvalidOperationException("formatter defect")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
