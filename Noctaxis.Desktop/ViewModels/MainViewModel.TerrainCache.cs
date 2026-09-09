using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Environment;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel
{
    private readonly TerrainDiskCache? _terrainDiskCache;
    private int _cacheUsageQueued;
    [ObservableProperty] private double _settingsTerrainCacheLimitGiB = 2;
    [ObservableProperty] private string _terrainCacheUsageText = "Not loaded";
    [ObservableProperty] private string _terrainCacheStatus = string.Empty;
    [ObservableProperty] private bool _isClearingTerrainCache;

    private void QueueTerrainCacheUsage()
    {
        if (Interlocked.Exchange(ref _cacheUsageQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _cacheUsageQueued, 0);
            UpdateTerrainCacheUsage();
        });
    }

    private void UpdateTerrainCacheUsage()
    {
        if (_terrainDiskCache is not { } cache) return;
        var usage = cache.Usage;
        TerrainCacheUsageText = $"{usage.Bytes / (1024d * 1024):N1} MiB / {usage.LimitBytes / (1024d * 1024 * 1024):0.##} GiB · {usage.Entries:N0} files";
    }

    [RelayCommand]
    private async Task ClearTerrainCache()
    {
        if (_terrainDiskCache is null || !await _dialogs.ConfirmClearTerrainCacheAsync()) return;
        IsClearingTerrainCache = true;
        try
        {
            _refreshCancellation?.Cancel();
            _terrainDebugMapCancellation?.Cancel();
            await _terrainDiskCache.ClearAsync();
            UpdateTerrainCacheUsage();
            TerrainCacheStatus = "Terrain cache cleared. Data will be downloaded again when needed.";
            if (Snapshot is not null || SelectedPageIndex == 1)
            {
                ScheduleObserverRefresh(0);
                await _activePlannerRefresh;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TerrainCacheStatus = $"Terrain cache could not be fully cleared: {ex.Message}";
        }
        finally { IsClearingTerrainCache = false; UpdateTerrainCacheUsage(); }
    }
}
