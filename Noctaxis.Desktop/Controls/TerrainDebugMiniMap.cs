using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
    public static readonly StyledProperty<double> CentreBearingDegreesProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, double>(nameof(CentreBearingDegrees));
    public static readonly StyledProperty<double> HorizontalFieldOfViewDegreesProperty =
        AvaloniaProperty.Register<TerrainDebugMiniMap, double>(nameof(HorizontalFieldOfViewDegrees), 60);

    static TerrainDebugMiniMap() =>
        AffectsRender<TerrainDebugMiniMap>(ProfileProperty, CentreBearingDegreesProperty,
            HorizontalFieldOfViewDegreesProperty);

    public TerrainHorizonProfile? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
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
        var profile = Profile;
        if (profile is null || bounds.Width < 40 || bounds.Height < 40) return;

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
        var tile = TerrariumTerrainProvider.LocatePixel(profile.Observer, 12).Tile;
        DrawLabel(context, $"observer {tile.Id}  range {displayDistance / 1000:F0} km",
            new Point(7, bounds.Height - 20), Color.Parse("#91A4B8"));
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

    private static void DrawLabel(DrawingContext context, string label, Point point, Color colour)
    {
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 9, new SolidColorBrush(colour));
        context.DrawText(text, point);
    }
}
