using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;
using Noctaxis.Desktop.Controls;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed class TerrainAvailabilityTests
{
    [Fact]
    public async Task InspectOptionalCachedFlatLocation()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_AVAILABILITY_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        using var http = new HttpClient();
        var cache = new ReadOnlyCache(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Noctaxis", "EnvironmentalData"));
        var provider = new TerrariumTerrainProvider(http, cache, NullLogger<TerrariumTerrainProvider>.Instance);
        var cover = new WorldCoverLandCoverProvider(http, cache, NullLogger<WorldCoverLandCoverProvider>.Instance);
        var resolver = new TerrainSurfaceResolver(provider, cover, NullLogger<TerrainSurfaceResolver>.Instance);
        var horizons = new HorizonService(resolver, NullLogger<HorizonService>.Instance);
        double Input(string name) => double.Parse(System.Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Set {name} for the cached-location probe."),
            System.Globalization.CultureInfo.InvariantCulture);
        var observer = new GeoCoordinate(Input("NOCTAXIS_AVAILABILITY_LATITUDE"),
            Input("NOCTAXIS_AVAILABILITY_LONGITUDE"));
        var profile = await horizons.GetProfileAsync(observer, new(), default);
        var map = await new TerrainDebugMapService(resolver).GetMapAsync(observer, new(), default);
        Assert.True(profile.HasTerrainCoverage);
        Assert.Contains(map.SurfaceElevationsMetres, v => v.HasValue);
        var clearBearing = Enumerable.Range(0, 360).First(b =>
            profile.Samples[b].TerrainHorizonElevationDegrees is <= 0 &&
            profile.Samples[(b + 1) % 360].TerrainHorizonElevationDegrees is <= 0) + .5;
        var coordinator = new EnvironmentalOverlayStateCoordinator();
        EnvironmentalOverlayState? initial = null;
        var rows = new List<object>();
        foreach (var pitch in new[] { -20d, -5, 0, 20 })
        {
            var frame = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"), profile,
                30, clearBearing, .5, cameraFrame: new(pitch, 52, 5));
            Assert.NotNull(frame.CameraDepth);
            var plan = coordinator.Update(observer, new(observer, clearBearing, .5, 500000), frame,
                EnvironmentalOverlayStateFactory.CreateProfileKey(profile, 1), profile);
            initial ??= plan;
            Assert.Same(initial, plan);
            Assert.All(plan.ProfileTexels, t => Assert.False(t.IsObstructed));
            rows.Add(new { pitch, available = frame.CameraDepth is not null,
                validColumns = frame.EffectiveTerrainObstructions.Count(s => s.FrameTerrainHorizonDegrees.HasValue),
                filled = frame.CameraDepth?.DistancesMetres.Count(d => d > 0) });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { profile.Status, profile.HasTerrainCoverage,
            profile.IsComplete, profile.CompletedBearingCount,
            validHorizons = profile.Samples.Count(s => s.TerrainHorizonElevationDegrees.HasValue),
            positiveHorizons = profile.Samples.Count(s => s.TerrainHorizonElevationDegrees > 0),
            mapValid = map.SurfaceElevationsMetres.Count(v => v.HasValue), clearBearing, horizontalFov = .5,
            planRebuilds = coordinator.Diagnostics.OverlayStateRebuilds, rows }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal sealed class ReadOnlyCache(string root) : IEnvironmentalTileCache
    {
        public string RootDirectory => root;
        public Task<EnvironmentalCacheResult> GetOrCreateAsync(EnvironmentalTileDescriptor d,
            Func<CancellationToken, Task<byte[]?>> acquire, Func<string, bool> validate, CancellationToken token) => Lookup(d);
        public Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(EnvironmentalTileDescriptor d,
            Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire, Func<string, bool> validate, CancellationToken token) => Lookup(d);
        private Task<EnvironmentalCacheResult> Lookup(EnvironmentalTileDescriptor d)
        {
            static string Safe(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(root, Safe(d.SourceId), Safe(d.SourceVersion), Safe(d.Layer), Safe(d.TileId) + "." + d.Extension.TrimStart('.'));
            return Task.FromResult(File.Exists(path)
                ? new EnvironmentalCacheResult(EnvironmentalDataState.Cached, path, true, "Read-only existing cache")
                : new EnvironmentalCacheResult(EnvironmentalDataState.Unavailable, null, false, "Not cached; no acquisition"));
        }
    }
}

public sealed partial class MainViewModelTests
{
    [AvaloniaTheory]
    [InlineData(-3d)]
    [InlineData(0d)]
    [InlineData(3d)]
    public async Task FullProfileAvailabilityIsIndependentOfPositivePlanMask(double angle)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var maps = new ControllableTerrainDebugMapService();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), terrainDebugMaps: maps);
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        Assert.True(await maps.Started.WaitAsync(TimeSpan.FromSeconds(3)));
        maps.Complete(0);
        await vm.WaitForTerrainDebugMapRefreshAsync();
        await vm.WaitForPlannerRefreshAsync();
        var profile = vm.Snapshot!.Terrain with { HasDemCoverage = true, HorizonState = EnvironmentalDataState.Available,
            Samples = Enumerable.Range(0, 360).Select(b => new TerrainHorizonSample(b, angle, 1000,
                Sightline: [new TerrainSightlineSample(1000, 10, 0, angle)])).ToArray() };
        vm.Snapshot = vm.Snapshot with { Terrain = profile, FieldOfView = new(24, 20, 30) };
        var coordinator = new EnvironmentalOverlayStateCoordinator();
        var pixels = new List<int>();
        foreach (var pitch in new[] { -20d, 0, 20 })
        {
            vm.CameraPitchDegrees = pitch;
            Assert.Equal(TerrainDebugMapLoadState.Ready, vm.TerrainDebugMapLoadState);
            Assert.NotNull(vm.TerrainDebugMap);
            Assert.Equal(string.Empty, vm.TerrainFrameStatus);
            var depth = Assert.IsType<CameraTerrainDepth>(vm.TerrainFrameDepth);
            pixels.Add(depth.DistancesMetres.Count(d => d > 0));
            var frame = new TerrainFrameView { Width = 208, Height = 160, Depth = depth, Status = vm.TerrainFrameStatus };
            frame.Measure(new Size(208, 160)); frame.Arrange(new Rect(0, 0, 208, 160));
            using var bitmap = new RenderTargetBitmap(new PixelSize(208, 160)); bitmap.Render(frame);
            var state = coordinator.Update(profile.Observer, new(profile.Observer, vm.CameraBearingDegrees, 24, 500000),
                vm.CameraFramingVisibility, EnvironmentalOverlayStateFactory.CreateProfileKey(profile, 1), profile);
            Assert.Equal(pitch == -20, state.TerrainFan!.GroundFacing);
            Assert.Equal(angle > 0 && pitch == 0, state.TerrainFan.Rays.Any(r => !r.Bands.IsEmpty));
            Assert.All(profile.Samples, s => Assert.Equal(angle, s.TerrainHorizonElevationDegrees));
            Assert.All(profile.SightlineAt(vm.CameraBearingDegrees), s => Assert.Equal(angle, s.TerrainElevationAngleDegrees!.Value, 8));
        }
        Assert.True(pixels[0] > pixels[1] && pixels[1] > pixels[2]);
        Assert.InRange(coordinator.Diagnostics.OverlayStateRebuilds, 2, 3);
        // A genuine absent profile remains unavailable even if the independently acquired
        // minimap is populated. Raster availability cannot manufacture a radial profile.
        vm.Snapshot = vm.Snapshot with { Terrain = profile with { HasDemCoverage = false,
            Samples = [], HorizonState = EnvironmentalDataState.Error } };
        Assert.Null(vm.TerrainFrameDepth);
        Assert.Equal("Terrain unavailable", vm.TerrainFrameStatus);
        Assert.Equal(TerrainDebugMapLoadState.Ready, vm.TerrainDebugMapLoadState);
    }
}
