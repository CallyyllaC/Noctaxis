using Noctaxis.Core.Localisation;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Environment;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel
{
    private readonly TerrainDiskCache? _terrainDiskCache;
    private int _cacheUsageQueued;
    [ObservableProperty] private string _terrainCacheUsageText = Strings.Get("Settings_Data_TerrainCache_NotLoaded");
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
        TerrainCacheUsageText = Strings.Format("Settings_Data_TerrainCache_Usage_Format",
            usage.Bytes / (1024d * 1024), usage.LimitBytes / (1024d * 1024 * 1024), usage.Entries);
        ShowTerrainCacheCapacity(usage.Bytes, usage.LimitBytes, usage.Entries);
    }

    [RelayCommand]
    private async Task ClearTerrainCache()
    {
        if (_terrainDiskCache is null || !await _dialogs.ConfirmClearTerrainCacheAsync()) return;
        IsClearingTerrainCache = true;
        try
        {
            _refreshCancellation?.Cancel();
            await _terrainDiskCache.ClearAsync();
            UpdateTerrainCacheUsage();
            TerrainCacheStatus = Strings.Get("Settings_Data_TerrainCache_Cleared");
            if (Snapshot is not null || SelectedPageIndex == 1)
            {
                ScheduleObserverRefresh(0);
                await _activePlannerRefresh;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TerrainCacheStatus = Strings.Format("Settings_Data_TerrainCache_ClearFailed_Format", ex.Message);
        }
        finally { IsClearingTerrainCache = false; UpdateTerrainCacheUsage(); }
    }
}
