using Avalonia.Controls;
using Avalonia.Threading;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

internal static class SettingsTestSupport
{
    /// <summary>Applies deferred settings changes and waits until they are applied and saved.</summary>
    public static async Task SettleSettingsAsync(this MainViewModel viewModel)
    {
        await viewModel.CommitPendingSettingsAsync();
        await viewModel.WaitForPendingSavesAsync();
    }

    /// <summary>Selects a Settings category tab by its header (the Settings page must be shown).</summary>
    public static TabItem SelectSettingsCategory(this Window window, string header)
    {
        var categories = window.FindControl<TabControl>("SettingsCategories")!;
        var item = categories.Items.OfType<TabItem>().Single(tab => (string?)tab.Header == header);
        categories.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();
        return item;
    }
}

internal sealed class FakeRestartService(ApplicationRestartResult result) : IApplicationRestartService
{
    public int Requests { get; private set; }

    public ApplicationRestartResult Restart()
    {
        Requests++;
        return result;
    }
}
