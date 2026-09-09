using Avalonia.Headless.XUnit;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task ExplicitTimezonePreferenceAndSavedLocationStillApplyTheirTimezone()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var location = new SavedLocation(Guid.NewGuid(), "Tokyo", new GeoCoordinate(35.7, 139.7), "Asia/Tokyo");
        var vm = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(SelectedTimeZoneId: "UTC"), [location],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        var environmentRequests = planning.EnvironmentRequests;
        await vm.ApplySettingsAsync(vm.Settings with { SelectedTimeZoneId = "Europe/London" });
        Assert.Equal("Europe/London", vm.TimeZoneId);
        Assert.Equal("Europe/London", vm.Session.TimeZoneId);
        Assert.Equal(environmentRequests, planning.EnvironmentRequests);
        vm.SelectedLocation = location;
        await vm.WaitForPlannerRefreshAsync();
        Assert.Equal("Asia/Tokyo", vm.TimeZoneId);
        Assert.Equal("Asia/Tokyo", vm.Session.TimeZoneId);
        var instant = vm.Session.Instant;
        var generation = vm.PlannerRefresh.Generation;
        await vm.ApplySettingsAsync(vm.Settings with { TerrainMinimapContext = 1.5 });
        Assert.Equal("Asia/Tokyo", vm.Session.TimeZoneId);
        Assert.Equal(instant, vm.Session.Instant);
        Assert.Equal(generation, vm.PlannerRefresh.Generation);
    }

    [AvaloniaFact]
    public async Task PresentationSettingsKeepBlockedBuildAndSharedFramingAssessment()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new StagedPlanning(catalogue);
        var calculator = new CountingFramingCalculator();
        var location = new SavedLocation(Guid.NewGuid(), "Saved observer", new GeoCoordinate(53, -1), "Europe/London");
        var vm = CreateViewModel(planning, catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(SelectedTimeZoneId: "UTC"), [location],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), location.TimeZoneId) with
                    { Observer = location.Coordinate, SavedLocationId = location.Id }, location.Id)),
            new FakeExporter(), framingVisibility: calculator);
        await vm.InitializeAsync();
        vm.ShowPlannerCommand.Execute(null);
        await planning.WeatherStarted.WaitAsync();
        var generation = vm.PlannerRefresh.Generation;
        var token = planning.LastRefreshToken;
        var originalWork = vm.WaitForPlannerRefreshAsync();
        foreach (var change in new Func<AppSettings, AppSettings>[]
        {
            s => s with { TerrainMinimapContext = 1.35 },
            s => s with { TerrainDebugOverlay = true },
            s => s with { CameraFraming = s.EffectiveCameraFraming with { LineThickness = 3 } },
            s => s with { CameraFraming = s.EffectiveCameraFraming with { IsOverlayVisible = false } },
            s => s with { CameraFraming = s.EffectiveCameraFraming with { IsOverlayVisible = true } }
        })
        {
            await vm.ApplySettingsAsync(change(vm.Settings));
            Assert.Equal(generation, vm.PlannerRefresh.Generation);
            Assert.Equal(location.TimeZoneId, vm.TimeZoneId);
            Assert.False(token.IsCancellationRequested);
            Assert.Same(originalWork, vm.WaitForPlannerRefreshAsync());
            Assert.Equal(1, planning.CoreRequestCount);
            Assert.Equal(1, planning.EnvironmentRequestCount);
            Assert.Equal(1, planning.WeatherRequestCount);
        }
        planning.CompleteCore(0);
        planning.CompleteWeather(0, DataState.Ready);
        planning.CompleteEnvironment(0, true, 10);
        await originalWork;
        Assert.True(vm.Snapshot!.Terrain.HasTerrainCoverage);
        var first = vm.CameraFramingVisibility;
        var count = calculator.Count;
        Assert.NotNull(first);
        Assert.Same(first, vm.CameraFramingVisibility);
        _ = vm.FramingVisibilityStatus;
        _ = vm.TerrainDebugText;
        Assert.Equal(count, calculator.Count);
        await vm.ApplySettingsAsync(vm.Settings with { TerrainMinimapContext = 2 });
        Assert.Same(first, vm.CameraFramingVisibility);
        Assert.Equal(count, calculator.Count);
        await vm.ApplySettingsAsync(vm.Settings with { CameraFraming = vm.Settings.EffectiveCameraFraming with
            { CompositionOffsetDegrees = 15 } });
        Assert.NotSame(first, vm.CameraFramingVisibility);
        Assert.Equal(count + 1, calculator.Count);
        Assert.Equal(1, planning.EnvironmentRequestCount);
        Assert.Equal(generation, vm.PlannerRefresh.Generation);

        var heightChange = vm.ApplySettingsAsync(vm.Settings with { CameraHeightAboveGroundMetres = 3 });
        await planning.WeatherStarted.WaitAsync();
        Assert.True(token.IsCancellationRequested);
        Assert.NotEqual(generation, vm.PlannerRefresh.Generation);
        Assert.Equal(2, planning.EnvironmentRequestCount);
        planning.CompleteCore(1); planning.CompleteWeather(1, DataState.Ready);
        planning.CompleteEnvironment(1, true, 10);
        await heightChange;
    }

    [AvaloniaFact]
    public async Task FramingMemoizationTracksActualCalculatorInputs()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var calculator = new CountingFramingCalculator();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            new FakeStore(new PersistedState(4, new AppSettings(), [],
                PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)),
            new FakeExporter(), framingVisibility: calculator);
        await vm.InitializeAsync(); vm.ShowPlannerCommand.Execute(null);
        await vm.WaitForPlannerRefreshAsync();
        _ = vm.CameraFramingVisibility;
        foreach (var change in new Func<PlanningSnapshot, PlanningSnapshot>[]
        {
            s => s with { Terrain = s.Terrain with { GeneratedAt = s.Terrain.GeneratedAt + Duration.FromSeconds(1) } },
            s => s with { Weather = s.Weather with { State = DataState.Error } },
            s => s with { Position = s.Position with { Horizontal = s.Position.Horizontal with { AltitudeDegrees = 12 } } },
            s => s with { Position = s.Position with { Horizontal = s.Position.Horizontal with { AzimuthDegrees = 42 } } },
            s => s with { FieldOfView = s.FieldOfView with { HorizontalDegrees = 25 } },
            s => s with { FieldOfView = s.FieldOfView with { VerticalDegrees = 22 } }
        })
        {
            var count = calculator.Count;
            vm.Snapshot = change(vm.Snapshot!);
            var assessment = vm.CameraFramingVisibility;
            Assert.Equal(count + 1, calculator.Count);
            Assert.Same(assessment, vm.CameraFramingVisibility);
            _ = vm.FramingVisibilityStatus;
            Assert.Equal(count + 1, calculator.Count);
        }
    }

    private sealed class CountingFramingCalculator : IFramingVisibilityCalculator
    {
        public int Count { get; private set; }
        public int DepthCount { get; private set; }
        public FramingVisibilityAssessment Calculate(WeatherResult weather, TerrainHorizonProfile terrain,
            double targetAltitudeDegrees, double cameraBearingDegrees, double horizontalFovDegrees = 0,
            double terrainCastAngularDetailDegrees = 1, double verticalFovDegrees = 0, CameraTerrainFrame? cameraFrame = null)
        {
            Count++;
            var result = new FramingVisibilityCalculator().Calculate(weather, terrain, targetAltitudeDegrees,
                cameraBearingDegrees, horizontalFovDegrees, terrainCastAngularDetailDegrees, verticalFovDegrees, cameraFrame);
            if (result.CameraDepth is not null) DepthCount++;
            return result;
        }
    }
}

