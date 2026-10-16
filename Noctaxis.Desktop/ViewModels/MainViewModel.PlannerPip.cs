using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>
/// The Picture-in-Picture map's state: whether it is shown, whether it follows the pin, and its own view —
/// centre, zoom and layers — saved with the settings like the main map's layers. The planning pin, time,
/// targets, camera and weather are the Planner's, shared by both maps, and never part of it.
/// </summary>
public partial class MainViewModel
{
    public PlannerPipSettings PlannerPip => Settings.EffectivePlannerPip;
    public bool IsPlannerPipEnabled => Settings.EffectivePlannerPip.IsEnabled;

    /// <summary>Whether it draws the basemap's roads, boundaries and names; the main map's basemap is its own.</summary>
    public bool PlannerPipShowRoadsAndLabels => Settings.EffectivePlannerPip.ShowRoadsAndLabels;

    /// <summary>Whether its relief is drawn in the stronger, small-view tone; the main map's is its own.</summary>
    public bool PlannerPipEnhanceContrast => Settings.EffectivePlannerPip.EnhanceContrast;

    /// <summary>The Picture-in-Picture map follows the pin: its centre only, never its zoom or layers.</summary>
    public bool PlannerPipFollowPin
    {
        get => Settings.EffectivePlannerPip.FollowPin;
        set => UpdatePlannerPip(pip => pip with { FollowPin = value });
    }

    /// <summary>Its terrain relief layer.</summary>
    public TerrainReliefPreferences PlannerPipTerrainRelief => Settings.EffectivePlannerPip.EffectiveTerrainRelief;

    /// <summary>Its light pollution layer, in the one palette the Planner uses.</summary>
    public LightPollutionPreferences PlannerPipLightPollution =>
        Settings.EffectivePlannerPip.EffectiveLightPollution with { PaletteId = LightPollutionMapPreferences.PaletteId };

    /// <summary>
    /// Raised once the saved settings have loaded, and after Reset Planner layout. The window, and its
    /// Picture-in-Picture map, are built before the load, so the map then takes its saved view: its zoom, and
    /// its centre unless it follows the pin.
    /// </summary>
    public event EventHandler? PlannerPipViewReset;

    /// <summary>Saves the Picture-in-Picture map's view after it moved or zoomed.</summary>
    public void SavePlannerPipView(GeoCoordinate centre, double resolutionMetresPerPixel) =>
        UpdatePlannerPip(pip => pip with
        {
            CentreLatitude = centre.Latitude, CentreLongitude = centre.Longitude,
            ResolutionMetresPerPixel = resolutionMetresPerPixel
        });

    /// <summary>
    /// The layer half of Switch: the main map takes the Picture-in-Picture map's layers and the Picture-in-
    /// Picture map takes the main map's, in one change and one save. The light pollution palette is the
    /// Planner's one palette, so it stays where it is.
    /// </summary>
    public void SwitchPlannerMapLayers()
    {
        var pip = Settings.EffectivePlannerPip;
        var mainTerrain = TerrainReliefMapPreferences;
        var mainLight = LightPollutionMapPreferences;
        Settings = Settings with
        {
            TerrainRelief = pip.EffectiveTerrainRelief,
            LightPollution = mainLight with { IsVisible = pip.EffectiveLightPollution.IsVisible, Opacity = pip.EffectiveLightPollution.Opacity },
            PlannerPip = pip with
            {
                TerrainRelief = mainTerrain,
                LightPollution = new LightPollutionPreferences(mainLight.IsVisible, mainLight.Opacity)
            }
        };
        OnPropertyChanged(nameof(TerrainReliefMapPreferences));
        OnPropertyChanged(nameof(LightPollutionMapPreferences));
        NotifyPlannerPip();
        RequestSave();
    }

    private void UpdatePlannerPip(Func<PlannerPipSettings, PlannerPipSettings> change)
    {
        var current = Settings.EffectivePlannerPip;
        var next = change(current);
        if (next == current) return;
        Settings = Settings with { PlannerPip = next };
        NotifyPlannerPip();
        RequestSave();
    }

    private void NotifyPlannerPip()
    {
        OnPropertyChanged(nameof(PlannerPip));
        OnPropertyChanged(nameof(PlannerPipFollowPin));
        OnPropertyChanged(nameof(PlannerPipShowRoadsAndLabels));
        OnPropertyChanged(nameof(PlannerPipEnhanceContrast));
        OnPropertyChanged(nameof(PlannerPipTerrainRelief));
        OnPropertyChanged(nameof(PlannerPipLightPollution));
    }

    /// <summary>How wide the user wants the map's two instruments, as shares of the map's width.</summary>
    public PlannerOverlaySizes PlannerOverlaySizes => Settings.EffectivePlannerOverlays;

    /// <summary>
    /// Saves a new preferred width for one instrument, leaving the other's preference as it was. The window
    /// calls this when a resize ends, not on every move of the pointer.
    /// </summary>
    public void SetPlannerOverlaySizes(double? pipWidthFraction = null, double? cameraFrameWidthFraction = null)
    {
        var current = Settings.EffectivePlannerOverlays;
        var next = (current with
        {
            PipWidthFraction = pipWidthFraction ?? current.PipWidthFraction,
            CameraFrameWidthFraction = cameraFrameWidthFraction ?? current.CameraFrameWidthFraction
        }).Normalised();
        if (next == current) return;
        Settings = Settings with { PlannerOverlays = next };
        OnPropertyChanged(nameof(PlannerOverlaySizes));
        RequestSave();
    }
}
