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
using Noctaxis.Core.Measurements;
using Noctaxis.Desktop.Services;

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
    private readonly IPlannerDialogService _dialogs;
    private readonly IReverseGeocodingProvider _reverseGeocoding;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _temporalCommitCancellation;
    private CancellationTokenSource? _temporalPreviewCancellation;
    private CancellationTokenSource? _reverseLookupCancellation;
    private Task _activePlannerRefresh = Task.CompletedTask;
    private readonly SemaphoreSlim _plannerSnapshotCommitGate = new(1, 1);
    private readonly object _plannerRefreshGate = new();
    private long _refreshGeneration;
    private long _reverseLookupGeneration;
    private string? _resolvedPlaceSuggestion;
    private PlanningSession _session;
    private bool _suppressChanges;
    private GeoCoordinate? _lastCustomCoordinate;
    private DateTimeOffset _dateSliderAnchor = DateTimeOffset.Now.Date;

    public MainViewModel(IPlanningService planning, ITargetCatalogue catalogue, ITimeZoneResolver timeZones,
        IUserDataStore store, IScoutingCardExporter exporter,
        ILogger<MainViewModel> logger, ILensCalculator lensCalculator,
        ICameraFramingGuideCalculator cameraFramingGuideCalculator,
        IFramingVisibilityCalculator framingVisibilityCalculator, ILocalHorizonCalculator localHorizonCalculator,
        IClock clock,
        LocationSearchViewModel locationSearch, ILocationResolver locationResolver,
        IDeviceLocationAvailabilityService deviceLocationAvailability, ITargetSearchService targetSearch,
        IPlannerDialogService dialogs, IReverseGeocodingProvider reverseGeocoding,
        ILocationMapThumbnailService? locationMapThumbnails = null)
    {
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
        _dialogs = dialogs;
        _reverseGeocoding = reverseGeocoding;
        _session = PlanningSession.Default(SystemClock.Instance.GetCurrentInstant(), timeZones.MachineTimeZoneId);
        Targets = catalogue.Targets;
        _selectedTarget = Targets[0];
        _localDate = DateTimeOffset.Now;
        _timeText = DateTime.Now.ToString("HH:mm");
        _minutesOfDay = DateTime.Now.TimeOfDay.TotalMinutes;
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
    public AppSettings Settings { get; private set; } = new();
    public PlanningSession Session => _session;
    public GeoCoordinate Observer => _session.Observer;

    [ObservableProperty] private AstralTarget? _selectedTarget;
    [ObservableProperty] private SavedLocation? _selectedLocation;
    [ObservableProperty] private DateTimeOffset? _localDate;
    [ObservableProperty] private double _minutesOfDay;
    [ObservableProperty] private string _timeText;
    [ObservableProperty] private string _timeZoneId;
    [ObservableProperty] private double _latitude;
    [ObservableProperty] private double _longitude;
    [ObservableProperty] private double _elevation;
    [ObservableProperty] private CameraProfile? _selectedCamera;
    [ObservableProperty] private LensProfile? _selectedLens;
    [ObservableProperty] private CameraOrientation _selectedOrientation;
    [ObservableProperty] private double _focalLength;
    [ObservableProperty] private string _locationName = "Current location";
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
    [ObservableProperty] private string _settingsUnits = "Metric";
    [ObservableProperty] private string _settingsTimeZoneId = AppSettings.UseSystemTimeZoneId;
    [ObservableProperty] private double _settingsCameraHeightAboveGroundMetres =
        AppSettings.DefaultCameraHeightAboveGroundMetres;
    [ObservableProperty] private double _settingsWeatherCacheDistance = 5;
    [ObservableProperty] private int _settingsTimeSnapMinutes = 5;
    [ObservableProperty] private double _settingsFramingShadingOpacityPercent = 10;
    [ObservableProperty] private double _settingsFramingLineThickness = 1.25;
    [ObservableProperty] private double _settingsTerrainCastAngularDetailDegrees =
        CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees;
    [ObservableProperty] private bool _settingsCameraFramingOverlayVisible = true;
    [ObservableProperty] private bool _settingsShowFramingVisibilityLimits = true;
    [ObservableProperty] private bool _settingsTerrainDebugOverlay;
    [ObservableProperty] private bool _isRefreshingLocationThumbnails;
    [ObservableProperty] private string? _locationThumbnailRefreshStatus;
    public bool CanExport => !IsLocationInteracting && Snapshot is not null;
    public int VisibleCelestialCount => CelestialObjects.Count(item => item.IsVisible);
    public string VisibleCelestialCountText => $"Visible objects: {VisibleCelestialCount} / {CelestialVisibilityPolicy.MaximumVisibleObjects}";
    [ObservableProperty] private string _statusMessage = "Starting Noctaxis…";

    public bool IsPlannerRefreshing => PlannerRefresh.IsRefreshing;
    public double PlannerRefreshProgress => PlannerRefresh.Progress;
    public string PlannerRefreshStatusText => PlannerRefresh.StatusText;
    public PlannerPinActivity PlannerPinActivity => PlannerRefresh.PinActivity;
    public bool CelestialOverlaysReady => PlannerRefresh.CelestialOverlayState == PlannerRefreshWorkState.Ready;
    public bool CameraOverlayReady => PlannerRefresh.CameraGeometryState == PlannerRefreshWorkState.Ready;
    public bool ShowTerrainDebugOverlay => Settings.TerrainDebugOverlay && CurrentTerrain is not null;
    public TerrainHorizonProfile? TerrainDebugProfile => CurrentTerrain;
    public double TerrainDebugBearing => CurrentCameraBearing ?? 0;
    public double TerrainDebugHorizontalFieldOfView => CameraFramingGuide?.HorizontalFieldOfViewDegrees ?? 60;
    public string TerrainDebugText => CurrentTerrain is { } terrain
        ? TerrainProfileDiagnostics.CreateDebugSnapshot(terrain,
            CurrentCameraBearing ?? Snapshot?.Position.Horizontal.AzimuthDegrees ?? 0)
        : "Terrain diagnostics are waiting for a production horizon profile.";
    public bool IsElevationManualOverride => _session.EffectiveObserverElevation.IsManualOverride;
    public bool CanResetGroundElevation => IsElevationManualOverride &&
        _session.EffectiveObserverElevation.TerrainGroundElevationAslMetres.HasValue;
    public string GroundElevationSourceText => IsElevationManualOverride
        ? "Manual ground-elevation override"
        : _session.EffectiveObserverElevation.TerrainGroundElevationAslMetres.HasValue
            ? "Terrain-derived ground elevation"
            : "Terrain ground elevation pending; using the saved fallback";
    public string EffectiveObserverAltitudeText =>
        $"Effective camera altitude: {_session.EffectiveObserverElevation.EffectiveObserverAltitudeAsl(
            _session.Observer.ElevationMetres, Settings.EffectiveCameraHeightAboveGroundMetres):F1} m ASL " +
        $"(ground + {Settings.EffectiveCameraHeightAboveGroundMetres:F1} m camera height)";
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
    public string LensFocalRangeText => SelectedLens is null ? "No lens selected" :
        SelectedLens.IsPrime ? $"Prime · {SelectedLens.MinimumFocalLengthMillimetres:0.#} mm" :
        $"Zoom · {SelectedLens.MinimumFocalLengthMillimetres:0.#}–{SelectedLens.MaximumFocalLengthMillimetres:0.#} mm";

    public string AzimuthText => Snapshot is null ? "—" : $"{Snapshot.Position.Horizontal.AzimuthDegrees:F1}°";
    public string AltitudeText => Snapshot is null ? "—" : $"{Snapshot.Position.Horizontal.AltitudeDegrees:+0.0;-0.0;0.0}°";
    private TargetLocalVisibility? CurrentTargetLocalVisibility => Snapshot is null
        ? null
        : _localHorizonCalculator.AssessTarget(Snapshot.Terrain,
            Snapshot.Position.Horizontal.AzimuthDegrees, Snapshot.Position.Horizontal.AltitudeDegrees);
    public string HorizonStatus => CurrentTargetLocalVisibility?.State switch
    {
        null => "Calculating…",
        TargetLocalVisibilityState.BelowAstronomicalHorizon => "Below astronomical horizon",
        TargetLocalVisibilityState.TerrainBlocked => "Terrain blocked",
        TargetLocalVisibilityState.Marginal => "Marginal",
        TargetLocalVisibilityState.Clear => "Clear",
        _ => "Above astronomical horizon · terrain unavailable"
    };
    public bool HasTargetLocalHorizonDetails => CurrentTargetLocalVisibility is
        { State: TargetLocalVisibilityState.TerrainBlocked or TargetLocalVisibilityState.Marginal or TargetLocalVisibilityState.Clear };
    public string TargetLocalHorizonText => FormatHorizonAngle(CurrentTargetLocalVisibility?.LocalHorizonAltitudeDegrees);
    public string TargetTerrainMarginLabel => CurrentTargetLocalVisibility?.State == TargetLocalVisibilityState.TerrainBlocked
        ? "Blocked by"
        : "Clearance";
    public string TargetTerrainMarginText => CurrentTargetLocalVisibility?.ClearanceDegrees is double clearance
        ? $"{Math.Abs(clearance):0.0}°"
        : "—";
    public string RiseText => FormatTime(Snapshot?.Position.Events.Rise);
    public string TransitText => FormatTime(Snapshot?.Position.Events.Transit);
    public string SetText => FormatTime(Snapshot?.Position.Events.Set);
    public string TerrainStatus => CurrentEnvironment is { } environment
        ? environment.ActiveSourceDescription
        : CurrentTerrain is { } terrain
            ? terrain.Status
            : "Loading terrain horizon…";
    public string TerrainCurrentLocationText => $"{Observer.Latitude:F5}, {Observer.Longitude:F5}";
    public string GroundHorizonState => IsRefreshWorkLoading(PlannerRefresh.GroundTerrainState)
        ? CurrentTerrain is { IsComplete: false, HasTerrainCoverage: true } ? "Ready · refining…" : "Loading…"
        : FormatHorizonState(CurrentTerrain?.HasTerrainCoverage == true,
            CurrentTerrain?.GroundHorizonState);
    public string GroundHorizonAngleText => FormatHorizonAngle(
        CurrentCameraBearing is double bearing ? CurrentTerrain?.GroundAltitudeAt(bearing) : null);
    public string TerrainDatumText => CurrentTerrain switch
    {
        null => "Observer datum: Loading",
        { ObserverDatumConfidence: ObserverDatumConfidence.Normal } => "Observer datum: Normal",
        _ => "Observer datum: Unavailable"
    };
    public string GroundObstructionText => FormatObstruction(
        CurrentCameraObstruction.HasGroundData,
        CurrentCameraObstruction.GroundFirstObstructionDistanceMetres);
    public string WeatherSummary => Snapshot?.Weather.Conditions is { } weather
        ? "Open-Meteo · " + weather.Summary
        : Snapshot?.Weather.Message ?? "Loading weather…";
    public string WeatherDetails
    {
        get
        {
            var w = Snapshot?.Weather.Conditions;
            if (w is null) return "No weather values";
            var units = Settings.EffectiveMeasurementSystem;
            var temperature = MeasurementUnits.FormatTemperature(w.TemperatureCelsius, units);
            var dew = MeasurementUnits.FormatTemperature(w.DewPointCelsius, units);
            var wind = MeasurementUnits.FormatWindSpeed(w.WindSpeedMetresPerSecond, units);
            var gust = MeasurementUnits.FormatWindSpeed(w.WindGustMetresPerSecond, units);
            var visibility = MeasurementUnits.FormatVisibility(w.VisibilityKilometres, units);
            var precipitation = MeasurementUnits.FormatPrecipitation(w.PrecipitationMillimetres, units);
            return $"{(w.IsStale ? "STALE · " : "")}Cloud {Value(w.CloudCoverPercent, "%")} · {temperature}\nLow/Mid/High {Value(w.LowCloudPercent, "%")} / {Value(w.MediumCloudPercent, "%")} / {Value(w.HighCloudPercent, "%")}\nPrecip {Value(w.PrecipitationProbabilityPercent, "%")} · {precipitation}\nWind {wind} from {Value(w.WindDirectionDegrees, "°")} · gust {gust}\nHumidity {Value(w.HumidityPercent, "%")} · Dew {dew} · Visibility {visibility}";
        }
    }
    public string MoonDetails => Snapshot?.Position.MoonIlluminatedFraction is double fraction
        ? $"{MoonPhaseName(Snapshot.Position.MoonPhaseAngleDegrees ?? 0)} · illumination {fraction * 100:F0}% · phase angle {Snapshot.Position.MoonPhaseAngleDegrees:F0}°"
        : string.Empty;
    public string ConfiguredWeatherDetails
    {
        get
        {
            var w = Snapshot?.Weather.Conditions;
            if (w is null) return "No weather values";
            var settings = Settings.EffectiveWeather;
            var units = Settings.EffectiveMeasurementSystem;
            var lines = new List<string>();
            void Add(WeatherField weatherField, string text) { if (settings.IsEnabled(weatherField)) lines.Add(text); }
            Add(WeatherField.TotalCloudCover, "Cloud " + Value(w.CloudCoverPercent, "%"));
            Add(WeatherField.LowCloudCover, "Low cloud " + Value(w.LowCloudPercent, "%"));
            Add(WeatherField.MediumCloudCover, "Medium cloud " + Value(w.MediumCloudPercent, "%"));
            Add(WeatherField.HighCloudCover, "High cloud " + Value(w.HighCloudPercent, "%"));
            Add(WeatherField.PrecipitationProbability, "Precipitation probability " + Value(w.PrecipitationProbabilityPercent, "%"));
            Add(WeatherField.PrecipitationAmount, "Precipitation " + MeasurementUnits.FormatPrecipitation(w.PrecipitationMillimetres, units));
            Add(WeatherField.PrecipitationType, "Precipitation type " + (w.PrecipitationType ?? "—"));
            Add(WeatherField.Temperature, "Temperature " + MeasurementUnits.FormatTemperature(w.TemperatureCelsius, units));
            Add(WeatherField.DewPoint, "Dew point " + MeasurementUnits.FormatTemperature(w.DewPointCelsius, units));
            Add(WeatherField.RelativeHumidity, "Humidity " + Value(w.HumidityPercent, "%"));
            Add(WeatherField.WindSpeed, "Wind speed " + MeasurementUnits.FormatWindSpeed(w.WindSpeedMetresPerSecond, units));
            Add(WeatherField.WindGusts, "Wind gusts " + MeasurementUnits.FormatWindSpeed(w.WindGustMetresPerSecond, units));
            Add(WeatherField.WindDirection, "Wind direction " + Value(w.WindDirectionDegrees, "°"));
            Add(WeatherField.Visibility, "Visibility " + MeasurementUnits.FormatVisibility(w.VisibilityKilometres, units));
            var t = Snapshot!.Astronomy.Sun.Twilight;
            Add(WeatherField.Sunrise, "Sunrise " + FormatTime(t?.Sunrise));
            Add(WeatherField.Sunset, "Sunset " + FormatTime(t?.Sunset));
            Add(WeatherField.CivilTwilight, $"Civil twilight {FormatTime(t?.CivilDawn)}–{FormatTime(t?.CivilDusk)}");
            Add(WeatherField.NauticalTwilight, $"Nautical twilight {FormatTime(t?.NauticalDawn)}–{FormatTime(t?.NauticalDusk)}");
            Add(WeatherField.AstronomicalTwilight, $"Astronomical twilight {FormatTime(t?.AstronomicalDawn)}–{FormatTime(t?.AstronomicalDusk)}");
            Add(WeatherField.AstronomicalDarkness, $"Astronomical darkness before {FormatTime(t?.AstronomicalDawn)} / after {FormatTime(t?.AstronomicalDusk)}");
            var moon = Snapshot.Astronomy.Moon;
            Add(WeatherField.MoonPhase, "Moon phase " + MoonPhaseName(moon.MoonPhaseAngleDegrees ?? 0));
            Add(WeatherField.MoonIllumination, $"Moon illumination {moon.MoonIlluminatedFraction * 100:F0}%");
            Add(WeatherField.Moonrise, "Moonrise " + FormatTime(moon.Events.Rise));
            Add(WeatherField.Moonset, "Moonset " + FormatTime(moon.Events.Set));
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
            return t is null ? string.Empty : $"Sunrise {FormatTime(t.Sunrise)} · Sunset {FormatTime(t.Sunset)}\nCivil {FormatTime(t.CivilDawn)}–{FormatTime(t.CivilDusk)} · Nautical {FormatTime(t.NauticalDawn)}–{FormatTime(t.NauticalDusk)}\nAstronomical {FormatTime(t.AstronomicalDawn)}–{FormatTime(t.AstronomicalDusk)}";
        }
    }
    public string FieldOfViewText => Snapshot is null ? "—" : $"{Snapshot.FieldOfView.HorizontalDegrees:F1}° × {Snapshot.FieldOfView.VerticalDegrees:F1}°";
    public CameraFramingGuide? CameraFramingGuide => Snapshot is null || !IsCameraFramingOverlayVisible
        ? null
        : _cameraFramingGuideCalculator.Calculate(
            Snapshot.FieldOfView,
            Snapshot.Position.Horizontal.AzimuthDegrees,
            Settings.EffectiveCameraFraming with { IsOverlayVisible = IsCameraFramingOverlayVisible });
    public FramingVisibilityAssessment? CameraFramingVisibility =>
        Snapshot is null || CameraFramingGuide is null || !ShowFramingVisibilityLimits
            ? null
            : _framingVisibilityCalculator.Calculate(
                Snapshot.Weather,
                Snapshot.Terrain,
                Snapshot.Position.Horizontal.AltitudeDegrees,
                CameraFramingGuide.CentreBearingDegrees,
                CameraFramingGuide.HorizontalFieldOfViewDegrees,
                Settings.EffectiveCameraFraming.TerrainCastAngularDetailDegrees,
                Snapshot.FieldOfView.VerticalDegrees);
    public string FramingVisibilityStatus => !IsCameraFramingOverlayVisible
        ? string.Empty
        : !ShowFramingVisibilityLimits
            ? "Visibility limits hidden"
            : CameraFramingVisibility?.Status ?? "Visibility data unavailable";
    public CameraFramingSettings CameraFramingMapSettings =>
        (Settings.EffectiveCameraFraming with { LineThickness = SettingsFramingLineThickness }).Normalised();

    private PlannerEnvironmentSnapshot? CurrentEnvironment =>
        Snapshot?.Environment is { } environment && IsCurrentObserver(environment.ObserverCoordinate)
            ? environment
            : null;

    private TerrainHorizonProfile? CurrentTerrain =>
        Snapshot is { } snapshot && IsCurrentObserver(snapshot.Session.Observer)
            ? snapshot.Terrain
            : null;

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

    public async Task InitializeAsync()
    {
        var persisted = await _store.LoadAsync(CancellationToken.None);
        Settings = persisted.Settings with
        {
            CameraHeightAboveGroundMetres = persisted.Settings.EffectiveCameraHeightAboveGroundMetres,
            Equipment = persisted.Settings.EffectiveEquipment(persisted.Session.Lens)
        };
        _suppressChanges = true;
        IsCameraFramingOverlayVisible = Settings.EffectiveCameraFraming.IsOverlayVisible;
        ShowFramingVisibilityLimits = Settings.EffectiveCameraFraming.ShowVisibilityLimits;
        _suppressChanges = false;
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        SavedLocations.Clear();
        foreach (var location in persisted.Locations) SavedLocations.Add(location);
        _lastCustomCoordinate = persisted.LastCustomCoordinate;
        var configuredZone = _timeZones.GetEffectiveId(Settings.SelectedTimeZoneId);
        _session = persisted.Session with { TimeZoneId = _timeZones.GetEffectiveId(persisted.Session.TimeZoneId) };
        if (_session.SavedLocationId is null) _session = _session with { TimeZoneId = configuredZone };
        LoadEquipmentOptions();
        EnsureCelestialSelections();
        BuildCelestialObjectItems();
        LoadSettingsEditor();
        Locations.Load(SavedLocations, _lastCustomCoordinate, _session.SavedLocationId);
        await Locations.RefreshDeviceAvailabilityAsync(CancellationToken.None);
        LoadSessionIntoControls();
        PreviewObserver = _session.Observer;
        PreviewMinutesOfDay = MinutesOfDay;
        _dateSliderAnchor = LocalDate ?? DateTimeOffset.Now.Date;
        SelectedPageIndex = 0;
    }

    public void MoveObserver(GeoCoordinate coordinate)
    {
        PreviewObserverLocation(coordinate);
        CommitObserverLocation(coordinate);
    }

    internal Task WaitForPlannerRefreshAsync() => _activePlannerRefresh;

    public void PreviewObserverLocation(GeoCoordinate coordinate)
    {
        var normalised = coordinate.Normalised();
        if (Angles.GreatCircleDistanceMetres(normalised, _session.Observer) > 2)
        {
            CancelReverseLocationLookup();
            _resolvedPlaceSuggestion = null;
            LocationName = "New location";
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
        => CommitObserverLocation(coordinate with { ElevationMetres = 0 }, null, resolvePlaceName: true);

    private void CommitObserverLocation(GeoCoordinate coordinate, string? resolvedName, bool resolvePlaceName)
    {
        var normalised = coordinate.Normalised();
        var locationChanged = Angles.GreatCircleDistanceMetres(normalised, _session.Observer) > 2;
        var elevationState = locationChanged ? new ObserverElevationState() :
            _session.EffectiveObserverElevation;
        normalised = normalised with
        {
            ElevationMetres = elevationState.ResolveGroundElevationAsl(normalised.ElevationMetres)
        };
        CancelReverseLocationLookup();
        _resolvedPlaceSuggestion = string.IsNullOrWhiteSpace(resolvedName) ? null : resolvedName.Trim();
        LocationName = _resolvedPlaceSuggestion ?? "New location";
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

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        Settings = settings with
        {
            Units = MeasurementUnits.NormaliseId(settings.Units),
            CelestialObjects = Settings.EffectiveCelestialObjects,
            CameraHeightAboveGroundMetres = settings.EffectiveCameraHeightAboveGroundMetres,
            Equipment = settings.EffectiveEquipment(_session.Lens)
        };
        LoadEquipmentOptions();
        _suppressChanges = true;
        IsCameraFramingOverlayVisible = Settings.EffectiveCameraFraming.IsOverlayVisible;
        ShowFramingVisibilityLimits = Settings.EffectiveCameraFraming.ShowVisibilityLimits;
        TimeZoneId = _timeZones.GetEffectiveId(settings.SelectedTimeZoneId);
        _suppressChanges = false;
        ApplyLocalDateTime(false);
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(WeatherDetails));
        OnPropertyChanged(nameof(ConfiguredWeatherDetails));
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus));
        OnPropertyChanged(nameof(ShowTerrainDebugOverlay));
        OnPropertyChanged(nameof(TerrainDebugProfile));
        OnPropertyChanged(nameof(TerrainDebugBearing));
        OnPropertyChanged(nameof(TerrainDebugHorizontalFieldOfView));
        OnPropertyChanged(nameof(TerrainDebugText));
        NotifyObserverElevationProperties();
        ScheduleObserverRefresh(0);
        await _activePlannerRefresh;
        await PersistAsync(CancellationToken.None);
    }

    private void LoadSettingsEditor()
    {
        SettingsUnits = MeasurementUnits.NormaliseId(Settings.Units);
        SettingsTimeZoneId = Settings.SelectedTimeZoneId;
        SettingsCameraHeightAboveGroundMetres = Settings.EffectiveCameraHeightAboveGroundMetres;
        SettingsWeatherCacheDistance = Settings.EffectiveWeather.CacheDistanceKilometres;
        SettingsTimeSnapMinutes = Settings.TimeSnapMinutes;
        SettingsFramingShadingOpacityPercent = Settings.EffectiveCameraFraming.ShadingOpacityPercent;
        SettingsFramingLineThickness = Settings.EffectiveCameraFraming.LineThickness;
        SettingsTerrainCastAngularDetailDegrees = Settings.EffectiveCameraFraming.TerrainCastAngularDetailDegrees;
        SettingsCameraFramingOverlayVisible = Settings.EffectiveCameraFraming.IsOverlayVisible;
        SettingsShowFramingVisibilityLimits = Settings.EffectiveCameraFraming.ShowVisibilityLimits;
        SettingsTerrainDebugOverlay = Settings.TerrainDebugOverlay;
        WeatherFieldOptions.Clear();
        foreach (var field in Enum.GetValues<WeatherField>())
            WeatherFieldOptions.Add(new WeatherFieldOptionViewModel(field, WeatherFieldLabel(field), Settings.EffectiveWeather.IsEnabled(field)));
        WeatherFieldGroups.Clear();
        foreach (var (label, fields) in WeatherFieldGroupDefinitions)
            WeatherFieldGroups.Add(new WeatherFieldGroupViewModel(label,
                fields.Select(field => WeatherFieldOptions.Single(option => option.Field == field)).ToArray()));
        EquipmentCameraEditors.Clear();
        foreach (var profile in Settings.EffectiveEquipment(_session.Lens).Cameras!)
            EquipmentCameraEditors.Add(new CameraProfileEditorViewModel(profile, RemoveCameraProfileEditor));
        EquipmentLensEditors.Clear();
        foreach (var profile in Settings.EffectiveEquipment(_session.Lens).Lenses!)
            EquipmentLensEditors.Add(new LensProfileEditorViewModel(profile, RemoveLensProfileEditor));
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
    private async Task SaveSettings()
    {
        if (EquipmentCameraEditors.Count == 0 || EquipmentLensEditors.Count == 0)
        {
            StatusMessage = "At least one valid camera and lens are required.";
            return;
        }
        var invalidEquipment = EquipmentCameraEditors.Select(item => item.ValidationMessage)
            .Concat(EquipmentLensEditors.Select(item => item.ValidationMessage))
            .FirstOrDefault(message => message is not null);
        if (invalidEquipment is not null)
        {
            StatusMessage = invalidEquipment;
            return;
        }
        var updated = Settings with
        {
            Units = MeasurementUnits.NormaliseId(SettingsUnits),
            SelectedTimeZoneId = SettingsTimeZoneId,
            Weather = new WeatherSettings(
                WeatherFieldOptions.Where(item => item.IsEnabled).Select(item => item.Field).ToArray(),
                Math.Clamp(SettingsWeatherCacheDistance, 0, 100)),
            TimeSnapMinutes = Math.Clamp(SettingsTimeSnapMinutes, 1, 30),
            CameraHeightAboveGroundMetres =
                AppSettings.NormaliseCameraHeight(SettingsCameraHeightAboveGroundMetres),
            TerrainDebugOverlay = SettingsTerrainDebugOverlay,
            Equipment = new EquipmentSettings(
                EquipmentCameraEditors.Select(item => item.Profile).ToArray(),
                EquipmentLensEditors.Select(item => item.Profile).ToArray()),
            CameraFraming = Settings.EffectiveCameraFraming with
            {
                IsOverlayVisible = SettingsCameraFramingOverlayVisible,
                ShowVisibilityLimits = SettingsShowFramingVisibilityLimits,
                ShadingOpacityPercent = Math.Clamp(SettingsFramingShadingOpacityPercent, 0, 50),
                LineThickness = Math.Clamp(SettingsFramingLineThickness, .5, 5),
                TerrainCastAngularDetailDegrees = Math.Clamp(SettingsTerrainCastAngularDetailDegrees,
                    CameraFramingSettings.MinimumTerrainCastAngularDetailDegrees,
                    CameraFramingSettings.MaximumTerrainCastAngularDetailDegrees)
            }
        };
        await ApplySettingsAsync(updated);
        StatusMessage = "Settings saved";
    }

    [RelayCommand] private void ResetSettingsEditor() => LoadSettingsEditor();

    [RelayCommand]
    private void AddCameraProfile() => EquipmentCameraEditors.Add(new CameraProfileEditorViewModel(
        new CameraProfile(Guid.NewGuid().ToString("N"), "New camera", 36, 24),
        RemoveCameraProfileEditor));

    [RelayCommand]
    private void AddLensProfile() => EquipmentLensEditors.Add(new LensProfileEditorViewModel(
        new LensProfile(Guid.NewGuid().ToString("N"), "New lens", 24, 70),
        RemoveLensProfileEditor));

    private void RemoveCameraProfileEditor(CameraProfileEditorViewModel profile) =>
        EquipmentCameraEditors.Remove(profile);

    private void RemoveLensProfileEditor(LensProfileEditorViewModel profile) =>
        EquipmentLensEditors.Remove(profile);

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

    partial void OnSettingsFramingLineThicknessChanged(double value) =>
        OnPropertyChanged(nameof(CameraFramingMapSettings));

    [RelayCommand(CanExecute = nameof(CanRefreshSavedLocationThumbnails))]
    private async Task RefreshSavedLocationThumbnails()
    {
        if (!await _dialogs.ConfirmRefreshSavedLocationThumbnailsAsync(Locations.Saved.Count)) return;
        await RunSavedLocationMapOperationAsync(SavedLocationMapRefreshMode.RefreshSource,
            "Regenerating saved location map images...", "map images regenerated");
    }

    [RelayCommand(CanExecute = nameof(CanRefreshSavedLocationThumbnails))]
    private Task ReapplySavedLocationMapStyles() => RunSavedLocationMapOperationAsync(
        SavedLocationMapRefreshMode.ReapplyStyle,
        "Reapplying saved location map image style...", "map image styles reapplied");

    [RelayCommand(CanExecute = nameof(CanRefreshSavedLocationThumbnails))]
    private async Task RefreshSavedLocationSettlementCaches()
    {
        if (!await _dialogs.ConfirmRefreshSavedLocationSettlementCachesAsync(Locations.Saved.Count)) return;
        await RunSavedLocationMapOperationAsync(SavedLocationMapRefreshMode.RefreshSettlement,
            "Refreshing saved location WSF settlement data...", "settlement data refreshed");
    }

    private async Task RunSavedLocationMapOperationAsync(SavedLocationMapRefreshMode mode,
        string busyMessage, string completedDescription)
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
                    "operation_failed", "the requested map operation did not complete");
                switch (outcome.Status)
                {
                    case MapFeatureFetchStatus.Complete: coreComplete++; break;
                    case MapFeatureFetchStatus.CachedPrevious:
                        coreCached++;
                        break;
                    default:
                        coreFailed++;
                        details.Add($"{card.Name}: {outcome.FailureReason ?? "road and water overlays unavailable"}");
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
                        details.Add($"{card.Name}: WSF settlement coverage is partial");
                        break;
                    case Noctaxis.Core.Environment.EnvironmentalDataState.TileAbsent:
                        settlementFailed++;
                        details.Add($"{card.Name}: WSF coverage tile absent");
                        break;
                    case Noctaxis.Core.Environment.EnvironmentalDataState.InvalidRaster:
                        settlementFailed++;
                        details.Add($"{card.Name}: WSF raster failed scientific validation");
                        break;
                    default:
                        settlementFailed++;
                        details.Add($"{card.Name}: WSF source unavailable");
                        break;
                }
            }
            Locations.RefreshThumbnailAttributions();
            LocationThumbnailRefreshStatus = mode == SavedLocationMapRefreshMode.ReapplyStyle
                ? $"{succeeded} refreshed, {failed} failed."
                : $"Raster maps: {rasterComplete} complete, {rasterCached} cached, {rasterFailed} failed.{Environment.NewLine}" +
                  $"Road and water overlays: {coreComplete} complete, {coreCached} cached, {coreFailed} failed.{Environment.NewLine}" +
                  $"WSF settlement layers: {settlementComplete} complete, {settlementCached} cached, " +
                  $"{settlementEmpty} valid empty, {settlementPartial} partial, {settlementFailed} failed." +
                  (details.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, details));
            StatusMessage = failed == 0 && coreFailed == 0 && settlementFailed == 0 && settlementPartial == 0
                ? $"Saved location {completedDescription}"
                : $"Saved location {completedDescription} with degraded or failed results";
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
        WeatherField.TotalCloudCover => "Total cloud cover", WeatherField.LowCloudCover => "Low cloud cover",
        WeatherField.MediumCloudCover => "Medium cloud cover", WeatherField.HighCloudCover => "High cloud cover",
        WeatherField.PrecipitationProbability => "Precipitation probability", WeatherField.PrecipitationAmount => "Precipitation amount",
        WeatherField.PrecipitationType => "Precipitation type", WeatherField.Visibility => "Visibility",
        WeatherField.Temperature => "Temperature", WeatherField.DewPoint => "Dew point", WeatherField.RelativeHumidity => "Relative humidity",
        WeatherField.WindSpeed => "Wind speed", WeatherField.WindGusts => "Wind gusts", WeatherField.WindDirection => "Wind direction",
        WeatherField.Sunrise => "Sunrise", WeatherField.Sunset => "Sunset",
        WeatherField.CivilTwilight => "Civil twilight", WeatherField.NauticalTwilight => "Nautical twilight",
        WeatherField.AstronomicalTwilight => "Astronomical twilight", WeatherField.AstronomicalDarkness => "Astronomical darkness",
        WeatherField.MoonPhase => "Moon phase", WeatherField.MoonIllumination => "Moon illumination",
        WeatherField.Moonrise => "Moonrise", WeatherField.Moonset => "Moonset",
        _ => field.ToString()
    };

    private static readonly IReadOnlyList<(string Label, IReadOnlyList<WeatherField> Fields)> WeatherFieldGroupDefinitions =
    [
        ("Weather",
        [
            WeatherField.TotalCloudCover, WeatherField.LowCloudCover, WeatherField.MediumCloudCover, WeatherField.HighCloudCover,
            WeatherField.PrecipitationProbability, WeatherField.PrecipitationAmount, WeatherField.PrecipitationType,
            WeatherField.Visibility, WeatherField.Temperature, WeatherField.DewPoint, WeatherField.RelativeHumidity,
            WeatherField.WindSpeed, WeatherField.WindGusts, WeatherField.WindDirection
        ]),
        ("Sun & darkness",
        [
            WeatherField.Sunrise, WeatherField.Sunset, WeatherField.CivilTwilight, WeatherField.NauticalTwilight,
            WeatherField.AstronomicalTwilight, WeatherField.AstronomicalDarkness
        ]),
        ("Moon", [WeatherField.MoonPhase, WeatherField.MoonIllumination, WeatherField.Moonrise, WeatherField.Moonset])
    ];

    public async Task<byte[]> CreateExportPngAsync(CancellationToken cancellationToken)
    {
        if (Snapshot is null) throw new InvalidOperationException("The plan is not ready to export.");
        StatusMessage = "Refreshing weather for export…";
        _logger.LogInformation("Starting export weather refresh");
        try
        {
            var result = await _planning.RefreshWeatherAsync(_session, Settings.EffectiveWeather, cancellationToken);
            if (result.State != DataState.Ready || result.Conditions is null)
                throw new InvalidOperationException("Export cancelled: " + result.Message);
            Snapshot = Snapshot with { Weather = result };
            NotifySnapshotProperties();
            var context = new ScoutingCardExportContext(LocationName, SelectedLocation, Settings.EffectiveWeather, Settings.Units);
            var bytes = await _exporter.RenderPngAsync(Snapshot, context, cancellationToken);
            StatusMessage = "Scouting card ready";
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
        StatusMessage = "Export failed: " + exception.Message;
        _logger.LogWarning(exception, "PNG export destination failed");
    }

    public async Task PersistAsync(CancellationToken cancellationToken)
    {
        var state = new PersistedState(4, Settings, SavedLocations.ToArray(), _session, SelectedLocation?.Id, _lastCustomCoordinate);
        await _store.SaveAsync(state, cancellationToken);
    }

    [RelayCommand] private void ShowLocations() { SelectedPageIndex = 0; _logger.LogInformation("Navigated to Locations"); }
    [RelayCommand] private void ShowPlanner()
    {
        SelectedPageIndex = 1;
        _logger.LogInformation("Navigated to Planner");
        if (Snapshot is null) ScheduleObserverRefresh(20);
    }
    [RelayCommand] private void ShowSettings() { SelectedPageIndex = 2; _logger.LogInformation("Navigated to Settings"); }

    private async Task OpenCustomAsync(LocationResolution resolution)
    {
        _lastCustomCoordinate = resolution.Coordinate;
        if (!string.IsNullOrWhiteSpace(resolution.TimeZoneId)) TimeZoneId = _timeZones.GetEffectiveId(resolution.TimeZoneId);
        SelectedPageIndex = 1;
        CommitObserverLocation(resolution.Coordinate, resolution.DisplayName, resolvePlaceName: resolution.DisplayName is null);
        await PersistAsync(CancellationToken.None);
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
            await PersistAsync(CancellationToken.None);
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
        SelectedPageIndex = 1;
        LoadSessionIntoControls();
        PreviewObserver = updated.Coordinate;
        OnPropertyChanged(nameof(Observer));
        ScheduleObserverRefresh(20);
        await PersistAsync(CancellationToken.None);
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
            StatusMessage = "A saved location already uses that name.";
            return;
        }
        var updated = card.Location with { Name = name, Notes = edit.Description };
        card.Update(updated);
        ReplaceLocation(updated);
        await PersistAsync(CancellationToken.None);
    }

    private async Task DeleteLocationAsync(LocationCardViewModel card)
    {
        if (!await _dialogs.ConfirmDeleteSavedLocationAsync(card.Location)) return;
        var match = SavedLocations.FirstOrDefault(location => location.Id == card.Id);
        if (match is not null) SavedLocations.Remove(match);
        if (_session.SavedLocationId == card.Id) _session = _session with { SavedLocationId = null };
        SyncLocationHomepage();
        await PersistAsync(CancellationToken.None);
    }

    private async Task ToggleFavouriteAsync(LocationCardViewModel card)
    {
        var updated = card.Location with { IsFavourite = !card.Location.IsFavourite };
        card.Update(updated);
        ReplaceLocation(updated);
        await PersistAsync(CancellationToken.None);
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
                target.Id.Equals(_session.TargetId, StringComparison.OrdinalIgnoreCase), CelestialColour(target, selection.Order),
                CelestialObjectChanged, MakePrimaryTarget, RemoveCelestialObject, MoveCelestialObject));
        }
        NotifyCelestialCount();
    }

    private void CelestialObjectChanged(CelestialObjectItemViewModel item)
    {
        if (item.IsVisible && VisibleCelestialCount > CelestialVisibilityPolicy.MaximumVisibleObjects)
        {
            item.SetVisibilitySilently(false);
            CelestialLimitMessage = "A maximum of eight celestial objects can be visible at once.";
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
                CelestialLimitMessage = "Hide another object before making this target visible.";
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
        var item = new CelestialObjectItemViewModel(target, visible, order, false, CelestialColour(target, order),
            CelestialObjectChanged, MakePrimaryTarget, RemoveCelestialObject, MoveCelestialObject);
        CelestialObjects.Add(item);
        if (visible) SetPrimaryTarget(item);
        else CelestialLimitMessage = "Object added hidden because eight objects are already visible.";
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
    private void RestoreCelestialDefaults()
    {
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

    private static string CelestialColour(AstralTarget target, int order)
    {
        string[] deepSky = ["#B790FF", "#63D6C5", "#F48FB1", "#A5D66A", "#FF9F68"];
        return target.IsSun ? "#F3B34C" : target.IsMoon ? "#79B8FF" : deepSky[Math.Abs(order) % deepSky.Length];
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
        StatusMessage = "Refreshing weather…";
        _logger.LogInformation("Manual weather refresh requested");
        try
        {
            var result = await _planning.RefreshWeatherAsync(_session, Settings.EffectiveWeather, CancellationToken.None);
            if (result.State == DataState.Ready && result.Conditions is not null && Snapshot is not null)
            {
                Snapshot = Snapshot with { Weather = result };
                StatusMessage = "Weather refreshed";
                NotifySnapshotProperties();
            }
            else
            {
                StatusMessage = "Weather refresh failed: " + result.Message;
                _logger.LogWarning("Manual weather refresh failed: {Message}", result.Message);
            }
        }
        catch (Exception ex) { StatusMessage = "Weather refresh failed: " + ex.Message; _logger.LogWarning(ex, "Manual weather refresh failed"); }
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
            StatusMessage = "A saved location already uses that name.";
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
        await PersistAsync(CancellationToken.None);
        if (requiresMapGeneration) await GenerateSavedLocationMapAsync(location);
        StatusMessage = existing is null ? "Location saved" : "Location updated";
    }

    [RelayCommand]
    private async Task SaveCurrentAsNewLocation()
    {
        var now = _clock.GetCurrentInstant().ToDateTimeOffset();
        var suggestedName = _resolvedPlaceSuggestion ?? (string.IsNullOrWhiteSpace(LocationName) ||
                            LocationName.Equals("Current location", StringComparison.OrdinalIgnoreCase) ||
                            LocationName.Equals("New location", StringComparison.OrdinalIgnoreCase)
            ? $"Location {SavedLocations.Count + 1}"
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
                ? "A location name is required."
                : "A saved location already uses that name.";
            return;
        }

        var location = draft with { Name = name, Notes = edit.Description };
        SavedLocations.Add(location);
        SelectedLocation = location;
        _session = _session with { SavedLocationId = location.Id };
        LocationName = location.Name;
        SyncLocationHomepage();
        await PersistAsync(CancellationToken.None);
        await GenerateSavedLocationMapAsync(location);
        StatusMessage = "Location saved";
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
        await PersistAsync(CancellationToken.None);
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
        var state = _session.EffectiveObserverElevation.WithManualOverride(value);
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
        var state = _session.EffectiveObserverElevation.ResetManualOverride();
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
        SettingsCameraFramingOverlayVisible = value;
        Settings = Settings with
        {
            CameraFraming = Settings.EffectiveCameraFraming with { IsOverlayVisible = value }
        };
        OnPropertyChanged(nameof(CameraFramingGuide));
        OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus));
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        _ = PersistCameraFramingPreferenceAsync();
    }
    partial void OnShowFramingVisibilityLimitsChanged(bool value)
    {
        if (_suppressChanges) return;
        SettingsShowFramingVisibilityLimits = value;
        Settings = Settings with
        {
            CameraFraming = Settings.EffectiveCameraFraming with { ShowVisibilityLimits = value }
        };
        OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus));
        OnPropertyChanged(nameof(CameraFramingMapSettings));
        _ = PersistCameraFramingPreferenceAsync();
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
                CameraTerrainBearings(guide, Settings.EffectiveCameraFraming.TerrainCastAngularDetailDegrees), token);
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

    private async Task PersistCameraFramingPreferenceAsync()
    {
        try
        {
            await PersistAsync(CancellationToken.None);
            _logger.LogDebug("Camera framing overlay preference persisted");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera framing overlay preference could not be persisted");
        }
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

    private void ScheduleObserverRefresh(int delayMilliseconds = 80) =>
        SchedulePlannerRefresh(PlannerRefreshScope.Observer, delayMilliseconds);

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
            StatusText = "Calculating celestial positions…"
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
        var priorityTerrainCommit = observerWork?.PriorityTerrain is null
            ? Task.CompletedTask
            : CommitPriorityTerrainAsync(observerWork.PriorityTerrain, coreCommit, requestedSession,
                generation, cancellationToken);
        if (observerWork?.PriorityTerrain is null)
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
                    ? "Planner ready · some environmental data unavailable"
                    : "Planner ready"
            });
            await PersistAsync(cancellationToken);
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
                    StatusText = "Planner update failed: " + ex.Message
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
            var bearings = CameraTerrainBearings(guide,
                Settings.EffectiveCameraFraming.TerrainCastAngularDetailDegrees);
            UpdateRefresh(generation, state => state with
            {
                CameraTerrainState = PlannerRefreshWorkState.Running,
                StatusText = "Planner ready · calculating current view terrain…"
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
                        "Planner ready · refining terrain horizon…"
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

    private static IReadOnlyList<double> CameraTerrainBearings(CameraFramingGuide guide,
        double angularDetailDegrees)
    {
        var detail = Math.Clamp(double.IsFinite(angularDetailDegrees) ? angularDetailDegrees :
                CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees,
            CameraFramingSettings.MinimumTerrainCastAngularDetailDegrees,
            CameraFramingSettings.MaximumTerrainCastAngularDetailDegrees);
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

        if (scope == PlannerRefreshScope.Astronomy && previousSnapshot is not null &&
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
            StatusText = "Updating target directions…"
        });
        Snapshot = core;
        NotifySnapshotProperties();
        UpdateRefresh(generation, state => state with
        {
            CelestialOverlayState = PlannerRefreshWorkState.Ready,
            Phase = PlannerRefreshPhase.UpdatingCameraOverlay,
            StatusText = "Updating camera view…"
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
                GroundTerrainState = PlannerRefreshWorkState.Running,
                EnvironmentMetadataState = PlannerRefreshWorkState.Running,
                StatusText = state.IsCoreReady ? "Planner ready · loading terrain…" : state.StatusText
            });
            var environment = await environmentTask;
            await coreCommit;
            cancellationToken.ThrowIfCancellationRequested();
            await _plannerSnapshotCommitGate.WaitAsync(cancellationToken);
            try
            {
                if (!IsCurrentRefresh(generation, requestedSession.Observer) || Snapshot is null) return;
                var horizon = environment.HorizonProfile;
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
                    GroundTerrainState = EnvironmentWorkState(horizon.HasTerrainCoverage, horizon.GroundHorizonState),
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
                StatusText = state.IsCoreReady ? "Planner ready · loading weather…" : state.StatusText
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
                    ? "Planner ready · refining terrain horizon…"
                    : "Planner ready · loading terrain…"
                : state.StatusText;
        if (IsRefreshWorkLoading(state.WeatherState))
            return state.IsCoreReady ? "Planner ready · loading weather…" : state.StatusText;
        return state.IsCoreReady ? "Applying environmental visibility…" : state.StatusText;
    }

    private void LoadSessionIntoControls()
    {
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
        OnPropertyChanged(nameof(TerrainStatus));
        OnPropertyChanged(nameof(TerrainCurrentLocationText));
        OnPropertyChanged(nameof(GroundHorizonState));
        OnPropertyChanged(nameof(GroundHorizonAngleText));
        OnPropertyChanged(nameof(TerrainDatumText));
        OnPropertyChanged(nameof(ShowTerrainDebugOverlay));
        OnPropertyChanged(nameof(TerrainDebugText));
        OnPropertyChanged(nameof(GroundObstructionText));
    }

    private void ApplyResolvedTerrainGround(TerrainHorizonProfile horizon)
    {
        double? rawTerrainGround = horizon.GroundElevationAtObserver is { HasValue: true } ground
            ? ground.Value : null;
        var terrainGround = _session.EffectiveObserverElevation.IsManualOverride
            ? rawTerrainGround
            : horizon.ChosenObserverGroundElevationMetres ?? rawTerrainGround;
        if (!terrainGround.HasValue) return;

        var state = _session.EffectiveObserverElevation.WithTerrainGroundElevation(terrainGround.Value);
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

    private void NotifyObserverElevationProperties()
    {
        OnPropertyChanged(nameof(IsElevationManualOverride));
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
        Angles.GreatCircleDistanceMetres(coordinate, _session.Observer) <= 2;

    private static bool IsRefreshWorkLoading(PlannerRefreshWorkState state) => state is
        PlannerRefreshWorkState.Pending or PlannerRefreshWorkState.Running;

    private string FormatHorizonState(bool isAvailable, EnvironmentalDataState? state)
    {
        if (CurrentTerrain is null) return "Loading";
        if (isAvailable) return "Ready";
        return state is EnvironmentalDataState.Error or EnvironmentalDataState.InvalidData
            ? "Error"
            : "Unavailable";
    }

    private string FormatObstruction(bool hasData, double? distanceMetres)
    {
        if (CurrentTerrain is null || !hasData) return "—";
        if (!distanceMetres.HasValue) return "Clear";
        var units = Settings.EffectiveMeasurementSystem;
        if (units == MeasurementSystem.Metric && distanceMetres.Value < 1_000)
        {
            var roundedMetres = Math.Round(distanceMetres.Value / 10,
                MidpointRounding.AwayFromZero) * 10;
            return $"{Math.Max(10, roundedMetres):F0} m";
        }
        if (units != MeasurementSystem.Metric && distanceMetres.Value < 1_609.344)
        {
            var feet = distanceMetres.Value * 3.280839895;
            var roundedFeet = Math.Round(feet / 50, MidpointRounding.AwayFromZero) * 50;
            return $"{Math.Max(50, roundedFeet):F0} ft";
        }
        return MeasurementUnits.Visibility(distanceMetres.Value / 1_000, units).Format();
    }

    private static string FormatHorizonAngle(double? angleDegrees) =>
        angleDegrees.HasValue ? $"{angleDegrees.Value:+0.0;-0.0;0.0}°" : "—";

    private string FormatTime(Instant? instant) => instant is null ? "—" : _timeZones.InZone(instant.Value, _session.TimeZoneId).ToString("HH:mm", null);
    private static string Value(double? value, string suffix) => value.HasValue ? $"{value:F0}{suffix}" : "—";
    private static string MoonPhaseName(double angle) => Angles.NormaliseDegrees(angle) switch
    {
        < 22.5 or >= 337.5 => "New Moon",
        < 67.5 => "Waxing crescent",
        < 112.5 => "First quarter",
        < 157.5 => "Waxing gibbous",
        < 202.5 => "Full Moon",
        < 247.5 => "Waning gibbous",
        < 292.5 => "Last quarter",
        _ => "Waning crescent"
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
