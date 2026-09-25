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

    [ObservableProperty] private ExternalMapProviderOption? _settingsExternalMapProvider;

    private bool CanOpenInMaps() => ExternalMapService.HasValidCoordinates(Latitude, Longitude);

    [RelayCommand(CanExecute = nameof(CanOpenInMaps))]
    private void OpenInMaps()
    {
        if (_externalMaps.TryOpen(Latitude, Longitude, Settings.ExternalMapProvider, out var error)) return;
        StatusMessage = $"Could not open Maps: {error}";
    }
}
