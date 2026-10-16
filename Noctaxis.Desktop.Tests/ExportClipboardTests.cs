using Avalonia.Headless.XUnit;

namespace Noctaxis.Desktop.Tests;

// S-V04 (Opus supplement): Copy PNG without a clipboard says so, instead of failing silently, and does not render
// a plan image it has nowhere to put.
public sealed partial class MainViewModelTests
{
    /// <summary>NX-32: raw PNG bytes from the clipboard are held to the same size limit as a file.</summary>
    [Fact]
    public async Task ClipboardPngBytesOverTheFileLimitAreRefusedLikeAnOversizedFile()
    {
        var oversized = new byte[Export.PlanImageSources.MaximumBytes + 1];
        var refused = await Export.PlanImageSources.FromClipboardAsync(ClipboardPng(oversized));
        Assert.Null(refused.Bytes);
        Assert.Equal(Export.PlanImageSourceProblem.UnreadableFile, refused.Problem);

        var ordinary = await Export.PlanImageSources.FromClipboardAsync(ClipboardPng([137, 80, 78, 71]));
        Assert.Equal(Export.PlanImageSourceProblem.None, ordinary.Problem);
    }

    [AvaloniaFact]
    public async Task CopyPngWithoutAClipboardReportsTheFailure()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            await vm.WaitForPlannerRefreshAsync();
            vm.StatusMessage = "";

            Assert.False(window.ClipboardAvailable(null));

            Assert.Equal("Export failed: The system clipboard is unavailable.", vm.StatusMessage);
        }
        finally { Close(window); }
    }
}
