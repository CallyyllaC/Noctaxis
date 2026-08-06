using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Views;

public sealed class DesktopDialogService(LocationSearchViewModel search) : IPlannerDialogService
{
    private readonly SemaphoreSlim _modalGate = new(1, 1);
    private NoctaxisDialogWindow? _activeDialog;
    public Window? Owner { get; set; }

    public async Task<LocationSearchResult?> ShowLocationSearchAsync(CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new LocationSearchDialog(search),
            default(LocationSearchResult?), cancellationToken);
    }

    public async Task<SavedLocationEdit?> ShowSavedLocationEditAsync(SavedLocation location, bool isCreateMode = false,
        CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new SavedLocationEditDialog(location, isCreateMode),
            default(SavedLocationEdit?), cancellationToken);
    }

    public async Task<bool> ConfirmDeleteSavedLocationAsync(SavedLocation location, CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new ConfirmationDialog(
            "Delete saved location",
            $"Delete ‘{location.Name}’? This cannot be undone.",
            "Delete"), false, cancellationToken);
    }

    public async Task<bool> ConfirmRefreshSavedLocationThumbnailsAsync(int locationCount, CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new ConfirmationDialog(
            "Regenerate all map images",
            $"Download fresh raster maps and road and water overlays for all {locationCount} saved locations? Compatible building caches will be reused.",
            "Regenerate all"), false, cancellationToken);
    }

    public async Task<bool> ConfirmRefreshSavedLocationBuildingCachesAsync(int locationCount,
        CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new ConfirmationDialog(
            "Refresh building caches",
            $"Refresh OpenStreetMap building-star caches for all {locationCount} saved locations without redownloading base maps or road and water data?",
            "Refresh buildings"), false, cancellationToken);
    }

    private async Task<TResult> ShowOwnedDialogAsync<TResult>(Func<NoctaxisDialogWindow> create,
        TResult cancelledResult, CancellationToken cancellationToken)
    {
        var owner = Owner;
        if (owner is null) return cancelledResult;
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _modalGate.WaitAsync(0, cancellationToken))
        {
            _activeDialog?.Activate();
            return cancelledResult;
        }
        try
        {
            var dialog = create();
            _activeDialog = dialog;
            using var cancellation = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) dialog.Close(); }));
            return await dialog.ShowDialog<TResult>(owner);
        }
        finally
        {
            _activeDialog = null;
            _modalGate.Release();
            owner.Activate();
        }
    }

    public async Task<string?> ChooseDemDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (Owner is null) return null;
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose SRTM terrain folder", AllowMultiple = false
        });
        cancellationToken.ThrowIfCancellationRequested();
        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }
}
