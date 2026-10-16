using Noctaxis.Core.Localisation;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Measurements;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>A language the application can run in: its culture identifier and its displayed name.</summary>
public sealed record LanguageOption(string Id, string Label);

/// <summary>When a changed setting is applied to the running application.</summary>
public enum SettingCommit
{
    /// <summary>At once; the save that follows is debounced so rapid changes coalesce into one write.</summary>
    Immediate,
    /// <summary>
    /// After a short pause in editing. For values whose application recomputes terrain, evicts cached
    /// files or refreshes weather over the network, so spun or partly typed intermediate values never
    /// start that work. The control shows the requested value throughout.
    /// </summary>
    Deferred
}

/// <summary>
/// Settings: every control in Settings is a view of <see cref="Settings"/>, the single source of truth.
/// Changing one updates that field only, applies it (at once or deferred, see <see cref="SettingCommit"/>)
/// and saves automatically through the state save pipeline. There is no separate editor copy to save or
/// revert. A setting marked <see cref="SettingDefinition.RequiresRestart"/> is saved immediately but its
/// consumers read <see cref="ActiveSettings"/>, the values this process started with, until restart.
/// </summary>
public partial class MainViewModel
{
    /// <summary>A user-facing setting: its view-model property, its label, and how to read it.</summary>
    /// <param name="Read">Its value in a settings record, as compared to detect changes.</param>
    /// <param name="RequiresRestart">
    /// True when the running process cannot adopt a new value safely. None does today; see docs/settings.md.
    /// </param>
    /// <param name="AlsoNotify">Derived properties that change with it.</param>
    internal sealed record SettingDefinition(string Property, string Label, Func<AppSettings, object?> Read,
        bool RequiresRestart = false, params string[] AlsoNotify);

    private AppSettings _settings = new();
    private AppSettings _activeSettings = new();
    private bool _settingsLoaded;
    private readonly List<SettingDefinition> _settingDefinitions = CreateSettingDefinitions();
    private readonly Dictionary<string, (object? Value, Func<AppSettings, AppSettings> Apply)> _deferredSettings =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? _deferredSettingsCancellation;
    private readonly List<Task> _settingsApplications = [];
    private readonly IApplicationRestartService _restartService;

    /// <summary>Quiet period before a <see cref="SettingCommit.Deferred"/> change is applied.</summary>
    internal TimeSpan DeferredSettingsDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    public AppSettings Settings
    {
        get => _settings;
        private set
        {
            var previous = _settings;
            if (Equals(previous, value)) return;
            _settings = value;
            // While the units change, a control may momentarily hold a number in the old unit against a
            // limit in the new one and write its corrected value back. That is not an edit.
            _changingMeasurementProfile = previous.EffectiveMeasurementSystem != value.EffectiveMeasurementSystem;
            try
            {
                foreach (var definition in _settingDefinitions)
                {
                    if (Equals(definition.Read(previous), definition.Read(value))) continue;
                    if (!_deferredSettings.ContainsKey(definition.Property)) OnPropertyChanged(definition.Property);
                    foreach (var dependent in definition.AlsoNotify) OnPropertyChanged(dependent);
                }
            }
            finally { _changingMeasurementProfile = false; }
            NotifyPendingRestart();
        }
    }

    /// <summary>
    /// Settings as this process uses them for anything that requires a restart: fixed at startup. For every
    /// other setting it equals the saved value.
    /// </summary>
    public AppSettings ActiveSettings => _activeSettings;

    private static List<SettingDefinition> CreateSettingDefinitions() =>
    [
        new(nameof(SettingsLanguage), Strings.Get("Settings_Name_Language"), s => s.EffectiveLanguage),
        // Everything that shows a physical quantity is drawn again in the new units.
        new(nameof(SettingsUnits), Strings.Get("Settings_Name_MeasurementSystem"), s => MeasurementUnits.NormaliseId(s.Units),
            AlsoNotify: [nameof(Measurements), nameof(WeatherDetails), nameof(ConfiguredWeatherDetails), nameof(GroundObstructionText),
                nameof(FramingVisibilityStatus),
                nameof(GroundElevationSourceText), nameof(EffectiveObserverAltitudeText),
                nameof(EffectiveCameraAltitudeShortText), nameof(EffectiveCameraAltitudeDetail),
                // The limit before the value, so the control ends on the value in the new unit.
                nameof(CameraHeightMaximum), nameof(SettingsCameraHeightAboveGround), nameof(CameraHeightLabel),
                nameof(CameraHeightAccessibleName), nameof(SettingsWeatherCacheDistanceDisplay), nameof(WeatherCacheDistanceLabel), nameof(WeatherCacheDistanceUnit),
                nameof(WeatherCacheDistanceAccessibleName)]),
        new(nameof(SettingsExternalMapProvider), Strings.Get("Settings_Name_ExternalMapProvider"), s => s.ExternalMapProvider),
        new(nameof(SettingsTimeZoneId), Strings.Get("Settings_Name_TimeZone"), s => s.SelectedTimeZoneId),
        new(nameof(SettingsTimeSnapMinutes), Strings.Get("Settings_Name_TimeSnapInterval"), s => s.TimeSnapMinutes),
        new(nameof(SettingsCameraHeightAboveGroundMetres), Strings.Get("Settings_Name_CameraHeight"), s => s.EffectiveCameraHeightAboveGroundMetres,
            AlsoNotify: [nameof(SettingsCameraHeightAboveGround),
                // The Planner overlay shows the effective camera altitude, which is the ground plus this height.
                nameof(EffectiveCameraAltitudeMetres), nameof(EffectiveObserverAltitudeText),
                nameof(EffectiveCameraAltitudeShortText), nameof(EffectiveCameraAltitudeDetail)]),
        new(nameof(SettingsWeatherCacheDistance), Strings.Get("Settings_Name_ForecastReuseDistance"), s => s.EffectiveWeather.CacheDistanceKilometres,
            AlsoNotify: [nameof(SettingsWeatherCacheDistanceDisplay)]),
        new(nameof(SettingsTerrainCacheLimitGiB), Strings.Get("Settings_Name_TerrainCacheSize"), s => s.EffectiveTerrainCacheLimitBytes),
        new(nameof(SettingsLightPollutionColorMap), Strings.Get("Settings_Name_LightPollutionColourMap"),
            s => Mapping.LightPollutionColorMaps.Resolve(s.LightPollution?.PaletteId).Id,
            AlsoNotify: [nameof(LightPollutionColorMapDescription), nameof(LightPollutionColourVisionAdvice),
                nameof(HasLightPollutionColourVisionAdvice), nameof(LightPollutionMapPreferences)]),
        new(nameof(SettingsEnableTerrainCalculations), Strings.Get("Settings_Name_TerrainCalculations"), s => s.EnableTerrainCalculations),
        new(nameof(SettingsMinimumTerrainFrameCoveragePercent), Strings.Get("Settings_Name_MinimumTerrainFrameCoverage"),
            s => s.EffectiveCameraFraming.MinimumTerrainFrameCoveragePercent),
        new(nameof(SettingsPlannerPipEnabled), Strings.Get("Settings_Name_PlannerPip"), s => s.EffectivePlannerPip.IsEnabled,
            AlsoNotify: [nameof(IsPlannerPipEnabled)]),
        new(nameof(SettingsPlannerPipShowRoadsAndLabels), Strings.Get("Settings_Name_PlannerPipRoadsAndLabels"),
            s => s.EffectivePlannerPip.ShowRoadsAndLabels, AlsoNotify: [nameof(PlannerPipShowRoadsAndLabels)]),
        new(nameof(SettingsPlannerPipEnhanceContrast), Strings.Get("Settings_Name_PlannerPipEnhanceContrast"),
            s => s.EffectivePlannerPip.EnhanceContrast, AlsoNotify: [nameof(PlannerPipEnhanceContrast)]),
        new(nameof(SettingsCameraFramingOverlayVisible), Strings.Get("Settings_Name_CameraFramingOnMap"), s => s.EffectiveCameraFraming.IsOverlayVisible),
        new(nameof(SettingsShowFramingVisibilityLimits), Strings.Get("Settings_Name_VisibilityLimits"),
            s => s.EffectiveCameraFraming.ShowVisibilityLimits),
        new(nameof(SettingsInvertHorizontalDrag), Strings.Get("Settings_Name_InvertHorizontalDrag"), s => s.EffectiveCameraFraming.InvertHorizontalDrag),
        new(nameof(SettingsInvertVerticalDrag), Strings.Get("Settings_Name_InvertVerticalDrag"), s => s.EffectiveCameraFraming.InvertVerticalDrag),
        new(nameof(SettingsFramingShadingOpacityPercent), Strings.Get("Settings_Name_CameraOverlayOpacity"), s => s.EffectiveCameraFraming.ShadingOpacityPercent),
        new(nameof(SettingsTerrainTintStrengthPercent), Strings.Get("Settings_Name_TerrainTintStrength"), s => s.EffectiveCameraFraming.TerrainTintStrengthPercent),
        new(nameof(SettingsTerrainObstructionColour), Strings.Get("Settings_Name_TerrainObstructionColour"), s => s.EffectiveCameraFraming.TerrainObstructionColour,
            AlsoNotify: [nameof(SettingsTerrainObstructionColourHex)]),
        new(nameof(SettingsFramingLineThickness), Strings.Get("Settings_Name_BoundaryLineThickness"), s => s.EffectiveCameraFraming.LineThickness),
        new(nameof(SettingsTerrainDebugOverlay), Strings.Get("Settings_Name_TerrainDiagnostics"), s => s.TerrainDebugOverlay),
    ];

    // ---- The settings, each a view of Settings --------------------------------------------------

    /// <summary>
    /// The languages Noctaxis ships, each with its name from the resources. The identifier is what is
    /// saved; the name is only what is shown.
    /// </summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = AppLanguages.SupportedIds
        .Select(id => new LanguageOption(id, Strings.Get("Settings_Language_Name_" + id.Replace('-', '_')))).ToArray();

    /// <summary>
    /// The application language: text, number and date formats, and the language preferred for place
    /// names on the map. There is no separate setting for any of those.
    /// </summary>
    public LanguageOption? SettingsLanguage
    {
        get => LanguageOptions.FirstOrDefault(option => option.Id == Settings.EffectiveLanguage) ?? LanguageOptions[0];
        set { if (value is not null) ChangeSetting(s => s with { Language = AppLanguages.Normalise(value.Id) }); }
    }

    public string SettingsUnits
    {
        get => MeasurementUnits.NormaliseId(Settings.Units);
        set => ChangeSetting(s => s with { Units = MeasurementUnits.NormaliseId(value) });
    }

    public ExternalMapProviderOption? SettingsExternalMapProvider
    {
        get => ExternalMapProviders.FirstOrDefault(option => option.Provider == Settings.ExternalMapProvider) ?? ExternalMapProviders[0];
        set { if (value is not null) ChangeSetting(s => s with { ExternalMapProvider = value.Provider }); }
    }

    public string SettingsTimeZoneId
    {
        get => Settings.SelectedTimeZoneId;
        set { if (!string.IsNullOrWhiteSpace(value)) ChangeSetting(s => s with { SelectedTimeZoneId = value }); }
    }

    public int SettingsTimeSnapMinutes
    {
        get => Settings.TimeSnapMinutes;
        set => ChangeSetting(s => s with { TimeSnapMinutes = Math.Clamp(value, 1, 30) });
    }

    public double SettingsCameraHeightAboveGroundMetres
    {
        // Re-resolving the observer re-runs terrain calculations.
        get => Requested(Settings.EffectiveCameraHeightAboveGroundMetres);
        set => ChangeSetting(s => s with { CameraHeightAboveGroundMetres = AppSettings.NormaliseCameraHeight(value) },
            SettingCommit.Deferred, value);
    }

    // ---- Quantities edited in the measurement profile's units ----------------------------------------
    // The setting is stored in SI (the two properties around this block). The controls edit it in
    // the unit the profile gives that quantity, to one decimal place, and name that unit.

    /// <summary>The measurement system as the map and drawing controls take it.</summary>
    public MeasurementSystem Measurements => Settings.EffectiveMeasurementSystem;

    private bool _changingMeasurementProfile;

    /// <summary>Camera height above ground in the profile's height unit.</summary>
    public double SettingsCameraHeightAboveGround
    {
        get => Math.Round(Settings.MeasurementProfile.Height.FromBase(SettingsCameraHeightAboveGroundMetres), 1);
        set
        {
            if (_changingMeasurementProfile || !double.IsFinite(value) || value == SettingsCameraHeightAboveGround) return;
            // The rule is the SI one: whatever was typed is converted, then held within the limits in metres.
            SettingsCameraHeightAboveGroundMetres = AppSettings.NormaliseCameraHeight(Settings.MeasurementProfile.Height.ToBase(value));
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The largest camera height the control accepts: <see cref="AppSettings.MaximumCameraHeightAboveGroundMetres"/>
    /// in the profile's height unit, rounded up to the control's one decimal place so that the limit
    /// itself can be shown and typed. It is a convenience for the control, not the rule; the setter
    /// holds every value to the limit in metres.
    /// </summary>
    public double CameraHeightMaximum =>
        Math.Ceiling(Settings.MeasurementProfile.Height.FromBase(AppSettings.MaximumCameraHeightAboveGroundMetres) * 10 - 1e-9) / 10;

    public string CameraHeightLabel =>
        Strings.Format("Settings_Equipment_CameraHeightAboveGround_Label_Format", Settings.MeasurementProfile.Height.Symbol);
    public string CameraHeightAccessibleName =>
        Strings.Format("Settings_Equipment_CameraHeightAboveGround_AccessibleName_Format", Settings.MeasurementProfile.Height.Name);

    /// <summary>Forecast reuse distance in the profile's long-distance unit.</summary>
    public double SettingsWeatherCacheDistanceDisplay
    {
        get => Math.Round(Settings.MeasurementProfile.LongDistance.FromBase(SettingsWeatherCacheDistance), 1);
        set
        {
            if (_changingMeasurementProfile || !double.IsFinite(value) || value == SettingsWeatherCacheDistanceDisplay) return;
            SettingsWeatherCacheDistance = Math.Clamp(Settings.MeasurementProfile.LongDistance.ToBase(value), 0, 100);
            OnPropertyChanged();
        }
    }

    public string WeatherCacheDistanceLabel => Strings.Get("Settings_Weather_ForecastReuseDistance_Label");
    /// <summary>The reuse distance's unit, shown after its value.</summary>
    public string WeatherCacheDistanceUnit => Settings.MeasurementProfile.LongDistance.Symbol;
    public string WeatherCacheDistanceAccessibleName =>
        Strings.Format("Settings_Weather_ForecastReuseDistance_AccessibleName_Format", Settings.MeasurementProfile.LongDistance.Name);

    public double SettingsWeatherCacheDistance
    {
        // A new reuse distance refreshes the forecast over the network.
        get => Requested(Settings.EffectiveWeather.CacheDistanceKilometres);
        set => ChangeSetting(s => s with { Weather = s.EffectiveWeather with
            { CacheDistanceKilometres = Math.Clamp(double.IsFinite(value) ? value : 5, 0, 100) } }, SettingCommit.Deferred, value);
    }

    /// <summary>
    /// The smallest configurable terrain cache. A size is a limit, never an instruction to delete everything:
    /// emptying the cache is the explicit Clear terrain cache action.
    /// </summary>
    public const double MinimumTerrainCacheLimitGiB = 0.25;

    public double SettingsTerrainCacheLimitGiB
    {
        // A lower limit deletes cached terrain files.
        get => Requested(Settings.EffectiveTerrainCacheLimitBytes / (1024d * 1024 * 1024));
        set => ChangeSetting(s => s with { TerrainCacheLimitBytes =
            (long)(Math.Clamp(double.IsFinite(value) ? value : 2, MinimumTerrainCacheLimitGiB, 1024) * 1024 * 1024 * 1024) }, SettingCommit.Deferred, value);
    }

    public Mapping.ILightPollutionColorMap SettingsLightPollutionColorMap
    {
        get => Mapping.LightPollutionColorMaps.Resolve(LightPollutionMapPreferences.PaletteId);
        set
        {
            if (value is null) return;
            ChangeSetting(s => s with { LightPollution = (s.LightPollution ?? new()).Normalised() with { PaletteId = value.Id } });
        }
    }

    public bool SettingsEnableTerrainCalculations
    {
        get => Settings.EnableTerrainCalculations;
        set => ChangeSetting(s => s with { EnableTerrainCalculations = value });
    }

    public double SettingsMinimumTerrainFrameCoveragePercent
    {
        get => Settings.EffectiveCameraFraming.MinimumTerrainFrameCoveragePercent;
        set => ChangeSetting(s => WithFraming(s, f => f with { MinimumTerrainFrameCoveragePercent = Math.Clamp(double.IsFinite(value) ? value : 5, 0, 50) }));
    }

    /// <summary>Whether the Planner shows its Picture-in-Picture map. Off, the map is not created at all.</summary>
    public bool SettingsPlannerPipEnabled
    {
        get => Settings.EffectivePlannerPip.IsEnabled;
        set => ChangeSetting(s => s with { PlannerPip = s.EffectivePlannerPip with { IsEnabled = value } });
    }

    /// <summary>Whether the Picture-in-Picture map draws roads, boundaries and names. The main map always does.</summary>
    public bool SettingsPlannerPipShowRoadsAndLabels
    {
        get => Settings.EffectivePlannerPip.ShowRoadsAndLabels;
        set => ChangeSetting(s => s with { PlannerPip = s.EffectivePlannerPip with { ShowRoadsAndLabels = value } });
    }

    /// <summary>Whether the Picture-in-Picture map draws its terrain relief in the stronger tone. The main map never does.</summary>
    public bool SettingsPlannerPipEnhanceContrast
    {
        get => Settings.EffectivePlannerPip.EnhanceContrast;
        set => ChangeSetting(s => s with { PlannerPip = s.EffectivePlannerPip with { EnhanceContrast = value } });
    }

    public bool SettingsCameraFramingOverlayVisible
    {
        get => Settings.EffectiveCameraFraming.IsOverlayVisible;
        // The same setting as the Planner's framing toggle.
        set => IsCameraFramingOverlayVisible = value;
    }

    public bool SettingsShowFramingVisibilityLimits
    {
        get => Settings.EffectiveCameraFraming.ShowVisibilityLimits;
        set => ShowFramingVisibilityLimits = value;
    }

    /// <summary>Whether a drag across the Camera frame turns it the other way. The bearing itself is unchanged.</summary>
    public bool SettingsInvertHorizontalDrag
    {
        get => Settings.EffectiveCameraFraming.InvertHorizontalDrag;
        set => ChangeSetting(s => WithFraming(s, f => f with { InvertHorizontalDrag = value }));
    }

    /// <summary>Whether a drag up and down the Camera frame tilts it the other way. The pitch itself is unchanged.</summary>
    public bool SettingsInvertVerticalDrag
    {
        get => Settings.EffectiveCameraFraming.InvertVerticalDrag;
        set => ChangeSetting(s => WithFraming(s, f => f with { InvertVerticalDrag = value }));
    }

    public double SettingsFramingShadingOpacityPercent
    {
        get => Settings.EffectiveCameraFraming.ShadingOpacityPercent;
        set => ChangeSetting(s => WithFraming(s, f => f with { ShadingOpacityPercent = value }));
    }

    public double SettingsTerrainTintStrengthPercent
    {
        // Drawing only, so dragging previews it live.
        get => Settings.EffectiveCameraFraming.TerrainTintStrengthPercent;
        set => ChangeSetting(s => WithFraming(s, f => f with { TerrainTintStrengthPercent = value }));
    }

    public Color SettingsTerrainObstructionColour
    {
        get => Color.Parse(Settings.EffectiveCameraFraming.TerrainObstructionColour);
        set => ChangeSetting(s => WithFraming(s, f => f with { TerrainObstructionColour = $"#{value.R:X2}{value.G:X2}{value.B:X2}" }));
    }

    public string SettingsTerrainObstructionColourHex => Settings.EffectiveCameraFraming.TerrainObstructionColour;

    public double SettingsFramingLineThickness
    {
        get => Settings.EffectiveCameraFraming.LineThickness;
        set => ChangeSetting(s => WithFraming(s, f => f with { LineThickness = value }));
    }

    public bool SettingsTerrainDebugOverlay
    {
        get => Settings.TerrainDebugOverlay;
        set => ChangeSetting(s => s with { TerrainDebugOverlay = value });
    }

    private static AppSettings WithFraming(AppSettings settings, Func<CameraFramingSettings, CameraFramingSettings> change) =>
        settings with { CameraFraming = change(settings.EffectiveCameraFraming).Normalised() };

    // ---- Committing changes ------------------------------------------------------------------------

    /// <summary>The requested value of a deferred setting still in its quiet period, otherwise the saved one.</summary>
    private T Requested<T>(T committed, [CallerMemberName] string property = "") =>
        _deferredSettings.TryGetValue(property, out var pending) && pending.Value is T value ? value : committed;

    private void ChangeSetting(Func<AppSettings, AppSettings> change, SettingCommit commit = SettingCommit.Immediate,
        object? requested = null, [CallerMemberName] string property = "")
    {
        // Controls may write their initial values back before persisted settings have been loaded.
        if (!_settingsLoaded) return;
        if (commit == SettingCommit.Deferred)
        {
            _deferredSettings[property] = (requested, change);
            OnPropertyChanged(property);
            ScheduleDeferredSettings();
            return;
        }
        CommitSettings(change(Settings));
    }

    private void CommitSettings(AppSettings next)
    {
        if (Equals(next, Settings)) return;
        TrackSettingsApplication(ApplySettingsCoreAsync(next, persistNow: false));
    }

    private void ScheduleDeferredSettings()
    {
        _deferredSettingsCancellation?.Cancel();
        _deferredSettingsCancellation?.Dispose();
        _deferredSettingsCancellation = new CancellationTokenSource();
        _ = CommitDeferredSettingsAfterDelayAsync(_deferredSettingsCancellation.Token);
    }

    private async Task CommitDeferredSettingsAfterDelayAsync(CancellationToken token)
    {
        // Resumes on the scheduling (UI) context, where settings are owned.
        try { await Task.Delay(DeferredSettingsDelay, token); }
        catch (OperationCanceledException) { return; }
        if (!token.IsCancellationRequested) CommitDeferredSettings();
    }

    /// <summary>Applies every deferred change now, as one update.</summary>
    private void CommitDeferredSettings()
    {
        _deferredSettingsCancellation?.Cancel();
        _deferredSettingsCancellation?.Dispose();
        _deferredSettingsCancellation = null;
        if (_deferredSettings.Count == 0) return;
        var next = Settings;
        foreach (var (_, pending) in _deferredSettings) next = pending.Apply(next);
        var properties = _deferredSettings.Keys.ToArray();
        _deferredSettings.Clear();
        CommitSettings(next);
        // The controls now show the saved (normalised) value.
        foreach (var property in properties) OnPropertyChanged(property);
    }

    private void TrackSettingsApplication(Task application)
    {
        lock (_settingsApplications) _settingsApplications.Add(application);
        _ = ObserveSettingsApplicationAsync(application);
    }

    private async Task ObserveSettingsApplicationAsync(Task application)
    {
        try { await application; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A settings change could not be applied");
            ShowOperationalNotice(Strings.Format("Shell_Notice_SettingNotApplied_Format", ex.Message));
        }
        finally { lock (_settingsApplications) _settingsApplications.Remove(application); }
    }

    /// <summary>
    /// Applies any deferred changes now and completes when every settings change has been applied. Saving
    /// still follows the usual debounce; <see cref="FlushAsync"/> writes immediately.
    /// </summary>
    public async Task CommitPendingSettingsAsync()
    {
        CommitDeferredSettings();
        Task[] running;
        lock (_settingsApplications) running = [.. _settingsApplications];
        try { await Task.WhenAll(running); }
        catch { /* Reported by ObserveSettingsApplicationAsync. */ }
    }

    // ---- Collections: equipment profiles and weather fields ------------------------------------------

    private void AttachWeatherFieldOption(WeatherFieldOptionViewModel option) =>
        option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(WeatherFieldOptionViewModel.IsEnabled)) return;
            // Requested fields change the forecast request, so a burst of ticks is refreshed once.
            ChangeSetting(s => s with { Weather = s.EffectiveWeather with
            {
                EnabledFields = WeatherFieldOptions.Where(item => item.IsEnabled).Select(item => item.Field).ToArray()
            } }, SettingCommit.Deferred, property: nameof(WeatherFieldOptions));
        };

    private void AttachEquipmentEditor(System.ComponentModel.INotifyPropertyChanged editor) =>
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Profile") EquipmentEdited();
        };

    /// <summary>Saves the equipment lists once every profile is valid; an invalid draft is shown, never saved.</summary>
    private void EquipmentEdited()
    {
        OnPropertyChanged(nameof(EquipmentValidationMessage));
        if (!EquipmentIsValid())
        {
            _deferredSettings.Remove(nameof(EquipmentCameraEditors));
            return;
        }
        ChangeSetting(s => EquipmentIsValid() ? s with
        {
            Equipment = new EquipmentSettings(
                EquipmentCameraEditors.Select(item => item.Profile).ToArray(),
                EquipmentLensEditors.Select(item => item.Profile).ToArray())
        } : s, SettingCommit.Deferred, property: nameof(EquipmentCameraEditors));
    }

    private bool EquipmentIsValid() => EquipmentCameraEditors.Count > 0 && EquipmentLensEditors.Count > 0 &&
        EquipmentCameraEditors.All(item => item.IsValid) && EquipmentLensEditors.All(item => item.IsValid);

    // ---- Restart-required settings -------------------------------------------------------------------

    /// <summary>
    /// Restart-required settings whose saved value differs from the value this process is using. Changing
    /// one back to its active value removes it again.
    /// </summary>
    public IReadOnlyList<string> PendingRestartSettings => _settingDefinitions
        .Where(definition => definition.RequiresRestart &&
                             !Equals(definition.Read(_activeSettings), definition.Read(Settings)))
        .Select(definition => definition.Label).ToArray();

    public bool HasPendingRestart => PendingRestartSettings.Count > 0;

    public string PendingRestartSummary => HasPendingRestart
        ? Strings.Format("Settings_Restart_Pending_Format", string.Join(", ", PendingRestartSettings))
        : string.Empty;

    [ObservableProperty] private string? _restartStatus;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestartApplicationCommand))] private bool _isRestarting;

    private void NotifyPendingRestart()
    {
        OnPropertyChanged(nameof(PendingRestartSettings));
        OnPropertyChanged(nameof(HasPendingRestart));
        OnPropertyChanged(nameof(PendingRestartSummary));
    }

    /// <summary>For tests: registers an additional setting, e.g. a restart-required one.</summary>
    internal void AddSettingDefinitionForTesting(SettingDefinition definition)
    {
        _settingDefinitions.Add(definition);
        NotifyPendingRestart();
    }

    private bool CanRestartApplication() => !IsRestarting;

    /// <summary>
    /// Saves everything, starts a new Noctaxis and closes this one. Nothing closes unless the save
    /// succeeded and the new process started; on failure this instance keeps running and says why.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestartApplication))]
    private async Task RestartApplication()
    {
        IsRestarting = true;
        RestartStatus = null;
        await CommitPendingSettingsAsync();
        if (!await FlushAsync())
        {
            RestartStatus = Strings.Get("Settings_Restart_NotSaved");
            IsRestarting = false;
            return;
        }
        var result = _restartService.Restart();
        if (result.Succeeded) return;
        _logger.LogWarning("Noctaxis could not restart itself: {Failure}", result.Failure);
        RestartStatus = Strings.Format("Settings_Restart_Failed_Format", result.Failure);
        IsRestarting = false;
    }
}
