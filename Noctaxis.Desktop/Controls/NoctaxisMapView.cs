using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System.Globalization;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.UI.Avalonia;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Terrain;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

public class NoctaxisMapView : UserControl
{
    public static readonly StyledProperty<double> GroundMetresPerPixelProperty =
        AvaloniaProperty.Register<NoctaxisMapView, double>(nameof(GroundMetresPerPixel), double.PositiveInfinity);
    public double GroundMetresPerPixel => GetValue(GroundMetresPerPixelProperty);
    public static readonly StyledProperty<double> ViewportHeightPixelsProperty =
        AvaloniaProperty.Register<NoctaxisMapView, double>(nameof(ViewportHeightPixels), double.PositiveInfinity);
    public double ViewportHeightPixels => GetValue(ViewportHeightPixelsProperty);
    private int _groundScaleUpdatePending;

    private void UpdateGroundScale(object? sender = null, ViewportChangedEventArgs? args = null)
    {
        // Animated Mapsui navigation can notify from the render thread. Both reading
        // Observer and publishing the minimap binding value require the Avalonia UI thread.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            if (Interlocked.Exchange(ref _groundScaleUpdatePending, 1) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    Interlocked.Exchange(ref _groundScaleUpdatePending, 0);
                    if (_attached) UpdateGroundScale(); // Read the latest viewport, not a queued old frame.
                });
            return;
        }
        if (_mapControl.Map is not { } map) return;
        var resolution = map.Navigator.Viewport.Resolution;
        if (double.IsFinite(resolution) && resolution > 0)
        {
            SetValue(GroundMetresPerPixelProperty, resolution * Math.Cos(Observer.Latitude * Angles.DegreesToRadians));
            SetValue(ViewportHeightPixelsProperty, map.Navigator.Viewport.Height);
        }
    }
    public static readonly StyledProperty<GeoCoordinate> ObserverProperty =
        AvaloniaProperty.Register<NoctaxisMapView, GeoCoordinate>(nameof(Observer), new GeoCoordinate(51.5074, -0.1278));
    public static readonly StyledProperty<PlanningSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<NoctaxisMapView, PlanningSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<CameraFramingGuide?> FramingGuideProperty =
        AvaloniaProperty.Register<NoctaxisMapView, CameraFramingGuide?>(nameof(FramingGuide));
    public static readonly StyledProperty<FramingVisibilityAssessment?> FramingVisibilityProperty =
        AvaloniaProperty.Register<NoctaxisMapView, FramingVisibilityAssessment?>(nameof(FramingVisibility));
    public static readonly StyledProperty<CameraFramingSettings> FramingSettingsProperty =
        AvaloniaProperty.Register<NoctaxisMapView, CameraFramingSettings>(nameof(FramingSettings), new CameraFramingSettings());
    public static readonly StyledProperty<PlannerPinActivity> PinActivityProperty =
        AvaloniaProperty.Register<NoctaxisMapView, PlannerPinActivity>(nameof(PinActivity));
    public static readonly StyledProperty<bool> ShowCelestialOverlaysProperty =
        AvaloniaProperty.Register<NoctaxisMapView, bool>(nameof(ShowCelestialOverlays));
    public static readonly StyledProperty<bool> ShowCameraOverlayProperty =
        AvaloniaProperty.Register<NoctaxisMapView, bool>(nameof(ShowCameraOverlay));
    public static readonly StyledProperty<bool> ShowTerrainDebugProperty =
        AvaloniaProperty.Register<NoctaxisMapView, bool>(nameof(ShowTerrainDebug));

    private readonly MapControl _mapControl;
    private readonly PlannerMapComposition _composition;
    private readonly Border _attributionStrip;
    private readonly TextBlock _attribution;
    private readonly Button _attributionInfo;
    private readonly StackPanel _attributionDetails;
    /// <summary>Opens a credit's link. Bound to the application's one external-link launcher.</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> OpenLinkCommandProperty =
        AvaloniaProperty.Register<NoctaxisMapView, System.Windows.Input.ICommand?>(nameof(OpenLinkCommand));
    public System.Windows.Input.ICommand? OpenLinkCommand { get => GetValue(OpenLinkCommandProperty); set => SetValue(OpenLinkCommandProperty, value); }
    /// <summary>
    /// Room the attribution leaves at the map's bottom-left for something the page lays over the map
    /// there, so the two never overlap; the strip wraps into the rest.
    /// </summary>
    public static readonly StyledProperty<double> AttributionReservedLeftProperty =
        AvaloniaProperty.Register<NoctaxisMapView, double>(nameof(AttributionReservedLeft));
    public double AttributionReservedLeft { get => GetValue(AttributionReservedLeftProperty); set => SetValue(AttributionReservedLeftProperty, value); }

    /// <summary>
    /// A secondary view: the Planner's Picture-in-Picture map. It is the same map — same composition,
    /// layers and providers — in a constrained mode: it draws the pin and a simplified camera frame (the
    /// framing outline, without the environmental shading or celestial rays), credits nothing itself
    /// (the main map's strip gives its credits) and takes no input. Its host decides what may reach it.
    /// </summary>
    public static readonly StyledProperty<bool> IsPictureInPictureProperty =
        AvaloniaProperty.Register<NoctaxisMapView, bool>(nameof(IsPictureInPicture));
    public bool IsPictureInPicture { get => GetValue(IsPictureInPictureProperty); set => SetValue(IsPictureInPictureProperty, value); }

    /// <summary>
    /// Whether the view centres on the pin whenever the pin moves. Only the centre follows: the zoom and
    /// the layers stay the view's own. Off, the view keeps whatever centre it has.
    /// </summary>
    public static readonly StyledProperty<bool> FollowObserverProperty =
        AvaloniaProperty.Register<NoctaxisMapView, bool>(nameof(FollowObserver), true);
    public bool FollowObserver { get => GetValue(FollowObserverProperty); set => SetValue(FollowObserverProperty, value); }

    /// <summary>
    /// Whether the view draws the basemap's reference pass: its roads, railways, boundaries and names.
    /// Off, that pass is gated out of this view's composition, so its tiles are neither fetched nor drawn;
    /// the geography, the environmental layers and the overlay stay. Each view has its own.
    /// </summary>
    public static readonly StyledProperty<bool> ShowRoadsAndLabelsProperty =
        AvaloniaProperty.Register<NoctaxisMapView, bool>(nameof(ShowRoadsAndLabels), true);
    public bool ShowRoadsAndLabels { get => GetValue(ShowRoadsAndLabelsProperty); set => SetValue(ShowRoadsAndLabelsProperty, value); }

    /// <summary>
    /// Whether this view draws its terrain relief in the stronger tone
    /// (<see cref="TerrainReliefTileSource.ReliefTone.Enhanced"/>): the Picture-in-Picture map's local contrast.
    /// Only this view's relief tiles are redrawn, from the shared shaded grid; nothing else changes.
    /// </summary>
    public static readonly StyledProperty<bool> EnhanceReliefContrastProperty =
        AvaloniaProperty.Register<NoctaxisMapView, bool>(nameof(EnhanceReliefContrast));
    public bool EnhanceReliefContrast { get => GetValue(EnhanceReliefContrastProperty); set => SetValue(EnhanceReliefContrastProperty, value); }
    private TerrainReliefTileSource.ReliefTone ReliefTone =>
        EnhanceReliefContrast ? TerrainReliefTileSource.ReliefTone.Enhanced : TerrainReliefTileSource.ReliefTone.Normal;

    /// <summary>A map view's own state: where it is centred and how far it is zoomed, as ground metres per pixel.</summary>
    public readonly record struct MapViewState(GeoCoordinate Centre, double Resolution);

    /// <summary>The zoom range every Planner view keeps to.</summary>
    public const double MinimumResolution = PlannerPipSettings.MinimumResolutionMetresPerPixel;
    public const double MaximumResolution = PlannerPipSettings.MaximumResolutionMetresPerPixel;

    private MapViewState? _pendingView;

    /// <summary>Where the view is now, once it has a viewport; otherwise the view it will open on, if one was given.</summary>
    public MapViewState? CurrentView
    {
        get
        {
            if (!_attached || _mapControl.Map?.Navigator.Viewport is not { Resolution: > 0 } viewport) return _pendingView;
            return new(WebMercator.ToWgs84(viewport.CenterX, viewport.CenterY), viewport.Resolution);
        }
    }

    /// <summary>
    /// Shows <paramref name="view"/>: its zoom always, and its centre unless the view follows the pin, in
    /// which case the pin is the centre. <paramref name="centreOnPin"/> overrides that for this once: the
    /// main map, which always follows the pin, still takes a switched-in centre until the pin next moves.
    /// Before the view is on screen it is kept and shown on arrival.
    /// </summary>
    public void ShowView(MapViewState view, bool? centreOnPin = null)
    {
        var resolution = Math.Clamp(double.IsFinite(view.Resolution) && view.Resolution > 0 ? view.Resolution : 650, MinimumResolution, MaximumResolution);
        _pendingView = view with { Resolution = resolution };
        if (!_attached || _mapControl.Map is null) return;
        var centre = WebMercator.FromWgs84(centreOnPin ?? FollowObserver ? Observer : view.Centre);
        _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(centre.X, centre.Y), resolution, 0);
        _overlay.InvalidateVisual();
    }

    /// <summary>
    /// Zooms in (positive) or out (negative) by whole steps of two, about the view's own centre, within
    /// the Planner's zoom range. Returns whether the zoom changed.
    /// </summary>
    public bool Zoom(int steps)
    {
        if (steps == 0 || !_attached || _mapControl.Map?.Navigator is not { } navigator || navigator.Viewport.Resolution <= 0) return false;
        var current = navigator.Viewport.Resolution;
        var next = Math.Clamp(current / Math.Pow(2, steps), MinimumResolution, MaximumResolution);
        if (Math.Abs(next - current) < current * 1e-9) return false;
        navigator.ZoomTo(next, 0);
        // The map has limits of its own: at one, nothing changed.
        if (Math.Abs(navigator.Viewport.Resolution - current) < current * 1e-9) return false;
        _pendingView = CurrentView;
        _overlay.InvalidateVisual();
        return true;
    }

    private NoctaxisMapView? _attributionCompanion;
    /// <summary>
    /// Another view whose credits this one's attribution strip gives as well: the Picture-in-Picture map,
    /// which credits nothing itself. Each credit is still given once.
    /// </summary>
    public NoctaxisMapView? AttributionCompanion
    {
        get => _attributionCompanion;
        set
        {
            if (ReferenceEquals(_attributionCompanion, value)) return;
            if (_attributionCompanion is { } previous) previous._composition.Runtime.AttributionChanged -= RefreshAttribution;
            _attributionCompanion = value;
            if (value is not null) value._composition.Runtime.AttributionChanged += RefreshAttribution;
            RefreshAttribution();
        }
    }
    private LightPollutionMapBinding? _lightPollution;
    public static readonly StyledProperty<LightPollutionPreferences> LightPollutionPreferencesProperty =
        AvaloniaProperty.Register<NoctaxisMapView, LightPollutionPreferences>(nameof(LightPollutionPreferences), new());
    public LightPollutionPreferences LightPollutionPreferences
    {
        get => GetValue(LightPollutionPreferencesProperty);
        set => SetValue(LightPollutionPreferencesProperty, value);
    }
    /// <summary>This map's reader of the installed atlas, which the shot-viability score shares.</summary>
    internal Noctaxis.Core.LightPollution.LightPollutionDataProvider? LightPollutionProvider => _lightPollution?.Provider;
    public void ConfigureLightPollution(Noctaxis.Core.LightPollution.LorenzInstallation installation)
    {
        if (_lightPollution is not null) throw new InvalidOperationException("Light Pollution is already configured.");
        _lightPollution = new(_composition, installation);
        _lightPollution.SetPalette(LightPollutionPreferences.PaletteId);
        if (_attached) _lightPollution.Attach();
    }
    private TerrainReliefMapBinding? _terrainRelief;
    public static readonly StyledProperty<TerrainReliefPreferences> TerrainReliefPreferencesProperty =
        AvaloniaProperty.Register<NoctaxisMapView, TerrainReliefPreferences>(nameof(TerrainReliefPreferences), new());
    public TerrainReliefPreferences TerrainReliefPreferences
    {
        get => GetValue(TerrainReliefPreferencesProperty);
        set => SetValue(TerrainReliefPreferencesProperty, value);
    }
    // The theme's factor on terrain relief (MapThemeTokens.TerrainReliefStrength): High Contrast weakens it.
    private static readonly StyledProperty<double> TerrainReliefStrengthProperty =
        AvaloniaProperty.Register<NoctaxisMapView, double>("TerrainReliefStrength", 1);
    internal double TerrainReliefStrengthForTesting => GetValue(TerrainReliefStrengthProperty);
    internal TerrainReliefMapBinding? TerrainReliefForTesting => _terrainRelief;

    /// <summary>Shows terrain relief from the terrain calculations' own Terrarium source.</summary>
    public void ConfigureTerrainRelief(Noctaxis.Core.Environment.ITerrariumTileSource elevation)
    {
        if (_terrainRelief is not null) throw new InvalidOperationException("Terrain relief is already configured.");
        if (!_composition.Composition.Layers.Any(x => x.Id == TerrainReliefMapBinding.LayerId)) return;
        _terrainRelief = new(_composition, elevation, () => GetValue(TerrainReliefStrengthProperty));
        _terrainRelief.SetTone(ReliefTone);
    }

    /// <summary>
    /// Shows the environmental layers <paramref name="primary"/> is configured with, from its own sources:
    /// the Picture-in-Picture map borrows the main map's terrain relief source (shaded relief, computed
    /// and cached once) and light pollution reader (the decoded atlas, read and held once). The layers,
    /// their state and their drawn tiles stay this view's. <paramref name="primary"/> must outlive it.
    /// </summary>
    internal void ShareEnvironmentalSources(NoctaxisMapView primary)
    {
        if (primary._terrainRelief is { } terrain && _terrainRelief is null &&
            _composition.Composition.Layers.Any(x => x.Id == TerrainReliefMapBinding.LayerId))
        {
            _terrainRelief = new(_composition, terrain.Source, () => GetValue(TerrainReliefStrengthProperty));
            _terrainRelief.SetTone(ReliefTone);
        }
        if (primary._lightPollution is { } light && _lightPollution is null)
        {
            _lightPollution = new(_composition, light.Installation, light.Provider);
            _lightPollution.SetPalette(LightPollutionPreferences.PaletteId);
            if (_attached) _lightPollution.Attach();
        }
    }

    internal LightPollutionMapBinding? LightPollutionForTesting => _lightPollution;

    // Final window close only; ordinary visual detachment must remain reattachable.
    public async ValueTask ReleaseLightPollutionAsync()
    {
        if (_lightPollution is not null) await _lightPollution.DisposeAsync();
        _mapControl.Dispose();
    }
    private readonly MapOverlay _overlay;
    private readonly DispatcherTimer _renderTimer;
    private readonly ViewportRedrawScheduler _viewportRedraw;
    private bool _attached;
    private bool _draggingPin;
    private bool _committingPin;
    private GeoCoordinate? _contextCoordinate;
    private bool _hasPendingPointerContextCoordinate;
    private readonly PlanningPinInteractionState _pinInteraction;
    private ViewportSignature? _lastViewportSignature;

    public NoctaxisMapView() : this(new PlannerMapComposition()) { }

    public NoctaxisMapView(PlannerMapComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        _composition = composition;
        var map = _composition.Map;
        _mapControl = new MapControl { Map = map };
        var setPinMenuItem = new MenuItem { Header = Strings.Get("Planner_Map_SetPinHere_MenuItem") };
        setPinMenuItem.Click += (_, _) =>
        {
            if (_contextCoordinate is { } coordinate) CommitExplicitCoordinate(coordinate);
        };
        var savePinMenuItem = new MenuItem { Header = Strings.Get("Planner_Map_SaveCurrentPin_MenuItem") };
        savePinMenuItem.Click += (_, _) => SaveCurrentPinRequested?.Invoke(this, EventArgs.Empty);
        var openMapsMenuItem = new MenuItem { Header = Strings.Get("Planner_Location_OpenMaps_Button") };
        openMapsMenuItem.Click += (_, _) =>
        {
            if (_contextCoordinate is { } coordinate) OpenInMapsRequested?.Invoke(this, coordinate.Normalised());
        };
        _mapControl.ContextMenu = new ContextMenu { ItemsSource = new object[] { setPinMenuItem, savePinMenuItem, openMapsMenuItem } };
        // Capture the coordinate before Avalonia transfers input to the context menu. ContextRequested
        // does not reliably retain a pointer position on every supported platform.
        _mapControl.AddHandler(PointerPressedEvent, MapPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _mapControl.ContextRequested += MapContextRequested;
        _pinInteraction = new PlanningPinInteractionState(Observer);
        _overlay = new MapOverlay(() => _mapControl.Map?.Navigator.Viewport)
        {
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        var attribution = _attributionStrip = CreateAttributionStrip(out _attribution, out _attributionInfo, out _attributionDetails);
        RefreshAttribution();
        // The map canvas shows before tiles arrive and beyond their extent. Mapsui paints its own opaque
        // Map.BackColor (white by default) over the control background, so both follow MapSurface.
        this.BindThemeDefault(BackgroundProperty, MapThemeTokens.Surface);
        this.BindThemeDefault(MapSurfaceColourProperty, MapThemeTokens.ColourKey(MapThemeTokens.Surface));
        _overlay.BindThemeDefault(MapOverlay.CelestialColoursProperty, CelestialThemeTokens.ColourSet);
        _overlay.BindThemeDefault(MapOverlay.LineHaloProperty, MapThemeTokens.ColourKey(MapThemeTokens.LabelHalo));
        _overlay.BindThemeDefault(MapOverlay.WeatherMarkProperty, MapThemeTokens.ColourKey(MapThemeTokens.Label));
        _overlay.BindThemeDefault(MapOverlay.PinFillProperty, MapThemeTokens.ColourKey(MapThemeTokens.PinFill));
        _overlay.BindThemeDefault(MapOverlay.PinOutlineProperty, MapThemeTokens.ColourKey(MapThemeTokens.PinOutline));
        _overlay.BindThemeDefault(MapOverlay.PinActivityColourProperty, MapThemeTokens.ColourKey(MapThemeTokens.PinActivity));
        // The vector basemap is drawn from the theme's basemap roles and restyled live.
        foreach (var (property, token) in BasemapRoleProperties.Zip(MapThemeTokens.Basemap))
            this.BindThemeDefault(property, MapThemeTokens.ColourKey(token));
        this.BindThemeDefault(TerrainReliefStrengthProperty, MapThemeTokens.TerrainReliefStrength);
        ApplyBasemapStyle();
        // Place names follow the application language: there is no separate map-language setting.
        _composition.ApplyLabelLanguage(AppLanguages.Current);
        Content = new Grid { Children = { _mapControl, _overlay, attribution } };

        _composition.Runtime.SetActive("camera-framing", ShowCameraOverlay);
        _composition.Runtime.SetActive("celestial-rays", ShowCelestialOverlays && !IsPictureInPicture);
        UpdateEnvironmentalLayerGate();
        foreach (var id in new[] { "environmental-shading", "camera-framing", "celestial-rays", "observer-pin" })
            _composition.Runtime.BindAdapter(id, new DelegateMapLayerStateAdapter(ApplyOverlayLayerState));

        AddHandler(PointerPressedEvent, PinPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, PinPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, PinPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, PinCaptureLost, RoutingStrategies.Tunnel);

        _viewportRedraw = new ViewportRedrawScheduler(
            Dispatcher.UIThread.CheckAccess,
            action => Dispatcher.UIThread.Post(action, DispatcherPriority.Render),
            () =>
            {
                _overlay.SyncProbe?.Invalidated();
                _overlay.InvalidateVisual();
            });
        _overlay.RenderStarting = _viewportRedraw.RenderStarting;
        // Animation keeps its cadence; navigation has no polling timer in production.
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) => TickOverlayAnimation();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            _lightPollution?.Attach();
            _composition.Runtime.AttributionChanged += RefreshAttribution;
            AppLanguages.CurrentChanged += ApplyLabelLanguage;
            ApplyLabelLanguage(AppLanguages.Current);
            RefreshAttribution();
            _viewportRedraw.Attach();
            map.Navigator.ViewportChanged += UpdateGroundScale;
            map.Navigator.ViewportChanged += ViewportChanged;
            _pinInteraction.SetCommittedCoordinate(Observer);
            UpdateOverlay();
            if (_pendingView is { } view) ShowView(view);
            else CenterOn(Observer);
            UpdateGroundScale();
            UpdateAnimationTimer();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _lightPollution?.Detach();
            _attributionInfo.Flyout?.Hide();
            _composition.Runtime.AttributionChanged -= RefreshAttribution;
            AppLanguages.CurrentChanged -= ApplyLabelLanguage;
            _viewportRedraw.Detach();
            _renderTimer.Stop();
            _lastViewportSignature = null;
            map.Navigator.ViewportChanged -= UpdateGroundScale;
            map.Navigator.ViewportChanged -= ViewportChanged;
            _overlay.ReleaseRenderer();
        };
    }

    public GeoCoordinate Observer { get => GetValue(ObserverProperty); set => SetValue(ObserverProperty, value); }
    public PlanningSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public CameraFramingGuide? FramingGuide { get => GetValue(FramingGuideProperty); set => SetValue(FramingGuideProperty, value); }
    public FramingVisibilityAssessment? FramingVisibility { get => GetValue(FramingVisibilityProperty); set => SetValue(FramingVisibilityProperty, value); }
    public CameraFramingSettings FramingSettings { get => GetValue(FramingSettingsProperty); set => SetValue(FramingSettingsProperty, value); }
    public PlannerPinActivity PinActivity { get => GetValue(PinActivityProperty); set => SetValue(PinActivityProperty, value); }
    public bool ShowCelestialOverlays { get => GetValue(ShowCelestialOverlaysProperty); set => SetValue(ShowCelestialOverlaysProperty, value); }
    public bool ShowCameraOverlay { get => GetValue(ShowCameraOverlayProperty); set => SetValue(ShowCameraOverlayProperty, value); }
    public bool ShowTerrainDebug { get => GetValue(ShowTerrainDebugProperty); set => SetValue(ShowTerrainDebugProperty, value); }
    public event EventHandler<GeoCoordinate>? PreviewCoordinateChanged;
    public event EventHandler<GeoCoordinate>? CoordinateCommitted;
    public event EventHandler<bool>? InteractionStateChanged;
    public event EventHandler? SaveCurrentPinRequested;
    public event EventHandler<GeoCoordinate>? OpenInMapsRequested;

    internal MapControl MapControlForTesting => _mapControl;
    protected MapLayerStateController LayerRuntime => _composition.Runtime;
    internal PlannerMapComposition CompositionForTesting => _composition;

    /// <summary>The themed vector basemap this view draws, for a clean export map; null while imagery is shown.</summary>
    internal Mapping.Vector.ThemedVectorTileSource? VectorBasemapSource =>
        (_composition.ActiveBasemap as Mapping.Vector.OpenFreeMapBasemap)?.Source;
    internal string? AttributionForTesting => _attribution.Text;
    internal TextBlock AttributionControlForTesting => _attribution;
    internal Border AttributionStripForTesting => _attributionStrip;
    internal Button AttributionInfoForTesting => _attributionInfo;
    internal StackPanel AttributionDetailsForTesting => _attributionDetails;
    internal (Color Fill, Color Outline, Color Activity) PinColoursForTesting =>
        (_overlay.PinFill, _overlay.PinOutline, _overlay.PinActivityColour);
    internal CelestialColourSet CelestialColoursForTesting => _overlay.CelestialColours;
    internal bool EnvironmentalLayerVisibleForTesting => _overlay.ShowEnvironmentalShading;
    internal bool CameraLayerVisibleForTesting => _overlay.ShowCameraOverlay;
    internal bool CelestialLayerVisibleForTesting => _overlay.ShowCelestialOverlays;
    internal Control OverlayForTesting => _overlay;
    internal PlannerPinProbe? PinProbe { get => _overlay.Probe; set { _overlay.Probe = value; UpdateAnimationTimer(); } }
    internal OverlaySynchronizationProbe? SyncProbe
    {
        get => _overlay.SyncProbe;
        set { _overlay.SyncProbe = value; _lastViewportSignature = null; UpdateAnimationTimer(); }
    }
    internal bool AnimationTimerEnabled => _renderTimer.IsEnabled;
    internal void TickAnimationForTesting() => TickOverlayAnimation();
    internal Point? PinScreenPointForTesting => _overlay.PinScreenPoint;
    internal EnvironmentalOverlayState? EnvironmentalStateForTesting => _overlay.EnvironmentalStateForTesting;

    public void CenterOn(GeoCoordinate coordinate)
    {
        if (!_attached || _mapControl.Map is null) return;
        var world = WebMercator.FromWgs84(coordinate);
        var resolution = _mapControl.Map.Navigator.Viewport.Resolution;
        _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(world.X, world.Y), resolution > 0 ? resolution : 650, 0);
        _overlay.InvalidateVisual();
    }

    private static readonly StyledProperty<Color> MapSurfaceColourProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("MapSurfaceColour", Colors.Transparent);

    private void ApplyMapSurface()
    {
        var colour = GetValue(MapSurfaceColourProperty);
        _composition.Map.BackColor = new Mapsui.Styles.Color(colour.R, colour.G, colour.B, colour.A);
        _mapControl?.RefreshGraphics();
    }

    internal Mapsui.Styles.Color MapBackColorForTesting => _composition.Map.BackColor;

    // One colour per MapThemeTokens.Basemap role (same order), bound to the theme's colour resources.
    private static readonly StyledProperty<Color> BasemapLandProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapLand");
    private static readonly StyledProperty<Color> BasemapBuiltUpProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapBuiltUp");
    private static readonly StyledProperty<Color> BasemapBuildingProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapBuilding");
    private static readonly StyledProperty<Color> BasemapWaterProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapWater");
    private static readonly StyledProperty<Color> BasemapVegetationProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapVegetation");
    private static readonly StyledProperty<Color> BasemapRoadMinorProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapRoadMinor");
    private static readonly StyledProperty<Color> BasemapRoadMajorProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapRoadMajor");
    private static readonly StyledProperty<Color> BasemapRoadCasingProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapRoadCasing");
    private static readonly StyledProperty<Color> BasemapRailProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapRail");
    private static readonly StyledProperty<Color> BasemapBoundaryProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapBoundary");
    private static readonly StyledProperty<Color> BasemapLabelProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapLabel");
    private static readonly StyledProperty<Color> BasemapLabelHaloProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapLabelHalo");
    private static readonly StyledProperty<Color> BasemapWaterLabelProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapWaterLabel");
    private static readonly StyledProperty<Color> BasemapPoiProperty = AvaloniaProperty.Register<NoctaxisMapView, Color>("BasemapPoi");
    private static readonly StyledProperty<Color>[] BasemapRoleProperties =
    [
        BasemapLandProperty, BasemapBuiltUpProperty, BasemapBuildingProperty, BasemapWaterProperty,
        BasemapVegetationProperty, BasemapRoadMinorProperty, BasemapRoadMajorProperty, BasemapRoadCasingProperty,
        BasemapRailProperty, BasemapBoundaryProperty, BasemapLabelProperty, BasemapLabelHaloProperty,
        BasemapWaterLabelProperty, BasemapPoiProperty
    ];
    private static readonly HashSet<AvaloniaProperty> BasemapRoleSet = BasemapRoleProperties.Length == MapThemeTokens.Basemap.Count &&
        BasemapRoleProperties.Zip(MapThemeTokens.Basemap).All(pair => "Basemap" + pair.Second[3..] == pair.First.Name)
            ? [.. BasemapRoleProperties]
            : throw new InvalidOperationException("Basemap role properties must mirror MapThemeTokens.Basemap.");
    private bool _basemapStylePending;

    /// <summary>Resolves the bound roles into a style; unresolved roles keep the Noctaxis defaults.</summary>
    internal Mapping.Vector.BasemapStyle ResolveBasemapStyle()
    {
        var roles = BasemapRoleProperties.Zip(MapThemeTokens.Basemap)
            .ToDictionary(pair => pair.Second, pair => pair.First);
        return Mapping.Vector.BasemapStyle.FromTokens(token => roles.TryGetValue(token, out var property) && IsSet(property)
            ? ToSkia(GetValue(property)) : null);
    }

    private static SkiaSharp.SKColor ToSkia(Color colour) => new(colour.R, colour.G, colour.B, colour.A);

    // Raised by whichever thread changed the language; the map is only touched on the UI thread.
    private void ApplyLabelLanguage(string language)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => ApplyLabelLanguage(language)); return; }
        if (_composition.ApplyLabelLanguage(language)) _mapControl.RefreshGraphics();
    }

    private void ApplyBasemapStyle()
    {
        _basemapStylePending = false;
        _composition?.ApplyBasemapStyle(ResolveBasemapStyle());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AttributionReservedLeftProperty)
        {
            _attributionStrip.Margin = new Thickness(8 + Math.Max(0, AttributionReservedLeft), 8, 8, 8);
            return;
        }
        if (change.Property == MapSurfaceColourProperty)
        {
            if (_composition is not null) ApplyMapSurface();
            return;
        }
        if (BasemapRoleSet.Contains(change.Property))
        {
            // A theme switch changes every role at once: restyle once, after the whole batch.
            if (!_basemapStylePending && _composition is not null)
            {
                _basemapStylePending = true;
                Dispatcher.UIThread.Post(ApplyBasemapStyle);
            }
            return;
        }
        if (change.Property == TerrainReliefPreferencesProperty && _composition is not null)
            TerrainReliefMapBinding.ApplyPreferences(_composition, TerrainReliefPreferences);
        if (change.Property == TerrainReliefStrengthProperty) _terrainRelief?.ThemeStrengthChanged();
        if (change.Property == LightPollutionPreferencesProperty && _composition is not null)
        {
            LightPollutionMapBinding.ApplyPreferences(_composition, LightPollutionPreferences);
            _lightPollution?.SetPalette(LightPollutionPreferences.PaletteId);
        }
        if (change.Property == ObserverProperty && _mapControl is not null) UpdateGroundScale();
        if (change.Property == SnapshotProperty)
        {
            _overlay.Snapshot = Snapshot;
            _overlay.InvalidateVisual();
        }
        else if (change.Property == FramingGuideProperty)
        {
            _overlay.FramingGuide = FramingGuide;
            _overlay.InvalidateVisual();
        }
        else if (change.Property == FramingVisibilityProperty)
        {
            var changed = (_overlay.FramingVisibility is null) != (FramingVisibility is null) ||
                _overlay.FramingVisibility?.CameraDepth != FramingVisibility?.CameraDepth ||
                _overlay.FramingVisibility?.WeatherVisibilityDistanceMetres != FramingVisibility?.WeatherVisibilityDistanceMetres;
            _overlay.FramingVisibility = FramingVisibility;
            if (changed) _overlay.InvalidateVisual();
        }
        else if (change.Property == FramingSettingsProperty)
        {
            var settings = FramingSettings.Normalised();
            if ((_overlay.FramingSettings with { CameraPitchDegrees = settings.CameraPitchDegrees,
                    MinimumTerrainFrameCoveragePercent = settings.MinimumTerrainFrameCoveragePercent }) == settings)
                return;
            _overlay.FramingSettings = settings;
            UpdateEnvironmentalLayerGate();
            _overlay.InvalidateVisual();
        }
        else if (change.Property == ObserverProperty)
        {
            _pinInteraction.SetCommittedCoordinate(Observer);
            UpdateOverlay();
            // Only a horizontal move is a new place to follow. Resolving the ground elevation replaces
            // Observer at the same latitude and longitude, seconds after a pin commit; that must neither
            // pull a map the user has since panned back to the pin nor consume the pending commit.
            var previous = change.GetOldValue<GeoCoordinate>();
            if (previous.Latitude != Observer.Latitude || previous.Longitude != Observer.Longitude)
            {
                if (_attached && !_committingPin && FollowObserver) CenterOn(Observer);
                _committingPin = false;
            }
        }
        else if (change.Property == PinActivityProperty)
        {
            _overlay.PinActivity = PinActivity;
            UpdatePinProbe();
            UpdateAnimationTimer();
            _overlay.InvalidateVisual();
        }
        else if (change.Property == IsPictureInPictureProperty)
        {
            // A secondary view is looked at, not worked: no input, no credits of its own, and only the
            // lightweight part of the camera overlay.
            IsHitTestVisible = !IsPictureInPicture;
            _attributionStrip.IsVisible = !IsPictureInPicture;
            _composition!.Runtime.SetActive("celestial-rays", ShowCelestialOverlays && !IsPictureInPicture);
            UpdateEnvironmentalLayerGate();
        }
        else if (change.Property == FollowObserverProperty)
        {
            if (FollowObserver && _attached) CenterOn(Observer);
        }
        else if (change.Property == ShowRoadsAndLabelsProperty)
        {
            _composition!.Runtime.SetActive(PlannerMapComposition.BasemapReferenceId, ShowRoadsAndLabels);
        }
        else if (change.Property == EnhanceReliefContrastProperty)
        {
            _terrainRelief?.SetTone(ReliefTone);
        }
        else if (change.Property == ShowCelestialOverlaysProperty)
        {
            _composition!.Runtime.SetActive("celestial-rays", ShowCelestialOverlays && !IsPictureInPicture);
        }
        else if (change.Property == ShowCameraOverlayProperty)
        {
            _composition!.Runtime.SetActive("camera-framing", ShowCameraOverlay);
        }
        else if (change.Property == ShowTerrainDebugProperty)
        {
            _overlay.ShowTerrainDebug = ShowTerrainDebug;
            _overlay.InvalidateVisual();
        }
    }

    private void PinPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(_overlay);
        if (e.ClickCount == 2 && TryCoordinateFromScreen(point, out var doubleClickCoordinate))
        {
            CommitExplicitCoordinate(doubleClickCoordinate);
            e.Handled = true;
            return;
        }
        var pin = _overlay.PinScreenPoint;
        if (pin is null || Distance(point, pin.Value) > 24) return;
        _draggingPin = true;
        _pinInteraction.BeginDrag();
        e.Pointer.Capture(this);
        e.Handled = true;
        InteractionStateChanged?.Invoke(this, true);
    }

    private void PinPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingPin) return;
        var point = e.GetPosition(_overlay);
        var viewport = _mapControl.Map?.Navigator.Viewport;
        if (!viewport.HasValue) return;
        var world = viewport.Value.ScreenToWorld(point.X, point.Y);
        // A newly selected map coordinate has no resolved terrain datum yet. Carrying the previous
        // pin's elevation makes an unrelated mountain, quarry or ocean fallback appear authoritative.
        _pinInteraction.UpdateDrag(WebMercator.ToWgs84(world.X, world.Y) with { ElevationMetres = 0 });
        UpdateOverlay();
        PreviewCoordinateChanged?.Invoke(this, _pinInteraction.PreviewCoordinate);
        e.Handled = true;
    }

    private void PinPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_draggingPin) return;
        _draggingPin = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        RaiseCoordinateCommitted(_pinInteraction.CompleteDrag());
        InteractionStateChanged?.Invoke(this, false);
    }

    private void PinCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_draggingPin) return;
        _draggingPin = false;
        _pinInteraction.CancelDrag();
        UpdateOverlay();
        InteractionStateChanged?.Invoke(this, false);
    }

    private void UpdateOverlay()
    {
        _overlay.Observer = _pinInteraction.PreviewCoordinate;
        UpdatePinProbe();
        _overlay.InvalidateVisual();
    }

    /// <summary>
    /// The map's one attribution surface: a compact line naming every provider whose content is drawn
    /// now, and an info button opening Map data &amp; licences, which gives each of those credits in full
    /// with its links. Both follow the active credits, and change only when they do.
    /// </summary>
    /// <summary>The credits this strip gives: this view's own, then its companion's, each once.</summary>
    internal IReadOnlyList<MapAttribution> StripAttributions => _attributionCompanion is { } companion
        ? MapAttributions.Distinct(_composition.Runtime.ActiveAttributions.Concat(companion._composition.Runtime.ActiveAttributions))
        : _composition.Runtime.ActiveAttributions;

    private void RefreshAttribution(object? sender = null, EventArgs? args = null)
    {
        var credits = StripAttributions;
        _attribution.Text = MapAttributions.Line(credits);
        _attributionDetails.Children.RemoveRange(1, _attributionDetails.Children.Count - 1);
        foreach (var credit in credits)
        {
            var links = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, ItemSpacing = 12 };
            if (credit.Url is { } url) links.Children.Add(AttributionLink(DisplayAddress(url), url));
            if (credit.LicenceUrl is { } licence && credit.LicenceName is { } licenceName)
                links.Children.Add(AttributionLink(Strings.Format("Planner_Map_Licence_Format", licenceName), licence));
            _attributionDetails.Children.Add(new StackPanel
            {
                Tag = credit.Id,
                Children = { new TextBlock { Text = credit.Credit, TextWrapping = TextWrapping.Wrap }, links }
            });
        }
    }

    private Border CreateAttributionStrip(out TextBlock line, out Button info, out StackPanel details)
    {
        line = new TextBlock
        {
            Text = _composition.Runtime.AttributionText,
            // Attribution is a licence obligation, so it wraps rather than running off the map: the map
            // is only as wide as the panels around it leave it, and at a large text size that is narrow.
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 3)
        };
        line.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MapChromeText"));
        line.Bind(TextBlock.FontSizeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TextSmall"));
        details = new StackPanel { Spacing = 8, MaxWidth = 320 };
        details.Children.Add(new TextBlock { Text = Strings.Get("Planner_Map_Licences_Heading"), FontWeight = FontWeight.SemiBold });
        // Small to look at, never small to hit; a click, a tap, Enter or Space opens it.
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Width = 13, Height = 13, Stretch = Stretch.Uniform,
            Data = Geometry.Parse("F0 M12,2 A10,10 0 1 1 12,22 A10,10 0 1 1 12,2 Z M12,3.8 A8.2,8.2 0 1 0 12,20.2 A8.2,8.2 0 1 0 12,3.8 Z M10.9,10 L13.1,10 L13.1,17.5 L10.9,17.5 Z M10.9,6.2 L13.1,6.2 L13.1,8.4 L10.9,8.4 Z")
        };
        glyph.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MapChromeText"));
        info = new Button
        {
            Name = "MapAttributionInfo", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2), MinWidth = 32, MinHeight = 28,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Content = glyph,
            Flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, Content = details }
        };
        var name = Strings.Get("Planner_Map_Licences_AccessibleName");
        Avalonia.Automation.AutomationProperties.SetName(info, name);
        // The icon alone does not say what it opens.
        ToolTip.SetTip(info, name);
        Grid.SetColumn(info, 1);
        var strip = new Border
        {
            Name = "MapAttribution", CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 0, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Margin = new Thickness(8),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { line, info } }
        };
        strip.Classes.Add("mapChrome");
        return strip;
    }

    private Button AttributionLink(string text, Uri url)
    {
        var content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextDecorations = TextDecorations.Underline };
        content.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Accent"));
        var link = new Button
        {
            Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 4), MinHeight = 28, Tag = url, Cursor = new Cursor(StandardCursorType.Hand)
        };
        link.Click += (_, _) =>
        {
            if (OpenLinkCommand is { } command && command.CanExecute(url)) command.Execute(url);
        };
        return link;
    }

    /// <summary>A link's address as its own label: no scheme, no "www.", no trailing slash.</summary>
    internal static string DisplayAddress(Uri url) =>
        (url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host) + url.AbsolutePath.TrimEnd('/');

    private void UpdateEnvironmentalLayerGate() =>
        _composition.Runtime.SetActive("environmental-shading",
            !IsPictureInPicture && _composition.Runtime["camera-framing"].IsRenderable && _overlay.FramingSettings.ShadingOpacityPercent > 0);

    // One control-plane adapter for the existing shared surface. Draw order and renderer
    // ownership stay in MapOverlay. Existing readiness bindings remain authoritative gates.
    private void ApplyOverlayLayerState(MapLayerState state)
    {
        switch (state.Id)
        {
            case "camera-framing":
                if (_overlay.ShowCameraOverlay != state.IsRenderable)
                {
                    _overlay.ShowCameraOverlay = state.IsRenderable;
                    _overlay.InvalidateCameraGeometry();
                }
                UpdateEnvironmentalLayerGate();
                break;
            case "celestial-rays":
                if (_overlay.ShowCelestialOverlays != state.IsRenderable)
                {
                    _overlay.ShowCelestialOverlays = state.IsRenderable;
                    _overlay.InvalidateCelestialGeometry();
                }
                break;
            case "environmental-shading":
                if (_overlay.ShowEnvironmentalShading == state.IsRenderable) break;
                _overlay.ShowEnvironmentalShading = state.IsRenderable;
                _overlay.InvalidateVisual();
                break;
            case "observer-pin":
                if (_overlay.ShowObserverPin == state.IsRenderable) break;
                _overlay.ShowObserverPin = state.IsRenderable;
                _overlay.InvalidateVisual();
                break;
        }
    }

    private void UpdatePinProbe()
    {
        if (_overlay.Probe is not { } probe) return;
        probe.Poll(false, _draggingPin, _overlay.Observer, _overlay.PinActivity);
        if (probe.Mode == PlannerPinMode.Layer) _mapControl.Map?.RefreshGraphics();
    }

    private void ViewportChanged(object? sender, ViewportChangedEventArgs args)
    {
        // Use the current viewport at Render time, not a queued frame's coordinates.
        _overlay.SyncProbe?.ViewportChanged(_mapControl.Map!.Navigator.Viewport);
        if (_overlay.SyncProbe?.UsePolling == true) return; // Historical A/B diagnostic only.
        if (!_viewportRedraw.Request()) _overlay.SyncProbe?.Coalesced();
    }

    private void UpdateAnimationTimer()
    {
        if (_renderTimer is null) return;
        var needed = _attached && (PinActivity != PlannerPinActivity.None ||
            _overlay.Probe is not null || _overlay.SyncProbe?.UsePolling == true);
        if (needed) _renderTimer.Start();
        else _renderTimer.Stop();
    }

    private void TickOverlayAnimation()
    {
        if (!_attached) return;
        var changed = false;
        var visibilityChanged = false;
        // Retain the first investigation's comparison modes, never enabled by the product.
        if (_overlay.Probe is not null || _overlay.SyncProbe?.UsePolling == true)
        {
            if (_mapControl.Map is not { } map) return;
            var signature = ViewportSignature.From(map.Navigator.Viewport);
            changed = _lastViewportSignature != signature;
            _lastViewportSignature = signature;
            visibilityChanged = _overlay.Probe?.Poll(changed, _draggingPin, _overlay.Observer, _overlay.PinActivity) == true;
        }
        if (_overlay.Probe?.Mode == PlannerPinMode.Layer && PinActivity != PlannerPinActivity.None)
            _mapControl.Map?.RefreshGraphics();
        if (changed || visibilityChanged || PinActivity != PlannerPinActivity.None)
        {
            _overlay.Probe?.CountTimerInvalidation();
            _overlay.SyncProbe?.Invalidated();
            _overlay.InvalidateVisual();
        }
    }

    private void MapContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_hasPendingPointerContextCoordinate)
        {
            _hasPendingPointerContextCoordinate = false;
            return;
        }

        // Keyboard-invoked context menus have no preceding pointer press. Use their supplied position
        // when available and otherwise fall back to the current planning pin.
        _contextCoordinate = e.TryGetPosition(_mapControl, out var point) &&
                             TryCoordinateFromScreen(point, out var coordinate)
            ? coordinate
            : Observer;
    }

    private void MapPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_mapControl).Properties.IsRightButtonPressed) return;

        _hasPendingPointerContextCoordinate =
            TryCoordinateFromScreen(e.GetPosition(_mapControl), out var coordinate);
        if (_hasPendingPointerContextCoordinate) _contextCoordinate = coordinate;
    }

    private bool TryCoordinateFromScreen(Point point, out GeoCoordinate coordinate)
    {
        var viewport = _mapControl.Map?.Navigator.Viewport;
        if (!viewport.HasValue)
        {
            coordinate = default;
            return false;
        }
        var world = viewport.Value.ScreenToWorld(point.X, point.Y);
        coordinate = WebMercator.ToWgs84(world.X, world.Y) with { ElevationMetres = 0 };
        return true;
    }

    private void CommitExplicitCoordinate(GeoCoordinate coordinate)
    {
        var normalised = coordinate.Normalised();
        _pinInteraction.SetCommittedCoordinate(normalised);
        UpdateOverlay();
        PreviewCoordinateChanged?.Invoke(this, normalised);
        RaiseCoordinateCommitted(normalised);
    }

    private void RaiseCoordinateCommitted(GeoCoordinate coordinate)
    {
        // Only a real move produces the Observer update that clears _committingPin. Committing the
        // current observer (e.g. pressing and releasing the pin without moving it) is not a new
        // location, and a stale flag would suppress the next genuine re-centre.
        if (MainViewModel.SameObserverPosition(coordinate.Normalised(), Observer)) return;
        _committingPin = true;
        CoordinateCommitted?.Invoke(this, coordinate);
    }

    private static double Distance(Point first, Point second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private readonly record struct ViewportSignature(
        double CenterX,
        double CenterY,
        double Resolution,
        double Rotation,
        double Width,
        double Height)
    {
        public static ViewportSignature From(Viewport viewport) => new(
            viewport.CenterX,
            viewport.CenterY,
            viewport.Resolution,
            viewport.Rotation,
            viewport.Width,
            viewport.Height);
    }

    private sealed class MapOverlay(Func<Viewport?> viewport) : Control
    {
        internal PlannerPinProbe? Probe { get; set; }
        internal OverlaySynchronizationProbe? SyncProbe { get; set; }
        internal Action? RenderStarting { get; set; }
        protected override Size MeasureOverride(Size availableSize)
        {
            Probe?.CountMeasure();
            return base.MeasureOverride(availableSize);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            Probe?.CountArrange();
            return base.ArrangeOverride(finalSize);
        }
        // Theme map symbols (MapThemeTokens). Defaults are the Noctaxis values, used when no theme is loaded.
        public static readonly StyledProperty<Color> PinFillProperty =
            AvaloniaProperty.Register<MapOverlay, Color>(nameof(PinFill), Color.Parse("#0B0F17"));
        public static readonly StyledProperty<Color> PinOutlineProperty =
            AvaloniaProperty.Register<MapOverlay, Color>(nameof(PinOutline), Colors.White);
        public static readonly StyledProperty<Color> PinActivityColourProperty =
            AvaloniaProperty.Register<MapOverlay, Color>(nameof(PinActivityColour), Color.FromArgb(220, 174, 218, 255));
        // Celestial data colours (theme + colour-vision mode), a halo that separates data lines from the
        // basemap, and the weather-limit marking colour (the basemap label colour: legible on its land).
        public static readonly StyledProperty<CelestialColourSet> CelestialColoursProperty =
            AvaloniaProperty.Register<MapOverlay, CelestialColourSet>(nameof(CelestialColours), CelestialColourSet.Default);
        public static readonly StyledProperty<Color> LineHaloProperty =
            AvaloniaProperty.Register<MapOverlay, Color>(nameof(LineHalo), Color.Parse("#0B0F17"));
        public static readonly StyledProperty<Color> WeatherMarkProperty =
            AvaloniaProperty.Register<MapOverlay, Color>(nameof(WeatherMark), Color.Parse("#C9CFDA"));
        static MapOverlay() => AffectsRender<MapOverlay>(PinFillProperty, PinOutlineProperty, PinActivityColourProperty,
            CelestialColoursProperty, LineHaloProperty, WeatherMarkProperty);
        public CelestialColourSet CelestialColours { get => GetValue(CelestialColoursProperty); set => SetValue(CelestialColoursProperty, value); }
        public Color LineHalo { get => GetValue(LineHaloProperty); set => SetValue(LineHaloProperty, value); }
        public Color WeatherMark { get => GetValue(WeatherMarkProperty); set => SetValue(WeatherMarkProperty, value); }
        public Color PinFill { get => GetValue(PinFillProperty); set => SetValue(PinFillProperty, value); }
        public Color PinOutline { get => GetValue(PinOutlineProperty); set => SetValue(PinOutlineProperty, value); }
        public Color PinActivityColour { get => GetValue(PinActivityColourProperty); set => SetValue(PinActivityColourProperty, value); }
        // Rebuilt only when a theme changes a symbol colour, never per frame.
        private IBrush? _markerFill;
        private Pen? _markerOutline;
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PinFillProperty) _markerFill = null;
            else if (change.Property == PinOutlineProperty) _markerOutline = null;
        }
        private IBrush MarkerFill => _markerFill ??= new Avalonia.Media.Immutable.ImmutableSolidColorBrush(PinFill);
        private Pen MarkerOutline => _markerOutline ??= new(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(PinOutline), 3);
        private static readonly StreamGeometry MarkerGeometry = CreateMarkerGeometry();
        private static readonly GeographicOverlayGeometryBuilder GeometryBuilder = new();
        private PlanningSnapshot? _snapshot;
        private CameraFramingGuide? _framingGuide;
        private FramingVisibilityAssessment? _framingVisibility;
        private GeoCoordinate _observer;
        private GeographicCameraOutline? _cameraGeometry;
        private IReadOnlyList<GeoCoordinate>? _cameraBaseFill;
        private IReadOnlyList<CelestialRayRenderGeometry>? _celestialGeometry;
        private readonly EnvironmentalOverlayStateCoordinator _environmentalCoordinator = new();
        private EnvironmentalOverlayRenderer? _environmentalRenderer;
        private EnvironmentalOverlayState? _environmentalState;
        internal EnvironmentalOverlayState? EnvironmentalStateForTesting => _environmentalState;
        public PlannerPinActivity PinActivity { get; set; }
        public bool ShowCelestialOverlays { get; set; }
        public bool ShowCameraOverlay { get; set; }
        public bool ShowEnvironmentalShading { get; set; } = true;
        public bool ShowObserverPin { get; set; } = true;
        public bool ShowTerrainDebug { get; set; }

        public PlanningSnapshot? Snapshot
        {
            get => _snapshot;
            set
            {
                if (ReferenceEquals(_snapshot, value)) return;
                _snapshot = value;
                _celestialGeometry = null;
                _environmentalState = null;
            }
        }

        public CameraFramingGuide? FramingGuide
        {
            get => _framingGuide;
            set
            {
                if (_framingGuide == value) return;
                _framingGuide = value;
                _cameraGeometry = null;
                _cameraBaseFill = null;
                _environmentalState = null;
            }
        }

        public FramingVisibilityAssessment? FramingVisibility
        {
            get => _framingVisibility;
            set
            {
                if (_framingVisibility == value) return;
                var changed = (_framingVisibility is null) != (value is null) ||
                    _framingVisibility?.CameraDepth != value?.CameraDepth ||
                    _framingVisibility?.WeatherVisibilityDistanceMetres != value?.WeatherVisibilityDistanceMetres;
                _framingVisibility = value;
                if (!changed) return;
                _cameraGeometry = null;
                _environmentalState = null;
            }
        }

        private CameraFramingSettings _framingSettings = new();
        public CameraFramingSettings FramingSettings
        {
            get => _framingSettings;
            set
            {
                if (_framingSettings == value) return;
                _framingSettings = value;
                _environmentalState = null;
            }
        }

        public GeoCoordinate Observer
        {
            get => _observer;
            set
            {
                if (_observer == value) return;
                _observer = value;
                _cameraGeometry = null;
                _cameraBaseFill = null;
                _celestialGeometry = null;
                _environmentalState = null;
            }
        }

        public Point? PinScreenPoint => ShowObserverPin ? Project(Observer, viewport()) : null;

        public void InvalidateCelestialGeometry()
        {
            _celestialGeometry = null;
            InvalidateVisual();
        }

        public void InvalidateCameraGeometry()
        {
            _cameraGeometry = null;
            _cameraBaseFill = null;
            _environmentalState = null;
            InvalidateVisual();
        }

        public void ReleaseRenderer()
        {
            _environmentalRenderer?.Dispose();
            _environmentalRenderer = null;
        }

        public override void Render(DrawingContext context)
        {
            RenderStarting?.Invoke();
            using var measurement = Probe?.MeasureOverlay();
            base.Render(context);
            var currentViewport = viewport();
            if (currentViewport.HasValue) SyncProbe?.OverlayRendered(currentViewport.Value);
            var projectionMeasurement = Probe?.MeasureProjection();
            var pin = Project(Observer, currentViewport);
            projectionMeasurement?.Dispose();
            if (pin is null || !currentViewport.HasValue) return;

            EnsureGeographicGeometry();
            var snapshot = Snapshot;
            if (IsSnapshotCurrent && snapshot is not null)
            {
                if (_cameraGeometry is not null)
                    DrawCameraFramingGuide(context, snapshot, _cameraGeometry, _cameraBaseFill,
                        FramingSettings, currentViewport.Value);
                if (_celestialGeometry is not null)
                    DrawCelestialRays(context, snapshot, _celestialGeometry, FramingSettings, currentViewport.Value);
                if (ShowTerrainDebug)
                    TerrainSampleRenderer.Draw(context, snapshot,
                        FramingGuide?.CentreBearingDegrees ?? snapshot.Position.Horizontal.AzimuthDegrees,
                        Observer, currentViewport.Value.Resolution,
                        coordinate => Project(coordinate, currentViewport.Value),
                        (path, pen) => DrawPath(context, path, pen, currentViewport.Value));
            }

            if (!ShowObserverPin || Probe?.HideFloatingPin == true) return;
            using var pinMeasurement = Probe?.MeasurePin();
            using (context.PushTransform(Matrix.CreateTranslation(pin.Value.X, pin.Value.Y)))
            {
                DrawPinActivity(context);
                context.DrawGeometry(MarkerFill, MarkerOutline, MarkerGeometry);
            }
            SyncProbe?.RecordComposition(context, Bounds, currentViewport.Value);
        }

        private void EnsureGeographicGeometry()
        {
            if (!IsSnapshotCurrent)
            {
                _cameraGeometry = null;
                _cameraBaseFill = null;
                _celestialGeometry = null;
                _environmentalState = null;
                return;
            }

            if (_cameraGeometry is null && ShowCameraOverlay && FramingGuide is not null)
            {
                var sector = new GeoSector(
                    Observer,
                    FramingGuide.CentreBearingDegrees,
                    FramingGuide.HorizontalFieldOfViewDegrees,
                    MapOverlayGeometry.MaximumRangeMetres);
                _cameraGeometry = GeometryBuilder.BuildCameraOutline(sector, FramingVisibility);
                _cameraBaseFill = GeometryBuilder.BuildCameraBaseFill(sector);
            }

            if (_environmentalState is null && _cameraGeometry is not null && Snapshot is not null)
            {
                var terrain = Snapshot.Environment?.HorizonProfile ?? Snapshot.Terrain;
                var profileKey = EnvironmentalOverlayStateFactory.CreateProfileKey(
                    terrain, CameraFramingSettings.DefaultTerrainCastAngularDetailDegrees);
                var previousRevision = _environmentalCoordinator.Diagnostics.OverlayStateRebuilds;
                _environmentalState = _environmentalCoordinator.Update(
                    Observer, _cameraGeometry.Sector, FramingVisibility, profileKey,
                    FramingVisibility is null ? TerrainHorizonProfile.Disabled(Observer, terrain.GeneratedAt, 0) : terrain);
#if DEBUG
                if (_environmentalCoordinator.Diagnostics.OverlayStateRebuilds != previousRevision)
                    TerrainConeTopologyDiagnostics.Write(_environmentalState);
#endif
            }

            if (_celestialGeometry is null && ShowCelestialOverlays && Snapshot is not null)
            {
                var plans = Snapshot.EffectiveObjectPlans;
                var rays = new CelestialRayRenderGeometry[plans.Count];
                for (var index = 0; index < plans.Count; index++)
                {
                    var plan = plans[index];
                    rays[index] = new CelestialRayRenderGeometry(
                        plan.Position.Target,
                        Snapshot.Session.IsTargetFocused && plan.Position.Target.Id.Equals(Snapshot.Position.Target.Id, StringComparison.OrdinalIgnoreCase),
                        GeometryBuilder.BuildRay(new GeoRay(
                            Observer,
                            plan.Position.Horizontal.AzimuthDegrees,
                            MapOverlayGeometry.MaximumRangeMetres)));
                }
                _celestialGeometry = rays;
            }
        }

        private bool IsSnapshotCurrent => Snapshot is not null &&
            Angles.GreatCircleDistanceMetres(Snapshot.Session.Observer, Observer) <= 2 &&
            Math.Abs(Snapshot.Session.Observer.ElevationMetres - Observer.ElevationMetres) <= .1;

        private void DrawPinActivity(DrawingContext context)
        {
            if (PinActivity == PlannerPinActivity.None) return;
            var isCore = PinActivity == PlannerPinActivity.CoreLoading;
            var periodMilliseconds = isCore ? 1_250d : 2_400d;
            var start = Environment.TickCount64 % periodMilliseconds / periodMilliseconds * 360 - 90;
            var sweep = isCore ? 255d : 165d;
            var radius = 18d;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                var firstRadians = start * Math.PI / 180;
                var endRadians = (start + sweep) * Math.PI / 180;
                path.BeginFigure(new Point(Math.Cos(firstRadians) * radius,
                    Math.Sin(firstRadians) * radius), false);
                path.ArcTo(new Point(Math.Cos(endRadians) * radius, Math.Sin(endRadians) * radius),
                    new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
                path.EndFigure(false);
            }
            // Secondary activity keeps its historical 135/220 opacity relative to core loading.
            var activity = PinActivityColour;
            var colour = isCore ? activity
                : Color.FromArgb((byte)Math.Round(activity.A * 135d / 220), activity.R, activity.G, activity.B);
            var thickness = isCore ? 1.7 : 1.1;
            // A pin-fill halo keeps the arc legible on light and dark imagery alike, whatever its hue.
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(colour.A, PinFill.R, PinFill.G, PinFill.B)),
                thickness + 2), geometry);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(colour), thickness), geometry);
        }

        private static Point? Project(GeoCoordinate coordinate, Viewport? current)
        {
            if (!current.HasValue || current.Value.Resolution <= 0) return null;
            var world = WebMercator.FromWgs84(coordinate);
            var wrappedX = WebMercator.WrapXNear(world.X, current.Value.CenterX);
            var screen = current.Value.WorldToScreen(wrappedX, world.Y);
            return new Point(screen.X, screen.Y);
        }

        private void DrawCameraFramingGuide(
            DrawingContext context,
            PlanningSnapshot snapshot,
            GeographicCameraOutline overlay,
            IReadOnlyList<GeoCoordinate>? baseFill,
            CameraFramingSettings framingSettings,
            Viewport currentViewport)
        {
            var colour = CelestialColours[CelestialPalette.Role(snapshot, snapshot.Position.Target)];
            var settings = framingSettings.Normalised();
            // The environmental shader owns the complete base/weather/terrain composition when
            // state is available. The geographic fallback fill is used only before that state exists.
            if (ShowEnvironmentalShading && baseFill is not null && _environmentalState is null && settings.ShadingOpacityPercent > 0)
            {
                var geometry = CreateProjectedGeometry(baseFill, currentViewport, close: true);
                if (geometry is not null)
                {
                    var alpha = (byte)Math.Round(255 * settings.ShadingOpacityPercent / 100,
                        MidpointRounding.AwayFromZero);
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B)),
                        null, geometry);
                }
            }
            if (ShowEnvironmentalShading && _environmentalState is not null && settings.ShadingOpacityPercent > 0)
            {
                var parameters = EnvironmentalRenderParameters.Default with
                {
                    ConeOpacity = (float)(settings.ShadingOpacityPercent / 100),
                    TerrainColourArgb = Color.Parse(settings.TerrainObstructionColour).ToUInt32(),
                    TerrainTintOpacity = (float)(settings.TerrainTintStrengthPercent / 100),
                    WeatherMarkArgb = WeatherMark.ToUInt32()
                };
                var frame = EnvironmentalOverlayMath.CreateFrame(
                    currentViewport, Bounds.Width, Bounds.Height, parameters);
                _environmentalCoordinator.UpdateRender(frame.RenderKey);
                _environmentalRenderer ??= new EnvironmentalOverlayRenderer(
                    _environmentalCoordinator.Diagnostics);
                _environmentalRenderer.Draw(context, new Rect(Bounds.Size),
                    _environmentalState, frame, colour);
            }

#if DEBUG
            if (TerrainConeTopologyDiagnostics.Enabled && _environmentalState is not null)
                DrawTerrainTopology(context, _environmentalState, currentViewport);
#endif

            // The field of view's two edges only: a centre line said nothing the edges and the camera do not.
            var edgeAlpha = overlay.Sector.HorizontalFovDegrees < 3 ? (byte)160 : (byte)128;
            DrawBoundary(context, overlay.LeftBoundary, colour, edgeAlpha, settings.LineThickness, currentViewport);
            DrawBoundary(context, overlay.RightBoundary, colour, edgeAlpha, settings.LineThickness, currentViewport);
        }

#if DEBUG
        private void DrawTerrainTopology(
            DrawingContext context,
            EnvironmentalOverlayState overlay,
            Viewport currentViewport)
        {
            var rayPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 235, 255)), .55,
                dashStyle: DashStyle.Dash);
            foreach (var sample in overlay.SourceSamples)
            {
                var markerDistance = sample.ObstructionDistanceMetres ?? Math.Min(10_000, overlay.MaximumDistanceMetres);
                var markerCoordinate = Angles.Destination(Observer, sample.UnwrappedBearingDegrees, markerDistance);
                DrawPath(context, [Observer, markerCoordinate], rayPen, currentViewport);
                var marker = Project(markerCoordinate, currentViewport);
                if (marker is null) continue;
                var fill = sample.IsObstructed
                    ? new SolidColorBrush(Color.FromArgb(240, 255, 76, 76))
                    : new SolidColorBrush(Color.FromArgb(240, 80, 255, 135));
                context.DrawEllipse(fill, null, marker.Value, 3, 3);
            }
        }
#endif

        private void DrawCelestialRays(
            DrawingContext context,
            PlanningSnapshot snapshot,
            IReadOnlyList<CelestialRayRenderGeometry> rays,
            CameraFramingSettings framingSettings,
            Viewport currentViewport)
        {
            var lineThickness = framingSettings.Normalised().LineThickness;
            var colours = CelestialColours;
            var halo = new SolidColorBrush(Color.FromArgb(170, LineHalo.R, LineHalo.G, LineHalo.B));
            foreach (var ray in rays)
            {
                var role = CelestialPalette.Role(snapshot, ray.Target);
                // The target's own stroke, as on the timeline and in its line sample; a halo in the basemap's
                // label-halo colour keeps it legible over roads, water and pale land. No shape on the ray.
                var stroke = CelestialPalette.Stroke(colours, role, ray.IsPrimary, lineThickness);
                DrawPath(context, ray.Geometry.Coordinates, new Pen(halo, stroke.Thickness + 2), currentViewport);
                DrawPath(context, ray.Geometry.Coordinates, stroke, currentViewport);
            }
        }

        // Beyond the weather visibility distance the edge is dotted as well as grey, so the limit reads without colour.
        private static readonly IDashStyle WeatherLimitedEdge = new Avalonia.Media.Immutable.ImmutableDashStyle([1.5, 2], 0);

        private static void DrawBoundary(
            DrawingContext context,
            IReadOnlyList<GeographicPathSegment> segments,
            Color colour,
            byte alpha,
            double thickness,
            Viewport currentViewport)
        {
            foreach (var segment in segments)
            {
                var pen = segment.Effect.HasFlag(CameraOverlayEffect.WeatherDesaturated)
                    ? new Pen(new SolidColorBrush(CameraOverlayColourPolicy.Grayscale(colour, alpha)), thickness, dashStyle: WeatherLimitedEdge)
                    : new Pen(new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B)), thickness);
                DrawPath(context, segment.Coordinates, pen, currentViewport);
            }
        }

        private static void DrawPath(
            DrawingContext context,
            IReadOnlyList<GeoCoordinate> coordinates,
            Pen pen,
            Viewport currentViewport)
        {
            var geometry = CreateProjectedGeometry(coordinates, currentViewport, false);
            if (geometry is not null) context.DrawGeometry(null, pen, geometry);
        }

        private static StreamGeometry? CreateProjectedGeometry(
            IReadOnlyList<GeoCoordinate> coordinates,
            Viewport currentViewport,
            bool close)
        {
            if (coordinates.Count < (close ? 3 : 2)) return null;
            double? previousWorldX = null;
            var first = ProjectContinuous(coordinates[0], currentViewport, ref previousWorldX);
            if (first is null) return null;
            var geometry = new StreamGeometry();
            using var path = geometry.Open();
            path.SetFillRule(FillRule.NonZero);
            path.BeginFigure(first.Value, close);
            for (var index = 1; index < coordinates.Count; index++)
            {
                var point = ProjectContinuous(coordinates[index], currentViewport, ref previousWorldX);
                if (point is not null) path.LineTo(point.Value);
            }
            path.EndFigure(close);
            return geometry;
        }

        private static Point? ProjectContinuous(
            GeoCoordinate coordinate,
            Viewport current,
            ref double? previousWorldX)
        {
            if (current.Resolution <= 0) return null;
            var world = WebMercator.FromWgs84(coordinate);
            var wrappedX = WebMercator.WrapXNear(world.X, previousWorldX ?? current.CenterX);
            previousWorldX = wrappedX;
            var screen = current.WorldToScreen(wrappedX, world.Y);
            return new Point(screen.X, screen.Y);
        }

        private sealed record CelestialRayRenderGeometry(
            AstralTarget Target,
            bool IsPrimary,
            GeographicRayGeometry Geometry);

        private static StreamGeometry CreateMarkerGeometry()
        {
            var geometry = new StreamGeometry();
            using var context = geometry.Open();
            context.BeginFigure(new Point(0, 14), true);
            context.ArcTo(new Point(0, -10), new Size(12, 12), 0, false, SweepDirection.Clockwise);
            context.ArcTo(new Point(0, 14), new Size(12, 12), 0, false, SweepDirection.Clockwise);
            context.EndFigure(true);
            return geometry;
        }
    }
}

public static class WebMercator
{
    private const double Radius = 6378137;
    private const double WorldCircumference = 2 * Math.PI * Radius;
    public static (double X, double Y) FromWgs84(GeoCoordinate coordinate)
    {
        var latitude = Math.Clamp(coordinate.Latitude, -85.05112878, 85.05112878);
        return (Radius * coordinate.Longitude * Math.PI / 180,
            Radius * Math.Log(Math.Tan(Math.PI / 4 + latitude * Math.PI / 360)));
    }

    public static GeoCoordinate ToWgs84(double x, double y) => new(
        (2 * Math.Atan(Math.Exp(y / Radius)) - Math.PI / 2) * 180 / Math.PI,
        x / Radius * 180 / Math.PI);

    public static double WrapXNear(double x, double referenceX)
    {
        while (x - referenceX > WorldCircumference / 2) x -= WorldCircumference;
        while (referenceX - x > WorldCircumference / 2) x += WorldCircumference;
        return x;
    }
}
