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
            $"Download fresh raster maps and road and water overlays for all {locationCount} saved locations? Shared environmental tiles will be reused.",
            "Regenerate all"), false, cancellationToken);
    }

    public async Task<bool> ConfirmRefreshSavedLocationSettlementCachesAsync(int locationCount,
        CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new ConfirmationDialog(
            "Refresh WSF settlement layers",
            $"Rebuild WSF settlement layers for all {locationCount} saved locations without redownloading base maps or road and water data?",
            "Refresh settlement"), false, cancellationToken);
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

}
