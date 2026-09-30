using Noctaxis.Core.Localisation;
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
    public Task<bool> ConfirmClearTerrainCacheAsync(CancellationToken cancellationToken = default) =>
        ShowOwnedDialogAsync(() => new ConfirmationDialog(Strings.Get("Dialogs_ClearTerrainCache_Title"),
            Strings.Get("Dialogs_ClearTerrainCache_Message"),
            Strings.Get("Dialogs_ClearTerrainCache_Confirm")), false, cancellationToken);

    public async Task<LocationSearchResult?> ShowLocationSearchAsync(string? initialQuery = null,
        CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new LocationSearchDialog(search, initialQuery),
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
            Strings.Get("Dialogs_DeleteLocation_Title"),
            Strings.Format("Dialogs_DeleteLocation_Message_Format", location.Name),
            Strings.Get("Dialogs_DeleteLocation_Confirm")), false, cancellationToken);
    }

    public async Task<bool> ConfirmRefreshSavedLocationThumbnailsAsync(int locationCount, CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new ConfirmationDialog(
            Strings.Get("Dialogs_RegenerateMapImages_Title"),
            Strings.Format("Dialogs_RegenerateMapImages_Message_Format", locationCount),
            Strings.Get("Dialogs_RegenerateMapImages_Confirm")), false, cancellationToken);
    }

    public async Task<bool> ConfirmRefreshSavedLocationSettlementCachesAsync(int locationCount,
        CancellationToken cancellationToken = default)
    {
        return await ShowOwnedDialogAsync(() => new ConfirmationDialog(
            Strings.Get("Dialogs_RefreshSettlement_Title"),
            Strings.Format("Dialogs_RefreshSettlement_Message_Format", locationCount),
            Strings.Get("Dialogs_RefreshSettlement_Confirm")), false, cancellationToken);
    }

    public Task<bool> ConfirmDestructiveAsync(DestructiveConfirmation request, CancellationToken cancellationToken = default) =>
        ShowOwnedDialogAsync(() => new ConfirmationDialog(request.Title, request.Message, request.ConfirmLabel), false,
            cancellationToken);

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
