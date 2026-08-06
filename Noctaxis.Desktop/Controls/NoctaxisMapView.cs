using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.UI.Avalonia;
using Noctaxis.Core.Domain;
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

    private readonly MapControl _mapControl;
    private readonly MapOverlay _overlay;
    private readonly DispatcherTimer _renderTimer;
    private bool _attached;
    private bool _draggingPin;
    private bool _committingPin;
    private GeoCoordinate? _contextCoordinate;
    private bool _hasPendingPointerContextCoordinate;
    private readonly PlanningPinInteractionState _pinInteraction;

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
        _overlay = new MapOverlay(() => _mapControl.Map?.Navigator.Viewport) { IsHitTestVisible = false };
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
            (_, _) => { _pinInteraction.ViewportChanged(); _overlay.InvalidateVisual(); });
        _renderTimer.Start();
        AttachedToVisualTree += (_, _) => { _attached = true; _pinInteraction.SetCommittedCoordinate(Observer); UpdateOverlay(); CenterOn(Observer); };
        DetachedFromVisualTree += (_, _) => _attached = false;
    }

    public GeoCoordinate Observer { get => GetValue(ObserverProperty); set => SetValue(ObserverProperty, value); }
    public PlanningSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public CameraFramingGuide? FramingGuide { get => GetValue(FramingGuideProperty); set => SetValue(FramingGuideProperty, value); }
    public FramingVisibilityAssessment? FramingVisibility { get => GetValue(FramingVisibilityProperty); set => SetValue(FramingVisibilityProperty, value); }
    public CameraFramingSettings FramingSettings { get => GetValue(FramingSettingsProperty); set => SetValue(FramingSettingsProperty, value); }
    public event EventHandler<GeoCoordinate>? PreviewCoordinateChanged;
    public event EventHandler<GeoCoordinate>? CoordinateCommitted;
    public event EventHandler<bool>? InteractionStateChanged;
    public event EventHandler? SaveCurrentPinRequested;

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
        _pinInteraction.UpdateDrag(WebMercator.ToWgs84(world.X, world.Y) with { ElevationMetres = Observer.ElevationMetres });
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
        coordinate = WebMercator.ToWgs84(world.X, world.Y) with { ElevationMetres = Observer.ElevationMetres };
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

    private sealed class MapOverlay(Func<Viewport?> viewport) : Control
    {
        private static readonly IBrush MarkerFill = new SolidColorBrush(Color.Parse("#0B0F17"));
        private static readonly Pen MarkerOutline = new(Brushes.White, 3);
        private static readonly StreamGeometry MarkerGeometry = CreateMarkerGeometry();
        private static readonly FramingWedgeLayoutCalculator WedgeLayout = new();
        public PlanningSnapshot? Snapshot { get; set; }
        public CameraFramingGuide? FramingGuide { get; set; }
        public FramingVisibilityAssessment? FramingVisibility { get; set; }
        public CameraFramingSettings FramingSettings { get; set; } = new();
        public GeoCoordinate Observer { get; set; }
        public Point? PinScreenPoint => Project(Observer);

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var pin = PinScreenPoint;
            if (pin is null) return;
            if (Snapshot is not null)
            {
                if (FramingGuide is not null)
                    DrawCameraFramingGuide(context, Observer, new Rect(Bounds.Size), Snapshot, FramingGuide,
                        FramingVisibility, FramingSettings, Project);

                foreach (var plan in Snapshot.EffectiveObjectPlans)
                {
                    var primary = plan.Position.Target.Id.Equals(Snapshot.Position.Target.Id, StringComparison.OrdinalIgnoreCase);
                    if (primary && FramingGuide is not null) continue;
                    var colour = CelestialPalette.Colour(plan.Position.Target, CelestialPalette.OrderFor(Snapshot, plan.Position.Target));
                    var bearing = plan.Position.Horizontal.AzimuthDegrees * Angles.DegreesToRadians;
                    var radius = Math.Clamp(Math.Min(Bounds.Width, Bounds.Height) * (primary ? .34 : .27), 90, 360);
                    var end = new Point(pin.Value.X + radius * Math.Sin(bearing), pin.Value.Y - radius * Math.Cos(bearing));
                    context.DrawLine(new Pen(new SolidColorBrush(colour), primary ? 3 : 1.5,
                        dashStyle: primary ? null : DashStyle.Dash), pin.Value, end);
                }
            }
            using (context.PushTransform(Matrix.CreateTranslation(pin.Value.X, pin.Value.Y)))
                context.DrawGeometry(MarkerFill, MarkerOutline, MarkerGeometry);
        }

        private Point? Project(GeoCoordinate coordinate)
        {
            var current = viewport();
            if (!current.HasValue || current.Value.Resolution <= 0) return null;
            var world = WebMercator.FromWgs84(coordinate);
            var screen = current.Value.WorldToScreen(world.X, world.Y);
            return new Point(screen.X, screen.Y);
        }

        private static void DrawCameraFramingGuide(
            DrawingContext context,
            GeoCoordinate observer,
            Rect viewportBounds,
            PlanningSnapshot snapshot,
            CameraFramingGuide guide,
            FramingVisibilityAssessment? visibility,
            CameraFramingSettings framingSettings,
            Func<GeoCoordinate, Point?> project)
        {
            var colour = CelestialPalette.Colour(snapshot.Position.Target, CelestialPalette.OrderFor(snapshot, snapshot.Position.Target));
            var layout = WedgeLayout.Calculate(observer, viewportBounds, guide, project, visibility);

            var settings = framingSettings.Normalised();
            DrawShading(context, layout.ShadeRegions, colour, settings.ShadingOpacityPercent);

            var edgeAlpha = guide.HorizontalFieldOfViewDegrees < 3 ? (byte)160 : (byte)128;
            var lineThickness = settings.LineThickness;
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(edgeAlpha, colour.R, colour.G, colour.B)), lineThickness);
            var centre = new Pen(new SolidColorBrush(Color.FromArgb(190, colour.R, colour.G, colour.B)), lineThickness * 1.45);
            var limitedEdge = new Pen(new SolidColorBrush(Color.FromArgb(104, 170, 176, 184)), lineThickness, dashStyle: DashStyle.Dash);
            var limitedCentre = new Pen(new SolidColorBrush(Color.FromArgb(132, 184, 190, 198)), lineThickness * 1.35, dashStyle: DashStyle.Dash);
            DrawRay(context, edge, limitedEdge, layout.LeftBoundary);
            DrawRay(context, edge, limitedEdge, layout.RightBoundary);
            DrawRay(context, centre, limitedCentre, layout.CentreBearing);
            DrawTransition(context, layout.Transition, lineThickness);
        }

        private static void DrawShading(
            DrawingContext context,
            IReadOnlyList<FramingShadeRegion> regions,
            Color colour,
            double opacityPercent)
        {
            var visibleAlpha = (byte)Math.Round(Math.Clamp(opacityPercent, 0, 50) / 100 * byte.MaxValue);
            foreach (var region in regions)
            {
                if (region.Points.Count < 3 || visibleAlpha == 0) continue;
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(region.Points[0], true);
                    for (var index = 1; index < region.Points.Count; index++) path.LineTo(region.Points[index]);
                    path.EndFigure(true);
                }
                var fill = region.State == FramingRaySegmentState.Visible
                    ? Color.FromArgb(visibleAlpha, colour.R, colour.G, colour.B)
                    : Color.FromArgb((byte)Math.Round(visibleAlpha * .55), 150, 157, 166);
                context.DrawGeometry(new SolidColorBrush(fill), null, geometry);
            }
        }

        private static void DrawRay(DrawingContext context, Pen visiblePen, Pen limitedPen, FramingRayGeometry? ray)
        {
            if (ray is null) return;
            foreach (var segment in ray.Segments)
                context.DrawLine(segment.State == FramingRaySegmentState.Visible ? visiblePen : limitedPen,
                    segment.Start, segment.End);
        }

        private static void DrawTransition(DrawingContext context, FramingTransitionGeometry? transition, double lineThickness)
        {
            if (transition?.Left is not Point left || transition.Right is not Point right) return;
            var markerPen = new Pen(new SolidColorBrush(Color.FromArgb(96, 190, 196, 204)),
                Math.Max(.75, lineThickness * .7), dashStyle: DashStyle.Dash);
            context.DrawLine(markerPen, left, right);
            if (transition.Centre is Point centre)
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 202, 207, 214)), null, centre, 2.25, 2.25);
        }

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

internal static class WebMercator
{
    private const double Radius = 6378137;
    public static (double X, double Y) FromWgs84(GeoCoordinate coordinate)
    {
        var latitude = Math.Clamp(coordinate.Latitude, -85.05112878, 85.05112878);
        return (Radius * coordinate.Longitude * Math.PI / 180,
            Radius * Math.Log(Math.Tan(Math.PI / 4 + latitude * Math.PI / 360)));
    }

    public static GeoCoordinate ToWgs84(double x, double y) => new(
        (2 * Math.Atan(Math.Exp(y / Radius)) - Math.PI / 2) * 180 / Math.PI,
        x / Radius * 180 / Math.PI);
}
