namespace Noctaxis.Core.Domain;

public static class Angles
{
    public const double DegreesToRadians = Math.PI / 180d;
    public const double RadiansToDegrees = 180d / Math.PI;

    public static double NormaliseDegrees(double degrees)
    {
        var value = degrees % 360d;
        return value < 0 ? value + 360d : value;
    }

    public static double NormaliseSignedDegrees(double degrees)
    {
        if (degrees >= -180d && degrees < 180d) return degrees;
        var value = NormaliseDegrees(degrees);
        return value >= 180d ? value - 360d : value;
    }

    public static double NormaliseLongitude(double longitude) => NormaliseSignedDegrees(longitude);

    public static double InitialBearing(GeoCoordinate from, GeoCoordinate to)
    {
        var lat1 = from.Latitude * DegreesToRadians;
        var lat2 = to.Latitude * DegreesToRadians;
        var deltaLongitude = (to.Longitude - from.Longitude) * DegreesToRadians;
        var y = Math.Sin(deltaLongitude) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLongitude);
        return NormaliseDegrees(Math.Atan2(y, x) * RadiansToDegrees);
    }

    public static GeoCoordinate Destination(GeoCoordinate origin, double bearingDegrees, double distanceMetres)
    {
        const double radius = 6_371_008.8;
        var angular = distanceMetres / radius;
        var bearing = bearingDegrees * DegreesToRadians;
        var lat1 = origin.Latitude * DegreesToRadians;
        var lon1 = origin.Longitude * DegreesToRadians;
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angular) + Math.Cos(lat1) * Math.Sin(angular) * Math.Cos(bearing));
        var lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat1), Math.Cos(angular) - Math.Sin(lat1) * Math.Sin(lat2));
        return new GeoCoordinate(lat2 * RadiansToDegrees, NormaliseLongitude(lon2 * RadiansToDegrees), origin.ElevationMetres);
    }

    public static double GreatCircleDistanceMetres(GeoCoordinate a, GeoCoordinate b)
    {
        const double radius = 6_371_008.8;
        var dLat = (b.Latitude - a.Latitude) * DegreesToRadians;
        var dLon = (b.Longitude - a.Longitude) * DegreesToRadians;
        var lat1 = a.Latitude * DegreesToRadians;
        var lat2 = b.Latitude * DegreesToRadians;
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * radius * Math.Asin(Math.Sqrt(h));
    }
}
