using System.Runtime.CompilerServices;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Measurements;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.ViewModels;

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
            foreach (var definition in _settingDefinitions)
            {
                if (Equals(definition.Read(previous), definition.Read(value))) continue;
                if (!_deferredSettings.ContainsKey(definition.Property)) OnPropertyChanged(definition.Property);
                foreach (var dependent in definition.AlsoNotify) OnPropertyChanged(dependent);
            }
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
        new(nameof(SettingsUnits), "Measurement system", s => MeasurementUnits.NormaliseId(s.Units),
            AlsoNotify: [nameof(WeatherDetails), nameof(ConfiguredWeatherDetails)]),
        new(nameof(SettingsExternalMapProvider), "External map provider", s => s.ExternalMapProvider),
        new(nameof(SettingsTimeZoneId), "Time zone", s => s.SelectedTimeZoneId),
        new(nameof(SettingsTimeSnapMinutes), "Time slider snap interval", s => s.TimeSnapMinutes),
        new(nameof(SettingsCameraHeightAboveGroundMetres), "Camera height", s => s.EffectiveCameraHeightAboveGroundMetres),
        new(nameof(SettingsWeatherCacheDistance), "Forecast reuse distance", s => s.EffectiveWeather.CacheDistanceKilometres),
        new(nameof(SettingsTerrainCacheLimitGiB), "Maximum terrain cache size", s => s.EffectiveTerrainCacheLimitBytes),
        new(nameof(SettingsLightPollutionColorMap), "Light pollution colour map",
            s => Mapping.LightPollutionColorMaps.Resolve(s.LightPollution?.PaletteId).Id,
            AlsoNotify: [nameof(LightPollutionColorMapDescription), nameof(LightPollutionColourVisionAdvice),
                nameof(HasLightPollutionColourVisionAdvice), nameof(LightPollutionMapPreferences)]),
        new(nameof(SettingsEnableTerrainCalculations), "Terrain calculations", s => s.EnableTerrainCalculations),
        new(nameof(SettingsMinimumTerrainFrameCoveragePercent), "Minimum terrain frame coverage",
            s => s.EffectiveCameraFraming.MinimumTerrainFrameCoveragePercent),
        new(nameof(SettingsTerrainMinimapContext), "Terrain minimap context", s => s.EffectiveTerrainMinimapContext),
        new(nameof(SettingsCameraFramingOverlayVisible), "Camera framing on map", s => s.EffectiveCameraFraming.IsOverlayVisible),
        new(nameof(SettingsShowFramingVisibilityLimits), "Weather and terrain visibility limits",
            s => s.EffectiveCameraFraming.ShowVisibilityLimits),
        new(nameof(SettingsFramingShadingOpacityPercent), "Camera overlay opacity", s => s.EffectiveCameraFraming.ShadingOpacityPercent),
        new(nameof(SettingsTerrainTintStrengthPercent), "Terrain tint strength", s => s.EffectiveCameraFraming.TerrainTintStrengthPercent),
        new(nameof(SettingsTerrainObstructionColour), "Terrain obstruction colour", s => s.EffectiveCameraFraming.TerrainObstructionColour,
            AlsoNotify: [nameof(SettingsTerrainObstructionColourHex)]),
        new(nameof(SettingsFramingLineThickness), "Boundary line thickness", s => s.EffectiveCameraFraming.LineThickness),
        new(nameof(SettingsTerrainDebugOverlay), "Terrain diagnostics", s => s.TerrainDebugOverlay),
    ];

    // ---- The settings, each a view of Settings --------------------------------------------------

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

    public double SettingsTerrainMinimapContext
    {
        // Only the minimap's drawn extent changes, so dragging previews it live.
        get => Settings.EffectiveTerrainMinimapContext;
        set => ChangeSetting(s => s with { TerrainMinimapContext = Math.Clamp(
            double.IsFinite(value) ? value : AppSettings.DefaultTerrainMinimapContext, 1.0, 2.5) });
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
            ShowOperationalNotice("A settings change could not be applied: " + ex.Message);
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
        ? "Waiting for restart: " + string.Join(", ", PendingRestartSettings) + "."
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
            RestartStatus = "Noctaxis was not restarted because your settings could not be saved.";
            IsRestarting = false;
            return;
        }
        var result = _restartService.Restart();
        if (result.Succeeded) return;
        _logger.LogWarning("Noctaxis could not restart itself: {Failure}", result.Failure);
        RestartStatus = $"Noctaxis could not restart itself ({result.Failure}). Your changes are saved; close and reopen Noctaxis to apply them.";
        IsRestarting = false;
    }
}
