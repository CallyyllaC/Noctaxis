using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed partial class LocationMapThumbnailServiceTests
{
    [Fact]
    public async Task ProfileProductionTileRetention()
    {
        var output = Environment.GetEnvironmentVariable("NOCTAXIS_REVIEW_PROFILE");
        if (string.IsNullOrEmpty(output)) return;
        using var files = new TestDirectory();
        var handler = new TileHandler(new byte[100_000]);
        using var service = CreateService(files, handler);
        var source = new TestSourceProvider("profile", "Profile").Current;
        var method = typeof(LocationMapThumbnailService).GetMethod("GetTileAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task Request(int x) => await (Task<byte[]>)method.Invoke(service.Value,
            [source, 13, x, 0, false, CancellationToken.None])!;
        var retainedBefore = GC.GetTotalMemory(true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        for (var x = 0; x < 512; x++) await Request(x);
        timer.Stop();
        var requests = handler.RequestCount;
        await Request(511);
        Assert.Equal(requests, handler.RequestCount);
        var result = new { Workload = "512 distinct 100 KB encoded tile payloads; real production tile path, synthetic HTTP", timer.Elapsed.TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            RetainedBytes = GC.GetTotalMemory(true) - retainedBefore, Requests = requests };
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "tile-retention.json"), JsonSerializer.Serialize(result));
        GC.KeepAlive(service);
    }
}

public sealed class ApplicationReviewProfileTests
{
    [AvaloniaFact]
    public void ProfileAndCaptureDesktopPages()
    {
        var output = Environment.GetEnvironmentVariable("NOCTAXIS_REVIEW_PROFILE");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output);
        var timer = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var startup = timer.Elapsed.TotalMilliseconds;
        var tabs = (TabControl)((Grid)window.Content!).Children[1];
        for (var page = 0; page < tabs.ItemCount; page++)
        {
            tabs.SelectedIndex = page;
            Dispatcher.UIThread.RunJobs();
            using var capture = new RenderTargetBitmap(new PixelSize(1440, 900));
            capture.Render(window);
            capture.Save(Path.Combine(output, $"page-{page}.png"), PngBitmapEncoderOptions.Default);
        }
        var settings = (TabControl)((Grid)((TabItem)tabs.Items[2]!).Content!).Children[0];
        for (var section = 0; section < settings.ItemCount; section++)
        {
            settings.SelectedIndex = section;
            Dispatcher.UIThread.RunJobs();
            using var capture = new RenderTargetBitmap(new PixelSize(1440, 900));
            capture.Render(window);
            capture.Save(Path.Combine(output, $"settings-{section}.png"), PngBitmapEncoderOptions.Default);
        }
        window.Close();
        File.WriteAllText(Path.Combine(output, "desktop-shell.json"), JsonSerializer.Serialize(new
        { StartupMilliseconds = startup, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated }));
    }
}
