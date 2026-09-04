using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Locations;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Weather;
using Noctaxis.Desktop.ViewModels;
using NodaTime;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrariumPlannerIntegrationTests
{
    [AvaloniaFact]
    public async Task ProductionCompositionMovesBetweenTerrariumLocationsWithoutElevationLeak()
    {
        using var fixture = new TerrariumFixtureCache();
        using var http = new HttpClient();
        var provider = new TerrariumTerrainProvider(http, fixture,
            NullLogger<TerrariumTerrainProvider>.Instance,
            new TerrariumTerrainOptions(12, 96));
        var initial = PlanningSession.Default(Instant.FromUtc(2026, 9, 4, 21, 0), "Europe/London");
        var store = new MemoryStore(new PersistedState(4, new AppSettings(), [], initial, null));

        await using var services = App.ConfigureServices(collection =>
        {
            collection.AddSingleton<ITerrainElevationProvider>(provider);
            collection.AddSingleton<IUserDataStore>(store);
            collection.AddSingleton<ILandCoverProvider, FixtureLandCover>();
            collection.AddSingleton<ISettlementDataProvider, MissingSettlement>();
            collection.AddSingleton<IWeatherProvider, OfflineWeatherProvider>();
            collection.AddSingleton<IReverseGeocodingProvider, MissingReverseGeocoder>();
        });
        Assert.IsType<TerrariumTerrainProvider>(services.GetRequiredService<ITerrainElevationProvider>());

        var viewModel = services.GetRequiredService<MainViewModel>();
        await viewModel.InitializeAsync();
        await viewModel.WaitForPlannerRefreshAsync().WaitAsync(TimeSpan.FromSeconds(30));

        await MoveAndAssert(viewModel, new GeoCoordinate(53.55865, -0.48052), 10);
        await MoveAndAssert(viewModel, new GeoCoordinate(53.00563, -3.95192), 250);

        viewModel.CommitUnresolvedObserverLocation(new GeoCoordinate(53.02135, -4.64836, 999));
        Assert.Equal(TerrainElevationResolutionState.Unresolved, viewModel.TerrainElevationResolutionState);
        Assert.Null(viewModel.ResolvedGroundElevationMetres);
        Assert.False(viewModel.IsElevationManualOverride);
        await viewModel.WaitForPlannerRefreshAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, viewModel.ResolvedGroundElevationMetres!.Value, 6);
        Assert.Equal(1.7, viewModel.Snapshot!.Terrain.ObserverAbsoluteElevationMetres!.Value, 6);
        Assert.Equal(-35, viewModel.Snapshot.Terrain.ObserverDiagnostics!.TerrainSample.InterpolatedElevationMetres!.Value, 6);
        Assert.True(viewModel.Snapshot.Terrain.ObserverDiagnostics.SurfaceWasAdjusted);
        Assert.Same(viewModel.Snapshot.Terrain, viewModel.Snapshot.Environment!.HorizonProfile);
        Assert.All(viewModel.Snapshot.Terrain.Samples,
            sample => Assert.Equal(sample.GroundHorizonElevationDegrees,
                sample.EffectiveHorizonElevationDegrees));
    }

    private static async Task MoveAndAssert(MainViewModel viewModel, GeoCoordinate coordinate,
        double expectedElevation)
    {
        var previousProfile = viewModel.TerrainDebugProfile;
        viewModel.CommitUnresolvedObserverLocation(coordinate with { ElevationMetres = 999 });
        Assert.Equal(TerrainElevationResolutionState.Unresolved, viewModel.TerrainElevationResolutionState);
        Assert.Null(viewModel.ResolvedGroundElevationMetres);
        Assert.Null(viewModel.TerrainDebugProfile);
        if (previousProfile is not null) Assert.NotEqual(previousProfile.Observer, coordinate);
        await viewModel.WaitForPlannerRefreshAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var terrain = viewModel.Snapshot!.Terrain;
        Assert.Same(terrain, viewModel.TerrainDebugProfile);
        Assert.Equal(coordinate.Latitude, terrain.Observer.Latitude, 10);
        Assert.Equal(coordinate.Longitude, terrain.Observer.Longitude, 10);
        Assert.Equal(TerrainSurfaceResolver.SourceId, terrain.GroundElevationAtObserver!.SourceId);
        Assert.Equal(TerrariumTerrainProvider.SourceId,
            terrain.ObserverDiagnostics!.TerrainSample.Provider);
        Assert.Equal(expectedElevation, terrain.GroundElevationAtObserver.Value, 6);
        Assert.Equal(expectedElevation, terrain.ChosenObserverGroundElevationMetres!.Value, 6);
        Assert.Equal(expectedElevation, viewModel.Elevation, 6);
        Assert.Equal(expectedElevation + AppSettings.DefaultCameraHeightAboveGroundMetres,
            terrain.ObserverAbsoluteElevationMetres!.Value, 6);
    }

    private sealed class MemoryStore(PersistedState state) : IUserDataStore
    {
        public string StorageDirectory => "memory";
        public Task<PersistedState> LoadAsync(CancellationToken token) => Task.FromResult(state);
        public Task SaveAsync(PersistedState value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class FixtureLandCover : ILandCoverProvider
    {
        public Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate,
            CancellationToken token) => Task.FromResult(new EnvironmentalValue<LandCoverClass>(
                EnvironmentalDataState.Available, Classify(coordinate), "test-cover", "1", "Fixture"));

        public Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken token) => Task.FromResult(new LandCoverBatchResult(
                EnvironmentalDataState.Available,
                coordinates.Select(coordinate => (LandCoverClass?)Classify(coordinate)).ToArray(),
                "test-cover", "1", "Fixture"));

        private static LandCoverClass Classify(GeoCoordinate coordinate) =>
            coordinate.Longitude < -4.2 ? LandCoverClass.PermanentWater : LandCoverClass.Grassland;
    }

    private sealed class MissingSettlement : ISettlementDataProvider
    {
        public Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request,
            CancellationToken token) => Task.FromResult(EnvironmentalValue<SettlementRaster>.Unavailable(
                "test-settlement", "1", "Unavailable"));
    }

    private sealed class MissingReverseGeocoder : IReverseGeocodingProvider
    {
        public Task<ReverseGeocodingResult?> ResolveAsync(GeoCoordinate coordinate,
            CancellationToken token) => Task.FromResult<ReverseGeocodingResult?>(null);
    }

    private sealed class TerrariumFixtureCache : IEnvironmentalTileCache, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "Noctaxis-App-Terrarium-" + Guid.NewGuid().ToString("N"));
        private readonly object _gate = new();

        public string RootDirectory => _directory;

        public Task<EnvironmentalCacheResult> GetOrCreateAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<byte[]?>> acquire, Func<string, bool> validate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
            Func<string, bool> validate, CancellationToken cancellationToken)
        {
            var path = Path.Combine(_directory, descriptor.Layer + "-" + descriptor.TileId + ".png");
            lock (_gate)
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(_directory);
                    var x = int.Parse(descriptor.TileId.Split('-')[0],
                        System.Globalization.CultureInfo.InvariantCulture);
                    var elevation = x >= 2_020 ? 10 : x >= 2_000 ? 250 : x >= 1_950 ? -35 : -3_000;
                    WriteTile(path, elevation);
                }
            }
            return Task.FromResult(new EnvironmentalCacheResult(EnvironmentalDataState.Cached,
                path, true, "Deterministic production-format fixture"));
        }

        private static void WriteTile(string path, double elevation)
        {
            var shifted = elevation + 32_768;
            var red = (byte)Math.Floor(shifted / 256);
            var green = (byte)Math.Floor(shifted % 256);
            var blue = (byte)Math.Round((shifted - Math.Floor(shifted)) * 256);
            using var bitmap = new SKBitmap(256, 256);
            bitmap.Erase(new SKColor(red, green, blue));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
