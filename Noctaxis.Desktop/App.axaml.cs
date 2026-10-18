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
using Noctaxis.Core.Environment;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using Noctaxis.Core.Locations;
using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;
using Noctaxis.Desktop.Services;
using Noctaxis.Core.Supporter;

namespace Noctaxis.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    public Themes.ThemeService? Themes { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Controls.InformationalToolTips.Register();
        Themes = new Themes.ThemeService(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The language comes first: every window and view model resolves its text as it is built.
            AppLanguages.Apply(AppLanguages.ReadSaved(JsonUserDataStore.StateFilePath(new PlatformUserDataPathProvider())));
            _services = ConfigureServices();
            var viewModel = _services.GetRequiredService<MainViewModel>();
            // The saved language is the one source of truth; the process follows it when it changes.
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SettingsLanguage)) AppLanguages.Apply(viewModel.Settings.EffectiveLanguage);
            };
            viewModel.AttachThemeService(Themes!);
            var dialogs = _services.GetRequiredService<DesktopDialogService>();
            desktop.MainWindow = new MainWindow(viewModel, dialogs);
            // Final state is saved by MainWindow.OnClosing, which holds the window open until the
            // save completes. The lifetime does not await Exit handlers, so nothing asynchronous
            // may be started here.
            desktop.Exit += (_, _) => Themes?.Dispose();
            _ = viewModel.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>How long a pooled HTTP connection is reused before a new one (and a fresh DNS lookup) is made.</summary>
    internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(10);

    internal static ServiceProvider ConfigureServices(Action<IServiceCollection>? configureForTest = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole().SetMinimumLevel(LogLevel.Information);
            if (Program.FileLogging is { } file)
            {
                // Owned and disposed by Program. The file keeps warnings for noisy or location-revealing
                // categories only: HTTP request URIs and terrain/environment Information messages
                // contain precise coordinates and per-tile chatter.
                builder.AddProvider(file);
                builder.AddFilter<Diagnostics.RollingFileLoggerProvider>("System.Net.Http", LogLevel.Warning);
                builder.AddFilter<Diagnostics.RollingFileLoggerProvider>("Microsoft", LogLevel.Warning);
                builder.AddFilter<Diagnostics.RollingFileLoggerProvider>("Noctaxis.Core.Environment", LogLevel.Warning);
                builder.AddFilter<Diagnostics.RollingFileLoggerProvider>("Noctaxis.Core.Terrain", LogLevel.Warning);
            }
        });
        // Typed clients are held by singletons for the life of the application, so the factory's handler rotation never
        // takes effect; a pooled connection lifetime keeps connections following DNS changes instead (NX-23).
        services.ConfigureHttpClientDefaults(builder => builder
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = PooledConnectionLifetime })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
        services.AddSingleton<ITimeZoneResolver, TimeZoneResolver>();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<ITargetCatalogue>(_ =>
            Program.CatalogueLoad?.GetAwaiter().GetResult() ?? new OpenNgcTargetCatalogue());
        services.AddSingleton<ITargetSearchService, LocalTargetSearchService>();
        services.AddSingleton<IAstronomyService, AstronomyEngineService>();
        services.AddSingleton<ILensCalculator, LensCalculator>();
        services.AddSingleton<ICameraFramingGuideCalculator, CameraFramingGuideCalculator>();
        services.AddSingleton<IFramingVisibilityCalculator, FramingVisibilityCalculator>();
        services.AddSingleton<ILocalHorizonCalculator, LocalHorizonCalculator>();
        services.AddSingleton<IEnvironmentalTileCache, EnvironmentalTileCache>();
        services.AddSingleton<TerrainDiskCache>();
        services.AddSingleton(new TerrariumTerrainOptions());
        services.AddHttpClient<ITerrainElevationProvider, TerrariumTerrainProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Noctaxis/1.0 (photographic planning; Mapzen-Tilezen terrain client)");
        });
        // The Planner map's terrain relief reads Terrarium through the same client configuration and
        // the same disk cache as the terrain calculations, so tiles are downloaded and stored once.
        // Its decoded tiles are held separately, so browsing the map never evicts a tile a horizon
        // calculation is using.
        services.AddSingleton<ITerrariumTileSource>(provider => new TerrariumTerrainProvider(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ITerrainElevationProvider)),
            provider.GetRequiredService<IEnvironmentalTileCache>(),
            provider.GetRequiredService<ILogger<TerrariumTerrainProvider>>(),
            provider.GetRequiredService<TerrariumTerrainOptions>()));
        services.AddHttpClient<ILandCoverProvider, WorldCoverLandCoverProvider>();
        services.AddSingleton<ITerrainSurfaceResolver, TerrainSurfaceResolver>();
        services.AddSingleton<IHorizonService>(provider =>
        {
            var horizon = new HorizonService(provider.GetRequiredService<ITerrainSurfaceResolver>(),
                provider.GetRequiredService<ILogger<HorizonService>>());
            provider.GetRequiredService<TerrainDiskCache>().Invalidated += horizon.InvalidateCache;
            return horizon;
        });
        services.AddHttpClient<IWsfCoverageSource, DlrWsfCoverageSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Noctaxis/1.0 (photographic planning; DLR WSF scientific coverage client)");
        });
        services.AddSingleton<ISettlementDataProvider, WsfSettlementDataProvider>();
        services.AddSingleton<IPlannerEnvironmentService, PlannerEnvironmentService>();
        // ILocationEnvironmentService, IAuroraProvider (NOAA SWPC) and ILightPollutionProvider were registered but never
        // resolved by any consumer (NX-51); they are no longer in the container, so no unused network client ships.
        services.AddSingleton<ILocationTransferService, LocationTransferService>();
        services.AddSingleton<IGeographicWeatherCache, GeographicWeatherCache>();
        // Shared so one downloaded forecast serves later planner times, whichever provider instance asks.
        services.AddSingleton<OpenMeteoForecastCache>();
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
        // OpenFreeMap vector tiles are the one source of map geography: the Planner basemap, saved-location map data
        // (landscape, roads and waterways) and location descriptions all read the same decoded tiles, downloaded once
        // through one client into one disk cache. No public Nominatim, Overpass or OpenStreetMap raster request is made.
        services.AddSingleton(_ => Mapping.Vector.VectorTileRepository.OpenFreeMap);
        services.AddSingleton<Noctaxis.Core.Locations.Descriptions.IGeographicFeatureSource,
            Mapping.Vector.OpenFreeMapGeographicFeatureSource>();
        services.AddSingleton<Noctaxis.Core.Locations.Descriptions.ILocationDescriptionService,
            Noctaxis.Core.Locations.Descriptions.LocationDescriptionService>();
        services.AddSingleton<IMapFeatureDataService, OpenFreeMapMapFeatureDataService>();
        services.AddSingleton<ISavedLocationLandscapeSource, OpenFreeMapLandscapeSource>();
        services.AddSingleton<IMapImageAcceleration>(_ => OpenCvMapImageAcceleration.Shared);
        services.AddSingleton<SettlementDensityBuilder>();
        services.AddSingleton<SettlementGlowGeometryCalculator>();
        services.AddSingleton<SettlementGlowCompositor>();
        services.AddSingleton<SettlementStarGenerator>();
        services.AddSingleton(SettlementGalaxyStyle.DefaultV1);
        services.AddSingleton<SavedLocationMapImageProcessor>();
        services.AddSingleton<ILocationMapThumbnailService, LocationMapThumbnailService>();
        services.AddSingleton<IUserDataPathProvider, PlatformUserDataPathProvider>();
        services.AddHttpClient("LorenzAtlas", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(45);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/1.0 (optional local Lorenz atlas installation)");
        });
        services.AddSingleton(provider => new Noctaxis.Core.LightPollution.LorenzInstallation(
            provider.GetRequiredService<IUserDataPathProvider>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("LorenzAtlas")));
        // Every save confirms that this process still owns the data folder (NX-31).
        if (Program.Ownership is { } ownership)
            services.AddSingleton<IDataDirectoryOwnership>(new InstanceDataDirectoryOwnership(ownership));
        services.AddSingleton<IUserDataStore, JsonUserDataStore>();
        services.AddSingleton<IAwooSupporterLicenceVerifier, AwooSupporterLicenceVerifier>();
        services.AddSingleton<IExternalUriLauncher, ShellExternalUriLauncher>();
        services.AddSingleton<ExternalMapService>();
        services.AddSingleton<IPlanningService, PlanningService>();
        services.AddSingleton<Export.IPlannerExportRenderer, Export.PlannerExportRenderer>();
        services.AddSingleton<LocationSearchViewModel>();
        services.AddSingleton<DesktopDialogService>();
        services.AddSingleton<IPlannerDialogService>(provider => provider.GetRequiredService<DesktopDialogService>());
        // Closing the main window runs the normal shutdown: final save, then resource release.
        services.AddSingleton<IApplicationRestartService>(provider => new ApplicationRestartService(
            () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Close()),
            provider.GetRequiredService<ILogger<ApplicationRestartService>>()));
        services.AddSingleton<MainViewModel>();
        configureForTest?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
