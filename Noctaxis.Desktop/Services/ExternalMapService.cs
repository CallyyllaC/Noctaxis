using System.Globalization;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Services;

public sealed class ExternalMapService(IExternalUriLauncher launcher)
{
    public static bool HasValidCoordinates(double? latitude, double? longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    public static Uri? CreateUri(double? latitude, double? longitude, ExternalMapProvider provider)
    {
        if (!HasValidCoordinates(latitude, longitude)) return null;
        var lat = latitude!.Value.ToString("G17", CultureInfo.InvariantCulture);
        var lon = longitude!.Value.ToString("G17", CultureInfo.InvariantCulture);
        var url = provider switch
        {
            ExternalMapProvider.OpenStreetMap => $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=16/{lat}/{lon}",
            ExternalMapProvider.GoogleMaps => $"https://www.google.com/maps/search/?api=1&query={lat}%2C{lon}",
            ExternalMapProvider.Mapillary => $"https://www.mapillary.com/app/?lat={lat}&lng={lon}&z=16&focus=map",
            _ => null
        };
        return url is null ? null : new Uri(url);
    }

    public bool TryOpen(double? latitude, double? longitude, ExternalMapProvider provider, out string? error)
    {
        var uri = CreateUri(latitude, longitude, provider);
        if (uri is null)
        {
            error = "The location or map provider is invalid.";
            return false;
        }
        return launcher.TryOpen(uri, out error);
    }
}
