using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Desktop.Controls;

/// <summary>North-up geographic diagnostic rendered from a pre-sampled production surface grid.</summary>
public sealed class LocalTerrainMap : Control
{
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
        HorizontalFieldOfViewDegreesProperty);

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
        var map = Map;
        if (map is null)
        {
            DrawLabel(context, $"{Observer.Latitude:F5}, {Observer.Longitude:F5}",
                new Point(7, 7), Color.Parse("#D9E5F2"));
            DrawLabel(context, $"generation {Generation}  {LoadState.ToString().ToLowerInvariant()}",
                new Point(7, 20), Color.Parse("#91A4B8"));
            return;
        }

        DrawTerrain(context, bounds, map);
        DrawFieldOfView(context, bounds);
        DrawFirstObstructionFrontier(context, bounds, map, Profile);
        DrawWinningSamples(context, bounds, map, Profile);
        DrawSelectedBearing(context, bounds);

        var centre = bounds.Center;
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#FFF2A8")), new Pen(Brushes.Black, 1),
            centre, 3.5, 3.5);
        DrawLabel(context, "N ↑", new Point(bounds.Width - 26, 6), Color.Parse("#E4ECF5"));
        DrawScale(context, bounds, map.RangeMetres);
        DrawLabel(context, $"radius {map.RangeMetres / 1_000:0.#} km  {map.Width}×{map.Height}",
            new Point(7, 6), Color.Parse("#E4ECF5"));
        DrawLabel(context, $"{map.Observer.Latitude:F5}, {map.Observer.Longitude:F5}  g{Generation}",
            new Point(7, bounds.Height - 12), Color.Parse("#C7D4E2"));
    }

    private static void DrawTerrain(DrawingContext context, Rect bounds, TerrainDebugMapSnapshot map)
    {
        var valid = map.SurfaceElevationsMetres.Where(value => value.HasValue)
            .Select(value => value!.Value).ToArray();
        var minimum = valid.DefaultIfEmpty(0).Min();
        var maximum = valid.DefaultIfEmpty(0).Max();
        var span = Math.Max(1, maximum - minimum);
        var cellWidth = bounds.Width / map.Width;
        var cellHeight = bounds.Height / map.Height;
        var brushes = new Dictionary<Color, SolidColorBrush>();
        for (var row = 0; row < map.Height; row++)
        for (var column = 0; column < map.Width; column++)
        {
            var index = map.Index(row, column);
            var elevation = map.SurfaceElevationsMetres[index];
            var colour = !elevation.HasValue
                ? Color.Parse("#5C2943")
                : map.Classifications[index] == LandCoverClass.PermanentWater
                    ? map.AdjustedSamples[index] ? Color.Parse("#216E91") : Color.Parse("#315F77")
                    : TerrainColour((elevation.Value - minimum) / span);
            if (!brushes.TryGetValue(colour, out var brush))
                brushes[colour] = brush = new SolidColorBrush(colour);
            context.FillRectangle(brush,
                new Rect(column * cellWidth, row * cellHeight,
                    Math.Ceiling(cellWidth + .05), Math.Ceiling(cellHeight + .05)));
        }
    }

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
        var scaleMetres = rangeMetres >= 20_000 ? 10_000 : rangeMetres >= 10_000 ? 5_000 : 2_000;
        var width = scaleMetres / (rangeMetres * 2) * bounds.Width;
        var y = bounds.Height - 26;
        var x = bounds.Width - width - 10;
        var pen = new Pen(Brushes.White, 1.5);
        context.DrawLine(pen, new Point(x, y), new Point(x + width, y));
        context.DrawLine(pen, new Point(x, y - 3), new Point(x, y + 3));
        context.DrawLine(pen, new Point(x + width, y - 3), new Point(x + width, y + 3));
        DrawLabel(context, $"{scaleMetres / 1_000:0.#} km", new Point(x, y - 14), Colors.White);
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
