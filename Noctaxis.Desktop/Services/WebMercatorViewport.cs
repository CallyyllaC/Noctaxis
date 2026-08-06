using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Services;

public readonly record struct MapPixelPoint(double X, double Y);

public sealed record MapGeographicBounds(double South, double West, double North, double East)
{
    public bool Contains(double latitude, double longitude)
    {
        if (latitude < South || latitude > North) return false;
        return West <= East
            ? longitude >= West && longitude <= East
            : longitude >= West || longitude <= East;
    }
}

/// <summary>An immutable Web-Mercator viewport shared by raster capture and semantic overlays.</summary>
public sealed record WebMercatorViewport
{
    public const double MaximumLatitude = 85.05112878;

    public WebMercatorViewport(int zoom, int tileSize, int width, int height,
        double centreLatitude, double centreLongitude)
    {
        if (zoom is < 0 or > 22) throw new ArgumentOutOfRangeException(nameof(zoom));
        if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Zoom = zoom;
        TileSize = tileSize;
        Width = width;
        Height = height;
        CentreLatitude = Math.Clamp(centreLatitude, -MaximumLatitude, MaximumLatitude);
        CentreLongitude = NormaliseLongitude(centreLongitude);
        WorldPixelSize = tileSize * Math.Pow(2, zoom);
        var centre = ProjectWorld(CentreLatitude, CentreLongitude);
        WorldPixelCentreX = centre.X;
        WorldPixelCentreY = centre.Y;
        WorldPixelLeft = centre.X - width / 2d;
        WorldPixelTop = centre.Y - height / 2d;
        Bounds = CalculateBounds();
    }

    public int Zoom { get; }
    public int TileSize { get; }
    public int Width { get; }
    public int Height { get; }
    public double CentreLatitude { get; }
    public double CentreLongitude { get; }
    public double WorldPixelSize { get; }
    public double WorldPixelCentreX { get; }
    public double WorldPixelCentreY { get; }
    public double WorldPixelLeft { get; }
    public double WorldPixelTop { get; }
    public MapGeographicBounds Bounds { get; }

    public static WebMercatorViewport Create(GeoCoordinate centre, int zoom, int tileSize, int width, int height) =>
        new(zoom, tileSize, width, height, centre.Latitude, centre.Longitude);

    public MapPixelPoint Project(double latitude, double longitude)
    {
        var world = ProjectWorld(latitude, longitude);
        var centreWorldX = WorldPixelLeft + Width / 2d;
        while (world.X - centreWorldX > WorldPixelSize / 2d) world = world with { X = world.X - WorldPixelSize };
        while (centreWorldX - world.X > WorldPixelSize / 2d) world = world with { X = world.X + WorldPixelSize };
        return new MapPixelPoint(world.X - WorldPixelLeft, world.Y - WorldPixelTop);
    }

    public GeoCoordinate Unproject(double x, double y)
    {
        var worldX = WorldPixelLeft + x;
        var worldY = Math.Clamp(WorldPixelTop + y, 0, WorldPixelSize);
        var longitude = NormaliseLongitude(worldX / WorldPixelSize * 360d - 180d);
        var mercator = Math.PI * (1d - 2d * worldY / WorldPixelSize);
        var latitude = Math.Atan(Math.Sinh(mercator)) * 180d / Math.PI;
        return new GeoCoordinate(latitude, longitude);
    }

    public bool ContainsPixel(MapPixelPoint point) =>
        point.X >= 0 && point.X <= Width && point.Y >= 0 && point.Y <= Height;

    public MapGeographicBounds CalculateBounds()
    {
        var topLeft = Unproject(0, 0);
        var bottomRight = Unproject(Width, Height);
        return new MapGeographicBounds(bottomRight.Latitude, topLeft.Longitude,
            topLeft.Latitude, bottomRight.Longitude);
    }

    private MapPixelPoint ProjectWorld(double latitude, double longitude)
    {
        latitude = Math.Clamp(latitude, -MaximumLatitude, MaximumLatitude);
        longitude = NormaliseLongitude(longitude);
        var x = (longitude + 180d) / 360d * WorldPixelSize;
        var radians = latitude * Math.PI / 180d;
        var y = (1d - Math.Asinh(Math.Tan(radians)) / Math.PI) / 2d * WorldPixelSize;
        return new MapPixelPoint(x, y);
    }

    private static double NormaliseLongitude(double longitude)
    {
        longitude %= 360d;
        if (longitude < -180d) longitude += 360d;
        if (longitude >= 180d) longitude -= 360d;
        return longitude;
    }
}
