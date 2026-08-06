using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Export;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using Noctaxis.Core.Locations;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices();
            var viewModel = _services.GetRequiredService<MainViewModel>();
            var dialogs = _services.GetRequiredService<DesktopDialogService>();
            desktop.MainWindow = new MainWindow(viewModel, dialogs);
            desktop.Exit += async (_, _) => await viewModel.PersistAsync(CancellationToken.None);
            _ = viewModel.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<ITimeZoneResolver, TimeZoneResolver>();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<ITargetCatalogue, OpenNgcTargetCatalogue>();
        services.AddSingleton<ITargetSearchService, LocalTargetSearchService>();
        services.AddSingleton<IAstronomyService, AstronomyEngineService>();
        services.AddSingleton<ILensCalculator, LensCalculator>();
        services.AddSingleton<ICameraFramingGuideCalculator, CameraFramingGuideCalculator>();
        services.AddSingleton<IFramingVisibilityCalculator, FramingVisibilityCalculator>();
        services.AddSingleton<IDemDirectoryProvider, DemDirectoryProvider>();
        services.AddSingleton<IElevationSource, SrtmElevationSource>();
        services.AddSingleton<ITerrainHorizonProvider, SrtmTerrainHorizonProvider>();
        services.AddSingleton<IGeographicWeatherCache, GeographicWeatherCache>();
        services.AddHttpClient<IWeatherProvider, OpenMeteoWeatherProvider>(client =>
        {
            client.BaseAddress = new Uri("https://api.open-meteo.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/1.0 (desktop photography planner)");
        });
        services.AddHttpClient<ILocationSearchProvider, OpenMeteoLocationSearchProvider>(client =>
        {
            client.BaseAddress = new Uri("https://geocoding-api.open-meteo.com/v1/");
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/1.0 (desktop photography planner)");
        });
        services.AddHttpClient<IReverseGeocodingProvider, NominatimReverseGeocodingProvider>(client =>
        {
            var configuredEndpoint = Environment.GetEnvironmentVariable("NOCTAXIS_REVERSE_GEOCODING_URL");
            client.BaseAddress = Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var endpoint) &&
                                 endpoint.Scheme == Uri.UriSchemeHttps
                ? endpoint
                : new Uri("https://nominatim.openstreetmap.org/");
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/1.0 (desktop astral photography planner)");
        });
        services.AddSingleton<IMapTileSourceProvider, DefaultMapTileSourceProvider>();
        services.AddSingleton(OverpassMapFeatureOptions.CreateDefault());
        services.AddHttpClient<IMapFeatureDataService, OverpassMapFeatureDataService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(35);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Noctaxis/1.0 (desktop astral photography planner; saved-location semantic maps)");
        });
        services.AddHttpClient<IBuildingFeatureDataService, OverpassBuildingFeatureDataService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(35);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Noctaxis/1.0 (desktop astral photography planner; building-star cache)");
        });
        services.AddSingleton<SavedLocationMapImageProcessor>();
        services.AddHttpClient<ILocationMapThumbnailService, LocationMapThumbnailService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/1.0 (desktop photography planner; saved-location thumbnails)");
        });
        services.AddSingleton<PlatformDeviceLocationProvider>();
        services.AddSingleton<IDeviceLocationProvider>(provider => provider.GetRequiredService<PlatformDeviceLocationProvider>());
        services.AddSingleton<IDeviceLocationAvailabilityService>(provider => provider.GetRequiredService<PlatformDeviceLocationProvider>());
        services.AddSingleton<ILocationResolver, LocationResolver>();
        services.AddSingleton<IUserDataPathProvider, PlatformUserDataPathProvider>();
        services.AddSingleton<IUserDataStore, JsonUserDataStore>();
        services.AddSingleton<IPlanningService, PlanningService>();
        services.AddSingleton<IScoutingCardExporter, ScoutingCardExporter>();
        services.AddSingleton<LocationSearchViewModel>();
        services.AddSingleton<CelestialSearchViewModel>();
        services.AddSingleton<DesktopDialogService>();
        services.AddSingleton<IPlannerDialogService>(provider => provider.GetRequiredService<DesktopDialogService>());
        services.AddSingleton<MainViewModel>();
        return services.BuildServiceProvider();
    }
}
