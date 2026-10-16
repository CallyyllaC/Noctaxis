using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>
/// The Planner's panel layout, and its one route to the settings file. Where the user puts a panel is
/// a preference like any other: it goes through <see cref="Settings"/> and the existing save pipeline,
/// not through a store of its own.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Where the Planner's panels are. Replaced once, when the saved layout has been read; until then
    /// it holds the default layout, so a window built before <see cref="InitializeAsync"/> still has
    /// a workspace to show rather than no panels at all. That first instance is not watched for changes,
    /// and does not need to be: nothing is written before the saved state has been read.
    /// </summary>
    [ObservableProperty] private PlannerDockViewModel _plannerDock = new();

    private void RestorePlannerPanels()
    {
        PlannerDock = new(Settings.EffectivePlannerDock);
    }

    partial void OnPlannerDockChanging(PlannerDockViewModel? oldValue, PlannerDockViewModel newValue)
    {
        if (oldValue is not null) { oldValue.Changed -= PlannerPanelsChanged; oldValue.LayoutReset -= PlannerLayoutReset; }
        newValue.Changed += PlannerPanelsChanged;
        newValue.LayoutReset += PlannerLayoutReset;
    }

    /// <summary>
    /// The rest of Reset Planner layout: the map's instruments back to their shipped sizes and the Picture-in-
    /// Picture map to its shipped zoom. Its centre, Follow and layers are the user's, and stay.
    /// </summary>
    private void PlannerLayoutReset(object? sender, EventArgs e)
    {
        Settings = Settings with
        {
            PlannerOverlays = null,
            PlannerPip = Settings.EffectivePlannerPip with { ResolutionMetresPerPixel = null }
        };
        OnPropertyChanged(nameof(PlannerOverlaySizes));
        // The view first: a Follow that is off saves the view the map has, which must by then be the reset one.
        PlannerPipViewReset?.Invoke(this, EventArgs.Empty);
        NotifyPlannerPip();
        RequestSave();
    }

    /// <summary>
    /// A panel has been moved, pinned, hidden or resized. The whole layout is written, because it is one
    /// arrangement rather than a set of independent values, and the debounce in the save pipeline turns
    /// a drag into a single write.
    /// </summary>
    private void PlannerPanelsChanged(object? sender, EventArgs e)
    {
        if (sender is not PlannerDockViewModel dock) return;
        Settings = Settings with { PlannerDock = dock.Capture() };
        RequestSave();
    }
}
