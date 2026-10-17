using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Skia;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(Noctaxis.Desktop.Tests.TestAppBuilder))]

namespace Noctaxis.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<Noctaxis.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
        // The runtime platform services are the first subsystem every isolated test sets up, so the headless
        // thread owns the UI dispatcher before anything (the compositor, the App) binds to it.
        var runtimePlatformServices = builder.RuntimePlatformServicesInitializer!;
        return builder.UseRuntimePlatformSubsystem(() =>
        {
            HeadlessUiThread.Claim();
            runtimePlatformServices();
        }, builder.RuntimePlatformServicesName ?? "");
    }
}

/// <summary>
/// Avalonia 12 makes the first thread to resolve <see cref="Dispatcher.UIThread"/> after the headless session's
/// per-test dispatcher reset the UI thread. Pool work left behind by earlier tests resolves it too: Mapsui's
/// MapControl.InvalidateCanvas hops through Task.Run, MainViewModel posts terrain-cache usage from
/// TerrainDiskCache callbacks, and plain [Fact]s build pens and brushes. If one of those lands between the reset
/// and this test's platform setup, a pool thread owns the UI dispatcher and setup fails in ServerCompositor with
/// "a different thread owns it". Claim undoes such a claim and takes the dispatcher for the headless thread
/// under the dispatcher's own lock, so no other thread can take it before the next reset.
/// </summary>
internal static class HeadlessUiThread
{
    private static readonly object DispatcherLock =
        typeof(Dispatcher).GetField("s_globalLock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        ?? throw new MissingFieldException(nameof(Dispatcher), "s_globalLock");
    private static readonly Action ResetDispatcher =
        typeof(Dispatcher).GetMethod("ResetBeforeUnitTests", BindingFlags.NonPublic | BindingFlags.Static)?.CreateDelegate<Action>()
        ?? throw new MissingMethodException(nameof(Dispatcher), "ResetBeforeUnitTests");

    public static void Claim()
    {
        lock (DispatcherLock)
        {
            if (!Dispatcher.UIThread.CheckAccess()) ResetDispatcher();
            Dispatcher.UIThread.VerifyAccess();
        }
    }
}
