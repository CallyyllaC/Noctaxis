using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mapsui;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Persistence;
using NodaTime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Noctaxis.Desktop.Tests;

public sealed partial class EnvironmentalOverlayTests
{
    [AvaloniaFact]
    public void ExportOptionalPitchComparison()
    {
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_TINT_COMPARISON");
        if (string.IsNullOrEmpty(output)) return;
        var terrain = Snapshot(Observer).Terrain with { HasDemCoverage = true, Samples = Enumerable.Range(0, 360).Select(b =>
            new TerrainHorizonSample(b, 10, null, Sightline: [new TerrainSightlineSample(1000, 100, 0, 10)])).ToArray() };
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(12) };
        var hosts = new List<EnvironmentalOverlayTestControl>();
        foreach (var colour in new[] { 0xff9a6f9eu, 0xff22bb88u })
        {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
        root.Children.Add(row);
        foreach (var pitch in new[] { 0d, 15, 30, 45 })
        {
            var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"), terrain,
                50, 90, 70, cameraFrame: new(pitch, 60, 5));
            var sector = new GeoSector(Observer, 90, 70, 500000);
            var state = new EnvironmentalOverlayStateCoordinator().Update(Observer, sector, assessment, ProfileKey());
            var point = WebMercator.FromWgs84(Angles.Destination(Observer, 90, 3000));
            var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(point.X, point.Y, 40, 0, 208, 160), 208, 160,
                EnvironmentalRenderParameters.Default with { TerrainColourArgb = colour });
            // Neutral FoV colour isolates terrain hue in this offline comparison.
            var host = new EnvironmentalOverlayTestControl(state, frame, Colors.LightGray) { Width = 208, Height = 160 };
            hosts.Add(host);
            row.Children.Add(new StackPanel { Width = 208, Spacing = 6, Children =
            {
                new TextBlock { Text = $"Pitch +{pitch:0}°", Foreground = Brushes.White, FontSize = 16 },
                new TextBlock { Text = $"Terrain #{colour & 0xffffff:X6}", Foreground = Brushes.White },
                new TerrainFrameView { Width = 208, Height = 160, Depth = assessment.CameraDepth, Threshold = .05 },
                new TextBlock { Text = $"Coverage {assessment.EffectiveTerrainObstructions[35].EffectiveCoverage:P0}", Foreground = Brushes.White },
                new Border { Background = new SolidColorBrush(Color.Parse("#839399")), Child = host }
            }});
        }
        }
        var window = new Window { Width = 892, Height = 870, Background = new SolidColorBrush(Color.Parse("#101620")), Content = root };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            using var image = new RenderTargetBitmap(new PixelSize(892, 870)); image.Render(window); image.Save(output, PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); foreach (var host in hosts) host.Dispose(); }
    }

    private static int HatchProbeAlpha(EnvironmentalOverlayState state)
    {
        var point = WebMercator.FromWgs84(Angles.Destination(Observer, state.CentreBearingDegrees, 10000));
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(point.X, point.Y, 1, 0, 64, 64), 64, 64,
            EnvironmentalRenderParameters.Default);
        using var host = new EnvironmentalOverlayTestControl(state, frame, Colors.DeepPink) { Width = 64, Height = 64 };
        host.Measure(new Size(64, 64)); host.Arrange(new Rect(0, 0, 64, 64));
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64)); bitmap.Render(host);
        using var copy = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var pixels = copy.Lock(); bitmap.CopyPixels(pixels);
        return Marshal.ReadByte(pixels.Address, 32 * pixels.RowBytes + 32 * 4 + 3);
    }

    [AvaloniaFact]
    public void TraceCoverageFromAssessmentThroughMapCacheAndShader()
    {
        var sector = new GeoSector(Observer, 90, 70, 500000);
        var coordinator = new EnvironmentalOverlayStateCoordinator();
        var rows = new List<object>(); var previousAlpha = -1;
        foreach (var coverage in new[] { 0d, .25, .5, .75, 1 })
        {
            var assessment = Visibility(sector, null, (0, 1000), (70, 1000));
            assessment = assessment with { TerrainObstructions = assessment.EffectiveTerrainObstructions.Select(s => s with { EffectiveCoverage = coverage }).ToArray() };
            var state = coordinator.Update(Observer, sector, assessment, ProfileKey());
            Assert.Equal(coverage, state.SourceSamples[0].EffectiveCoverage);
            Assert.Equal(coverage, state.ProfileTexels[0].EffectiveCoverage, 6);
            var alpha = HatchProbeAlpha(state);
            Assert.True(alpha > previousAlpha); previousAlpha = alpha;
            if (coverage == 0)
            {
                var clear = coordinator.Update(Observer, sector, assessment with { TerrainObstructions = [] }, ProfileKey());
                Assert.Equal(HatchProbeAlpha(clear), alpha);
            }
            rows.Add(new { coverage, display = TerrainTintPresentation.DisplayStrength(coverage), alpha });
        }

        var snapshot = Snapshot(Observer);
        var terrain = snapshot.Terrain with { HasDemCoverage = true, Samples = Enumerable.Range(0, 360).Select(b =>
            new TerrainHorizonSample(b, 10, null, Sightline: [new TerrainSightlineSample(1000, 100, 0, 10)])).ToArray() };
        var view = new NoctaxisMapView { Width = 500, Height = 400, Observer = Observer,
            Snapshot = snapshot with { Terrain = terrain }, ShowCameraOverlay = true,
            FramingGuide = new(90, 70, 55, 125, CameraFramingDirectionSource.ManualBearing) };
        view.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = 500, Height = 400, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            using var rendered = new RenderTargetBitmap(new PixelSize(500, 400));
            var previousRaw = 2d; var previousEffective = 2d; previousAlpha = 256; long revision = -1;
            foreach (var pitch in new[] { -10d, 0, 10, 20, 30, 45 })
            {
                var assessment = new FramingVisibilityCalculator().Calculate(new(DataState.Loading, null, "Offline"), terrain,
                    50, 90, 70, cameraFrame: new(pitch, 60, 5));
                view.FramingVisibility = assessment;
                rendered.Render(view.OverlayForTesting);
                var state = view.EnvironmentalStateForTesting!;
                var sample = assessment.EffectiveTerrainObstructions[35];
                Assert.True(sample.RawCoverage < previousRaw); previousRaw = sample.RawCoverage;
                Assert.True(sample.EffectiveCoverage < previousEffective); previousEffective = sample.EffectiveCoverage;
                Assert.True(state.TerrainTextureRevision > revision); revision = state.TerrainTextureRevision;
                Assert.Equal(sample.EffectiveCoverage, state.SourceSamples[35].EffectiveCoverage);
                var alpha = HatchProbeAlpha(state); Assert.True(alpha < previousAlpha); previousAlpha = alpha;
                var depthCoverage = assessment.CameraDepth!.DistancesMetres.Count(d => d > 0) / (double)assessment.CameraDepth.DistancesMetres.Length;
                Assert.InRange(Math.Abs(depthCoverage - sample.RawCoverage), 0, 1d / 72);
                rows.Add(new { pitch, raw = sample.RawCoverage, effective = sample.EffectiveCoverage,
                    display = TerrainTintPresentation.DisplayStrength(sample.EffectiveCoverage), depthCoverage, alpha });
            }
        }
        finally { window.Close(); }
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_HATCH_TRACE");
        if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task CameraSlidersShareNumericStateAndOnlyCalculateDerivedFrames()
    {
        var catalogue = new OpenNgcTargetCatalogue(); var planning = new FakePlanning(catalogue);
        var store = new CameraSaveProbeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var calculator = new CountingFramingCalculator();
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter(), framingVisibility: calculator);
        await vm.InitializeAsync(); vm.ShowPlannerCommand.Execute(null); await vm.WaitForPlannerRefreshAsync();
        var terrain = vm.Snapshot!.Terrain with { HasDemCoverage = true, Samples = Enumerable.Range(0, 360).Select(b =>
            new TerrainHorizonSample(b, 10, null, Sightline: [new TerrainSightlineSample(1000, 100, 0, 10)])).ToArray() };
        vm.Snapshot = vm.Snapshot with { Terrain = terrain, FieldOfView = new(70, 60, 80) };
        var window = new Noctaxis.Desktop.Views.MainWindow { DataContext = vm };
        window.FindControl<NoctaxisMapView>("PlannerMap")!.MapControlForTesting.Map!.Layers.Clear();
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var bearing = window.FindControl<Slider>("CameraBearingSlider")!;
            var pitch = window.FindControl<Slider>("CameraPitchSlider")!;
            var bearingNumber = window.FindControl<NumericUpDown>("CameraBearingInput")!;
            var pitchNumber = window.FindControl<NumericUpDown>("CameraPitchInput")!;
            Assert.False(bearingNumber.ShowButtonSpinner); Assert.False(pitchNumber.ShowButtonSpinner);
            Assert.Equal(new Thickness(0), bearingNumber.BorderThickness);
            Assert.Equal(new Thickness(0), pitchNumber.BorderThickness);
            Assert.False(bearing.IsSnapToTickEnabled); Assert.False(pitch.IsSnapToTickEnabled);
            Assert.Equal(0, bearing.Minimum); Assert.Equal(360, bearing.Maximum);
            Assert.Equal(-90, pitch.Minimum); Assert.Equal(90, pitch.Maximum);
            var generation = vm.PlannerRefresh.Generation; var environment = planning.EnvironmentRequests;
            var core = planning.SnapshotCalculations;
            _ = vm.TerrainFrameDepth;
            var calculations = calculator.Count; var depths = calculator.DepthCount;
            var writes = store.WriteCount; store.BlockSaves = true;
            var changes = 0;
            foreach (var value in Enumerable.Range(-10, 56))
            {
                var before = calculator.Count;
                pitch.Value = value;
                var result = vm.CameraFramingVisibility;
                Assert.Equal(value, vm.CameraPitchDegrees); Assert.Equal((decimal)value, pitchNumber.Value);
                Assert.Equal(before + 1, calculator.Count);
                Assert.Same(result, vm.CameraFramingVisibility); _ = vm.TerrainFrameDepth;
                Assert.Equal(before + 1, calculator.Count); changes++;
            }
            foreach (var value in new[] { 359d, 0, 1, 358, 359, 0 })
            {
                var before = calculator.Count; bearing.Value = value;
                Assert.Equal(value, vm.CameraBearingDegrees, 8); Assert.Equal((decimal)value, bearingNumber.Value);
                Assert.Equal(value, vm.CameraFramingGuide!.CentreBearingDegrees, 8);
                _ = vm.TerrainFrameDepth; _ = vm.CameraFramingVisibility;
                Assert.Equal(before + 1, calculator.Count); changes++;
            }
            Assert.Equal(62, changes);
            Assert.Equal(changes, calculator.Count - calculations);
            Assert.Equal(changes, calculator.DepthCount - depths);
            Assert.Equal(writes + 1, store.WriteCount); Assert.Equal(1, store.MaximumActive);
            store.ReleaseSaves(); await vm.WaitForCameraFramingPersistenceAsync();
            Assert.Equal(writes + 2, store.WriteCount);
            Assert.Equal(45, store.State.Settings.EffectiveCameraFraming.CameraPitchDegrees);
            var cached = vm.CameraFramingVisibility;
            await vm.ApplySettingsAsync(vm.Settings with { TerrainMinimapContext = 1.5 });
            Assert.Same(cached, vm.CameraFramingVisibility);
            Assert.Equal(generation, vm.PlannerRefresh.Generation); Assert.Same(terrain, vm.Snapshot.Terrain);
            Assert.Equal(environment, planning.EnvironmentRequests); Assert.Equal(core, planning.SnapshotCalculations);
            pitch.Value = 12.5; bearing.Value = 123.5;
            Assert.Equal(12.5m, pitchNumber.Value); Assert.Equal(123.5m, bearingNumber.Value);
            Assert.Equal(12.5, pitch.Value); Assert.Equal(123.5, bearing.Value);
            Assert.Equal(12.5, vm.CameraPitchDegrees); Assert.Equal(123.5, vm.CameraBearingDegrees, 8);
            await vm.WaitForCameraFramingPersistenceAsync();
            Assert.Equal(12.5, store.State.Settings.EffectiveCameraFraming.CameraPitchDegrees);
            Assert.Equal(123.5, store.State.Settings.EffectiveCameraFraming.ManualBearingDegrees);
        }
        finally { store.ReleaseSaves(); window.Close(); }
    }

    private sealed class CameraSaveProbeStore(PersistedState state) : IUserDataStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PersistedState State { get; private set; } = state;
        public string StorageDirectory => "unused";
        public bool BlockSaves { get; set; }
        public int WriteCount { get; private set; }
        private int _active;
        public int MaximumActive { get; private set; }
        public Task<PersistedState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public async Task SaveAsync(PersistedState value, CancellationToken cancellationToken)
        {
            WriteCount++; MaximumActive = Math.Max(MaximumActive, ++_active);
            try { if (BlockSaves) await _release.Task; State = value; }
            finally { _active--; }
        }
        public void ReleaseSaves() => _release.TrySetResult();
    }
}

public sealed class TerrainLivePresentationTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(.25, .5)] [InlineData(.5, .707106781)]
    [InlineData(.75, .866025404)] [InlineData(1, 1)]
    public void DisplayCurvePreservesPhysicalCoverageAndEndpoints(double effective, double expected) =>
        Assert.Equal(expected, TerrainTintPresentation.DisplayStrength(effective), 8);

    [Fact]
    public void LegendUsesTheActualDepthPalette()
    {
        Assert.Equal(TerrainFrameView.DepthColour(100), TerrainFrameView.NearBrush.Color);
        Assert.Equal(TerrainFrameView.DepthColour(500000), TerrainFrameView.FarBrush.Color);
        Assert.Equal(TerrainFrameView.DepthColour(0), TerrainFrameView.SkyBrush.Color);
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Noctaxis.Desktop", "Views", "MainWindow.axaml")));
        Assert.Contains("x:Static controls:TerrainFrameView.NearBrush", source);
        Assert.Contains("x:Static controls:TerrainFrameView.FarBrush", source);
        Assert.Contains("x:Static controls:TerrainFrameView.SkyBrush", source);
        Assert.DoesNotContain("Sky (blue)", source);
    }
}
