using Noctaxis.Core.Localisation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.ViewModels;

public sealed record ExternalMapProviderOption(ExternalMapProvider Provider, string Label);

public partial class MainViewModel
{
    private readonly ExternalMapService _externalMaps;
    public IReadOnlyList<ExternalMapProviderOption> ExternalMapProviders { get; } =
    [
        new(ExternalMapProvider.OpenStreetMap, "OpenStreetMap"),
        new(ExternalMapProvider.GoogleMaps, "Google Maps"),
        new(ExternalMapProvider.Mapillary, "Mapillary")
    ];

    private bool CanOpenInMaps() => ExternalMapService.HasValidCoordinates(Latitude, Longitude);

    [RelayCommand(CanExecute = nameof(CanOpenInMaps))]
    private void OpenInMaps() => OpenMapCoordinates(Latitude, Longitude);

    private bool CanOpenMapCoordinate(GeoCoordinate? coordinate) => coordinate is { } value &&
        ExternalMapService.HasValidCoordinates(value.Latitude, value.Longitude);

    /// <summary>Open a map-menu position without changing the Planner's observer or session.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenMapCoordinate))]
    private void OpenMapCoordinate(GeoCoordinate? coordinate)
    {
        if (coordinate is { } value && CanOpenMapCoordinate(value)) OpenMapCoordinates(value.Latitude, value.Longitude);
    }

    private void OpenMapCoordinates(double? latitude, double? longitude)
    {
        if (_externalMaps.TryOpen(latitude, longitude, Settings.ExternalMapProvider, out var error)) return;
        StatusMessage = Strings.Format("Planner_Location_OpenMapsFailed_Format", error);
    }

    /// <summary>A map credit's source or licence page, from Map data &amp; licences.</summary>
    [RelayCommand]
    private void OpenMapDataLink(Uri? uri)
    {
        if (uri is null || _externalUriLauncher.TryOpen(uri, out var error)) return;
        StatusMessage = Strings.Format("Planner_Map_OpenLinkFailed_Format", uri.Host, error);
    }
}
