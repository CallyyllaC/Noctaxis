using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Controls;
using NodaTime;
using System.Collections.Immutable;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    [CoversSettingsInput("SettingsMinimumTerrainFrameCoveragePercent")]
    public async Task PitchBearingAndCoverageArePersistedDerivedInputsOnly()
    {
        var catalogue = new OpenNgcTargetCatalogue(); var planning = new FakePlanning(catalogue);
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await vm.InitializeAsync(); vm.ShowPlannerCommand.Execute(null); await vm.WaitForPlannerRefreshAsync();
        var terrain = vm.Snapshot!.Terrain with { HasDemCoverage = true, Samples = Enumerable.Range(0, 360)
            .Select(b => new TerrainHorizonSample(b, 3, null, Sightline: [new TerrainSightlineSample(1000, 10, 0, 3)])).ToArray() };
        vm.Snapshot = vm.Snapshot with { Terrain = terrain };
        var generation = vm.PlannerRefresh.Generation; var calls = planning.EnvironmentRequests;
        var first = vm.CameraFramingVisibility; Assert.NotNull(first!.CameraDepth);
        Assert.Same(first, vm.CameraFramingVisibility);
        vm.CameraPitchDegrees = 30;
        Assert.All(vm.TerrainFrameDepth!.DistancesMetres, d => Assert.Equal(0, d));
        Assert.NotSame(first, vm.CameraFramingVisibility);
        vm.CameraBearingDegrees = 123;
        Assert.Equal(123, vm.CameraFramingGuide!.CentreBearingDegrees, 8);
        vm.SettingsMinimumTerrainFrameCoveragePercent = 20;
        vm.ResetSettingsEditorCommand.Execute(null);
        Assert.Equal(5, vm.SettingsMinimumTerrainFrameCoveragePercent);
        vm.SettingsMinimumTerrainFrameCoveragePercent = 20;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(20, store.State.Settings.EffectiveCameraFraming.MinimumTerrainFrameCoveragePercent);
        Assert.Equal(30, store.State.Settings.EffectiveCameraFraming.CameraPitchDegrees);
        Assert.Equal(generation, vm.PlannerRefresh.Generation);
        Assert.Equal(calls, planning.EnvironmentRequests); Assert.Same(terrain, vm.Snapshot.Terrain);
        vm.Snapshot = vm.Snapshot with { FieldOfView = new(60, 60, 80) };
        Assert.NotNull(vm.TerrainFrameDepth); Assert.Equal(calls, planning.EnvironmentRequests);
        await vm.ApplySettingsAsync(vm.Settings with { EnableTerrainCalculations = false });
        calls = planning.EnvironmentRequests;
        vm.CameraPitchDegrees = -20; vm.CameraBearingDegrees = 70;
        Assert.Null(vm.TerrainFrameDepth); Assert.Equal("Terrain calculations disabled", vm.TerrainFrameStatus);
        Assert.Equal(calls, planning.EnvironmentRequests);
    }

    [AvaloniaFact]
    public async Task CameraFrameClearsOldObserverAndUsesLatestPitchAfterBlockedCompletion()
    {
        var catalogue = new OpenNgcTargetCatalogue(); var planning = new StagedPlanning(catalogue);
        var vm = CreateViewModel(planning, catalogue, new FakeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null)), new FakeExporter());
        await vm.InitializeAsync(); vm.ShowPlannerCommand.Execute(null); await planning.WeatherStarted.WaitAsync();
        Assert.Null(vm.TerrainFrameDepth); Assert.Equal("Resolving terrain…", vm.TerrainFrameStatus);
        vm.CameraPitchDegrees = 30;
        await vm.ApplySettingsAsync(vm.Settings with { CameraFraming = vm.Settings.EffectiveCameraFraming with { MinimumTerrainFrameCoveragePercent = 10 } });
        Assert.Equal(1, planning.EnvironmentRequestCount);
        vm.MoveObserver(new GeoCoordinate(55, -2)); await planning.WeatherStarted.WaitAsync();
        var work = vm.WaitForPlannerRefreshAsync();
        planning.CompleteCore(1); planning.CompleteWeather(1, DataState.Ready); planning.CompleteEnvironment(1, true, 10);
        await work;
        var current = vm.TerrainFrameDepth;
        Assert.NotNull(current); Assert.Equal(30 - vm.Snapshot!.FieldOfView.VerticalDegrees / 2, current.LowerAltitude);
        planning.CompleteCore(0); planning.CompleteWeather(0, DataState.Ready); planning.CompleteEnvironment(0, true, 10);
        Assert.Same(current, vm.TerrainFrameDepth);
    }
}

public sealed class TerrainFrameViewTests
{
    [AvaloniaFact]
    public void DepthPreviewIsReactiveAndThresholdDoesNotMutateDepth()
    {
        var depth = new CameraTerrainDepth([350, 0, 10], -10, 10,
            Enumerable.Repeat(1000d, 3 * 72).ToImmutableArray());
        var view = new TerrainFrameView { Width = 208, Height = 160, Depth = depth, IsHitTestVisible = false };
        view.Measure(new Size(208, 160)); view.Arrange(new Rect(0, 0, 208, 160));
        using var bitmap = new RenderTargetBitmap(new PixelSize(208, 160));
        bitmap.Render(view); var rectangle = view.FrameRectangle;
        view.Threshold = .5; bitmap.Render(view);
        Assert.Same(depth, view.Depth); Assert.Equal(rectangle, view.FrameRectangle);
        Assert.True(TerrainFrameView.DepthColour(100).R > TerrainFrameView.DepthColour(1000).R);
        Assert.True(TerrainFrameView.DepthColour(1000).R > TerrainFrameView.DepthColour(10000).R);
        Assert.NotEqual(TerrainFrameView.DepthColour(0), TerrainFrameView.DepthColour(50000));
        foreach (var status in new[] { "Resolving terrain…", "Terrain unavailable", "Terrain calculations disabled" })
        { view.Depth = null; view.Status = status; bitmap.Render(view); Assert.Null(view.Depth); }
        view.Depth = depth with { LowerAltitude = -30, UpperAltitude = 30 }; bitmap.Render(view);
        Assert.True(view.FrameRectangle.Width < rectangle.Width);
    }
}

public sealed partial class EnvironmentalOverlayTests
{
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public void ShaderCoverageChangesOnlyTextureStrengthWhileWeatherRemainsIndependent(bool beyondWeather)
    {
        var alpha = new List<int>();
        foreach (var strength in new[] { 0d, .25, .5, 1 })
        {
            var sector = Sector(0, 40);
            var visibility = Visibility(sector, beyondWeather ? 100 : null, (0, 100), (40, 100));
            visibility = visibility with { TerrainObstructions = visibility.EffectiveTerrainObstructions.Select(s => s with { EffectiveCoverage = strength }).ToArray() };
            var state = new EnvironmentalOverlayStateCoordinator(profileTextureWidth: 32).Update(Observer, sector, visibility, ProfileKey());
            var point = WebMercator.FromWgs84(Angles.Destination(Observer, sector.CentreBearingDegrees, 1000));
            var viewport = new Mapsui.Viewport(point.X, point.Y, 1, 0, 64, 64);
            var frame = EnvironmentalOverlayMath.CreateFrame(viewport, 64, 64, EnvironmentalRenderParameters.Default);
            using var host = new EnvironmentalOverlayTestControl(state, frame, Avalonia.Media.Colors.DeepPink) { Width = 64, Height = 64 };
            host.Measure(new Size(64, 64)); host.Arrange(new Rect(0, 0, 64, 64));
            using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64)); bitmap.Render(host);
            using var copy = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using var pixels = copy.Lock(); bitmap.CopyPixels(pixels);
            int Channel(int c) => System.Runtime.InteropServices.Marshal.ReadByte(pixels.Address, 32 * pixels.RowBytes + 32 * 4 + c);
            alpha.Add(Channel(3));
            if (beyondWeather && strength == 0) Assert.InRange(Math.Abs(Channel(2) - Channel(1)), 0, 1);
            else Assert.True(Channel(2) > Channel(1));
        }
        Assert.True(alpha[0] < alpha[1] && alpha[1] < alpha[2] && alpha[2] < alpha[3]);
        Assert.True(alpha[3] < 180);
    }

    [Fact]
    public void CoverageStrengthInterpolatesWithoutSmearingIntoClearBearings()
    {
        ImmutableArray<EnvironmentalTerrainSample> samples = [new(0, 0, 0, true, 100, .25), new(1, 1, 1, true, 200, .75), new(2, 2, 2, false, null, 0)];
        var middle = EnvironmentalOverlayStateFactory.SampleAtOffset(samples, .5);
        Assert.Equal(.5, middle.EffectiveCoverage); Assert.Equal(150, middle.ObstructionDistanceMetres);
        Assert.False(EnvironmentalOverlayStateFactory.SampleAtOffset(samples, 1.9).IsObstructed);
        var texels = EnvironmentalOverlayStateFactory.Resample(samples, 2, 5);
        Assert.Equal(.5f, texels[1].EffectiveCoverage);
        Assert.False(texels[^1].IsObstructed);
    }
}

