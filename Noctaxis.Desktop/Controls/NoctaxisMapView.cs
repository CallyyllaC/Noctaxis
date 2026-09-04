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

namespace Noctaxis.Desktop.Controls;

public sealed class NoctaxisMapView : UserControl
{
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
    private readonly MapOverlay _overlay;
    private readonly DispatcherTimer _renderTimer;
    private bool _attached;
    private bool _draggingPin;
    private bool _committingPin;
    private GeoCoordinate? _contextCoordinate;
    private bool _hasPendingPointerContextCoordinate;
    private readonly PlanningPinInteractionState _pinInteraction;
    private ViewportSignature? _lastViewportSignature;

    public NoctaxisMapView()
    {
        var map = new Map();
        map.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer());
        _mapControl = new MapControl { Map = map };
        var setPinMenuItem = new MenuItem { Header = "Set planning pin here" };
        setPinMenuItem.Click += (_, _) =>
        {
            if (_contextCoordinate is { } coordinate) CommitExplicitCoordinate(coordinate);
        };
        var savePinMenuItem = new MenuItem { Header = "Save current pin location" };
        savePinMenuItem.Click += (_, _) => SaveCurrentPinRequested?.Invoke(this, EventArgs.Empty);
        _mapControl.ContextMenu = new ContextMenu { ItemsSource = new object[] { setPinMenuItem, savePinMenuItem } };
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
        var attribution = new TextBlock
        {
            Text = MapProvider.Attribution, FontSize = 11, Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(190, 15, 20, 29)), Padding = new Thickness(7, 3),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Margin = new Thickness(8)
        };
        Content = new Grid { Children = { _mapControl, _overlay, attribution } };

        AddHandler(PointerPressedEvent, PinPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, PinPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, PinPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, PinCaptureLost, RoutingStrategies.Tunnel);

        // Viewport movement only shifts the cached overlay geometry on screen. It never changes planning state.
        _renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render,
            (_, _) =>
            {
                var current = _mapControl.Map?.Navigator.Viewport;
                if (!current.HasValue) return;
                var signature = ViewportSignature.From(current.Value);
                var viewportChanged = _lastViewportSignature != signature;
                if (viewportChanged)
                {
                    _lastViewportSignature = signature;
                    _pinInteraction.ViewportChanged();
                }
                if (viewportChanged || _overlay.PinActivity != PlannerPinActivity.None)
                    _overlay.InvalidateVisual();
            });
        _renderTimer.Start();
        AttachedToVisualTree += (_, _) => { _attached = true; _pinInteraction.SetCommittedCoordinate(Observer); UpdateOverlay(); CenterOn(Observer); };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
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

    internal MapControl MapControlForTesting => _mapControl;
    internal Control OverlayForTesting => _overlay;

    public void CenterOn(GeoCoordinate coordinate)
    {
        if (!_attached || _mapControl.Map is null) return;
        var world = WebMercator.FromWgs84(coordinate);
        var resolution = _mapControl.Map.Navigator.Viewport.Resolution;
        _mapControl.Map.Navigator.CenterOnAndZoomTo(new MPoint(world.X, world.Y), resolution > 0 ? resolution : 650, 0);
        _overlay.InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
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
            _overlay.FramingVisibility = FramingVisibility;
            _overlay.InvalidateVisual();
        }
        else if (change.Property == FramingSettingsProperty)
        {
            _overlay.FramingSettings = FramingSettings.Normalised();
            _overlay.InvalidateVisual();
        }
        else if (change.Property == ObserverProperty)
        {
            _pinInteraction.SetCommittedCoordinate(Observer);
            UpdateOverlay();
            if (_attached && !_committingPin) CenterOn(Observer);
            _committingPin = false;
        }
        else if (change.Property == PinActivityProperty)
        {
            _overlay.PinActivity = PinActivity;
            _overlay.InvalidateVisual();
        }
        else if (change.Property == ShowCelestialOverlaysProperty)
        {
            _overlay.ShowCelestialOverlays = ShowCelestialOverlays;
            _overlay.InvalidateCelestialGeometry();
        }
        else if (change.Property == ShowCameraOverlayProperty)
        {
            _overlay.ShowCameraOverlay = ShowCameraOverlay;
            _overlay.InvalidateCameraGeometry();
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
        _committingPin = true;
        CoordinateCommitted?.Invoke(this, _pinInteraction.CompleteDrag());
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
        _overlay.InvalidateVisual();
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
        _committingPin = true;
        CoordinateCommitted?.Invoke(this, normalised);
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
        private static readonly IBrush MarkerFill = new SolidColorBrush(Color.Parse("#0B0F17"));
        private static readonly Pen MarkerOutline = new(Brushes.White, 3);
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
        public PlannerPinActivity PinActivity { get; set; }
        public bool ShowCelestialOverlays { get; set; }
        public bool ShowCameraOverlay { get; set; }
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
                _framingVisibility = value;
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

        public Point? PinScreenPoint => Project(Observer, viewport());

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
            base.Render(context);
            var currentViewport = viewport();
            var pin = Project(Observer, currentViewport);
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
                    DrawTerrainDebugSamples(context, snapshot, currentViewport.Value);
            }

            using (context.PushTransform(Matrix.CreateTranslation(pin.Value.X, pin.Value.Y)))
            {
                DrawPinActivity(context);
                context.DrawGeometry(MarkerFill, MarkerOutline, MarkerGeometry);
            }
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
                    terrain, FramingSettings.TerrainCastAngularDetailDegrees);
                var previousRevision = _environmentalCoordinator.Diagnostics.OverlayStateRebuilds;
                _environmentalState = _environmentalCoordinator.Update(
                    Observer, _cameraGeometry.Sector, FramingVisibility, profileKey);
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
                        plan.Position.Target.Id.Equals(Snapshot.Position.Target.Id, StringComparison.OrdinalIgnoreCase),
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
            var colour = isCore ? Color.FromArgb(220, 174, 218, 255) : Color.FromArgb(135, 174, 218, 255);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(colour), isCore ? 1.7 : 1.1), geometry);
        }

        private void DrawTerrainDebugSamples(DrawingContext context, PlanningSnapshot snapshot,
            Viewport currentViewport)
        {
            var profile = snapshot.Environment?.HorizonProfile ?? snapshot.Terrain;
            var bearing = FramingGuide?.CentreBearingDegrees ?? snapshot.Position.Horizontal.AzimuthDegrees;
            var sample = TerrainProfileDiagnostics.NearestBearingSample(profile, bearing);
            if (sample is null) return;

            var rawSamples = profile.ObserverDiagnostics?.TerrainSample.RawSamples ?? [];
            foreach (var raw in rawSamples)
            {
                var point = Project(raw.Coordinate, currentViewport);
                if (point is null) continue;
                var fill = DebugBrush(raw.Status);
                context.DrawEllipse(fill, new Pen(Brushes.Black, 1), point.Value, 4, 4);
                if (currentViewport.Resolution <= 12)
                    DrawDebugLabel(context, point.Value, raw.RawElevationMetres, raw.Status);
            }

            var sightline = sample.Value.Sightline ?? [];
            if (sightline.Count == 0) return;
            var stride = Math.Max(1, (int)Math.Ceiling(sightline.Count / 80d));
            for (var index = 0; index < sightline.Count; index += stride)
            {
                var radial = sightline[index];
                var coordinate = Angles.Destination(Observer, sample.Value.BearingDegrees, radial.DistanceMetres);
                var point = Project(coordinate, currentViewport);
                if (point is null) continue;
                var status = radial.GroundElevationMetres.HasValue
                    ? TerrainSampleStatus.Valid : radial.GroundStatus;
                context.DrawEllipse(DebugBrush(status), null, point.Value, 2.2, 2.2);
                if (currentViewport.Resolution <= 4 && index < 32)
                    DrawDebugLabel(context, point.Value,
                        radial.GroundElevationMetres, status);
            }

            if (sample.Value.EffectiveHorizonFeatureDistanceMetres is not double winningDistance) return;
            var winningCoordinate = Angles.Destination(Observer, sample.Value.BearingDegrees, winningDistance);
            DrawPath(context, [Observer, winningCoordinate],
                new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 88, 72)), 1.2, dashStyle: DashStyle.Dash),
                currentViewport);
            var winningPoint = Project(winningCoordinate, currentViewport);
            if (winningPoint is not null)
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(245, 255, 70, 58)),
                    new Pen(Brushes.White, 1.5), winningPoint.Value, 6, 6);
        }

        private static IBrush DebugBrush(TerrainSampleStatus status) => status switch
        {
            TerrainSampleStatus.Water => new SolidColorBrush(Color.FromArgb(235, 52, 181, 255)),
            TerrainSampleStatus.Valid => new SolidColorBrush(Color.FromArgb(235, 92, 238, 148)),
            TerrainSampleStatus.NoData or TerrainSampleStatus.Error =>
                new SolidColorBrush(Color.FromArgb(245, 255, 75, 75)),
            _ => new SolidColorBrush(Color.FromArgb(230, 255, 183, 73))
        };

        private static void DrawDebugLabel(DrawingContext context, Point point, double? elevation,
            TerrainSampleStatus status)
        {
            var label = elevation.HasValue ? $"{elevation.Value:F1} m" : status.ToString();
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 9, Brushes.White);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 7, 12, 19)), null,
                new Rect(point.X + 5, point.Y - 7, text.Width + 4, text.Height + 2));
            context.DrawText(text, new Point(point.X + 7, point.Y - 6));
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
            var colour = CelestialPalette.Colour(snapshot.Position.Target,
                CelestialPalette.OrderFor(snapshot, snapshot.Position.Target));
            var settings = framingSettings.Normalised();
            if (baseFill is not null && settings.ShadingOpacityPercent > 0)
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
            if (_environmentalState is not null && settings.ShadingOpacityPercent > 0)
            {
                var parameters = EnvironmentalRenderParameters.Default with
                {
                    ConeOpacity = (float)(settings.ShadingOpacityPercent / 100)
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

            var edgeAlpha = overlay.Sector.HorizontalFovDegrees < 3 ? (byte)160 : (byte)128;
            DrawBoundary(context, overlay.LeftBoundary, colour, edgeAlpha, settings.LineThickness, currentViewport);
            DrawBoundary(context, overlay.RightBoundary, colour, edgeAlpha, settings.LineThickness, currentViewport);
            DrawBoundary(context, overlay.CentreBearing, colour, 190, settings.LineThickness * 1.45, currentViewport);
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

        private static void DrawCelestialRays(
            DrawingContext context,
            PlanningSnapshot snapshot,
            IReadOnlyList<CelestialRayRenderGeometry> rays,
            CameraFramingSettings framingSettings,
            Viewport currentViewport)
        {
            var lineThickness = framingSettings.Normalised().LineThickness;
            foreach (var ray in rays)
            {
                var colour = CelestialPalette.Colour(ray.Target, CelestialPalette.OrderFor(snapshot, ray.Target));
                var pen = new Pen(new SolidColorBrush(colour), lineThickness,
                    dashStyle: ray.IsPrimary ? null : DashStyle.Dash);
                DrawPath(context, ray.Geometry.Coordinates, pen, currentViewport);
            }
        }

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
                    ? new Pen(new SolidColorBrush(CameraOverlayColourPolicy.Grayscale(colour, alpha)), thickness)
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
