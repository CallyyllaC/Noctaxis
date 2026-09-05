using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Developer-only plan view built entirely from the production horizon profile. It performs no
/// downloads or terrain sampling of its own, so the picture and horizon calculations cannot drift.
/// </summary>
public sealed class TerrainDebugMiniMap : Control
{
    public static readonly StyledProperty<TerrainHorizonProfile?> ProfileProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, TerrainHorizonProfile?>(nameof(Profile));
    public static readonly StyledProperty<GeoCoordinate> ObserverProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, GeoCoordinate>(nameof(Observer));
    public static readonly StyledProperty<long> GenerationProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, long>(nameof(Generation));
    public static readonly StyledProperty<double?> TargetAltitudeDegreesProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, double?>(nameof(TargetAltitudeDegrees));
    public static readonly StyledProperty<double> CentreBearingDegreesProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, double>(nameof(CentreBearingDegrees));
    public static readonly StyledProperty<double> HorizontalFieldOfViewDegreesProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, double>(nameof(HorizontalFieldOfViewDegrees), 60);
    public static readonly StyledProperty<double?> WeatherVisibilityDistanceMetresProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, double?>(nameof(WeatherVisibilityDistanceMetres));

    static TerrainDebugMiniMap() =>
        AffectsRender<TerrainDebugMiniMap>(ProfileProperty, ObserverProperty, GenerationProperty,
            TargetAltitudeDegreesProperty, CentreBearingDegreesProperty, HorizontalFieldOfViewDegreesProperty,
            WeatherVisibilityDistanceMetresProperty);

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

    public double? TargetAltitudeDegrees
    {
        get => GetValue(TargetAltitudeDegreesProperty);
        set => SetValue(TargetAltitudeDegreesProperty, value);
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

    public double? WeatherVisibilityDistanceMetres
    {
        get => GetValue(WeatherVisibilityDistanceMetresProperty);
        set => SetValue(WeatherVisibilityDistanceMetresProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0A1018")), bounds);
        var profile = Profile;
        if (bounds.Width < 40 || bounds.Height < 40) return;
        if (profile is null)
        {
            DrawLabel(context, $"observer {Observer.Latitude:F5}, {Observer.Longitude:F5}",
                new Point(7, 7), Color.Parse("#D9E5F2"));
            DrawLabel(context, $"generation {Generation}  resolving…",
                new Point(7, 20), Color.Parse("#91A4B8"));
            return;
        }

        var centre = bounds.Center;
        var radius = Math.Max(1, Math.Min(bounds.Width, bounds.Height) * .43);
        var displayDistance = Math.Min(Math.Max(profile.MaximumAnalysisDistanceMetres, 1_000), 20_000);
        var elevations = profile.Samples.SelectMany(sample => sample.Sightline ?? [])
            .Where(point => point.DistanceMetres <= displayDistance && point.GroundElevationMetres.HasValue)
            .Select(point => point.GroundElevationMetres!.Value).ToArray();
        var minimum = elevations.DefaultIfEmpty(0).Min();
        var maximum = elevations.DefaultIfEmpty(0).Max();

        DrawTileGrid(context, profile.Observer, displayDistance, centre, radius);
        DrawTerrainSamples(context, profile, displayDistance, centre, radius, minimum, maximum);
        DrawRangeRings(context, centre, radius);
        DrawFieldOfView(context, centre, radius);
        DrawWinningSamples(context, profile, displayDistance, centre, radius);

        context.DrawEllipse(new SolidColorBrush(Color.Parse("#FFF2A8")), new Pen(Brushes.Black, 1),
            centre, 4, 4);
        DrawLabel(context, $"Terrarium z12  {minimum:F0}..{maximum:F0} m", new Point(7, 6),
            Color.Parse("#D9E5F2"));
        DrawLabel(context, $"polar profile  radius {displayDistance / 1000:0.#} km", new Point(7, 30),
            Color.Parse("#B7C7D9"));
        DrawLabel(context,
            $"weather {Distance(WeatherVisibilityDistanceMetres)}  cone {LocalHorizonCalculator.MaximumTerrainCastDistanceMetres / 1000:0} km",
            new Point(7, 42), Color.Parse("#91A4B8"));
        var observerDiagnostics = profile.ObserverDiagnostics;
        DrawLabel(context,
            $"raw {observerDiagnostics?.TerrainSample.InterpolatedElevationMetres:0.0}  surface {observerDiagnostics?.ResolvedSurfaceElevationMetres:0.0}  {observerDiagnostics?.Classification?.ToString() ?? "unknown"}{(observerDiagnostics?.SurfaceWasAdjusted == true ? " adjusted" : string.Empty)}",
            new Point(7, 18), Color.Parse("#91CFE8"));
        var tile = TerrariumTerrainProvider.LocatePixel(profile.Observer, 12).Tile;
        var horizon = profile.EffectiveAltitudeAt(CentreBearingDegrees);
        var geometry = profile.TerrainObstructionAt(CentreBearingDegrees);
        var occultation = TargetAltitudeDegrees is double targetAltitude
            ? profile.OccultationAt(CentreBearingDegrees, targetAltitude)
            : default;
        var occulted = TargetAltitudeDegrees.HasValue
            ? occultation.EffectiveFirstObstructionDistanceMetres.HasValue.ToString()
            : "n/a";
        DrawLabel(context,
            $"az {Angles.NormaliseDegrees(CentreBearingDegrees):F1}°  horizon {Angle(horizon)}  geometry {Distance(geometry.EffectiveFirstObstructionDistanceMetres)}",
            new Point(7, bounds.Height - 33), Color.Parse("#B7C7D9"));
        DrawLabel(context,
            $"target {Angle(TargetAltitudeDegrees)}  occulted {occulted}",
            new Point(7, bounds.Height - 20), Color.Parse("#B7C7D9"));
        DrawLabel(context,
            $"{profile.Observer.Latitude:F5},{profile.Observer.Longitude:F5}  g{Generation}  {tile.Id}",
            new Point(7, bounds.Height - 7), Color.Parse("#91A4B8"));
    }

    private static void DrawTerrainSamples(DrawingContext context, TerrainHorizonProfile profile,
        double displayDistance, Point centre, double radius, double minimum, double maximum)
    {
        var span = Math.Max(1, maximum - minimum);
        for (var bearingIndex = 0; bearingIndex < profile.Samples.Count; bearingIndex += 2)
        {
            var sample = profile.Samples[bearingIndex];
            var line = sample.Sightline ?? [];
            for (var index = 0; index < line.Count; index += 2)
            {
                var point = line[index];
                if (point.DistanceMetres > displayDistance) break;
                var screen = Project(centre, radius, point.DistanceMetres / displayDistance,
                    sample.BearingDegrees);
                if (!point.GroundElevationMetres.HasValue)
                {
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#D05C8D")), .8),
                        new Rect(screen.X - 1.5, screen.Y - 1.5, 3, 3));
                    continue;
                }
                if (point.Classification == LandCoverClass.PermanentWater)
                {
                    var waterColour = point.SurfaceWasAdjusted ? Color.Parse("#238EC2") : Color.Parse("#326A89");
                    context.FillRectangle(new SolidColorBrush(waterColour),
                        new Rect(screen.X - 1.3, screen.Y - 1.3, 2.6, 2.6));
                    continue;
                }
                var fraction = Math.Clamp((point.GroundElevationMetres.Value - minimum) / span, 0, 1);
                var colour = ElevationColour(fraction);
                context.FillRectangle(new SolidColorBrush(colour),
                    new Rect(screen.X - 1.2, screen.Y - 1.2, 2.4, 2.4));
            }
        }
    }

    private static void DrawTileGrid(DrawingContext context, GeoCoordinate observer,
        double displayDistance, Point centre, double radius)
    {
        const int zoom = 12;
        var tileCount = 1 << zoom;
        var north = Angles.Destination(observer, 0, displayDistance);
        var south = Angles.Destination(observer, 180, displayDistance);
        var east = Angles.Destination(observer, 90, displayDistance);
        var west = Angles.Destination(observer, 270, displayDistance);
        var minX = TerrariumTerrainProvider.LocatePixel(west, zoom).Tile.X;
        var maxX = TerrariumTerrainProvider.LocatePixel(east, zoom).Tile.X;
        var minY = TerrariumTerrainProvider.LocatePixel(north, zoom).Tile.Y;
        var maxY = TerrariumTerrainProvider.LocatePixel(south, zoom).Tile.Y;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#334B62")), .7);

        if (maxX >= minX)
            for (var x = minX; x <= maxX + 1; x++)
            {
                var longitude = x / (double)tileCount * 360 - 180;
                var metresEast = (longitude - observer.Longitude) * 111_195 *
                                 Math.Cos(observer.Latitude * Angles.DegreesToRadians);
                var screenX = centre.X + metresEast / displayDistance * radius;
                context.DrawLine(pen, new Point(screenX, centre.Y - radius),
                    new Point(screenX, centre.Y + radius));
            }
        for (var y = minY; y <= maxY + 1; y++)
        {
            var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / (double)tileCount))) *
                           Angles.RadiansToDegrees;
            var metresNorth = (latitude - observer.Latitude) * 111_195;
            var screenY = centre.Y - metresNorth / displayDistance * radius;
            context.DrawLine(pen, new Point(centre.X - radius, screenY),
                new Point(centre.X + radius, screenY));
        }
    }

    private static void DrawRangeRings(DrawingContext context, Point centre, double radius)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#42566C")), .7);
        context.DrawEllipse(null, pen, centre, radius * .5, radius * .5);
        context.DrawEllipse(null, pen, centre, radius, radius);
        foreach (var bearing in new[] { 0d, 90, 180, 270 })
        {
            var end = Project(centre, radius, 1, bearing);
            context.DrawLine(pen, centre, end);
            DrawLabel(context, bearing switch { 0 => "N", 90 => "E", 180 => "S", _ => "W" },
                Project(centre, radius + 8, 1, bearing) - new Point(4, 7), Color.Parse("#9AAABE"));
        }
    }

    private void DrawFieldOfView(DrawingContext context, Point centre, double radius)
    {
        var half = Math.Clamp(HorizontalFieldOfViewDegrees, 0, 180) / 2;
        var left = Project(centre, radius, 1, CentreBearingDegrees - half);
        var right = Project(centre, radius, 1, CentreBearingDegrees + half);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(centre, true);
            figure.LineTo(left);
            figure.LineTo(right);
            figure.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#285FCBEA")),
            new Pen(new SolidColorBrush(Color.Parse("#7EDCF3")), 1), geometry);
    }

    private static void DrawWinningSamples(DrawingContext context, TerrainHorizonProfile profile,
        double displayDistance, Point centre, double radius)
    {
        var brush = new SolidColorBrush(Color.Parse("#FFB45D"));
        foreach (var sample in profile.Samples)
        {
            if (sample.EffectiveHorizonFeatureDistanceMetres is not double distance ||
                distance > displayDistance) continue;
            var point = Project(centre, radius, distance / displayDistance, sample.BearingDegrees);
            context.DrawEllipse(brush, null, point, 1.8, 1.8);
        }
    }

    private static Point Project(Point centre, double radius, double fraction, double bearing)
    {
        var radians = bearing * Angles.DegreesToRadians;
        return new Point(centre.X + Math.Sin(radians) * radius * fraction,
            centre.Y - Math.Cos(radians) * radius * fraction);
    }

    private static Color ElevationColour(double value)
    {
        if (value < .25) return Color.Parse("#1F5870");
        if (value < .5) return Color.Parse("#3A755F");
        if (value < .75) return Color.Parse("#918052");
        return Color.Parse("#D7D0B8");
    }

    private static string Angle(double? value) => value is double angle ? $"{angle:F2}°" : "n/a";
    private static string Distance(double? value) => value is double distance ? $"{distance:F0}m" : "clear";

    private static void DrawLabel(DrawingContext context, string label, Point point, Color colour)
    {
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 9, new SolidColorBrush(colour));
        context.DrawText(text, point);
    }
}
