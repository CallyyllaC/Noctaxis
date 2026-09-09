using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Buffers;
using System.Runtime.InteropServices;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Desktop.Controls;

/// <summary>Informational north-up terrain minimap using an immutable production surface grid.</summary>
public sealed class LocalTerrainMap : Control
{
    private const double GlobalReliefSpanFloorMetres = 20;
    private const double MinimumLocalSpanMetres = 6;
    private const double LocalGainReferenceSpanMetres = 30;
    private const double MaximumLocalContrastGain = 5;
    private static readonly Color MissingTerrainColour = Color.Parse("#5C2943");
    private static readonly Color AdjustedWaterColour = Color.Parse("#216E91");
    private static readonly Color WaterColour = Color.Parse("#315F77");

    public static readonly StyledProperty<double> MetresPerPixelProperty =
        AvaloniaProperty.Register<LocalTerrainMap, double>(nameof(MetresPerPixel), double.PositiveInfinity);
    public double MetresPerPixel { get => GetValue(MetresPerPixelProperty); set => SetValue(MetresPerPixelProperty, value); }
    public static readonly StyledProperty<double> MainViewportHeightPixelsProperty =
        AvaloniaProperty.Register<LocalTerrainMap, double>(nameof(MainViewportHeightPixels), double.PositiveInfinity);
    public double MainViewportHeightPixels { get => GetValue(MainViewportHeightPixelsProperty); set => SetValue(MainViewportHeightPixelsProperty, value); }
    public static readonly StyledProperty<double> ContextMultiplierProperty = AvaloniaProperty.Register<LocalTerrainMap, double>(nameof(ContextMultiplier), 2.5);
    public double ContextMultiplier { get => GetValue(ContextMultiplierProperty); set => SetValue(ContextMultiplierProperty, value); }
    public double DisplayedRadiusMetres => Math.Min(Map?.RangeMetres ?? 20_000,
        double.IsFinite(MetresPerPixel) && MetresPerPixel > 0
            ? Math.Clamp(MetresPerPixel * (double.IsFinite(MainViewportHeightPixels) && MainViewportHeightPixels > 0 ? MainViewportHeightPixels / 2 : Bounds.Height / 2) * Math.Clamp(ContextMultiplier, 1, 2.5), 1_000, 20_000)
            : Map?.RangeMetres ?? 20_000);
    public double ScaleBarMetres => ScaleDistance(DisplayedRadiusMetres);
    internal Rect SourceRectangle
    {
        get
        {
            if (Map is not { } map) return default;
            var fraction = DisplayedRadiusMetres / map.RangeMetres;
            return new Rect(map.Width * (1 - fraction) / 2, map.Height * (1 - fraction) / 2,
                map.Width * fraction, map.Height * fraction);
        }
    }
    private WriteableBitmap? _raster;
    private TerrainDebugMapSnapshot? _rasterSource;
    private TerrainDebugMapSnapshot? _contrastSource;
    private TerrainDebugMapSnapshot? _globalSource;
    private double _globalMinimum;
    private double _globalSpan = GlobalReliefSpanFloorMetres;
    private Rect _contrastRectangle;
    private TerrainContrastAnalysis _contrastAnalysis;
    internal int RasterBuildCount { get; private set; }
    internal Bitmap? DisplayBitmap => _raster;
    internal int VisibleValidCellCount => _contrastAnalysis.ValidCellCount;
    internal double? VisibleMinimumElevationMetres => _contrastAnalysis.MinimumElevationMetres;
    internal double? VisibleMaximumElevationMetres => _contrastAnalysis.MaximumElevationMetres;
    internal double? VisibleFifthPercentileMetres => _contrastAnalysis.FifthPercentileMetres;
    internal double? VisibleNinetyFifthPercentileMetres => _contrastAnalysis.NinetyFifthPercentileMetres;
    internal double? VisibleEffectiveElevationRangeMetres => _contrastAnalysis.EffectiveRangeMetres;
    internal double EffectiveLocalContrastGain => _contrastAnalysis.EffectiveGain;
    internal double EffectiveLocalContrastBlend => _contrastAnalysis.EffectiveBlend;

    public LocalTerrainMap()
    {
        DetachedFromVisualTree += (_, _) =>
        {
            _raster?.Dispose();
            _raster = null;
            _rasterSource = null;
            _contrastSource = null;
            _globalSource = null;
        };
    }
    public static readonly StyledProperty<TerrainDebugMapSnapshot?> MapProperty =
        AvaloniaProperty.Register<LocalTerrainMap, TerrainDebugMapSnapshot?>(nameof(Map));
    public static readonly StyledProperty<TerrainHorizonProfile?> ProfileProperty =
        AvaloniaProperty.Register<LocalTerrainMap, TerrainHorizonProfile?>(nameof(Profile));
    public static readonly StyledProperty<GeoCoordinate> ObserverProperty =
        AvaloniaProperty.Register<LocalTerrainMap, GeoCoordinate>(nameof(Observer));
    public static readonly StyledProperty<long> GenerationProperty =
        AvaloniaProperty.Register<LocalTerrainMap, long>(nameof(Generation));
    public static readonly StyledProperty<TerrainDebugMapLoadState> LoadStateProperty =
        AvaloniaProperty.Register<LocalTerrainMap, TerrainDebugMapLoadState>(nameof(LoadState),
            TerrainDebugMapLoadState.Disabled);
    public static readonly StyledProperty<double> CentreBearingDegreesProperty =
        AvaloniaProperty.Register<LocalTerrainMap, double>(nameof(CentreBearingDegrees));
    public static readonly StyledProperty<double> HorizontalFieldOfViewDegreesProperty =
        AvaloniaProperty.Register<LocalTerrainMap, double>(nameof(HorizontalFieldOfViewDegrees), 60);

    static LocalTerrainMap() => AffectsRender<LocalTerrainMap>(MapProperty, ProfileProperty,
        ObserverProperty, GenerationProperty, LoadStateProperty, CentreBearingDegreesProperty,
        HorizontalFieldOfViewDegreesProperty, MetresPerPixelProperty, MainViewportHeightPixelsProperty, ContextMultiplierProperty);

    public TerrainDebugMapSnapshot? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public TerrainHorizonProfile? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public GeoCoordinate Observer
    {
        get => GetValue(ObserverProperty);
        set => SetValue(ObserverProperty, value);
    }

    public long Generation
    {
        get => GetValue(GenerationProperty);
        set => SetValue(GenerationProperty, value);
    }

    public TerrainDebugMapLoadState LoadState
    {
        get => GetValue(LoadStateProperty);
        set => SetValue(LoadStateProperty, value);
    }

    public double CentreBearingDegrees
    {
        get => GetValue(CentreBearingDegreesProperty);
        set => SetValue(CentreBearingDegreesProperty, value);
    }

    public double HorizontalFieldOfViewDegrees
    {
        get => GetValue(HorizontalFieldOfViewDegreesProperty);
        set => SetValue(HorizontalFieldOfViewDegreesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0A1018")), bounds);
        if (bounds.Width < 40 || bounds.Height < 40) return;
        var map = LoadState == TerrainDebugMapLoadState.Ready ? Map : null;
        if (map is null)
        {
            DrawLabel(context, LoadState switch
            {
                TerrainDebugMapLoadState.Disabled => "Terrain calculations disabled",
                TerrainDebugMapLoadState.Resolving => "Resolving terrain…",
                _ => "Terrain unavailable"
            }, new Point(7, bounds.Height / 2), Color.Parse("#91A4B8"));
            return;
        }

        DrawTerrain(context, bounds, map);
        DrawFieldOfView(context, bounds);
        DrawSelectedBearing(context, bounds);

        var centre = bounds.Center;
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#FFF2A8")), new Pen(Brushes.Black, 1),
            centre, 3.5, 3.5);
        DrawLabel(context, "N ↑", new Point(bounds.Width - 26, 6), Color.Parse("#E4ECF5"));
        DrawScale(context, bounds, DisplayedRadiusMetres);
        DrawLabel(context, DisplayedRadiusMetres >= 1000
                ? $"{DisplayedRadiusMetres / 1_000:0.##} km radius" : $"{DisplayedRadiusMetres:0.#} m radius",
            new Point(7, 6), Color.Parse("#E4ECF5"));
    }

    private void DrawTerrain(DrawingContext context, Rect bounds, TerrainDebugMapSnapshot map)
    {
        if (!ReferenceEquals(_rasterSource, map))
        {
            _raster?.Dispose();
            _raster = new WriteableBitmap(new PixelSize(map.Width, map.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            _rasterSource = map;
            RasterBuildCount++;
        }
        var source = SourceRectangle;
        if (!ReferenceEquals(_contrastSource, map) || _contrastRectangle != source)
        {
            _contrastAnalysis = AnalyseVisibleCrop(map, source);
            WriteRaster(map, _contrastAnalysis);
            _contrastSource = map;
            _contrastRectangle = source;
        }
        using (context.PushClip(bounds))
            context.DrawImage(_raster!, SourceRectangle, bounds);
    }

    private void WriteRaster(TerrainDebugMapSnapshot map, TerrainContrastAnalysis analysis)
    {
        if (_raster is null) return;
        if (!ReferenceEquals(_globalSource, map))
        {
            var globalMinimum = 0d;
            var globalMaximum = 0d;
            var hasGlobal = false;
            for (var index = 0; index < map.SurfaceElevationsMetres.Count; index++)
            {
                if (map.SurfaceElevationsMetres[index] is not double elevation ||
                    !IsUsableStatus(map.SampleStatuses[index])) continue;
                if (!hasGlobal) { globalMinimum = globalMaximum = elevation; hasGlobal = true; }
                else { globalMinimum = Math.Min(globalMinimum, elevation); globalMaximum = Math.Max(globalMaximum, elevation); }
            }
            _globalMinimum = hasGlobal ? globalMinimum : 0;
            _globalSpan = hasGlobal ? Math.Max(GlobalReliefSpanFloorMetres, globalMaximum - globalMinimum) : GlobalReliefSpanFloorMetres;
            _globalSource = map;
        }
        using var pixels = _raster.Lock();
        for (var row = 0; row < map.Height; row++)
        for (var column = 0; column < map.Width; column++)
        {
            var index = map.Index(row, column);
            var elevation = map.SurfaceElevationsMetres[index];
            var colour = !elevation.HasValue
                ? MissingTerrainColour
                : map.Classifications[index] == LandCoverClass.PermanentWater
                    ? map.AdjustedSamples[index] ? AdjustedWaterColour : WaterColour
                    : TerrainColour(BlendTerrainValue((elevation.Value - _globalMinimum) / _globalSpan,
                        elevation.Value, analysis));
            Marshal.WriteInt32(pixels.Address, row * pixels.RowBytes + column * 4,
                unchecked((int)0xff000000) | colour.R << 16 | colour.G << 8 | colour.B);
        }
    }

    private static double BlendTerrainValue(double globalValue, double elevation,
        TerrainContrastAnalysis analysis)
    {
        if (analysis.EffectiveBlend <= 0 || analysis.NinetyFifthPercentileMetres is not double p95 ||
            analysis.FifthPercentileMetres is not double p5 || p95 <= p5)
            return Math.Clamp(globalValue, 0, 1);
        var midpoint = (p5 + p95) / 2;
        // Gain expresses the slope relative to a 30 m reference; the 6 m floor
        // prevents small variations from being stretched across the whole palette.
        var localValue = .5 + (elevation - midpoint) / LocalGainReferenceSpanMetres * analysis.EffectiveGain;
        localValue = Math.Clamp(localValue, 0, 1);
        return Math.Clamp(globalValue * (1 - analysis.EffectiveBlend) + localValue * analysis.EffectiveBlend, 0, 1);
    }

    private static TerrainContrastAnalysis AnalyseVisibleCrop(TerrainDebugMapSnapshot map, Rect source)
    {
        var capacity = Math.Max(1, map.CellCount);
        var values = ArrayPool<double>.Shared.Rent(capacity);
        try
        {
            var left = Math.Max(0, (int)Math.Floor(source.Left));
            var right = Math.Min(map.Width, (int)Math.Ceiling(source.Right));
            var top = Math.Max(0, (int)Math.Floor(source.Top));
            var bottom = Math.Min(map.Height, (int)Math.Ceiling(source.Bottom));
            var count = 0;
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            for (var row = top; row < bottom; row++)
            for (var column = left; column < right; column++)
            {
                var centreX = column + .5;
                var centreY = row + .5;
                if (centreX < source.Left || centreX > source.Right || centreY < source.Top || centreY > source.Bottom) continue;
                var index = map.Index(row, column);
                var elevation = map.SurfaceElevationsMetres[index];
                if (elevation is not double value || !IsUsableStatus(map.SampleStatuses[index]) ||
                    map.Classifications[index] == LandCoverClass.PermanentWater) continue;
                values[count++] = value;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
            if (count == 0)
            return new TerrainContrastAnalysis(0, null, null, null, null, 0, 0, 0);
            Array.Sort(values, 0, count);
            var p5 = values[(int)Math.Floor((count - 1) * .05)];
            var p95 = values[(int)Math.Ceiling((count - 1) * .95)];
            var range = Math.Max(0, p95 - p5);
            var span = Math.Max(range, MinimumLocalSpanMetres);
            var gain = Math.Min(MaximumLocalContrastGain, LocalGainReferenceSpanMetres / span);
            var blend = range <= 60
                ? .80 - .15 * SmoothStep((range - 6) / 54)
                : .65 - .25 * SmoothStep((range - 60) / 240);
            return new TerrainContrastAnalysis(count, minimum, maximum, p5, p95, span, gain, blend);
        }
        finally { ArrayPool<double>.Shared.Return(values); }
    }

    private readonly record struct TerrainContrastAnalysis(int ValidCellCount,
        double? MinimumElevationMetres, double? MaximumElevationMetres,
        double? FifthPercentileMetres, double? NinetyFifthPercentileMetres,
        double EffectiveRangeMetres, double EffectiveGain, double EffectiveBlend);

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static bool IsUsableStatus(TerrainSampleStatus status) =>
        status is not (TerrainSampleStatus.NoData or TerrainSampleStatus.Water or
            TerrainSampleStatus.Error or TerrainSampleStatus.Unavailable);

    private void DrawFieldOfView(DrawingContext context, Rect bounds)
    {
        var centre = bounds.Center;
        var radius = Math.Min(bounds.Width, bounds.Height) / 2;
        var half = Math.Clamp(HorizontalFieldOfViewDegrees, 0, 180) / 2;
        var left = BearingPoint(centre, radius, CentreBearingDegrees - half);
        var right = BearingPoint(centre, radius, CentreBearingDegrees + half);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(centre, true);
            figure.LineTo(left);
            figure.LineTo(right);
            figure.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#245FCBEA")),
            new Pen(new SolidColorBrush(Color.Parse("#88DCF4")), 1), geometry);
    }

    private void DrawSelectedBearing(DrawingContext context, Rect bounds)
    {
        var centre = bounds.Center;
        var radius = Math.Min(bounds.Width, bounds.Height) / 2;
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#F5D070")), 1.2), centre,
            BearingPoint(centre, radius, CentreBearingDegrees));
    }

    private static void DrawWinningSamples(DrawingContext context, Rect bounds,
        TerrainDebugMapSnapshot map, TerrainHorizonProfile? profile)
    {
        if (profile is null) return;
        var brush = new SolidColorBrush(Color.Parse("#FFB45D"));
        foreach (var sample in profile.Samples)
        {
            if (sample.EffectiveHorizonFeatureDistanceMetres is not double distance ||
                distance > map.RangeMetres) continue;
            var coordinate = Angles.Destination(profile.Observer, sample.BearingDegrees, distance);
            var local = LocalTerrainMapProjection.ToLocalMetres(map.Observer, coordinate);
            var point = new Point(bounds.Center.X + local.EastMetres / map.RangeMetres * bounds.Width / 2,
                bounds.Center.Y - local.NorthMetres / map.RangeMetres * bounds.Height / 2);
            if (bounds.Contains(point)) context.DrawEllipse(brush, null, point, 1.7, 1.7);
        }
    }

    private static void DrawFirstObstructionFrontier(DrawingContext context, Rect bounds,
        TerrainDebugMapSnapshot map, TerrainHorizonProfile? profile)
    {
        if (profile is null || profile.Samples.Count < 2) return;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#F85F68")), 1.2);
        for (var index = 0; index < profile.Samples.Count; index++)
        {
            var next = (index + 1) % profile.Samples.Count;
            var first = FirstHitPoint(profile, profile.Samples[index].BearingDegrees, map, bounds);
            var second = FirstHitPoint(profile, profile.Samples[next].BearingDegrees, map, bounds);
            if (first.HasValue && second.HasValue)
                context.DrawLine(pen, first.Value, second.Value);
        }
    }

    private static Point? FirstHitPoint(TerrainHorizonProfile profile, double bearingDegrees,
        TerrainDebugMapSnapshot map, Rect bounds)
    {
        var distance = profile.TerrainObstructionAt(bearingDegrees)
            .EffectiveFirstObstructionDistanceMetres;
        if (distance is not double firstHit || firstHit <= 0 || firstHit > map.RangeMetres)
            return null;
        var coordinate = Angles.Destination(profile.Observer, bearingDegrees, firstHit);
        var local = LocalTerrainMapProjection.ToLocalMetres(map.Observer, coordinate);
        return new Point(bounds.Center.X + local.EastMetres / map.RangeMetres * bounds.Width / 2,
            bounds.Center.Y - local.NorthMetres / map.RangeMetres * bounds.Height / 2);
    }

    private static void DrawScale(DrawingContext context, Rect bounds, double rangeMetres)
    {
        var scaleMetres = ScaleDistance(rangeMetres);
        var width = scaleMetres / (rangeMetres * 2) * bounds.Width;
        var y = bounds.Height - 26;
        var x = bounds.Width - width - 10;
        var pen = new Pen(Brushes.White, 1.5);
        context.DrawLine(pen, new Point(x, y), new Point(x + width, y));
        context.DrawLine(pen, new Point(x, y - 3), new Point(x, y + 3));
        context.DrawLine(pen, new Point(x + width, y - 3), new Point(x + width, y + 3));
        DrawLabel(context, scaleMetres >= 1000 ? $"{scaleMetres / 1_000:0.#} km" : $"{scaleMetres:0.#} m",
            new Point(x, y - 14), Colors.White);
    }

    private static double ScaleDistance(double radius)
    {
        var target = radius / 2;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var leading = target / magnitude;
        return (leading >= 5 ? 5 : leading >= 2 ? 2 : 1) * magnitude;
    }

    internal static Point BearingPoint(Point centre, double radius, double bearingDegrees)
    {
        var radians = bearingDegrees * Angles.DegreesToRadians;
        return new Point(centre.X + Math.Sin(radians) * radius,
            centre.Y - Math.Cos(radians) * radius);
    }

    private static Color TerrainColour(double value)
    {
        var shade = (byte)Math.Round(42 + Math.Clamp(value, 0, 1) * 174);
        return Color.FromRgb(shade, shade, shade);
    }

    private static void DrawLabel(DrawingContext context, string label, Point point, Color colour)
    {
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 9, new SolidColorBrush(colour));
        context.DrawText(text, point);
    }
}
