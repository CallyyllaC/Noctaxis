using System.Diagnostics;

namespace Noctaxis.Core.Environment;

/// <summary>Ambient development instrumentation scoped to one terrain build.</summary>
internal static class EnvironmentalPerformanceDiagnostics
{
    private static readonly AsyncLocal<Action<string, double>?> Sink = new();

    public static IDisposable Use(Action<string, double> sink)
    {
        var previous = Sink.Value;
        Sink.Value = sink;
        return new Restore(previous);
    }

    public static async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> operation)
    {
        var timer = Stopwatch.StartNew();
        try { return await operation().ConfigureAwait(false); }
        finally
        {
            timer.Stop();
            Sink.Value?.Invoke(stage, timer.Elapsed.TotalMilliseconds);
        }
    }

    public static T Measure<T>(string stage, Func<T> operation)
    {
        var timer = Stopwatch.StartNew();
        try { return operation(); }
        finally
        {
            timer.Stop();
            Sink.Value?.Invoke(stage, timer.Elapsed.TotalMilliseconds);
        }
    }

    public static void Add(string stage, double milliseconds) => Sink.Value?.Invoke(stage, milliseconds);

    private sealed class Restore(Action<string, double>? previous) : IDisposable
    {
        public void Dispose() => Sink.Value = previous;
    }
}
