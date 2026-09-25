using CommunityToolkit.Mvvm.ComponentModel;
using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>Controls proxy runtime state, never a second visibility/opacity model.
/// Owned by the Planner map; subscribes once for that map's lifetime.</summary>
public sealed class PlannerLayersViewModel : ObservableObject
{
    private readonly MapLayerStateController _runtime;
    private const string Id = LightPollutionMapBinding.LayerId;
    private int _revision, _savedRevision, _requestedRevision;
    private Task _commit = Task.CompletedTask;
    public event EventHandler? PreferencesEdited;
    public Func<Task>? PersistAsync { private get; set; }
    private string? _persistenceError;
    public string? PersistenceError => _persistenceError;
    public bool HasPersistenceError => _persistenceError is not null;

    public async Task CommitSafelyAsync()
    {
        try { await CommitAsync(); _persistenceError = null; }
        // Only expected save failures are recoverable here; anything else is a defect and must surface.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _persistenceError = "Layer preferences could not be saved. Reopen Layers and try again."; }
        OnPropertyChanged(nameof(PersistenceError));
        OnPropertyChanged(nameof(HasPersistenceError));
    }

    public PlannerLayersViewModel(MapLayerStateController runtime)
    {
        _runtime = runtime;
        runtime.StateChanged += (_, state) =>
        {
            if (state.Id != Id) return;
            OnPropertyChanged(nameof(ShowLightPollution));
            OnPropertyChanged(nameof(LightPollutionOpacityPercent));
        };
    }
    public bool ShowLightPollution
    {
        get => _runtime[Id].IsVisible;
        set { if (_runtime.SetVisibility(Id, value)) Edited(); }
    }
    public double LightPollutionOpacityPercent
    {
        get => _runtime[Id].Opacity * 100;
        set
        {
            if (!double.IsFinite(value)) return;
            if (_runtime.SetOpacity(Id, Math.Clamp(value / 100, 0, 1))) Edited();
        }
    }
    private void Edited() { _revision++; PreferencesEdited?.Invoke(this, EventArgs.Empty); }

    public Task CommitAsync()
    {
        _requestedRevision = _revision;
        if (!_commit.IsCompleted) return _commit;
        return _revision == _savedRevision || PersistAsync is null ? Task.CompletedTask : _commit = SaveAsync();
    }
    private async Task SaveAsync()
    {
        // Coalesce explicit commits during disk IO. A new drag alone must not
        // start repeated writes just because an earlier save is still in flight.
        while (_savedRevision < _requestedRevision)
        {
            var revision = _revision;
            await PersistAsync!();
            _savedRevision = revision;
        }
    }
}
