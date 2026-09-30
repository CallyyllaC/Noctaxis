using Noctaxis.Core.Localisation;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using NodaTime;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Locations;
using Noctaxis.Core.Environment;
using System.Diagnostics;
using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using Noctaxis.Core.Measurements;
using Noctaxis.Desktop.Services;
using Noctaxis.Core.Supporter;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IPlanningService _planning;
    private readonly ITargetCatalogue _catalogue;
    private readonly ITimeZoneResolver _timeZones;
    private readonly IUserDataStore _store;
    private readonly IScoutingCardExporter _exporter;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILensCalculator _lensCalculator;
    private readonly ICameraFramingGuideCalculator _cameraFramingGuideCalculator;
    private readonly IFramingVisibilityCalculator _framingVisibilityCalculator;
    private readonly ILocalHorizonCalculator _localHorizonCalculator;
    private readonly IClock _clock;
    private readonly IAwooSupporterLicenceVerifier _supporterLicenceVerifier;
    private readonly IExternalUriLauncher _externalUriLauncher;
    private readonly IPlannerDialogService _dialogs;
    private readonly IReverseGeocodingProvider _reverseGeocoding;
    private readonly ITerrainDebugMapService? _terrainDebugMaps;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _temporalCommitCancellation;
    private CancellationTokenSource? _temporalPreviewCancellation;
    private CancellationTokenSource? _reverseLookupCancellation;
    private CancellationTokenSource? _terrainDebugMapCancellation;
    private Task _activePlannerRefresh = Task.CompletedTask;
    private Task _activeTerrainDebugMapRefresh = Task.CompletedTask;
    private readonly SemaphoreSlim _plannerSnapshotCommitGate = new(1, 1);
    private readonly object _plannerRefreshGate = new();
    private long _refreshGeneration;
    private long _reverseLookupGeneration;
    private long _terrainDebugMapGeneration;
    private string? _resolvedPlaceSuggestion;
    private PlanningSession _session;
    private bool _suppressChanges;
    private GeoCoordinate? _lastCustomCoordinate;
    private DateTimeOffset _dateSliderAnchor;
    private readonly Instant _startupInstant;
    private long _settingsApplyGeneration;
    private readonly StateSaveScheduler _stateSaves;
    // One slot per source, so clearing one source's notice cannot discard another's.
    private string? _saveFailureNotice;
    private string? _environmentNotice;

    public MainViewModel(IPlanningService planning, ITargetCatalogue catalogue, ITimeZoneResolver timeZones,
        IUserDataStore store, IScoutingCardExporter exporter,
        ILogger<MainViewModel> logger, ILensCalculator lensCalculator,
        ICameraFramingGuideCalculator cameraFramingGuideCalculator,
        IFramingVisibilityCalculator framingVisibilityCalculator, ILocalHorizonCalculator localHorizonCalculator,
        IClock clock,
        LocationSearchViewModel locationSearch, ILocationResolver locationResolver,
        IDeviceLocationAvailabilityService deviceLocationAvailability, ITargetSearchService targetSearch,
        IPlannerDialogService dialogs, IReverseGeocodingProvider reverseGeocoding,
        ILocationMapThumbnailService? locationMapThumbnails = null,
        ITerrainDebugMapService? terrainDebugMaps = null, TerrainDiskCache? terrainDiskCache = null,
        IHorizonService? terrainDiagnostics = null,
        IAwooSupporterLicenceVerifier? supporterLicenceVerifier = null,
        IExternalUriLauncher? externalUriLauncher = null,
        ExternalMapService? externalMaps = null,
        Noctaxis.Core.LightPollution.LorenzInstallation? lightPollutionInstallation = null,
        Func<TimeSpan, CancellationToken, Task>? persistenceDelay = null,
        IApplicationRestartService? restartService = null,
        ITerrariumTileSource? terrainReliefSource = null)
    {
        TerrainReliefSource = terrainReliefSource;
        _restartService = restartService ?? new UnavailableApplicationRestartService();
        _planning = planning;
        _catalogue = catalogue;
        _timeZones = timeZones;
        _store = store;
        _exporter = exporter;
        _logger = logger;
        _lensCalculator = lensCalculator;
        _cameraFramingGuideCalculator = cameraFramingGuideCalculator;
        _framingVisibilityCalculator = framingVisibilityCalculator;
        _localHorizonCalculator = localHorizonCalculator;
        _clock = clock;
        _supporterLicenceVerifier = supporterLicenceVerifier ?? new AwooSupporterLicenceVerifier();
        _externalUriLauncher = externalUriLauncher ?? new ShellExternalUriLauncher();
        _externalMaps = externalMaps ?? new ExternalMapService(_externalUriLauncher);
        LightPollutionInstallation = lightPollutionInstallation;
        _dialogs = dialogs;
        _reverseGeocoding = reverseGeocoding;
        _terrainDebugMaps = terrainDebugMaps;
        _terrainDiagnostics = terrainDiagnostics as HorizonService;
        _terrainDiskCache = terrainDiskCache;
        _stateSaves = new StateSaveScheduler(store.SaveAsync, logger, delay: persistenceDelay)
        {
            // A non-filesystem failure in a debounced save is a programming error with no awaiting
            // caller; rethrowing it on the UI thread makes it fatal (and logged) instead of lost.
            UnexpectedBackgroundFailure = exception =>
                Dispatcher.UIThread.Post(() => ExceptionDispatchInfo.Throw(exception))
        };
        _stateSaves.Completed += OnStateSaveCompleted;
        if (_terrainDiskCache is not null) _terrainDiskCache.Changed += QueueTerrainCacheUsage;
        _startupInstant = PlannerStartupTime.RoundedLocalHour(clock, timeZones);
        _session = PlanningSession.Default(_startupInstant, timeZones.MachineTimeZoneId);
        Targets = catalogue.Targets;
        _selectedTarget = Targets[0];
        var startupLocal = timeZones.InZone(_startupInstant, timeZones.MachineTimeZoneId);
        _localDate = new DateTimeOffset(startupLocal.Year, startupLocal.Month, startupLocal.Day, 0, 0, 0, TimeSpan.Zero);
        _dateSliderAnchor = _localDate.Value;
        _timeText = $"{startupLocal.Hour:00}:00";
        _minutesOfDay = startupLocal.Hour * 60;
        _latitude = _session.Observer.Latitude;
        _longitude = _session.Observer.Longitude;
        _elevation = _session.Observer.ElevationMetres;
        _timeZoneId = _session.TimeZoneId;
        _focalLength = _session.Lens.FocalLengthMillimetres;
        _selectedOrientation = _session.Lens.Orientation;
        _previewObserver = _session.Observer;
        LocationSearch = locationSearch;
        Locations = new LocationsViewModel(locationResolver, deviceLocationAvailability, OpenCustomAsync, UseSearchResultAsync,
            () => _dialogs.ShowLocationSearchAsync(),
            OpenSavedLocationAsync, EditLocationAsync, DeleteLocationAsync, ToggleFavouriteAsync,
            SaveCurrentAsNewLocation,
            locationMapThumbnails ?? new NullLocationMapThumbnailService(),
            () => _clock.GetCurrentInstant().ToDateTimeOffset());
        // The collection and its subscriber share this view model's lifetime, so the
        // notification subscription cannot keep an otherwise-discarded view model alive.
        Locations.Saved.CollectionChanged += OnSavedLocationCardsChanged;
        SettingsCelestialSearch = new CelestialSearchViewModel(targetSearch, catalogue);
    }

    public IReadOnlyList<AstralTarget> Targets { get; }
    public IReadOnlyList<CameraOrientation> Orientations { get; } = Enum.GetValues<CameraOrientation>();
    public IReadOnlyList<string> SettingsTimeZoneOptions => [AppSettings.UseSystemTimeZoneId, .. _timeZones.AvailableIds.Where(id => !id.Equals(AppSettings.UseSystemTimeZoneId, StringComparison.OrdinalIgnoreCase))];
    public IReadOnlyList<string> UnitsOptions { get; } = MeasurementUnits.Options;
    public ObservableCollection<SavedLocation> SavedLocations { get; } = [];
    public ObservableCollection<CelestialObjectItemViewModel> CelestialObjects { get; } = [];
    public ObservableCollection<WeatherFieldOptionViewModel> WeatherFieldOptions { get; } = [];
    public ObservableCollection<WeatherFieldGroupViewModel> WeatherFieldGroups { get; } = [];
    public ObservableCollection<CameraProfile> Cameras { get; } = [];
    public ObservableCollection<LensProfile> Lenses { get; } = [];
    public ObservableCollection<CameraProfileEditorViewModel> EquipmentCameraEditors { get; } = [];
    public ObservableCollection<LensProfileEditorViewModel> EquipmentLensEditors { get; } = [];
    public LocationSearchViewModel LocationSearch { get; }
    public LocationsViewModel Locations { get; }
    public CelestialSearchViewModel SettingsCelestialSearch { get; }
    public PlanningSession Session => _session;
    public GeoCoordinate Observer => _session.Observer;

    [ObservableProperty] private AstralTarget? _selectedTarget;
    [ObservableProperty] private SavedLocation? _selectedLocation;
    [ObservableProperty] private DateTimeOffset? _localDate;
    [ObservableProperty] private double _minutesOfDay;
    [ObservableProperty] private string _timeText;
    [ObservableProperty] private string _timeZoneId;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenInMapsCommand))] private double _latitude;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenInMapsCommand))] private double _longitude;
    [ObservableProperty] private double _elevation;
    [ObservableProperty] private CameraProfile? _selectedCamera;
    [ObservableProperty] private LensProfile? _selectedLens;
    [ObservableProperty] private CameraOrientation _selectedOrientation;
    [ObservableProperty] private double _focalLength;
    [ObservableProperty] private string _locationName = Strings.Get("Planner_Location_CurrentLocation_Name");
    [ObservableProperty] private string? _currentLocationAttribution;
    public bool HasCurrentLocationAttribution => !string.IsNullOrWhiteSpace(CurrentLocationAttribution);
    [ObservableProperty] private PlanningSnapshot? _snapshot;
    [ObservableProperty] private PlannerRefreshState _plannerRefresh = PlannerRefreshState.Idle;
    [ObservableProperty] private bool _isWeatherRefreshing;
    [ObservableProperty] private bool _isInspectorOpen = true;
    [ObservableProperty] private int _selectedPageIndex;
    [ObservableProperty] private GeoCoordinate _previewObserver;
    [ObservableProperty] private double _previewMinutesOfDay;
    [ObservableProperty] private double _previewDateOffsetDays;
    [ObservableProperty] private bool _isCameraFramingOverlayVisible = true;
    [ObservableProperty] private bool _showFramingVisibilityLimits = true;
    [ObservableProperty] private bool _isLocationInteracting;
    [ObservableProperty] private string? _celestialLimitMessage;
    [ObservableProperty] private TerrainDebugMapSnapshot? _terrainDebugMap;
    [ObservableProperty] private TerrainDebugMapLoadState _terrainDebugMapLoadState =
        TerrainDebugMapLoadState.Disabled;
    [ObservableProperty] private bool _isRefreshingLocationThumbnails;
    [ObservableProperty] private string? _locationThumbnailRefreshStatus;
    public bool CanExport => !IsLocationInteracting && Snapshot is not null;
    public int VisibleCelestialCount => CelestialObjects.Count(item => item.IsVisible);
    public string VisibleCelestialCountText => Strings.Format("Planner_Targets_VisibleCount_Format", VisibleCelestialCount, CelestialVisibilityPolicy.MaximumVisibleObjects);
    [ObservableProperty] private string _statusMessage = Strings.Get("Shell_Status_Starting");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStartupError))] private string? _startupError;
    public bool HasStartupError => StartupError is not null;
    /// <summary>
    /// A recoverable operational problem (e.g. the state file could not be saved), shown as a
    /// dismissible, non-modal warning. Distinct from <see cref="StartupError"/>, which is fatal to the Planner.
    /// </summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOperationalNotice))] private string? _operationalNotice;
    public bool HasOperationalNotice => OperationalNotice is not null;

    public bool IsPlannerRefreshing => PlannerRefresh.IsRefreshing;
    public double PlannerRefreshProgress => PlannerRefresh.Progress;
    public string PlannerRefreshStatusText => PlannerRefresh.StatusText;
    public PlannerPinActivity PlannerPinActivity => PlannerRefresh.PinActivity;
    public bool CelestialOverlaysReady => PlannerRefresh.CelestialOverlayState == PlannerRefreshWorkState.Ready;
    public bool CameraOverlayReady => PlannerRefresh.CameraGeometryState == PlannerRefreshWorkState.Ready;
    public bool ShowTerrainDebugOverlay => Settings.EnableTerrainCalculations && Settings.TerrainDebugOverlay;
    public TerrainHorizonProfile? EnabledTerrainDiagnosticsProfile => ShowTerrainDebugOverlay ? CurrentTerrain : null;
    public TerrainHorizonProfile? TerrainDebugProfile => Settings.EnableTerrainCalculations ? CurrentTerrain : null;
    public long TerrainDebugGeneration => PlannerRefresh.Generation;
    public long TerrainDebugMapGeneration => Volatile.Read(ref _terrainDebugMapGeneration);
    public double? TerrainDebugTargetAltitude => CurrentTerrain is not null
        ? CurrentSnapshot?.Position.Horizontal.AltitudeDegrees
        : null;
    public double TerrainDebugBearing => CurrentCameraBearing ?? 0;
    public double TerrainDebugHorizontalFieldOfView => CameraFramingGuide?.HorizontalFieldOfViewDegrees ?? 60;
    public string TerrainDebugText => !Settings.EnableTerrainCalculations ? Strings.Get("Terrain_CalculationsDisabled") : !Settings.TerrainDebugOverlay ? Strings.Get("Settings_Diagnostics_TerrainDiagnosticsDisabled") : CurrentTerrain is { } terrain
        ? TerrainProfileDiagnostics.CreateDebugSnapshot(terrain,
            CurrentCameraBearing ?? CurrentSnapshot?.Position.Horizontal.AzimuthDegrees ?? 0,
            TerrainDebugTargetAltitude, TerrainDebugGeneration,
            TerrainDebugMap, TerrainDebugMapGeneration,
            CameraFramingVisibility?.WeatherVisibilityDistanceMetres)
        : $"TERRAIN DEBUG\nObserver\nLatitude: {_session.Observer.Latitude:F6}\n" +
          $"Longitude: {_session.Observer.Longitude:F6}\nGeneration: {TerrainDebugGeneration}\n" +
          $"State: Resolving terrain profile\nLocal terrain map: {TerrainDebugMapLoadState}";
    public bool IsElevationManualOverride => _session.EffectiveObserverElevation.IsManualOverride;
    public TerrainElevationResolutionState TerrainElevationResolutionState =>
        _session.EffectiveObserverElevation.ResolutionState;
    public double? ResolvedGroundElevationMetres =>
        _session.EffectiveObserverElevation.ResolvedGroundElevationAslMetres;
    public bool CanResetGroundElevation => IsElevationManualOverride &&
        (!Settings.EnableTerrainCalculations || _session.EffectiveObserverElevation.TerrainGroundElevationAslMetres.HasValue);
    public string GroundElevationSourceText => IsElevationManualOverride
        ? Strings.Get("Terrain_GroundSource_ManualOverride")
        // With terrain off the ground is taken as mean sea level: zero, in the profile's height unit.
        : !Settings.EnableTerrainCalculations ? Strings.Format("Planner_Location_GroundSource_TerrainDisabled_Format",
            Settings.MeasurementProfile.Height.Of(0).Format())
        : _session.EffectiveObserverElevation.TerrainGroundElevationAslMetres.HasValue
            ? Strings.Get("Planner_Location_GroundSource_TerrainDerived")
            : Strings.Get("Planner_Location_GroundSource_Resolving");
    /// <summary>The ground elevation in the measurement profile's height unit, beside <see cref="GroundElevationUnitText"/>.</summary>
    public double? GroundElevationDisplay => ResolvedGroundElevationMetres is double ground
        ? Math.Round(Settings.MeasurementProfile.Height.FromBase(ground), 1)
        : null;
    public string GroundElevationUnitText =>
        Strings.Format("Planner_Location_AboveSeaLevel_Unit_Format", Settings.MeasurementProfile.Height.Symbol);
    public string EffectiveObserverAltitudeText => ResolvedGroundElevationMetres is double ground
        ? Strings.Format("Planner_Location_EffectiveAltitude_Format",
            Settings.MeasurementProfile.Height.Of(ground + Settings.EffectiveCameraHeightAboveGroundMetres, 1).Format(),
            Settings.MeasurementProfile.Height.Of(Settings.EffectiveCameraHeightAboveGroundMetres, 1).Format())
        : Strings.Get("Planner_Location_EffectiveAltitude_Resolving");
    public bool IsFocalLengthEditable => SelectedLens is { IsPrime: false };
    public double FocalLengthMinimum => SelectedLens?.MinimumFocalLengthMillimetres ?? 1;
    public double FocalLengthMaximum => SelectedLens?.MaximumFocalLengthMillimetres ?? 2_000;
    public bool IsLandscapeOrientation
    {
        get => SelectedOrientation == CameraOrientation.Landscape;
        set
        {
            if (value) SelectedOrientation = CameraOrientation.Landscape;
        }
    }
    public bool IsPortraitOrientation
    {
        get => SelectedOrientation == CameraOrientation.Portrait;
        set
        {
            if (value) SelectedOrientation = CameraOrientation.Portrait;
        }
    }
    public string LensFocalRangeText => SelectedLens is null ? Strings.Get("Planner_Camera_NoLensSelected") :
        SelectedLens.IsPrime ? Strings.Format("Planner_Camera_PrimeLens_Format", SelectedLens.MinimumFocalLengthMillimetres) :
        Strings.Format("Planner_Camera_ZoomLens_Format", SelectedLens.MinimumFocalLengthMillimetres, SelectedLens.MaximumFocalLengthMillimetres);

    public string AzimuthText => Snapshot is null ? "—" : $"{Snapshot.Position.Horizontal.AzimuthDegrees:F1}°";
    public string AltitudeText => Snapshot is null ? "—" : $"{Snapshot.Position.Horizontal.AltitudeDegrees:+0.0;-0.0;0.0}°";
    private TargetLocalVisibility? CurrentTargetLocalVisibility => !Settings.EnableTerrainCalculations || Snapshot is null
        ? null
        : _localHorizonCalculator.AssessTarget(Snapshot.Terrain,
            Snapshot.Position.Horizontal.AzimuthDegrees, Snapshot.Position.Horizontal.AltitudeDegrees);
    public string HorizonStatus => !Settings.EnableTerrainCalculations ? Strings.Get("Terrain_CalculationsDisabled") : CurrentTargetLocalVisibility?.State switch
    {
        null => Strings.Get("Planner_Target_Horizon_Calculating"),
        TargetLocalVisibilityState.BelowAstronomicalHorizon => Strings.Get("Planner_Target_Horizon_BelowAstronomical"),
        TargetLocalVisibilityState.TerrainBlocked => Strings.Get("Planner_Target_Horizon_TerrainBlocked"),
        TargetLocalVisibilityState.Marginal => Strings.Get("Planner_Target_Horizon_Marginal"),
        TargetLocalVisibilityState.Clear => Strings.Get("Planner_Target_Horizon_Clear"),
        _ => Strings.Get("Planner_Target_Horizon_TerrainUnavailable")
    };
    public bool HasTargetLocalHorizonDetails => CurrentTargetLocalVisibility is
        { State: TargetLocalVisibilityState.TerrainBlocked or TargetLocalVisibilityState.Marginal or TargetLocalVisibilityState.Clear };
    public string TargetLocalHorizonText => FormatHorizonAngle(CurrentTargetLocalVisibility?.LocalHorizonAltitudeDegrees);
    public string TargetTerrainMarginLabel => CurrentTargetLocalVisibility?.State == TargetLocalVisibilityState.TerrainBlocked
        ? Strings.Get("Planner_Target_BlockedBy_Label")
        : Strings.Get("Planner_Target_Clearance_Label");
    public string TargetTerrainMarginText => CurrentTargetLocalVisibility?.ClearanceDegrees is double clearance
        ? $"{Math.Abs(clearance):0.0}°"
        : "—";
    public string RiseText => FormatTime(Snapshot?.Position.Events.Rise);
    public string TransitText => FormatTime(Snapshot?.Position.Events.Transit);
    public string SetText => FormatTime(Snapshot?.Position.Events.Set);
    public string TerrainStatus => !Settings.EnableTerrainCalculations ? Strings.Get("Terrain_CalculationsDisabled") : CurrentEnvironment is { } environment
        ? environment.ActiveSourceDescription
        : CurrentTerrain is { } terrain
            ? terrain.Status
            : Strings.Get("Planner_Horizon_Loading");
    public string TerrainCurrentLocationText => $"{Observer.Latitude:F5}, {Observer.Longitude:F5}";
    public string GroundHorizonState => !Settings.EnableTerrainCalculations ? Strings.Get("Planner_Horizon_State_Disabled") : IsRefreshWorkLoading(PlannerRefresh.GroundTerrainState)
        ? CurrentTerrain is { IsComplete: false, HasTerrainCoverage: true } ? Strings.Get("Planner_Horizon_State_Refining") : Strings.Get("Planner_Horizon_State_LoadingEllipsis")
        : FormatHorizonState(CurrentTerrain?.HasTerrainCoverage == true,
            CurrentTerrain?.GroundHorizonState);
    public string GroundHorizonAngleText => FormatHorizonAngle(
        CurrentCameraBearing is double bearing ? CurrentTerrain?.GroundAltitudeAt(bearing) : null);
    public string TerrainDatumText => !Settings.EnableTerrainCalculations ? GroundElevationSourceText : CurrentTerrain switch
    {
        null => Strings.Get("Planner_Horizon_ObserverDatum_Loading"),
        { ObserverDatumConfidence: ObserverDatumConfidence.Normal } => Strings.Get("Planner_Horizon_ObserverDatum_Normal"),
        _ => Strings.Get("Planner_Horizon_ObserverDatum_Unavailable")
    };
    public string GroundObstructionText => FormatObstruction(
        CurrentCameraObstruction.HasGroundData,
        CurrentCameraObstruction.GroundFirstObstructionDistanceMetres);
    public string WeatherSummary => Snapshot?.Weather.Conditions is { } weather
        ? Strings.Format("Planner_Weather_Summary_Format", weather.Summary)
        : Snapshot?.Weather.Message ?? Strings.Get("Weather_Loading");
    public string WeatherDetails
    {
        get
        {
            var w = Snapshot?.Weather.Conditions;
            if (w is null) return Strings.Get("Planner_Weather_NoValues");
            var units = Settings.EffectiveMeasurementSystem;
            var temperature = MeasurementUnits.FormatTemperature(w.TemperatureCelsius, units);
            var dew = MeasurementUnits.FormatTemperature(w.DewPointCelsius, units);
            var wind = MeasurementUnits.FormatWindSpeed(w.WindSpeedMetresPerSecond, units);
            var gust = MeasurementUnits.FormatWindSpeed(w.WindGustMetresPerSecond, units);
            var visibility = MeasurementUnits.FormatVisibility(w.VisibilityKilometres, units);
            var precipitation = MeasurementUnits.FormatPrecipitation(w.PrecipitationMillimetres, units);
            return Strings.Format("Planner_Weather_Details_Format", w.IsStale ? Strings.Get("Planner_Weather_Stale_Prefix") : "",
                Value(w.CloudCoverPercent, "%"), temperature, Value(w.LowCloudPercent, "%"), Value(w.MediumCloudPercent, "%"),
                Value(w.HighCloudPercent, "%"), Value(w.PrecipitationProbabilityPercent, "%"), precipitation, wind,
                Value(w.WindDirectionDegrees, "°"), gust, Value(w.HumidityPercent, "%"), dew, visibility);
        }
    }
    public string MoonDetails => Snapshot?.Position.MoonIlluminatedFraction is double fraction
        ? Strings.Format("Planner_Moon_Details_Format", MoonPhaseName(Snapshot.Position.MoonPhaseAngleDegrees ?? 0), fraction * 100, Snapshot.Position.MoonPhaseAngleDegrees)
        : string.Empty;
    public string ConfiguredWeatherDetails
    {
        get
        {
            var w = Snapshot?.Weather.Conditions;
            if (w is null) return Strings.Get("Planner_Weather_NoValues");
            var settings = Settings.EffectiveWeather;
            var units = Settings.EffectiveMeasurementSystem;
            var lines = new List<string>();
            void Add(WeatherField weatherField, string text) { if (settings.IsEnabled(weatherField)) lines.Add(text); }
            Add(WeatherField.TotalCloudCover, Strings.Format("Planner_Weather_Cloud_Format", Value(w.CloudCoverPercent, "%")));
            Add(WeatherField.LowCloudCover, Strings.Format("Planner_Weather_LowCloud_Format", Value(w.LowCloudPercent, "%")));
            Add(WeatherField.MediumCloudCover, Strings.Format("Planner_Weather_MediumCloud_Format", Value(w.MediumCloudPercent, "%")));
            Add(WeatherField.HighCloudCover, Strings.Format("Planner_Weather_HighCloud_Format", Value(w.HighCloudPercent, "%")));
            Add(WeatherField.PrecipitationProbability, Strings.Format("Planner_Weather_PrecipitationProbability_Format", Value(w.PrecipitationProbabilityPercent, "%")));
            Add(WeatherField.PrecipitationAmount, Strings.Format("Planner_Weather_Precipitation_Format", MeasurementUnits.FormatPrecipitation(w.PrecipitationMillimetres, units)));
            Add(WeatherField.PrecipitationType, Strings.Format("Planner_Weather_PrecipitationType_Format", w.PrecipitationType ?? "—"));
            Add(WeatherField.Temperature, Strings.Format("Planner_Weather_Temperature_Format", MeasurementUnits.FormatTemperature(w.TemperatureCelsius, units)));
            Add(WeatherField.DewPoint, Strings.Format("Planner_Weather_DewPoint_Format", MeasurementUnits.FormatTemperature(w.DewPointCelsius, units)));
            Add(WeatherField.RelativeHumidity, Strings.Format("Planner_Weather_Humidity_Format", Value(w.HumidityPercent, "%")));
            Add(WeatherField.WindSpeed, Strings.Format("Planner_Weather_WindSpeed_Format", MeasurementUnits.FormatWindSpeed(w.WindSpeedMetresPerSecond, units)));
            Add(WeatherField.WindGusts, Strings.Format("Planner_Weather_WindGusts_Format", MeasurementUnits.FormatWindSpeed(w.WindGustMetresPerSecond, units)));
            Add(WeatherField.WindDirection, Strings.Format("Planner_Weather_WindDirection_Format", Value(w.WindDirectionDegrees, "°")));
            Add(WeatherField.Visibility, Strings.Format("Planner_Weather_Visibility_Format", MeasurementUnits.FormatVisibility(w.VisibilityKilometres, units)));
            var t = Snapshot!.Astronomy.Sun.Twilight;
            Add(WeatherField.Sunrise, Strings.Format("Planner_Weather_Sunrise_Format", FormatTime(t?.Sunrise)));
            Add(WeatherField.Sunset, Strings.Format("Planner_Weather_Sunset_Format", FormatTime(t?.Sunset)));
            Add(WeatherField.CivilTwilight, Strings.Format("Planner_Weather_CivilTwilight_Format", FormatTime(t?.CivilDawn), FormatTime(t?.CivilDusk)));
            Add(WeatherField.NauticalTwilight, Strings.Format("Planner_Weather_NauticalTwilight_Format", FormatTime(t?.NauticalDawn), FormatTime(t?.NauticalDusk)));
            Add(WeatherField.AstronomicalTwilight, Strings.Format("Planner_Weather_AstronomicalTwilight_Format", FormatTime(t?.AstronomicalDawn), FormatTime(t?.AstronomicalDusk)));
            Add(WeatherField.AstronomicalDarkness, Strings.Format("Planner_Weather_AstronomicalDarkness_Format", FormatTime(t?.AstronomicalDawn), FormatTime(t?.AstronomicalDusk)));
            var moon = Snapshot.Astronomy.Moon;
            Add(WeatherField.MoonPhase, Strings.Format("Planner_Weather_MoonPhase_Format", MoonPhaseName(moon.MoonPhaseAngleDegrees ?? 0)));
            Add(WeatherField.MoonIllumination, Strings.Format("Planner_Weather_MoonIllumination_Format", moon.MoonIlluminatedFraction * 100));
            Add(WeatherField.Moonrise, Strings.Format("Planner_Weather_Moonrise_Format", FormatTime(moon.Events.Rise)));
            Add(WeatherField.Moonset, Strings.Format("Planner_Weather_Moonset_Format", FormatTime(moon.Events.Set)));
            return string.Join('\n', lines);
        }
    }
    public bool HasMoonDetails => Snapshot?.Position.Target.IsMoon == true;
    public bool HasSunDetails => Snapshot?.Position.Target.IsSun == true;
    public string SunDetails
    {
        get
        {
            var t = Snapshot?.Position.Twilight;
            return t is null ? string.Empty : Strings.Format("Planner_Sun_Details_Format", FormatTime(t.Sunrise), FormatTime(t.Sunset),
                FormatTime(t.CivilDawn), FormatTime(t.CivilDusk), FormatTime(t.NauticalDawn), FormatTime(t.NauticalDusk),
                FormatTime(t.AstronomicalDawn), FormatTime(t.AstronomicalDusk));
        }
    }
    public string FieldOfViewText => Snapshot is null ? "—" : $"{Snapshot.FieldOfView.HorizontalDegrees:F1}° × {Snapshot.FieldOfView.VerticalDegrees:F1}°";
    public CameraFramingGuide? CameraFramingGuide => CurrentSnapshot is not { } snapshot || !IsCameraFramingOverlayVisible
        ? null
        : _cameraFramingGuideCalculator.Calculate(
            snapshot.FieldOfView,
            snapshot.Position.Horizontal.AzimuthDegrees,
            Settings.EffectiveCameraFraming with { IsOverlayVisible = IsCameraFramingOverlayVisible });
    private FramingInputs? _framingInputs;
    private FramingVisibilityAssessment? _framingAssessment;
    private readonly record struct FramingInputs(TerrainHorizonProfile Terrain, WeatherResult Weather,
        double Altitude, double Bearing, double HorizontalFov, double VerticalFov, double Pitch, double Threshold);

    public FramingVisibilityAssessment? CameraFramingVisibility => IsCameraFramingOverlayVisible && ShowFramingVisibilityLimits
        ? DerivedCameraFraming : null;

    private FramingVisibilityAssessment? DerivedCameraFraming
    {
        get
        {
            if (CurrentSnapshot is not { } snapshot || CurrentTerrain is not { } terrain) return null;
            var guide = _cameraFramingGuideCalculator.Calculate(snapshot.FieldOfView,
                snapshot.Position.Horizontal.AzimuthDegrees, Settings.EffectiveCameraFraming);
            var inputs = new FramingInputs(terrain, snapshot.Weather,
                snapshot.Position.Horizontal.AltitudeDegrees, guide.CentreBearingDegrees,
                guide.HorizontalFieldOfViewDegrees, snapshot.FieldOfView.VerticalDegrees, CameraPitchDegrees, Settings.EffectiveCameraFraming.MinimumTerrainFrameCoveragePercent);
            // A single UI-owned derived result. Compare actual calculator inputs rather than
            // presentation notifications or the containing snapshot (which changes independently).
            if (_framingInputs is not { } prior || !ReferenceEquals(prior.Terrain, terrain) ||
                prior != inputs)
            {
                _framingAssessment = _framingVisibilityCalculator.Calculate(inputs.Weather, terrain,
                    inputs.Altitude, inputs.Bearing, inputs.HorizontalFov,
                    CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees, inputs.VerticalFov,
                    new CameraTerrainFrame(inputs.Pitch, inputs.VerticalFov, inputs.Threshold));
                _framingInputs = inputs;
            }
            return _framingAssessment;
        }
    }
    public string FramingVisibilityStatus => !IsCameraFramingOverlayVisible
        ? string.Empty
        : !ShowFramingVisibilityLimits
            ? Strings.Get("Planner_Framing_LimitsHidden")
            : CameraFramingVisibility?.StatusFor(Settings.MeasurementProfile) ?? Strings.Get("Planning_Visibility_DataUnavailable");
    public CameraFramingSettings CameraFramingMapSettings => Settings.EffectiveCameraFraming;

    private PlanningSnapshot? CurrentSnapshot =>
        Snapshot is { } snapshot && IsCurrentObserver(snapshot.Session.Observer) ? snapshot : null;

    private PlannerEnvironmentSnapshot? CurrentEnvironment =>
        CurrentSnapshot?.Environment is { } environment && IsCurrentObserver(environment.ObserverCoordinate)
            ? environment
            : null;

    private TerrainHorizonProfile? CurrentTerrain =>
        CurrentSnapshot is { } snapshot && IsCurrentObserver(snapshot.Terrain.Observer)
            ? snapshot.Terrain
            : null;

    private TerrainHorizonProfile DisabledTerrain() => TerrainHorizonProfile.Disabled(_session.Observer,
        _session.Instant, Settings.EffectiveCameraHeightAboveGroundMetres,
        _session.EffectiveObserverElevation.ManualGroundElevationOverrideAslMetres);

    private HorizonObstruction CurrentCameraObstruction
    {
        get
        {
            if (Snapshot is not { } snapshot || CurrentTerrain is not { } terrain) return default;
            return CurrentCameraBearing is double bearing
                ? terrain.TerrainObstructionAt(bearing)
                : default;
        }
    }

    private double? CurrentCameraBearing
    {
        get
        {
            if (Snapshot is not { } snapshot || CurrentTerrain is null) return null;
            return _cameraFramingGuideCalculator.Calculate(snapshot.FieldOfView,
                snapshot.Position.Horizontal.AzimuthDegrees,
                Settings.EffectiveCameraFraming).CentreBearingDegrees;
        }
    }

    /// <summary>
    /// Application entry point for <see cref="InitializeAsync"/>. Expected persisted-data and cache
    /// failures are recovered inside initialisation; anything reaching here is unexpected, so it is
    /// logged and shown, and the Planner is left inactive rather than continuing on partial state.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Noctaxis initialisation failed; the Planner was not activated");
            StartupError = Strings.Format("Shell_Status_StartFailed_Format", ex.Message);
            StatusMessage = StartupError;
        }
    }

    public async Task InitializeAsync()
    {
        var persisted = await _store.LoadAsync(CancellationToken.None);
        Settings = persisted.Settings with
        {
            // A language this version does not ship (a hand edit, a newer version's file) becomes the default.
            Language = persisted.Settings.EffectiveLanguage,
            CameraHeightAboveGroundMetres = persisted.Settings.EffectiveCameraHeightAboveGroundMetres,
            Equipment = persisted.Settings.EffectiveEquipment(persisted.Session.Lens)
        };
        // What this process runs with; restart-required settings keep these values until restart.
        _activeSettings = Settings;
        _settingsLoaded = true;
        NotifyPendingRestart();
        LoadSupporterLicenceState();
        _suppressChanges = true;
        IsCameraFramingOverlayVisible = Settings.EffectiveCameraFraming.IsOverlayVisible;
        ShowFramingVisibilityLimits = Settings.EffectiveCameraFraming.ShowVisibilityLimits;
        _suppressChanges = false;
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        // The window is bound before settings load: the map layers must take the loaded preferences.
        OnPropertyChanged(nameof(TerrainReliefMapPreferences));
        OnPropertyChanged(nameof(LightPollutionMapPreferences));
        SavedLocations.Clear();
        foreach (var location in persisted.Locations) SavedLocations.Add(location);
        if (_terrainDiskCache is not null && Settings.EnableTerrainCalculations)
        {
            try
            {
                await _terrainDiskCache.ConfigureAsync(Settings.EffectiveTerrainCacheLimitBytes,
                    SavedLocations.Select(location => location.Coordinate));
                UpdateTerrainCacheUsage();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The on-disk cache is an optimisation; an unreadable cache directory must not
                // prevent the rest of the application from starting.
                _logger.LogWarning(ex, "Terrain cache could not be reconciled at startup");
                TerrainCacheStatus = Strings.Format("Settings_Data_TerrainCacheUnreadable_Format", ex.Message);
                ShowOperationalNotice(TerrainCacheStatus);
            }
        }
        _lastCustomCoordinate = persisted.LastCustomCoordinate;
        var configuredZone = _timeZones.GetEffectiveId(Settings.SelectedTimeZoneId);
        // state.json is the automatic last-working-state restore, not an explicit historical-session load.
        _session = (persisted.Session with { Instant = _startupInstant,
            TimeZoneId = _timeZones.GetEffectiveId(persisted.Session.TimeZoneId) })
            .WithTerrainCalculationMode(Settings.EnableTerrainCalculations);
        if (_session.SavedLocationId is null) _session = _session with { TimeZoneId = configuredZone };
        LoadEquipmentOptions();
        EnsureCelestialSelections();
        BuildCelestialObjectItems();
        LoadSettingsEditor();
        await DetectLightPollutionAsync();
        Locations.Load(SavedLocations, _lastCustomCoordinate, _session.SavedLocationId);
        await Locations.RefreshDeviceAvailabilityAsync(CancellationToken.None);
        LoadSessionIntoControls();
        PreviewObserver = _session.Observer;
        PreviewMinutesOfDay = MinutesOfDay;
        _dateSliderAnchor = LocalDate ?? _startupInstant.ToDateTimeOffset();
        _initialised = true;
        // Saving starts only once persisted state is fully loaded: an earlier or failed startup must
        // never overwrite the user's file with defaults.
        _stateSaves.Enable();
        ActivatePlannerIfReady();
    }

    public void MoveObserver(GeoCoordinate coordinate)
    {
        PreviewObserverLocation(coordinate);
        CommitObserverLocation(coordinate);
    }

    internal Task WaitForPlannerRefreshAsync() => _activePlannerRefresh;
    internal Task WaitForTerrainDebugMapRefreshAsync() => _activeTerrainDebugMapRefresh;

    public void PreviewObserverLocation(GeoCoordinate coordinate)
    {
        var normalised = coordinate.Normalised();
        if (Angles.GreatCircleDistanceMetres(normalised, _session.Observer) > 2)
        {
            CancelReverseLocationLookup();
            _resolvedPlaceSuggestion = null;
            LocationName = Strings.Get("Planner_Location_NewLocation_Name");
            CurrentLocationAttribution = null;
        }
        SetPreviewObserver(normalised);
        _logger.LogDebug("Map preview coordinate changed");
    }

    private void SetPreviewObserver(GeoCoordinate normalised)
    {
        PreviewObserver = normalised;
        _suppressChanges = true;
        Latitude = normalised.Latitude;
        Longitude = normalised.Longitude;
        Elevation = normalised.ElevationMetres;
        _suppressChanges = false;
    }

    public void CommitObserverLocation(GeoCoordinate coordinate)
        => CommitObserverLocation(coordinate, null, resolvePlaceName: true);

    public void CommitUnresolvedObserverLocation(GeoCoordinate coordinate)
    {
        // A pin/coordinate interaction that lands on the current observer is not a new location:
        // it must not detach the saved location, rename it or restart planning work.
        if (SameObserverPosition(coordinate.Normalised(), _session.Observer)) return;
        CommitObserverLocation(coordinate with { ElevationMetres = 0 }, null, resolvePlaceName: true);
    }

    private void CommitObserverLocation(GeoCoordinate coordinate, string? resolvedName, bool resolvePlaceName)
    {
        var normalised = coordinate.Normalised();
        var locationChanged = !SameObserverPosition(normalised, _session.Observer);
        var elevationState = locationChanged ? new ObserverElevationState { TerrainCalculationsEnabled = Settings.EnableTerrainCalculations } :
            _session.EffectiveObserverElevation;
        normalised = normalised with
        {
            ElevationMetres = elevationState.ResolveGroundElevationAsl(normalised.ElevationMetres)
        };
        CancelReverseLocationLookup();
        _resolvedPlaceSuggestion = string.IsNullOrWhiteSpace(resolvedName) ? null : resolvedName.Trim();
        LocationName = _resolvedPlaceSuggestion ?? Strings.Get("Planner_Location_NewLocation_Name");
        CurrentLocationAttribution = null;
        SetPreviewObserver(normalised);
        _session = _session with
        {
            Observer = normalised,
            SavedLocationId = null,
            ObserverElevation = elevationState
        };
        SelectedLocation = null;
        _lastCustomCoordinate = normalised;
        OnPropertyChanged(nameof(Observer));
        NotifyTerrainProperties();
        NotifyObserverElevationProperties();
        _logger.LogInformation("Map coordinate committed; scheduling authoritative recalculation");
        if (resolvePlaceName) ScheduleReverseLocationLookup(normalised);
        ScheduleObserverRefresh(80);
    }

    private void ScheduleReverseLocationLookup(GeoCoordinate coordinate)
    {
        _reverseLookupCancellation = new CancellationTokenSource();
        var cancellationToken = _reverseLookupCancellation.Token;
        var generation = Interlocked.Increment(ref _reverseLookupGeneration);
        _ = ResolveLocationNameAsync(coordinate, generation, cancellationToken);
    }

    private async Task ResolveLocationNameAsync(GeoCoordinate coordinate, long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(700, cancellationToken);
            var result = await _reverseGeocoding.ResolveAsync(coordinate, cancellationToken);
            if (result is null) return;
            var isCurrent = generation == Volatile.Read(ref _reverseLookupGeneration) &&
                            Angles.GreatCircleDistanceMetres(coordinate, _session.Observer) <= 25;
            if (!isCurrent)
            {
                _logger.LogDebug("Discarded stale reverse-geocoding result for superseded planning coordinate");
                return;
            }
            _resolvedPlaceSuggestion = result.PlaceName;
            LocationName = $"Near {result.PlaceName}";
            CurrentLocationAttribution = result.Attribution;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Reverse-geocoding request cancelled for superseded planning coordinate");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reverse geocoding could not resolve the current planning location");
        }
    }

    private void CancelReverseLocationLookup()
    {
        Interlocked.Increment(ref _reverseLookupGeneration);
        _reverseLookupCancellation?.Cancel();
        _reverseLookupCancellation?.Dispose();
        _reverseLookupCancellation = null;
    }

    partial void OnCurrentLocationAttributionChanged(string? value) =>
        OnPropertyChanged(nameof(HasCurrentLocationAttribution));

    public void SetLocationInteraction(bool isInteracting) => IsLocationInteracting = isInteracting;
    partial void OnIsLocationInteractingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    /// <summary>
    /// Applies settings and saves them now. Returns false only when the settings were applied but could
    /// not be saved; that failure has already been logged and shown.
    /// </summary>
    public Task<bool> ApplySettingsAsync(AppSettings settings)
    {
        CommitDeferredSettings();
        return ApplySettingsCoreAsync(settings, persistNow: true);
    }

    /// <summary>
    /// Applies a complete settings record to the running application, doing only the work its changed
    /// fields need. With <paramref name="persistNow"/> false the save is requested (debounced) rather
    /// than written before returning; the returned task still completes when application has finished.
    /// </summary>
    private async Task<bool> ApplySettingsCoreAsync(AppSettings settings, bool persistNow)
    {
        var previous = Settings;
        var physicalChanged = settings.EnableTerrainCalculations != previous.EnableTerrainCalculations ||
            settings.EffectiveCameraHeightAboveGroundMetres != previous.EffectiveCameraHeightAboveGroundMetres;
        var timeZoneChanged = settings.SelectedTimeZoneId != previous.SelectedTimeZoneId &&
            _timeZones.GetEffectiveId(settings.SelectedTimeZoneId) !=
            _timeZones.GetEffectiveId(previous.SelectedTimeZoneId);
        var weatherPolicyChanged = settings.EffectiveWeather.CacheDistanceKilometres != previous.EffectiveWeather.CacheDistanceKilometres;
        // Requested fields are part of the forecast request: newly enabled ones need fetching.
        var weatherFieldsChanged = !settings.EffectiveWeather.EffectiveFields.SequenceEqual(previous.EffectiveWeather.EffectiveFields);
        var oldEquipment = previous.EffectiveEquipment(_session.Lens);
        var newEquipment = settings.EffectiveEquipment(_session.Lens);
        var equipmentChanged = !oldEquipment.Cameras!.SequenceEqual(newEquipment.Cameras!) ||
            !oldEquipment.Lenses!.SequenceEqual(newEquipment.Lenses!);
        var settingsGeneration = Interlocked.Increment(ref _settingsApplyGeneration);
        var configureTerrainCache = settings.EnableTerrainCalculations && !previous.EnableTerrainCalculations ||
            settings.EffectiveTerrainCacheLimitBytes != previous.EffectiveTerrainCacheLimitBytes;
        var terrainModeChanged = settings.EnableTerrainCalculations != Settings.EnableTerrainCalculations;
        if (terrainModeChanged)
        {
            _refreshCancellation?.Cancel();
            Interlocked.Increment(ref _refreshGeneration);
        }
        Settings = settings with
        {
            Units = MeasurementUnits.NormaliseId(settings.Units),
            CelestialObjects = Settings.EffectiveCelestialObjects,
            CameraHeightAboveGroundMetres = settings.EffectiveCameraHeightAboveGroundMetres,
            Equipment = settings.EffectiveEquipment(_session.Lens)
        };
        // Captured now, so closing Noctaxis while the change is still being applied cannot lose it.
        if (!persistNow) RequestSave();
        // Only a terrain-mode change alters the planning session; other settings leave it untouched.
        if (_session.EnableTerrainCalculations != Settings.EnableTerrainCalculations)
            _session = _session.WithTerrainCalculationMode(Settings.EnableTerrainCalculations);
        if (terrainModeChanged)
        {
            _suppressChanges = true;
            Elevation = _session.Observer.ElevationMetres;
            PreviewObserver = _session.Observer;
            _suppressChanges = false;
            if (Snapshot is not null) Snapshot = Snapshot with { Session = _session,
                Terrain = Settings.EnableTerrainCalculations
                    ? new TerrainHorizonProfile(_session.Observer, [], false, Strings.Get("Terrain_Status_ResolvingProfile"), _session.Instant)
                    : DisabledTerrain(), TerrainCrossings = new(null, null), Environment = null };
            NotifySnapshotProperties();
        }
        if (!Settings.EnableTerrainCalculations) ScheduleTerrainDebugMapRefresh();
        if (_terrainDiskCache is not null && configureTerrainCache)
        {
            string? cacheFailure = null;
            try
            {
                await _terrainDiskCache.ConfigureAsync(settings.EffectiveTerrainCacheLimitBytes,
                    SavedLocations.Select(location => location.Coordinate));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // As at startup: the cache is an optimisation and must not abort applying settings.
                _logger.LogWarning(ex, "Terrain cache could not be reconfigured");
                cacheFailure = Strings.Format("Settings_Data_TerrainCacheUnreadable_Format", ex.Message);
            }
            // A newer toggle owns the UI (and its save) even if this reconciliation completed later.
            if (settingsGeneration != Volatile.Read(ref _settingsApplyGeneration)) return true;
            if (cacheFailure is null) UpdateTerrainCacheUsage();
            else
            {
                TerrainCacheStatus = cacheFailure;
                ShowOperationalNotice(cacheFailure);
            }
        }
        if (equipmentChanged) LoadEquipmentOptions();
        _suppressChanges = true;
        IsCameraFramingOverlayVisible = Settings.EffectiveCameraFraming.IsOverlayVisible;
        ShowFramingVisibilityLimits = Settings.EffectiveCameraFraming.ShowVisibilityLimits;
        if (timeZoneChanged) TimeZoneId = _timeZones.GetEffectiveId(settings.SelectedTimeZoneId);
        _suppressChanges = false;
        if (timeZoneChanged) ApplyLocalDateTime(false);
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(LightPollutionMapPreferences));
        OnPropertyChanged(nameof(TerrainReliefMapPreferences));
        OnPropertyChanged(nameof(WeatherDetails));
        OnPropertyChanged(nameof(ConfiguredWeatherDetails));
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus));
        OnPropertyChanged(nameof(ShowTerrainDebugOverlay));
        OnPropertyChanged(nameof(TerrainDebugProfile));
        OnPropertyChanged(nameof(EnabledTerrainDiagnosticsProfile));
        OnPropertyChanged(nameof(TerrainDebugBearing));
        OnPropertyChanged(nameof(TerrainDebugHorizontalFieldOfView));
        OnPropertyChanged(nameof(TerrainDebugText));
        NotifyObserverElevationProperties();
        if (physicalChanged) ScheduleObserverRefresh(0);
        else if (timeZoneChanged) ScheduleAstronomyRefresh(0);
        NotifyTerrainProperties();
        if (physicalChanged || timeZoneChanged) await _activePlannerRefresh;
        else if (weatherPolicyChanged || weatherFieldsChanged && Snapshot is not null) await RefreshWeather();
        return !persistNow || await FlushAsync();
    }

    /// <summary>
    /// Builds the Settings page's list editors (weather fields and equipment profiles) from the loaded
    /// settings. Scalar settings need no loading: they are views of <see cref="Settings"/>.
    /// </summary>
    private void LoadSettingsEditor()
    {
        LoadAppearance();
        WeatherFieldOptions.Clear();
        foreach (var field in Enum.GetValues<WeatherField>())
        {
            var option = new WeatherFieldOptionViewModel(field, WeatherFieldLabel(field), Settings.EffectiveWeather.IsEnabled(field));
            AttachWeatherFieldOption(option);
            WeatherFieldOptions.Add(option);
        }
        WeatherFieldGroups.Clear();
        foreach (var (labelKey, fields) in WeatherFieldGroupDefinitions)
            WeatherFieldGroups.Add(new WeatherFieldGroupViewModel(Strings.Get(labelKey),
                fields.Select(field => WeatherFieldOptions.Single(option => option.Field == field)).ToArray()));
        EquipmentCameraEditors.Clear();
        foreach (var profile in Settings.EffectiveEquipment(_session.Lens).Cameras!)
            EquipmentCameraEditors.Add(NewCameraEditor(profile));
        EquipmentLensEditors.Clear();
        foreach (var profile in Settings.EffectiveEquipment(_session.Lens).Lenses!)
            EquipmentLensEditors.Add(NewLensEditor(profile));
    }

    private CameraProfileEditorViewModel NewCameraEditor(CameraProfile profile)
    {
        var editor = new CameraProfileEditorViewModel(profile, RemoveCameraProfileEditor);
        AttachEquipmentEditor(editor);
        return editor;
    }

    private LensProfileEditorViewModel NewLensEditor(LensProfile profile)
    {
        var editor = new LensProfileEditorViewModel(profile, RemoveLensProfileEditor);
        AttachEquipmentEditor(editor);
        return editor;
    }

    private void LoadEquipmentOptions()
    {
        var equipment = Settings.EffectiveEquipment(_session.Lens);
        Cameras.Clear();
        foreach (var camera in equipment.Cameras!) Cameras.Add(camera);
        Lenses.Clear();
        foreach (var lens in equipment.Lenses!) Lenses.Add(lens);

        var cameraSelection = Cameras.FirstOrDefault(item => item.Id.Equals(_session.CameraProfileId,
            StringComparison.OrdinalIgnoreCase)) ?? Cameras[0];
        var lensSelection = Lenses.FirstOrDefault(item => item.Id.Equals(_session.LensProfileId,
            StringComparison.OrdinalIgnoreCase)) ?? Lenses[0];
        _suppressChanges = true;
        SelectedCamera = cameraSelection;
        SelectedLens = lensSelection;
        FocalLength = lensSelection.ClampFocalLength(_session.Lens.FocalLengthMillimetres);
        _suppressChanges = false;
        ApplyLens();
        NotifyEquipmentProperties();
    }

    [RelayCommand]
    private void AddCameraProfile()
    {
        EquipmentCameraEditors.Add(NewCameraEditor(new CameraProfile(Guid.NewGuid().ToString("N"), Strings.Get("Settings_Equipment_NewCamera_Name"), 36, 24)));
        EquipmentListChanged();
    }

    [RelayCommand]
    private void AddLensProfile()
    {
        EquipmentLensEditors.Add(NewLensEditor(new LensProfile(Guid.NewGuid().ToString("N"), Strings.Get("Settings_Equipment_NewLens_Name"), 24, 70)));
        EquipmentListChanged();
    }

    private async Task RemoveCameraProfileEditor(CameraProfileEditorViewModel profile)
    {
        if (!await ConfirmRemoveEquipmentAsync("Dialogs_RemoveCamera", profile.DisplayName)) return;
        EquipmentCameraEditors.Remove(profile);
        EquipmentRemoved();
    }

    private async Task RemoveLensProfileEditor(LensProfileEditorViewModel profile)
    {
        if (!await ConfirmRemoveEquipmentAsync("Dialogs_RemoveLens", profile.DisplayName)) return;
        EquipmentLensEditors.Remove(profile);
        EquipmentRemoved();
    }

    // Each kind has its own whole sentences (keys <prefix>_Title, _Unnamed_Message, _Confirm), so a
    // translation never has to fit a noun into a shared sentence.
    private Task<bool> ConfirmRemoveEquipmentAsync(string keyPrefix, string name) =>
        _dialogs.ConfirmDestructiveAsync(new DestructiveConfirmation(Strings.Get(keyPrefix + "_Title"),
            string.IsNullOrWhiteSpace(name)
                ? Strings.Get(keyPrefix + "_Unnamed_Message")
                : Strings.Format("Dialogs_RemoveEquipment_Named_Message", name.Trim()),
            Strings.Get(keyPrefix + "_Confirm")));

    /// <summary>A confirmed removal is applied and saved now, not after the usual editing pause.</summary>
    private void EquipmentRemoved()
    {
        EquipmentListChanged();
        CommitDeferredSettings();
    }

    private void EquipmentListChanged() => EquipmentEdited();

    /// <summary>Why equipment edits are not being saved yet, or null when they are.</summary>
    public string? EquipmentValidationMessage =>
        EquipmentCameraEditors.Count == 0 ? Strings.Get("Settings_Equipment_Validation_AddCamera") :
        EquipmentLensEditors.Count == 0 ? Strings.Get("Settings_Equipment_Validation_AddLens") :
        EquipmentIsValid() ? null : Strings.Get("Settings_Equipment_Validation_Incomplete");

    private bool CanRefreshSavedLocationThumbnails() =>
        !IsRefreshingLocationThumbnails && Locations.Saved.Count > 0;

    private void OnSavedLocationCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshSavedLocationThumbnailsCommand.NotifyCanExecuteChanged();
        RefreshSavedLocationSettlementCachesCommand.NotifyCanExecuteChanged();
        ReapplySavedLocationMapStylesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRefreshingLocationThumbnailsChanged(bool value)
    {
        RefreshSavedLocationThumbnailsCommand.NotifyCanExecuteChanged();
        RefreshSavedLocationSettlementCachesCommand.NotifyCanExecuteChanged();
        ReapplySavedLocationMapStylesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefreshSavedLocationThumbnails))]
    private async Task RefreshSavedLocationThumbnails()
    {
        if (!await _dialogs.ConfirmRefreshSavedLocationThumbnailsAsync(Locations.Saved.Count)) return;
        await RunSavedLocationMapOperationAsync(SavedLocationMapRefreshMode.RefreshSource,
            Strings.Get("Settings_Data_MapImages_Regenerating"), Strings.Get("Settings_Data_MapImages_Regenerated"),
            Strings.Get("Settings_Data_MapImages_RegeneratedDegraded"));
    }

    [RelayCommand(CanExecute = nameof(CanRefreshSavedLocationThumbnails))]
    private Task ReapplySavedLocationMapStyles() => RunSavedLocationMapOperationAsync(
        SavedLocationMapRefreshMode.ReapplyStyle,
        Strings.Get("Settings_Data_MapImages_Reapplying"), Strings.Get("Settings_Data_MapImages_Reapplied"),
        Strings.Get("Settings_Data_MapImages_ReappliedDegraded"));

    [RelayCommand(CanExecute = nameof(CanRefreshSavedLocationThumbnails))]
    private async Task RefreshSavedLocationSettlementCaches()
    {
        if (!await _dialogs.ConfirmRefreshSavedLocationSettlementCachesAsync(Locations.Saved.Count)) return;
        await RunSavedLocationMapOperationAsync(SavedLocationMapRefreshMode.RefreshSettlement,
            Strings.Get("Settings_Data_MapImages_RefreshingSettlement"), Strings.Get("Settings_Data_MapImages_SettlementRefreshed"),
            Strings.Get("Settings_Data_MapImages_SettlementRefreshedDegraded"));
    }

    private async Task RunSavedLocationMapOperationAsync(SavedLocationMapRefreshMode mode,
        string busyMessage, string completedMessage, string degradedMessage)
    {
        IsRefreshingLocationThumbnails = true;
        LocationThumbnailRefreshStatus = busyMessage;
        var succeeded = 0;
        var failed = 0;
        var rasterComplete = 0;
        var rasterCached = 0;
        var rasterFailed = 0;
        var coreComplete = 0;
        var coreCached = 0;
        var coreFailed = 0;
        var settlementComplete = 0;
        var settlementCached = 0;
        var settlementEmpty = 0;
        var settlementPartial = 0;
        var settlementFailed = 0;
        var details = new List<string>();
        try
        {
            foreach (var card in Locations.Saved.ToArray())
            {
                var result = await card.RefreshMapThumbnailWithResultAsync(mode);
                if (result?.RefreshSucceeded == true)
                {
                    succeeded++;
                }
                else failed++;

                if (mode == SavedLocationMapRefreshMode.ReapplyStyle) continue;
                var operation = result?.Operation;
                if (operation?.RasterSucceeded != true) rasterFailed++;
                else if (operation.RasterUsedPrevious) rasterCached++;
                else rasterComplete++;
                var outcome = operation?.Semantic ?? MapFeatureFetchOutcome.Failure(
                    "operation_failed", Strings.Get("Settings_Data_MapImages_OperationIncomplete"));
                switch (outcome.Status)
                {
                    case MapFeatureFetchStatus.Complete: coreComplete++; break;
                    case MapFeatureFetchStatus.CachedPrevious:
                        coreCached++;
                        break;
                    default:
                        coreFailed++;
                        details.Add(Strings.Format("Settings_Data_MapImages_FeaturesFailed_Format", card.Name,
                            outcome.FailureReason ?? Strings.Get("Settings_Data_MapImages_FeaturesUnavailable")));
                        break;
                }

                switch (operation?.SettlementState)
                {
                    case Noctaxis.Core.Environment.EnvironmentalDataState.Available:
                        settlementComplete++;
                        break;
                    case Noctaxis.Core.Environment.EnvironmentalDataState.Cached:
                        settlementCached++;
                        break;
                    case Noctaxis.Core.Environment.EnvironmentalDataState.Empty:
                        settlementEmpty++;
                        break;
                    case Noctaxis.Core.Environment.EnvironmentalDataState.Partial:
                        settlementPartial++;
                        details.Add(Strings.Format("Settings_Data_MapImages_SettlementPartial_Format", card.Name));
                        break;
                    case Noctaxis.Core.Environment.EnvironmentalDataState.TileAbsent:
                        settlementFailed++;
                        details.Add(Strings.Format("Settings_Data_MapImages_SettlementTileAbsent_Format", card.Name));
                        break;
                    case Noctaxis.Core.Environment.EnvironmentalDataState.InvalidRaster:
                        settlementFailed++;
                        details.Add(Strings.Format("Settings_Data_MapImages_SettlementInvalid_Format", card.Name));
                        break;
                    default:
                        settlementFailed++;
                        details.Add(Strings.Format("Settings_Data_MapImages_SettlementUnavailable_Format", card.Name));
                        break;
                }
            }
            Locations.RefreshThumbnailAttributions();
            LocationThumbnailRefreshStatus = mode == SavedLocationMapRefreshMode.ReapplyStyle
                ? Strings.Format("Settings_Data_MapImages_StyleSummary_Format", succeeded, failed)
                : Strings.Format("Settings_Data_MapImages_RasterSummary_Format", rasterComplete, rasterCached, rasterFailed) + Environment.NewLine +
                  Strings.Format("Settings_Data_MapImages_OverlaySummary_Format", coreComplete, coreCached, coreFailed) + Environment.NewLine +
                  Strings.Format("Settings_Data_MapImages_SettlementSummary_Format", settlementComplete, settlementCached,
                      settlementEmpty, settlementPartial, settlementFailed) +
                  (details.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, details));
            StatusMessage = failed == 0 && coreFailed == 0 && settlementFailed == 0 && settlementPartial == 0
                ? completedMessage
                : degradedMessage;
            _logger.LogInformation(
                "Saved-location map operation {RefreshMode} completed: raster complete {RasterComplete}, cached {RasterCached}, failed {RasterFailed}; core complete {CoreComplete}, cached {CoreCached}, failed {CoreFailed}; WSF settlement complete {SettlementComplete}, cached {SettlementCached}, valid empty {SettlementEmpty}, partial {SettlementPartial}, failed {SettlementFailed}; thumbnails failed {Failed}",
                mode, rasterComplete, rasterCached, rasterFailed, coreComplete, coreCached, coreFailed,
                settlementComplete, settlementCached, settlementEmpty, settlementPartial, settlementFailed, failed);
        }
        finally
        {
            IsRefreshingLocationThumbnails = false;
        }
    }

    private static string WeatherFieldLabel(WeatherField field) => field switch
    {
        // One key per field, named after the enum member: Settings_Weather_Field_<Member>.
        _ when Enum.IsDefined(field) => Strings.Get("Settings_Weather_Field_" + field),
        _ => field.ToString()
    };

    private static readonly IReadOnlyList<(string LabelKey, IReadOnlyList<WeatherField> Fields)> WeatherFieldGroupDefinitions =
    [
        ("Settings_Weather_Group_Weather",
        [
            WeatherField.TotalCloudCover, WeatherField.LowCloudCover, WeatherField.MediumCloudCover, WeatherField.HighCloudCover,
            WeatherField.PrecipitationProbability, WeatherField.PrecipitationAmount, WeatherField.PrecipitationType,
            WeatherField.Visibility, WeatherField.Temperature, WeatherField.DewPoint, WeatherField.RelativeHumidity,
            WeatherField.WindSpeed, WeatherField.WindGusts, WeatherField.WindDirection
        ]),
        ("Settings_Weather_Group_SunAndDarkness",
        [
            WeatherField.Sunrise, WeatherField.Sunset, WeatherField.CivilTwilight, WeatherField.NauticalTwilight,
            WeatherField.AstronomicalTwilight, WeatherField.AstronomicalDarkness
        ]),
        ("Settings_Weather_Group_Moon", [WeatherField.MoonPhase, WeatherField.MoonIllumination, WeatherField.Moonrise, WeatherField.Moonset])
    ];

    public async Task<byte[]> CreateExportPngAsync(CancellationToken cancellationToken)
    {
        // Everything the card describes is captured before the first await: the plan being exported
        // and the labels around it. The weather is then fetched for that plan's own session, so the
        // card can never pair one plan with weather fetched for another place or another moment.
        var exported = Snapshot ?? throw new InvalidOperationException("The plan is not ready to export.");
        var exportedWeather = Settings.EffectiveWeather;
        var context = new ScoutingCardExportContext(LocationName, SelectedLocation, exportedWeather, Settings.Units);
        StatusMessage = Strings.Get("Planner_Export_RefreshingWeather");
        _logger.LogInformation("Starting export weather refresh");
        try
        {
            var result = await _planning.RefreshWeatherAsync(exported.Session, exportedWeather, cancellationToken);
            if (result.State != DataState.Ready || result.Conditions is null)
                throw new InvalidOperationException(Strings.Format("Planner_Export_Cancelled_Format", result.Message));
            // The card always pairs the plan the export started from with the weather fetched for it.
            var rendered = exported with { Weather = result };
            if (IsCurrentWeatherRequest(exported.Session, exportedWeather) && Snapshot is { } current)
            {
                // The plan on screen would still make this same request, so it takes the result too.
                Snapshot = ReferenceEquals(current, exported) ? rendered : current with { Weather = result };
                NotifySnapshotProperties();
            }
            else _logger.LogInformation("The plan changed during export; the card keeps the plan it started from");
            var bytes = await _exporter.RenderPngAsync(rendered, context, cancellationToken);
            StatusMessage = Strings.Get("Planner_Export_Ready");
            return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Scouting card export failed");
            StatusMessage = ex.Message;
            throw;
        }
    }

    public void ReportExportDestinationFailure(Exception exception)
    {
        StatusMessage = Strings.Format("Planner_Export_Failed_Format", exception.Message);
        _logger.LogWarning(exception, "PNG export destination failed");
    }

    /// <summary>
    /// Persists the newest state now, bypassing the debounce. Returns false when the write failed for
    /// an expected filesystem reason; that failure has already been logged and shown to the user, so
    /// callers only need the result when their own feedback depends on it. Nothing is written before
    /// initialisation has completed.
    /// </summary>
    public Task<bool> FlushAsync()
    {
        // A setting still in its quiet period is the user's latest choice: it is applied and written too.
        CommitDeferredSettings();
        return _stateSaves.IsEnabled ? _stateSaves.FlushAsync(CaptureState()) : Task.FromResult(true);
    }

    /// <summary>Schedules a debounced save for frequent, fire-and-forget state changes.</summary>
    private void RequestSave()
    {
        if (_stateSaves.IsEnabled) _stateSaves.RequestSave(CaptureState());
    }

    internal Task WaitForPendingSavesAsync() => _stateSaves.WhenIdleAsync();

    /// <summary>
    /// Snapshots the state to be written. Called synchronously by the two methods above, so it always
    /// runs on the thread that requested the save; the resulting record is immutable, so the write
    /// itself never reads this view model. StateSaveScheduler depends on both of those properties.
    /// </summary>
    private PersistedState CaptureState()
    {
        _terrainDiskCache?.SetSavedLocations(SavedLocations.Select(location => location.Coordinate));
        return new PersistedState(4, Settings, SavedLocations.ToArray(), _session, SelectedLocation?.Id, _lastCustomCoordinate);
    }

    private void OnStateSaveCompleted(StateSaveResult result)
    {
        // A successful save resolves only the save-failure notice; an unrelated warning stays.
        _saveFailureNotice = result.Succeeded
            ? null
            : Strings.Format("Shell_Notice_SaveFailed_Format", result.Failure?.Message);
        UpdateOperationalNotice();
    }

    /// <summary>Reports a recoverable problem that is not a state-file save failure.</summary>
    private void ShowOperationalNotice(string message)
    {
        _environmentNotice = message;
        UpdateOperationalNotice();
    }

    // A save failure blocks the user's work, so it is shown first when both sources are active.
    private void UpdateOperationalNotice() => OperationalNotice = _saveFailureNotice ?? _environmentNotice;

    [RelayCommand]
    private void DismissOperationalNotice()
    {
        // Dismiss acknowledges everything currently reported; a later failure raises the bar again.
        _saveFailureNotice = null;
        _environmentNotice = null;
        UpdateOperationalNotice();
    }

    [RelayCommand] private void ShowLocations() { SelectedPageIndex = 0; _logger.LogInformation("Navigated to Locations"); }
    [RelayCommand] private void ShowPlanner()
    {
        SelectedPageIndex = 1;
        _logger.LogInformation("Navigated to Planner");
        ActivatePlannerIfReady();
    }

    private bool _initialised;
    private bool _plannerActivated;
    partial void OnSelectedPageIndexChanged(int value) => ActivatePlannerIfReady();

    private void ActivatePlannerIfReady()
    {
        if (!_initialised || SelectedPageIndex != 1 || _plannerActivated) return;
        _plannerActivated = true;
        ScheduleObserverRefresh(0);
    }
    [RelayCommand] private void ShowSettings() { SelectedPageIndex = 2; _logger.LogInformation("Navigated to Settings"); }

    private async Task OpenCustomAsync(LocationResolution resolution)
    {
        _lastCustomCoordinate = resolution.Coordinate;
        if (!string.IsNullOrWhiteSpace(resolution.TimeZoneId)) TimeZoneId = _timeZones.GetEffectiveId(resolution.TimeZoneId);
        CommitObserverLocation(resolution.Coordinate, resolution.DisplayName, resolvePlaceName: resolution.DisplayName is null);
        SelectedPageIndex = 1;
        await FlushAsync();
    }

    private async Task UseSearchResultAsync(LocationSearchResult result, bool save)
    {
        var resolution = new LocationResolution(result.Coordinate, LocationResolutionSource.SearchResult,
            IsApproximate: false, DisplayName: result.DisplayName, RegionDescription: result.RegionDescription,
            TimeZoneId: result.TimeZoneId);
        await OpenCustomAsync(resolution);
        if (save)
        {
            var location = new SavedLocation(Guid.NewGuid(), result.DisplayName, result.Coordinate,
                _timeZones.GetEffectiveId(result.TimeZoneId), RegionDescription: result.RegionDescription,
                LastUsedUtc: _clock.GetCurrentInstant().ToDateTimeOffset(), SortOrder: SavedLocations.Count,
                DateAddedUtc: _clock.GetCurrentInstant().ToDateTimeOffset());
            SavedLocations.Add(location);
            SelectedLocation = location;
            _session = _session with { SavedLocationId = location.Id };
            SyncLocationHomepage();
            await FlushAsync();
            await GenerateSavedLocationMapAsync(location);
        }
    }

    [RelayCommand]
    private async Task UsePlannerSearchResult(LocationSearchResult result)
    {
        LocationSearch.Reset();
        await UseSearchResultAsync(result, false);
    }

    private async Task OpenSavedLocationAsync(LocationCardViewModel card)
    {
        CancelReverseLocationLookup();
        _resolvedPlaceSuggestion = null;
        CurrentLocationAttribution = null;
        var updated = card.Location with { LastUsedUtc = _clock.GetCurrentInstant().ToDateTimeOffset() };
        card.Update(updated);
        ReplaceLocation(updated);
        _session = _session with
        {
            Observer = updated.Coordinate,
            TimeZoneId = _timeZones.GetEffectiveId(updated.TimeZoneId),
            SavedLocationId = updated.Id,
            ObserverElevation = updated.ObserverElevation
        };
        SyncLocationHomepage();
        LocationName = updated.Name;
        LoadSessionIntoControls();
        PreviewObserver = updated.Coordinate;
        OnPropertyChanged(nameof(Observer));
        ScheduleObserverRefresh(20);
        SelectedPageIndex = 1;
        await FlushAsync();
    }

    private async Task EditLocationAsync(LocationCardViewModel card)
    {
        var edit = await _dialogs.ShowSavedLocationEditAsync(card.Location);
        if (edit is null) return;
        var name = edit.Name.Trim();
        if (name.Length == 0) return;
        if (SavedLocations.Any(location => location.Id != card.Id &&
                                           location.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusMessage = Strings.Get("Locations_Save_NameInUse");
            return;
        }
        var updated = card.Location with { Name = name, Notes = edit.Description };
        card.Update(updated);
        ReplaceLocation(updated);
        await FlushAsync();
    }

    private async Task DeleteLocationAsync(LocationCardViewModel card)
    {
        if (!await _dialogs.ConfirmDeleteSavedLocationAsync(card.Location)) return;
        var match = SavedLocations.FirstOrDefault(location => location.Id == card.Id);
        if (match is not null) SavedLocations.Remove(match);
        if (_session.SavedLocationId == card.Id) _session = _session with { SavedLocationId = null };
        // Matches the Planner delete command: a deleted record must not remain selected, otherwise
        // it is persisted as the most recent location and SaveLocation would re-create it.
        if (SelectedLocation?.Id == card.Id) SelectedLocation = null;
        SyncLocationHomepage();
        await FlushAsync();
    }

    private async Task ToggleFavouriteAsync(LocationCardViewModel card)
    {
        var updated = card.Location with { IsFavourite = !card.Location.IsFavourite };
        card.Update(updated);
        ReplaceLocation(updated);
        await FlushAsync();
    }

    private void ReplaceLocation(SavedLocation updated)
    {
        var existing = SavedLocations.FirstOrDefault(location => location.Id == updated.Id);
        if (existing is not null)
        {
            var index = SavedLocations.IndexOf(existing);
            SavedLocations[index] = updated;
        }
        if (SelectedLocation?.Id == updated.Id) SelectedLocation = updated;
        SyncLocationHomepage();
    }

    private void SyncLocationHomepage()
    {
        Locations.Load(SavedLocations, _lastCustomCoordinate, _session.SavedLocationId);
    }

    private async Task GenerateSavedLocationMapAsync(SavedLocation location)
    {
        var card = Locations.Saved.FirstOrDefault(candidate => candidate.Id == location.Id);
        if (card is null)
        {
            _logger.LogWarning("Saved-location map generation could not find the newly persisted card");
            return;
        }

        var succeeded = await card.RefreshMapThumbnailAsync(SavedLocationMapRefreshMode.RefreshSource);
        Locations.RefreshThumbnailAttributions();
        if (!succeeded)
            _logger.LogWarning("Initial saved-location map generation failed; the location remains saved");
    }

    private void EnsureCelestialSelections()
    {
        var source = Settings.CelestialObjects?.ConfiguredObjects ?? _session.VisibleObjects ?? CelestialObjectSettings.Defaults;
        var recovered = new List<CelestialObjectSelection>();
        foreach (var item in source)
        {
            var resolved = _catalogue.ResolveConfiguredTargetId(item.TargetId);
            if (resolved is null)
            {
                _logger.LogWarning("Discarded unresolved configured celestial target {TargetId}", item.TargetId);
                continue;
            }
            recovered.Add(item with { TargetId = resolved });
        }
        var withMandatory = recovered
            .Append(new CelestialObjectSelection("sun", false, int.MaxValue - 1))
            .Append(new CelestialObjectSelection("moon", false, int.MaxValue))
            .DistinctBy(item => item.TargetId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Order).ToArray();
        var normalised = CelestialVisibilityPolicy.Normalise(withMandatory);
        if (withMandatory.Count(item => item.IsVisible) > CelestialVisibilityPolicy.MaximumVisibleObjects)
            _logger.LogWarning("Saved celestial configuration exceeded the visible-object limit; extra objects were retained but hidden");
        var requestedPrimary = Settings.CelestialObjects?.DefaultPrimaryTargetId ?? _session.TargetId;
        var primary = _catalogue.ResolveConfiguredTargetId(requestedPrimary);
        if (primary is null)
        {
            _logger.LogWarning("Discarded unresolved configured primary celestial target {TargetId}", requestedPrimary);
            primary = "sun";
        }
        if (!normalised.Any(item => item.IsVisible && item.TargetId.Equals(primary, StringComparison.OrdinalIgnoreCase)))
            primary = normalised.FirstOrDefault(item => item.IsVisible)?.TargetId ?? "sun";
        if (!normalised.Any(item => item.IsVisible))
            normalised = normalised.Select(item => item.TargetId.Equals("sun", StringComparison.OrdinalIgnoreCase) ? item with { IsVisible = true } : item).ToArray();
        _session = _session with { VisibleObjects = normalised, TargetId = primary };
        Settings = Settings with { CelestialObjects = new CelestialObjectSettings(normalised, primary) };
    }

    private void BuildCelestialObjectItems()
    {
        CelestialObjects.Clear();
        foreach (var selection in _session.EffectiveVisibleObjects.OrderBy(item => item.Order))
        {
            var target = Targets.FirstOrDefault(item => item.Id.Equals(selection.TargetId, StringComparison.OrdinalIgnoreCase));
            if (target is null) continue;
            CelestialObjects.Add(new CelestialObjectItemViewModel(target, selection.IsVisible, selection.Order,
                target.Id.Equals(_session.TargetId, StringComparison.OrdinalIgnoreCase),
                CelestialObjectChanged, MakePrimaryTarget, RemoveCelestialObject, MoveCelestialObject));
        }
        NotifyCelestialCount();
    }

    private void CelestialObjectChanged(CelestialObjectItemViewModel item)
    {
        if (item.IsVisible && VisibleCelestialCount > CelestialVisibilityPolicy.MaximumVisibleObjects)
        {
            item.SetVisibilitySilently(false);
            CelestialLimitMessage = Strings.Get("Settings_Celestial_Limit_Maximum");
            NotifyCelestialCount();
            return;
        }
        CelestialLimitMessage = null;
        if (item.IsPrimary && !item.IsVisible)
        {
            var replacement = CelestialObjects.FirstOrDefault(candidate => candidate.IsVisible);
            if (replacement is null) { item.SetVisibilitySilently(true); return; }
            SetPrimaryTarget(replacement);
        }
        SaveCelestialSelections(); ScheduleAstronomyRefresh(120);
    }

    private void MakePrimaryTarget(CelestialObjectItemViewModel item)
    {
        if (!item.IsVisible)
        {
            if (VisibleCelestialCount >= CelestialVisibilityPolicy.MaximumVisibleObjects)
            {
                CelestialLimitMessage = Strings.Get("Settings_Celestial_Limit_HideAnother");
                return;
            }
            item.SetVisibilitySilently(true);
        }
        SetPrimaryTarget(item);
        SaveCelestialSelections(); ScheduleAstronomyRefresh(40);
    }

    private void SetPrimaryTarget(CelestialObjectItemViewModel item)
    {
        foreach (var candidate in CelestialObjects) candidate.IsPrimary = candidate == item;
        _session = _session with { TargetId = item.TargetId };
        _suppressChanges = true; SelectedTarget = item.Target; _suppressChanges = false;
        NotifyCelestialCount();
    }

    private void RemoveCelestialObject(CelestialObjectItemViewModel item)
    {
        if (!item.CanRemove) return;
        CelestialObjects.Remove(item);
        if (item.IsPrimary)
        {
            var replacement = CelestialObjects.FirstOrDefault(candidate => candidate.IsVisible)
                ?? CelestialObjects.First(candidate => candidate.Target.IsSun);
            if (!replacement.IsVisible) replacement.SetVisibilitySilently(true);
            SetPrimaryTarget(replacement);
        }
        SaveCelestialSelections(); ScheduleAstronomyRefresh(80);
    }

    [RelayCommand]
    private void AddCelestialObject(AstralTarget target)
    {
        if (CelestialObjects.Any(item => item.TargetId.Equals(target.Id, StringComparison.OrdinalIgnoreCase))) return;
        var order = CelestialObjects.Count;
        var visible = VisibleCelestialCount < CelestialVisibilityPolicy.MaximumVisibleObjects;
        var item = new CelestialObjectItemViewModel(target, visible, order, false,
            CelestialObjectChanged, MakePrimaryTarget, RemoveCelestialObject, MoveCelestialObject);
        CelestialObjects.Add(item);
        if (visible) SetPrimaryTarget(item);
        else CelestialLimitMessage = Strings.Get("Settings_Celestial_Limit_AddedHidden");
        SaveCelestialSelections(); ScheduleAstronomyRefresh(80);
    }

    [RelayCommand]
    private void AddSettingsCelestialObject(AstralTarget target)
    {
        AddCelestialObject(target);
        SettingsCelestialSearch.ClearResults();
    }

    private void SaveCelestialSelections()
    {
        // The list order is the identity slot the chart and map use, so keep each item's swatch in step.
        for (var index = 0; index < CelestialObjects.Count; index++) CelestialObjects[index].Order = index;
        var selections = CelestialObjects.Select((item, index) => new CelestialObjectSelection(item.TargetId, item.IsVisible, index)).ToArray();
        _session = _session with { VisibleObjects = selections };
        Settings = Settings with { CelestialObjects = new CelestialObjectSettings(selections, _session.TargetId) };
        if (Snapshot is not null) Snapshot = Snapshot with { Session = _session };
        NotifyCelestialCount();
    }

    private void MoveCelestialObject(CelestialObjectItemViewModel item, int direction)
    {
        var oldIndex = CelestialObjects.IndexOf(item);
        var newIndex = Math.Clamp(oldIndex + direction, 0, CelestialObjects.Count - 1);
        if (oldIndex == newIndex) return;
        CelestialObjects.Move(oldIndex, newIndex);
        SaveCelestialSelections();
    }

    [RelayCommand]
    private async Task RestoreCelestialDefaults()
    {
        // Replacing a list the user has shaped loses their choices; the default list itself needs no question.
        var current = Settings.EffectiveCelestialObjects;
        var isDefault = current.DefaultPrimaryTargetId == "sun" &&
                        current.EffectiveConfiguredObjects.SequenceEqual(CelestialObjectSettings.Defaults);
        if (!isDefault && !await _dialogs.ConfirmDestructiveAsync(new DestructiveConfirmation(Strings.Get("Dialogs_RestoreDefaultObjects_Title"),
                Strings.Get("Dialogs_RestoreDefaultObjects_Message"), Strings.Get("Dialogs_RestoreDefaultObjects_Confirm"))))
            return;
        _session = _session with { VisibleObjects = CelestialObjectSettings.Defaults, TargetId = "sun" };
        Settings = Settings with { CelestialObjects = new CelestialObjectSettings(CelestialObjectSettings.Defaults, "sun") };
        BuildCelestialObjectItems();
        CelestialLimitMessage = null;
        ScheduleAstronomyRefresh(40);
    }

    private void NotifyCelestialCount()
    {
        OnPropertyChanged(nameof(VisibleCelestialCount));
        OnPropertyChanged(nameof(VisibleCelestialCountText));
    }

    [RelayCommand]
    private void PreviousDay() => ChangeDay(-1);

    [RelayCommand]
    private void NextDay() => ChangeDay(1);

    [RelayCommand]
    private void Now()
    {
        _session = _session with { Instant = SystemClock.Instance.GetCurrentInstant() };
        LoadTimeControls();
        ScheduleAstronomyRefresh();
    }

    [RelayCommand(CanExecute = nameof(CanRefreshWeather))]
    private async Task RefreshWeather()
    {
        IsWeatherRefreshing = true;
        RefreshWeatherCommand.NotifyCanExecuteChanged();
        StatusMessage = Strings.Get("Planner_Weather_Refreshing");
        _logger.LogInformation("Manual weather refresh requested");
        // Captured before the first await: what this answer describes.
        var requested = _session;
        var requestedWeather = Settings.EffectiveWeather;
        try
        {
            var result = await _planning.RefreshWeatherAsync(requested, requestedWeather, CancellationToken.None);
            // A superseded request describes a plan the planner has left: another place, or the same
            // place at another time. It is dropped without a message, because the user did not ask
            // for it any more and its outcome is not news.
            if (!IsCurrentWeatherRequest(requested, requestedWeather))
            {
                _logger.LogDebug("Discarded a weather result for a superseded request");
                return;
            }
            if (result.State == DataState.Ready && result.Conditions is not null && Snapshot is not null)
            {
                Snapshot = Snapshot with { Weather = result };
                StatusMessage = Strings.Get("Planner_Weather_Refreshed");
                NotifySnapshotProperties();
            }
            else
            {
                StatusMessage = Strings.Format("Planner_Weather_RefreshFailed_Format", result.Message);
                _logger.LogWarning("Manual weather refresh failed: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            if (!IsCurrentWeatherRequest(requested, requestedWeather))
            {
                _logger.LogDebug(ex, "Discarded a failed weather request that had been superseded");
                return;
            }
            StatusMessage = Strings.Format("Planner_Weather_RefreshFailed_Format", ex.Message);
            _logger.LogWarning(ex, "Manual weather refresh failed");
        }
        finally { IsWeatherRefreshing = false; RefreshWeatherCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanRefreshWeather() => !IsWeatherRefreshing;

    [RelayCommand]
    private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

    [RelayCommand]
    private async Task SaveLocation()
    {
        var existing = SelectedLocation;
        var requiresMapGeneration = existing is null || existing.Coordinate != _session.Observer;
        var proposedName = string.IsNullOrWhiteSpace(LocationName) ? $"Location {SavedLocations.Count + 1}" : LocationName.Trim();
        if (SavedLocations.Any(location => location.Id != existing?.Id &&
                                           location.Name.Equals(proposedName, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusMessage = Strings.Get("Locations_Save_NameInUse");
            return;
        }
        var location = existing is null
            ? new SavedLocation(Guid.NewGuid(), proposedName, _session.Observer, _session.TimeZoneId,
                LastUsedUtc: _clock.GetCurrentInstant().ToDateTimeOffset(), SortOrder: SavedLocations.Count,
                DateAddedUtc: _clock.GetCurrentInstant().ToDateTimeOffset(),
                ObserverElevation: _session.EffectiveObserverElevation)
            : existing with { Name = proposedName, Coordinate = _session.Observer,
                TimeZoneId = _session.TimeZoneId,
                LastUsedUtc = _clock.GetCurrentInstant().ToDateTimeOffset(),
                ObserverElevation = _session.EffectiveObserverElevation };
        if (existing is not null) SavedLocations.Remove(existing);
        SavedLocations.Add(location);
        SelectedLocation = location;
        _session = _session with { SavedLocationId = location.Id };
        SyncLocationHomepage();
        var saved = await FlushAsync();
        if (requiresMapGeneration) await GenerateSavedLocationMapAsync(location);
        StatusMessage = !saved ? Strings.Get("Locations_Save_Failed")
            : existing is null ? Strings.Get("Locations_Save_Saved") : Strings.Get("Locations_Save_Updated");
    }

    [RelayCommand]
    private async Task SaveCurrentAsNewLocation()
    {
        var now = _clock.GetCurrentInstant().ToDateTimeOffset();
        var suggestedName = _resolvedPlaceSuggestion ?? (string.IsNullOrWhiteSpace(LocationName) ||
                            LocationName.Equals(Strings.Get("Planner_Location_CurrentLocation_Name"), StringComparison.OrdinalIgnoreCase) ||
                            LocationName.Equals(Strings.Get("Planner_Location_NewLocation_Name"), StringComparison.OrdinalIgnoreCase)
            ? Strings.Format("Locations_DefaultName_Format", SavedLocations.Count + 1)
            : LocationName.Trim());
        var draft = new SavedLocation(Guid.NewGuid(), suggestedName, _session.Observer, _session.TimeZoneId,
            SortOrder: SavedLocations.Count, DateAddedUtc: now,
            ObserverElevation: _session.EffectiveObserverElevation);
        var edit = await _dialogs.ShowSavedLocationEditAsync(draft, isCreateMode: true);
        if (edit is null) return;
        var name = edit.Name.Trim();
        if (name.Length == 0 || SavedLocations.Any(location =>
                location.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            StatusMessage = name.Length == 0
                ? Strings.Get("Locations_Save_NameRequired")
                : Strings.Get("Locations_Save_NameInUse");
            return;
        }

        var location = draft with { Name = name, Notes = edit.Description };
        SavedLocations.Add(location);
        SelectedLocation = location;
        _session = _session with { SavedLocationId = location.Id };
        LocationName = location.Name;
        SyncLocationHomepage();
        var saved = await FlushAsync();
        await GenerateSavedLocationMapAsync(location);
        StatusMessage = saved ? Strings.Get("Locations_Save_Saved") : Strings.Get("Locations_Save_Failed");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteLocation))]
    private async Task DeleteLocation()
    {
        if (SelectedLocation is null) return;
        if (!await _dialogs.ConfirmDeleteSavedLocationAsync(SelectedLocation)) return;
        SavedLocations.Remove(SelectedLocation);
        SelectedLocation = null;
        _session = _session with { SavedLocationId = null };
        SyncLocationHomepage();
        await FlushAsync();
    }

    private bool CanDeleteLocation() => SelectedLocation is not null;

    partial void OnSelectedTargetChanged(AstralTarget? value)
    {
        if (_suppressChanges || value is null) return;
        var item = CelestialObjects.FirstOrDefault(item => item.TargetId.Equals(value.Id, StringComparison.OrdinalIgnoreCase));
        if (item is not null) MakePrimaryTarget(item);
    }

    partial void OnSelectedLocationChanged(SavedLocation? value)
    {
        DeleteLocationCommand.NotifyCanExecuteChanged();
        if (_suppressChanges || value is null) return;
        CancelReverseLocationLookup();
        _resolvedPlaceSuggestion = null;
        CurrentLocationAttribution = null;
        _session = _session with
        {
            Observer = value.Coordinate,
            ObserverElevation = value.ObserverElevation,
            TimeZoneId = _timeZones.GetEffectiveId(value.TimeZoneId),
            SavedLocationId = value.Id
        };
        LocationName = value.Name;
        LoadSessionIntoControls();
        ScheduleObserverRefresh();
    }

    partial void OnLocalDateChanged(DateTimeOffset? value)
    {
        if (_suppressChanges || value is null) return;
        _dateSliderAnchor = value.Value.Date;
        _suppressChanges = true; PreviewDateOffsetDays = 0; _suppressChanges = false;
        ApplyLocalDateTime(true);
    }
    partial void OnMinutesOfDayChanged(double value)
    {
        if (_suppressChanges) return;
        var minutes = Math.Clamp((int)Math.Round(value), 0, 1439);
        _suppressChanges = true;
        TimeText = $"{minutes / 60:00}:{minutes % 60:00}";
        _suppressChanges = false;
        ApplyLocalDateTime(true);
    }
    partial void OnTimeTextChanged(string value)
    {
        if (_suppressChanges || !LocalTimePatternHelper.TryParse(value, out var time)) return;
        _suppressChanges = true;
        MinutesOfDay = time.Hour * 60 + time.Minute;
        _suppressChanges = false;
        ApplyLocalDateTime(true);
    }
    partial void OnPreviewMinutesOfDayChanged(double value)
    {
        if (_suppressChanges) return;
        var snap = Math.Clamp(Settings.TimeSnapMinutes, 1, 30);
        var minutes = Math.Clamp((int)Math.Round(value / snap) * snap, 0, 1439);
        _suppressChanges = true;
        MinutesOfDay = minutes;
        TimeText = $"{minutes / 60:00}:{minutes % 60:00}";
        _suppressChanges = false;
        PreviewTemporalPosition();
        ScheduleTemporalCommit();
    }
    partial void OnPreviewDateOffsetDaysChanged(double value)
    {
        if (_suppressChanges) return;
        var days = (int)Math.Round(value);
        _suppressChanges = true;
        LocalDate = _dateSliderAnchor.AddDays(days);
        _suppressChanges = false;
        PreviewTemporalPosition();
        ScheduleTemporalCommit();
    }
    partial void OnTimeZoneIdChanged(string value) { if (!_suppressChanges) ApplyLocalDateTime(true); }
    partial void OnLatitudeChanged(double value)
    {
        if (_suppressChanges) return;
        var coordinate = new GeoCoordinate(value, Longitude, 0);
        PreviewObserverLocation(coordinate);
        CommitUnresolvedObserverLocation(coordinate);
    }
    partial void OnLongitudeChanged(double value)
    {
        if (_suppressChanges) return;
        var coordinate = new GeoCoordinate(Latitude, value, 0);
        PreviewObserverLocation(coordinate);
        CommitUnresolvedObserverLocation(coordinate);
    }
    partial void OnElevationChanged(double value)
    {
        if (_suppressChanges) return;
        var state = RememberSuppliedGroundElevation().WithManualOverride(value);
        var observer = _session.Observer with { ElevationMetres = state.ResolveGroundElevationAsl(value) };
        _session = _session with { Observer = observer, ObserverElevation = state };
        PreviewObserver = observer;
        SelectedLocation = null;
        OnPropertyChanged(nameof(Observer));
        NotifyObserverElevationProperties();
        ScheduleObserverRefresh();
    }

    [RelayCommand]
    private void ResetGroundElevation()
    {
        var state = RememberSuppliedGroundElevation().ResetManualOverride();
        var groundElevation = state.ResolveGroundElevationAsl(_session.Observer.ElevationMetres);
        var observer = _session.Observer with { ElevationMetres = groundElevation };
        _session = _session with { Observer = observer, ObserverElevation = state };
        _suppressChanges = true;
        Elevation = groundElevation;
        PreviewObserver = observer;
        _suppressChanges = false;
        OnPropertyChanged(nameof(Observer));
        NotifyObserverElevationProperties();
        ScheduleObserverRefresh();
    }
    partial void OnSelectedCameraChanged(CameraProfile? value) { if (!_suppressChanges) ApplyLens(); }
    partial void OnSelectedLensChanged(LensProfile? value)
    {
        if (_suppressChanges) return;
        ApplyLens();
        NotifyEquipmentProperties();
    }
    partial void OnSelectedOrientationChanged(CameraOrientation value)
    {
        OnPropertyChanged(nameof(IsLandscapeOrientation));
        OnPropertyChanged(nameof(IsPortraitOrientation));
        if (!_suppressChanges) ApplyLens();
    }
    partial void OnFocalLengthChanged(double value)
    {
        if (_suppressChanges || SelectedLens is null) return;
        var clamped = SelectedLens.ClampFocalLength(value);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            _suppressChanges = true;
            FocalLength = clamped;
            _suppressChanges = false;
        }
        ApplyLens();
    }
    partial void OnSnapshotChanged(PlanningSnapshot? value)
    {
        OnPropertyChanged(nameof(CameraFramingGuide));
        OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus));
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        NotifyTerrainProperties();
    }
    partial void OnPlannerRefreshChanged(PlannerRefreshState value)
    {
        OnPropertyChanged(nameof(IsPlannerRefreshing));
        OnPropertyChanged(nameof(PlannerRefreshProgress));
        OnPropertyChanged(nameof(PlannerRefreshStatusText));
        OnPropertyChanged(nameof(PlannerPinActivity));
        OnPropertyChanged(nameof(CelestialOverlaysReady));
        OnPropertyChanged(nameof(CameraOverlayReady));
        NotifyTerrainProperties();
    }
    partial void OnIsCameraFramingOverlayVisibleChanged(bool value)
    {
        if (_suppressChanges) return;
        Settings = Settings with
        {
            CameraFraming = Settings.EffectiveCameraFraming with { IsOverlayVisible = value }
        };
        OnPropertyChanged(nameof(CameraFramingGuide));
        OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus));
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        RequestSave();
    }
    partial void OnShowFramingVisibilityLimitsChanged(bool value)
    {
        if (_suppressChanges) return;
        Settings = Settings with
        {
            CameraFraming = Settings.EffectiveCameraFraming with { ShowVisibilityLimits = value }
        };
        OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus));
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        RequestSave();
    }

    private void ChangeDay(int days)
    {
        if (LocalDate is null) return;
        LocalDate = LocalDate.Value.AddDays(days);
    }

    private void ApplyLocalDateTime(bool fullRefresh)
    {
        if (LocalDate is null || !LocalTimePatternHelper.TryParse(TimeText, out var time)) return;
        var date = NodaTime.LocalDate.FromDateTime(LocalDate.Value.DateTime);
        _session = _session with { Instant = _timeZones.ResolveLocal(date, time, TimeZoneId), TimeZoneId = TimeZoneId };
        if (fullRefresh) ScheduleAstronomyRefresh(); else UpdateCurrentOnly();
    }

    private void PreviewTemporalPosition()
    {
        if (LocalDate is null || !LocalTimePatternHelper.TryParse(TimeText, out var time)) return;
        var date = NodaTime.LocalDate.FromDateTime(LocalDate.Value.DateTime);
        var preview = _session with { Instant = _timeZones.ResolveLocal(date, time, TimeZoneId) };
        _temporalPreviewCancellation?.Cancel();
        _temporalPreviewCancellation?.Dispose();
        _temporalPreviewCancellation = new CancellationTokenSource();
        _ = CalculateTemporalPreviewAsync(preview, _temporalPreviewCancellation.Token);
    }

    private async Task CalculateTemporalPreviewAsync(PlanningSession preview, CancellationToken cancellationToken)
    {
        try
        {
            var current = await Task.Run(() => _planning.CalculateCurrent(preview), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Snapshot is not null)
            {
                var plans = Snapshot.EffectiveObjectPlans.Select(plan => plan.Position.Target.Id.Equals(current.Target.Id, StringComparison.OrdinalIgnoreCase)
                    ? plan with { Position = current, Path = plan.Path with { SelectedInstant = preview.Instant } }
                    : plan).ToArray();
                Snapshot = Snapshot with { Session = preview, Position = current, Path = Snapshot.Path with { SelectedInstant = preview.Instant }, ObjectPlans = plans };
            }
            NotifySnapshotProperties();
        }
        catch (OperationCanceledException) { _logger.LogDebug("Obsolete celestial preview cancelled"); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void ScheduleTemporalCommit()
    {
        _temporalCommitCancellation?.Cancel();
        _temporalPreviewCancellation?.Cancel();
        _temporalCommitCancellation?.Dispose();
        _temporalCommitCancellation = new CancellationTokenSource();
        _ = CommitTemporalAfterDelayAsync(_temporalCommitCancellation.Token);
    }

    private async Task CommitTemporalAfterDelayAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(350, cancellationToken); CommitTemporalPreview(); }
        catch (OperationCanceledException) { _logger.LogDebug("Obsolete date/time preview cancelled"); }
    }

    [RelayCommand]
    public void CommitTemporalPreview()
    {
        if (LocalDate is null || !LocalTimePatternHelper.TryParse(TimeText, out var time)) return;
        _temporalCommitCancellation?.Cancel();
        var date = NodaTime.LocalDate.FromDateTime(LocalDate.Value.DateTime);
        _session = _session with { Instant = _timeZones.ResolveLocal(date, time, TimeZoneId) };
        _logger.LogInformation("Date/time preview committed; scheduling authoritative recalculation");
        ScheduleAstronomyRefresh(50);
    }

    private void ApplyLens()
    {
        if (SelectedCamera is null || SelectedLens is null) return;
        var focalLength = SelectedLens.ClampFocalLength(FocalLength);
        if (Math.Abs(focalLength - FocalLength) > 1e-9)
        {
            _suppressChanges = true;
            FocalLength = focalLength;
            _suppressChanges = false;
        }
        _session = _session with
        {
            CameraProfileId = SelectedCamera.Id,
            LensProfileId = SelectedLens.Id,
            Lens = new LensConfiguration(SensorPreset.Custom,
                SelectedCamera.SensorWidthMillimetres, SelectedCamera.SensorHeightMillimetres,
                focalLength, SelectedOrientation)
        };
        if (Snapshot is not null)
        {
            Snapshot = Snapshot with { Session = _session, FieldOfView = _lensCalculator.Calculate(_session.Lens) };
            OnPropertyChanged(nameof(FieldOfViewText));
            if (!Snapshot.Terrain.IsComplete) _ = PrioritiseCurrentCameraTerrainAsync();
        }
        NotifyEquipmentProperties();
    }

    private async Task PrioritiseCurrentCameraTerrainAsync()
    {
        var generation = Volatile.Read(ref _refreshGeneration);
        var token = _refreshCancellation?.Token ?? CancellationToken.None;
        var requestedSession = _session;
        try
        {
            if (Snapshot is null) return;
            var guide = _cameraFramingGuideCalculator.Calculate(Snapshot.FieldOfView,
                Snapshot.Position.Horizontal.AzimuthDegrees, Settings.EffectiveCameraFraming);
            var horizon = await _planning.PrioritiseTerrainAsync(requestedSession,
                Settings.EffectiveCameraHeightAboveGroundMetres,
                CameraTerrainBearings(guide), token);
            if (!IsCurrentRefresh(generation, requestedSession.Observer) || Snapshot is null ||
                Snapshot.Terrain.IsComplete) return;
            await _plannerSnapshotCommitGate.WaitAsync(token);
            try
            {
                if (IsCurrentRefresh(generation, requestedSession.Observer) &&
                    Snapshot is { Terrain: { IsComplete: false } })
                {
                    Snapshot = Snapshot with { Terrain = horizon };
                    NotifySnapshotProperties();
                }
            }
            finally { _plannerSnapshotCommitGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Camera terrain priority update failed"); }
    }

    private void UpdateCurrentOnly()
    {
        try
        {
            var current = _planning.CalculateCurrent(_session);
            if (Snapshot is not null)
                Snapshot = Snapshot with { Session = _session, Position = current, Path = Snapshot.Path with { SelectedInstant = _session.Instant } };
            NotifySnapshotProperties();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void ScheduleObserverRefresh(int delayMilliseconds = 80)
    {
        // A normal observer request already initialises the same presentation workflow.
        if (_initialised) _plannerActivated = true;
        SchedulePlannerRefresh(PlannerRefreshScope.Observer, delayMilliseconds);
        ScheduleTerrainDebugMapRefresh();
    }

    private void ScheduleAstronomyRefresh(int delayMilliseconds = 80) =>
        SchedulePlannerRefresh(PlannerRefreshScope.Astronomy, delayMilliseconds);

    private void SchedulePlannerRefresh(PlannerRefreshScope scope, int delayMilliseconds)
    {
        if (_refreshCancellation is { IsCancellationRequested: false })
            _logger.LogDebug("Cancelling obsolete planning calculation");
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var token = _refreshCancellation.Token;
        var generation = Interlocked.Increment(ref _refreshGeneration);
        PlannerRefresh = scope == PlannerRefreshScope.Observer
            ? PlannerRefreshState.BeginObserver(generation)
            : PlannerRefreshState.BeginAstronomy(generation);
        StatusMessage = PlannerRefresh.StatusText;
        _activePlannerRefresh = DebouncedRefreshAsync(delayMilliseconds, generation, scope, token);
    }

    private async Task DebouncedRefreshAsync(int delayMilliseconds, long generation,
        PlannerRefreshScope scope, CancellationToken token)
    {
        try
        {
            if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, token);
            await RefreshNowAsync(token, generation, scope);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshNowAsync(CancellationToken cancellationToken, long generation,
        PlannerRefreshScope scope)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestedSession = _session;
        var previousSnapshot = Snapshot;
        UpdateRefresh(generation, state => state with
        {
            Phase = PlannerRefreshPhase.CalculatingAstronomy,
            AstronomyState = PlannerRefreshWorkState.Running,
            StatusText = Strings.Get("Planning_Status_CalculatingPositions")
        });

        var observerWork = scope == PlannerRefreshScope.Observer
            ? _planning.StartRefresh(requestedSession, Settings.EffectiveWeather,
                Settings.EffectiveCameraHeightAboveGroundMetres, cancellationToken)
            : null;
        var coreTask = observerWork?.Core ??
                       _planning.CalculateCoreSnapshotAsync(requestedSession, cancellationToken);
        var environmentTask = observerWork?.Environment;
        var weatherTask = observerWork?.Weather ??
                          _planning.LoadWeatherAsync(requestedSession, Settings.EffectiveWeather, cancellationToken);
        var coreCommit = CommitCoreAsync(coreTask, requestedSession, previousSnapshot, scope,
            generation, cancellationToken);
        var environmentCommit = environmentTask is null
            ? Task.CompletedTask
            : CommitEnvironmentAsync(environmentTask, coreCommit, requestedSession, generation, cancellationToken);
        var priorityTerrainCommit = !Settings.EnableTerrainCalculations || observerWork?.PriorityTerrain is null
            ? Task.CompletedTask
            : CommitPriorityTerrainAsync(observerWork.PriorityTerrain, coreCommit, requestedSession,
                generation, cancellationToken);
        if (!Settings.EnableTerrainCalculations || observerWork?.PriorityTerrain is null)
            UpdateRefresh(generation, state => state with { CameraTerrainState = PlannerRefreshWorkState.NotRequired });
        var weatherCommit = CommitWeatherAsync(weatherTask, coreCommit, requestedSession, generation,
            cancellationToken);

        try
        {
            await Task.WhenAll(coreCommit, priorityTerrainCommit, environmentCommit, weatherCommit);
            if (!IsCurrentRefresh(generation, requestedSession.Observer)) return;
            var partial = PlannerRefresh.HasOptionalFailure;
            UpdateRefresh(generation, state => state with
            {
                Phase = partial ? PlannerRefreshPhase.Partial : PlannerRefreshPhase.Ready,
                StatusText = partial
                    ? Strings.Get("Planner_Status_ReadyPartial")
                    : Strings.Get("Planner_Status_Ready")
            });
            // Scheduled rather than awaited inside this try: a save failure is reported by the save
            // pipeline and must not be shown as a planner-calculation failure.
            RequestSave();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Planning calculation cancelled after {ElapsedMilliseconds} ms", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            if (IsCurrentRefresh(generation, requestedSession.Observer))
            {
                UpdateRefresh(generation, state => state with
                {
                    Phase = PlannerRefreshPhase.Error,
                    AstronomyState = PlannerRefreshWorkState.Error,
                    StatusText = Strings.Format("Planner_Status_UpdateFailed_Format", ex.Message)
                });
            }
            else _logger.LogDebug(ex, "Ignored failure from superseded planning refresh");
        }
        finally
        {
            stopwatch.Stop();
            if (!cancellationToken.IsCancellationRequested && IsCurrentRefresh(generation, requestedSession.Observer))
                _logger.LogInformation("Staged planning refresh completed in {ElapsedMilliseconds} ms", stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task CommitPriorityTerrainAsync(
        Func<IReadOnlyList<double>, CancellationToken, Task<TerrainHorizonProfile>> priorityTerrain,
        Task coreCommit, PlanningSession requestedSession, long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await coreCommit;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentRefresh(generation, requestedSession.Observer) || Snapshot is null) return;
            var guide = _cameraFramingGuideCalculator.Calculate(Snapshot.FieldOfView,
                Snapshot.Position.Horizontal.AzimuthDegrees, Settings.EffectiveCameraFraming);
            var bearings = CameraTerrainBearings(guide);
            UpdateRefresh(generation, state => state with
            {
                CameraTerrainState = PlannerRefreshWorkState.Running,
                StatusText = Strings.Get("Planner_Status_CalculatingViewTerrain")
            });
            var horizon = await priorityTerrain(bearings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _plannerSnapshotCommitGate.WaitAsync(cancellationToken);
            try
            {
                if (!IsCurrentRefresh(generation, requestedSession.Observer) || Snapshot is null) return;
                if (Snapshot.Terrain.Samples.Count == 0 || !Snapshot.Terrain.IsComplete)
                {
                    ApplyResolvedTerrainGround(horizon);
                    Snapshot = Snapshot with
                    {
                        Session = _session,
                        Terrain = horizon
                    };
                    NotifySnapshotProperties();
                }
                UpdateRefresh(generation, state => state with
                {
                    CameraTerrainState = PlannerRefreshWorkState.Ready,
                    StatusText = horizon.IsComplete ? EnrichmentStatus(state) :
                        Strings.Get("Planner_Status_RefiningHorizon")
                });
            }
            finally { _plannerSnapshotCommitGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (!IsCurrentRefresh(generation, requestedSession.Observer)) return;
            _logger.LogWarning(ex, "Current camera terrain priority calculation failed");
            UpdateRefresh(generation, state => state with
            {
                CameraTerrainState = PlannerRefreshWorkState.Error,
                StatusText = EnrichmentStatus(state)
            });
        }
    }

    private static IReadOnlyList<double> CameraTerrainBearings(CameraFramingGuide guide)
    {
        const double detail = CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees;
        var segments = Math.Max(1, (int)Math.Ceiling(guide.HorizontalFieldOfViewDegrees / detail));
        var spacing = guide.HorizontalFieldOfViewDegrees / segments;
        var bearings = new double[segments + 1];
        var left = guide.CentreBearingDegrees - guide.HorizontalFieldOfViewDegrees / 2;
        for (var index = 0; index < bearings.Length; index++) bearings[index] = left + index * spacing;
        return bearings;
    }

    private async Task CommitCoreAsync(Task<PlanningSnapshot> coreTask, PlanningSession requestedSession,
        PlanningSnapshot? previousSnapshot, PlannerRefreshScope scope, long generation,
        CancellationToken cancellationToken)
    {
        var core = await coreTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentRefresh(generation, requestedSession.Observer)) return;

        if (!Settings.EnableTerrainCalculations)
            core = core with { Terrain = DisabledTerrain(), TerrainCrossings = new(null, null), Environment = null };
        if (Settings.EnableTerrainCalculations && scope == PlannerRefreshScope.Astronomy && previousSnapshot is not null &&
            Angles.GreatCircleDistanceMetres(previousSnapshot.Session.Observer, requestedSession.Observer) <= 2)
        {
            core = core with
            {
                Terrain = previousSnapshot.Terrain,
                TerrainCrossings = TerrainCrossingCalculator.Calculate(core.Path, previousSnapshot.Terrain),
                Environment = previousSnapshot.Environment
            };
        }

        UpdateRefresh(generation, state => state with
        {
            AstronomyState = PlannerRefreshWorkState.Ready,
            Phase = PlannerRefreshPhase.UpdatingCelestialOverlays,
            StatusText = Strings.Get("Planner_Status_UpdatingTargets")
        });
        Snapshot = core;
        NotifySnapshotProperties();
        UpdateRefresh(generation, state => state with
        {
            CelestialOverlayState = PlannerRefreshWorkState.Ready,
            Phase = PlannerRefreshPhase.UpdatingCameraOverlay,
            StatusText = Strings.Get("Planner_Status_UpdatingCamera")
        });
        UpdateRefresh(generation, state => state with
        {
            CameraGeometryState = PlannerRefreshWorkState.Ready,
            Phase = PlannerRefreshPhase.LoadingEnvironment,
            StatusText = EnrichmentStatus(state)
        });
    }

    private async Task CommitEnvironmentAsync(Task<PlannerEnvironmentSnapshot> environmentTask,
        Task coreCommit, PlanningSession requestedSession, long generation, CancellationToken cancellationToken)
    {
        try
        {
            UpdateRefresh(generation, state => state with
            {
                GroundTerrainState = Settings.EnableTerrainCalculations
                    ? PlannerRefreshWorkState.Running : PlannerRefreshWorkState.NotRequired,
                EnvironmentMetadataState = PlannerRefreshWorkState.Running,
                StatusText = state.IsCoreReady ? (Settings.EnableTerrainCalculations
                    ? Strings.Get("Planner_Status_LoadingTerrain") : Strings.Get("Planner_Status_LoadingEnvironment")) : state.StatusText
            });
            var environment = await environmentTask;
            await coreCommit;
            cancellationToken.ThrowIfCancellationRequested();
            await _plannerSnapshotCommitGate.WaitAsync(cancellationToken);
            try
            {
                if (!IsCurrentRefresh(generation, requestedSession.Observer) || Snapshot is null) return;
                var horizon = Settings.EnableTerrainCalculations ? environment.HorizonProfile : DisabledTerrain();
                environment = environment with { HorizonProfile = horizon };
                ApplyResolvedTerrainGround(horizon);
                Snapshot = Snapshot with
                {
                    Session = _session,
                    Terrain = horizon,
                    TerrainCrossings = TerrainCrossingCalculator.Calculate(Snapshot.Path, horizon),
                    Environment = environment
                };
                NotifySnapshotProperties();
                UpdateRefresh(generation, state => state with
                {
                    GroundTerrainState = Settings.EnableTerrainCalculations
                        ? EnvironmentWorkState(horizon.HasTerrainCoverage, horizon.GroundHorizonState) : PlannerRefreshWorkState.NotRequired,
                    EnvironmentMetadataState = EnvironmentMetadataWorkState(environment),
                    StatusText = EnrichmentStatus(state)
                });
            }
            finally
            {
                _plannerSnapshotCommitGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (IsCurrentRefresh(generation, requestedSession.Observer))
            {
                _logger.LogWarning(ex, "Optional Planner environment enrichment failed");
                UpdateRefresh(generation, state => state with
                {
                    GroundTerrainState = PlannerRefreshWorkState.Error,
                    EnvironmentMetadataState = PlannerRefreshWorkState.Error,
                    StatusText = EnrichmentStatus(state)
                });
            }
        }
    }

    private async Task CommitWeatherAsync(Task<WeatherResult> weatherTask, Task coreCommit,
        PlanningSession requestedSession, long generation, CancellationToken cancellationToken)
    {
        try
        {
            UpdateRefresh(generation, state => state with
            {
                WeatherState = PlannerRefreshWorkState.Running,
                StatusText = state.IsCoreReady ? Strings.Get("Planner_Status_LoadingWeather") : state.StatusText
            });
            var weather = await weatherTask;
            await coreCommit;
            cancellationToken.ThrowIfCancellationRequested();
            await _plannerSnapshotCommitGate.WaitAsync(cancellationToken);
            try
            {
                if (!IsCurrentRefresh(generation, requestedSession.Observer) || Snapshot is null) return;
                Snapshot = Snapshot with { Weather = weather };
                NotifySnapshotProperties();
                UpdateRefresh(generation, state => state with
                {
                    WeatherState = WeatherWorkState(weather.State),
                    StatusText = EnrichmentStatus(state)
                });
            }
            finally
            {
                _plannerSnapshotCommitGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (IsCurrentRefresh(generation, requestedSession.Observer))
            {
                _logger.LogWarning(ex, "Optional Planner weather enrichment failed");
                UpdateRefresh(generation, state => state with
                {
                    WeatherState = PlannerRefreshWorkState.Error,
                    StatusText = EnrichmentStatus(state)
                });
            }
        }
    }

    private void UpdateRefresh(long generation, Func<PlannerRefreshState, PlannerRefreshState> update)
    {
        lock (_plannerRefreshGate)
        {
            if (generation != Volatile.Read(ref _refreshGeneration) || PlannerRefresh.Generation != generation) return;
            PlannerRefresh = update(PlannerRefresh);
            StatusMessage = PlannerRefresh.StatusText;
        }
    }

    private bool IsCurrentRefresh(long generation, GeoCoordinate observer) =>
        generation == Volatile.Read(ref _refreshGeneration) && PlannerRefresh.Generation == generation &&
        IsCurrentObserver(observer);

    private static PlannerRefreshWorkState EnvironmentWorkState(bool hasCoverage, EnvironmentalDataState state) =>
        hasCoverage && state is EnvironmentalDataState.Available or EnvironmentalDataState.Cached or EnvironmentalDataState.Partial
            ? PlannerRefreshWorkState.Ready
            : state is EnvironmentalDataState.Error or EnvironmentalDataState.InvalidData
                ? PlannerRefreshWorkState.Error
                : PlannerRefreshWorkState.Unavailable;

    private static PlannerRefreshWorkState WeatherWorkState(DataState state) => state switch
    {
        DataState.Ready or DataState.Stale => PlannerRefreshWorkState.Ready,
        DataState.Error => PlannerRefreshWorkState.Error,
        _ => PlannerRefreshWorkState.Unavailable
    };

    private static PlannerRefreshWorkState EnvironmentMetadataWorkState(PlannerEnvironmentSnapshot environment)
    {
        var states = new[] { environment.LandCover.State, environment.Settlement.State };
        if (states.Any(state => state is EnvironmentalDataState.Error or EnvironmentalDataState.InvalidData))
            return PlannerRefreshWorkState.Error;
        return environment.LandCover.HasValue && environment.Settlement.HasValue
            ? PlannerRefreshWorkState.Ready
            : PlannerRefreshWorkState.Unavailable;
    }

    private static string EnrichmentStatus(PlannerRefreshState state)
    {
        if (IsRefreshWorkLoading(state.GroundTerrainState))
            return state.IsCoreReady
                ? state.CameraTerrainState == PlannerRefreshWorkState.Ready
                    ? Strings.Get("Planner_Status_RefiningHorizon")
                    : Strings.Get("Planner_Status_LoadingTerrain")
                : state.StatusText;
        if (IsRefreshWorkLoading(state.WeatherState))
            return state.IsCoreReady ? Strings.Get("Planner_Status_LoadingWeather") : state.StatusText;
        return state.IsCoreReady ? Strings.Get("Planner_Status_ApplyingVisibility") : state.StatusText;
    }

    private void LoadSessionIntoControls()
    {
        _session = _session.WithTerrainCalculationMode(Settings.EnableTerrainCalculations);
        _suppressChanges = true;
        SelectedTarget = Targets.FirstOrDefault(x => x.Id == _session.TargetId) ?? Targets[0];
        Latitude = _session.Observer.Latitude;
        Longitude = _session.Observer.Longitude;
        Elevation = _session.Observer.ElevationMetres;
        PreviewObserver = _session.Observer;
        TimeZoneId = _session.TimeZoneId;
        SelectedOrientation = _session.Lens.Orientation;
        SelectedCamera = Cameras.FirstOrDefault(item => item.Id.Equals(_session.CameraProfileId,
            StringComparison.OrdinalIgnoreCase)) ?? Cameras.FirstOrDefault();
        SelectedLens = Lenses.FirstOrDefault(item => item.Id.Equals(_session.LensProfileId,
            StringComparison.OrdinalIgnoreCase)) ?? Lenses.FirstOrDefault();
        FocalLength = SelectedLens?.ClampFocalLength(_session.Lens.FocalLengthMillimetres) ??
            _session.Lens.FocalLengthMillimetres;
        SelectedLocation = SavedLocations.FirstOrDefault(x => x.Id == _session.SavedLocationId);
        if (SelectedLocation is not null) LocationName = SelectedLocation.Name;
        LoadTimeControls();
        _suppressChanges = false;
        OnPropertyChanged(nameof(Observer));
        ApplyLens();
        NotifyObserverElevationProperties();
    }

    private void LoadTimeControls()
    {
        var local = _timeZones.InZone(_session.Instant, _session.TimeZoneId);
        _suppressChanges = true;
        LocalDate = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TimeSpan.Zero);
        MinutesOfDay = local.Hour * 60 + local.Minute;
        PreviewMinutesOfDay = MinutesOfDay;
        _dateSliderAnchor = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TimeSpan.Zero);
        PreviewDateOffsetDays = 0;
        TimeText = $"{local.Hour:00}:{local.Minute:00}";
        _suppressChanges = false;
    }

    private void NotifySnapshotProperties()
    {
        OnPropertyChanged(nameof(AzimuthText)); OnPropertyChanged(nameof(AltitudeText)); OnPropertyChanged(nameof(HorizonStatus));
        OnPropertyChanged(nameof(HasTargetLocalHorizonDetails)); OnPropertyChanged(nameof(TargetLocalHorizonText));
        OnPropertyChanged(nameof(TargetTerrainMarginLabel)); OnPropertyChanged(nameof(TargetTerrainMarginText));
        OnPropertyChanged(nameof(RiseText)); OnPropertyChanged(nameof(TransitText)); OnPropertyChanged(nameof(SetText));
        NotifyTerrainProperties(); OnPropertyChanged(nameof(WeatherSummary)); OnPropertyChanged(nameof(WeatherDetails));
        OnPropertyChanged(nameof(ConfiguredWeatherDetails));
        OnPropertyChanged(nameof(MoonDetails)); OnPropertyChanged(nameof(HasMoonDetails)); OnPropertyChanged(nameof(SunDetails)); OnPropertyChanged(nameof(HasSunDetails));
        OnPropertyChanged(nameof(FieldOfViewText)); OnPropertyChanged(nameof(Observer));
        OnPropertyChanged(nameof(CanExport));
    }

    private void NotifyTerrainProperties()
    {
        NotifyCameraFrameProperties();
        OnPropertyChanged(nameof(EnabledTerrainDiagnosticsProfile));
        OnPropertyChanged(nameof(TerrainDiagnosticsStatus));
        OnPropertyChanged(nameof(TerrainDiagnosticsBearing));
        OnPropertyChanged(nameof(TerrainDiagnosticsSurface));
        OnPropertyChanged(nameof(TerrainDiagnosticsPerformance));
        OnPropertyChanged(nameof(TerrainDiagnosticsAdvanced));
        OnPropertyChanged(nameof(TerrainStatus));
        OnPropertyChanged(nameof(TerrainCurrentLocationText));
        OnPropertyChanged(nameof(GroundHorizonState));
        OnPropertyChanged(nameof(GroundHorizonAngleText));
        OnPropertyChanged(nameof(TerrainDatumText));
        OnPropertyChanged(nameof(ShowTerrainDebugOverlay));
        OnPropertyChanged(nameof(TerrainDebugProfile));
        OnPropertyChanged(nameof(TerrainDebugGeneration));
        OnPropertyChanged(nameof(TerrainDebugMapGeneration));
        OnPropertyChanged(nameof(TerrainDebugTargetAltitude));
        OnPropertyChanged(nameof(TerrainDebugBearing));
        OnPropertyChanged(nameof(TerrainDebugHorizontalFieldOfView));
        OnPropertyChanged(nameof(TerrainDebugText));
        OnPropertyChanged(nameof(GroundObstructionText));
        OnPropertyChanged(nameof(CameraFramingGuide));
        OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus));
    }

    partial void OnTerrainDebugMapChanged(TerrainDebugMapSnapshot? value)
    {
        OnPropertyChanged(nameof(TerrainDiagnosticsAdvanced));
        OnPropertyChanged(nameof(TerrainDebugText));
    }

    partial void OnTerrainDebugMapLoadStateChanged(TerrainDebugMapLoadState value)
    {
        OnPropertyChanged(nameof(TerrainDiagnosticsAdvanced));
        OnPropertyChanged(nameof(TerrainDebugText));
    }

    private GeoCoordinate? _localMapRequestedObserver;
    private long _localMapSourceGeneration;

    private void ScheduleTerrainDebugMapRefresh()
    {
        var sourceGeneration = _terrainDiskCache?.Generation ?? 0;
        if (Settings.EnableTerrainCalculations && _terrainDebugMaps is not null &&
            _localMapRequestedObserver is { } requested && SameObserverPosition(requested, _session.Observer) &&
            _localMapSourceGeneration == sourceGeneration) return;
        _terrainDebugMapCancellation?.Cancel();
        _terrainDebugMapCancellation?.Dispose();
        _terrainDebugMapCancellation = null;
        Interlocked.Increment(ref _terrainDebugMapGeneration);
        TerrainDebugMap = null;
        OnPropertyChanged(nameof(TerrainDebugMapGeneration));
        if (!Settings.EnableTerrainCalculations || _terrainDebugMaps is null)
        {
            _localMapRequestedObserver = null;
            TerrainDebugMapLoadState = TerrainDebugMapLoadState.Disabled;
            _activeTerrainDebugMapRefresh = Task.CompletedTask;
            return;
        }

        TerrainDebugMapLoadState = TerrainDebugMapLoadState.Resolving;
        _terrainDebugMapCancellation = new CancellationTokenSource();
        var generation = TerrainDebugMapGeneration;
        var observer = _session.Observer;
        _localMapRequestedObserver = observer;
        _localMapSourceGeneration = sourceGeneration;
        _activeTerrainDebugMapRefresh = ResolveTerrainDebugMapAsync(
            observer, generation, _terrainDebugMapCancellation.Token);
    }

    private async Task ResolveTerrainDebugMapAsync(GeoCoordinate observer, long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var map = await _terrainDebugMaps!.GetMapAsync(observer,
                new TerrainDebugMapRequest(), cancellationToken);
            if (generation != TerrainDebugMapGeneration ||
                !SameObserverPosition(observer, _session.Observer)) return;
            var hasTerrain = map.SurfaceElevationsMetres.Any(value => value.HasValue);
            TerrainDebugMap = hasTerrain ? map : null;
            TerrainDebugMapLoadState = hasTerrain ? TerrainDebugMapLoadState.Ready : TerrainDebugMapLoadState.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == TerrainDebugMapGeneration &&
                SameObserverPosition(observer, _session.Observer))
            {
                TerrainDebugMapLoadState = TerrainDebugMapLoadState.Unavailable;
                _logger.LogWarning(ex, "Terrain diagnostic map sampling failed");
            }
        }
    }

    private void ApplyResolvedTerrainGround(TerrainHorizonProfile horizon)
    {
        if (!Settings.EnableTerrainCalculations) return;
        double? resolvedSurface = horizon.GroundElevationAtObserver is { HasValue: true } ground
            ? ground.Value : null;
        if (!resolvedSurface.HasValue) return;
        var terrainGround = _session.EffectiveObserverElevation.IsManualOverride
            ? resolvedSurface
            : horizon.ChosenObserverGroundElevationMetres ?? resolvedSurface;

        var state = RememberSuppliedGroundElevation().WithTerrainGroundElevation(terrainGround.Value);
        var resolvedGround = state.ResolveGroundElevationAsl(_session.Observer.ElevationMetres);
        var observer = _session.Observer with { ElevationMetres = resolvedGround };
        _session = _session with { Observer = observer, ObserverElevation = state };
        _suppressChanges = true;
        Elevation = resolvedGround;
        PreviewObserver = observer;
        _suppressChanges = false;
        if (_lastCustomCoordinate is { } custom &&
            Angles.GreatCircleDistanceMetres(custom, observer) <= 2)
            _lastCustomCoordinate = observer;
        OnPropertyChanged(nameof(Observer));
        NotifyObserverElevationProperties();
    }

    /// <summary>
    /// Records the observer datum as supplied, immediately before a derived elevation replaces it in
    /// the session. Terrain requests fall back on the recorded value, so resolving the ground here
    /// cannot change the identity of an otherwise identical horizon request. A location change starts
    /// a fresh state, whose supplied datum is the elevation that came with the new location.
    /// </summary>
    private ObserverElevationState RememberSuppliedGroundElevation() =>
        _session.EffectiveObserverElevation.WithSuppliedGroundElevation(_session.Observer.ElevationMetres);

    private void NotifyObserverElevationProperties()
    {
        OnPropertyChanged(nameof(IsElevationManualOverride));
        OnPropertyChanged(nameof(TerrainElevationResolutionState));
        OnPropertyChanged(nameof(ResolvedGroundElevationMetres));
        OnPropertyChanged(nameof(GroundElevationDisplay));
        OnPropertyChanged(nameof(CanResetGroundElevation));
        OnPropertyChanged(nameof(GroundElevationSourceText));
        OnPropertyChanged(nameof(EffectiveObserverAltitudeText));
    }

    private void NotifyEquipmentProperties()
    {
        OnPropertyChanged(nameof(IsFocalLengthEditable));
        OnPropertyChanged(nameof(FocalLengthMinimum));
        OnPropertyChanged(nameof(FocalLengthMaximum));
        OnPropertyChanged(nameof(LensFocalRangeText));
    }

    private bool IsCurrentObserver(GeoCoordinate coordinate) =>
        SameObserverPosition(coordinate, _session.Observer);

    /// <summary>
    /// True when the plan on screen would still make the weather request this result answers.
    /// PlanningService.CreateWeatherRequest derives a request from the observer, the planner instant
    /// and the weather settings, and from nothing else, so nothing else is compared here.
    /// </summary>
    private bool IsCurrentWeatherRequest(PlanningSession requested, WeatherSettings settings) =>
        IsCurrentObserver(requested.Observer) && _session.Instant == requested.Instant &&
        Settings.EffectiveWeather == settings;

    /// <summary>The single observer-identity rule; the map view uses it to predict no-op commits.</summary>
    internal static bool SameObserverPosition(GeoCoordinate left, GeoCoordinate right) =>
        Math.Abs(left.Latitude - right.Latitude) <= 1e-10 &&
        Math.Abs(Angles.NormaliseSignedDegrees(left.Longitude - right.Longitude)) <= 1e-10;

    private static bool IsRefreshWorkLoading(PlannerRefreshWorkState state) => state is
        PlannerRefreshWorkState.Pending or PlannerRefreshWorkState.Running;

    private string FormatHorizonState(bool isAvailable, EnvironmentalDataState? state)
    {
        if (CurrentTerrain is null) return Strings.Get("Planner_Horizon_State_Loading");
        if (isAvailable) return Strings.Get("Planner_Horizon_State_Ready");
        return state is EnvironmentalDataState.Error or EnvironmentalDataState.InvalidData
            ? Strings.Get("Planner_Horizon_State_Error")
            : Strings.Get("Planner_Horizon_State_Unavailable");
    }

    private string FormatObstruction(bool hasData, double? distanceMetres)
    {
        if (CurrentTerrain is null || !hasData) return "—";
        if (!distanceMetres.HasValue) return Strings.Get("Planner_Horizon_Obstruction_Clear");
        return Settings.MeasurementProfile.ApproximateDistance(distanceMetres.Value).Format();
    }

    private static string FormatHorizonAngle(double? angleDegrees) =>
        angleDegrees.HasValue ? $"{angleDegrees.Value:+0.0;-0.0;0.0}°" : "—";

    private string FormatTime(Instant? instant) => instant is null ? "—" : _timeZones.InZone(instant.Value, _session.TimeZoneId).ToString("HH:mm", null);
    private static string Value(double? value, string suffix) => value.HasValue ? $"{value:F0}{suffix}" : "—";
    private static string MoonPhaseName(double angle) => Angles.NormaliseDegrees(angle) switch
    {
        < 22.5 or >= 337.5 => Strings.Get("Planner_Moon_Phase_New"),
        < 67.5 => Strings.Get("Planner_Moon_Phase_WaxingCrescent"),
        < 112.5 => Strings.Get("Planner_Moon_Phase_FirstQuarter"),
        < 157.5 => Strings.Get("Planner_Moon_Phase_WaxingGibbous"),
        < 202.5 => Strings.Get("Planner_Moon_Phase_Full"),
        < 247.5 => Strings.Get("Planner_Moon_Phase_WaningGibbous"),
        < 292.5 => Strings.Get("Planner_Moon_Phase_LastQuarter"),
        _ => Strings.Get("Planner_Moon_Phase_WaningCrescent")
    };
}

internal static class LocalTimePatternHelper
{
    public static bool TryParse(string? value, out LocalTime time)
    {
        var result = NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm").Parse(value ?? string.Empty);
        time = result.Success ? result.Value : default;
        return result.Success;
    }
}
