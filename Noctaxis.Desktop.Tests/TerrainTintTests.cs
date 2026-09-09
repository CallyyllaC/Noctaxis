using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mapsui;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Controls;
using NodaTime;
using System.Runtime.InteropServices;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    [CoversSettingsInput("SettingsTerrainObstructionColour")]
    [CoversSettingsInput("SettingsTerrainTintStrengthPercent")]
    [CoversSettingsInput("SettingsTerrainMinimapContext")]
    public async Task TerrainColourPickerPreviewsSavesAndResetsWithoutEnvironmentOrFrameWork()
    {
        var catalogue = new OpenNgcTargetCatalogue(); var planning = new FakePlanning(catalogue);
        var store = new FakeStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var calculator = new CountingFramingCalculator();
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter(), framingVisibility: calculator);
        await vm.InitializeAsync(); vm.ShowPlannerCommand.Execute(null); await vm.WaitForPlannerRefreshAsync();
        var cached = vm.CameraFramingVisibility; var terrain = vm.Snapshot!.Terrain;
        var generation = vm.PlannerRefresh.Generation; var environment = planning.EnvironmentRequests;
        var core = planning.SnapshotCalculations; var frames = calculator.Count; var depths = calculator.DepthCount;
        var window = new Noctaxis.Desktop.Views.MainWindow { DataContext = vm };
        var map = window.FindControl<NoctaxisMapView>("PlannerMap")!;
        map.MapControlForTesting.Map!.Layers.Clear();
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var picker = window.FindControl<ColorPicker>("TerrainObstructionColourPicker")!;
            Assert.NotNull(picker); Assert.False(picker.IsAlphaEnabled);
            var strength = window.FindControl<Slider>("TerrainTintStrengthSlider")!;
            Assert.Equal(15, strength.Minimum); Assert.Equal(55, strength.Maximum);
            Assert.Equal(40, strength.Value);
            strength.Value = 55;
            Assert.Equal(55, map.FramingSettings.TerrainTintStrengthPercent);
            Assert.Equal("#9A6F9E", vm.SettingsTerrainObstructionColourHex);
            picker.Color = Color.Parse("#22BB88");
            Assert.Equal("#22BB88", vm.CameraFramingMapSettings.TerrainObstructionColour);
            Assert.Equal("#22BB88", map.FramingSettings.TerrainObstructionColour);
            Assert.Equal("#9A6F9E", store.State.Settings.EffectiveCameraFraming.TerrainObstructionColour);
            vm.ResetSettingsEditorCommand.Execute(null);
            Assert.Equal(Color.Parse("#9A6F9E"), picker.Color);
            Assert.Equal(40, strength.Value);
            strength.Value = 50;
            vm.SettingsTerrainMinimapContext = 1.5;
            picker.Color = Color.Parse("#22BB88"); await vm.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal("#22BB88", store.State.Settings.EffectiveCameraFraming.TerrainObstructionColour);
            Assert.Equal(50, store.State.Settings.EffectiveCameraFraming.TerrainTintStrengthPercent);
            Assert.Equal(1.5, store.State.Settings.TerrainMinimapContext);
            vm.SettingsTerrainMinimapContext = 2;
            strength.Value = 15;
            picker.Color = Colors.Orange; vm.ResetSettingsEditorCommand.Execute(null);
            Assert.Equal(Color.Parse("#22BB88"), picker.Color);
            Assert.Equal(50, strength.Value);
            Assert.Equal(1.5, vm.SettingsTerrainMinimapContext);
            Assert.Same(cached, vm.CameraFramingVisibility); Assert.Same(terrain, vm.Snapshot.Terrain);
            Assert.Equal(generation, vm.PlannerRefresh.Generation);
            Assert.Equal(environment, planning.EnvironmentRequests); Assert.Equal(core, planning.SnapshotCalculations);
            Assert.Equal(frames, calculator.Count); Assert.Equal(depths, calculator.DepthCount);
        }
        finally { window.Close(); }
    }
}

public sealed partial class EnvironmentalOverlayTests
{
    [AvaloniaFact]
    public void TintUsesConfiguredHueAndBoundedOpacityWithoutPatternAndColoursFrontier()
    {
        Assert.DoesNotContain("hatch", EnvironmentalOverlayShader.Source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fract(", EnvironmentalOverlayShader.Source);
        var sector = new GeoSector(Observer, 90, 70, 500000);
        var coordinator = new EnvironmentalOverlayStateCoordinator();
        var state = coordinator.Update(Observer, sector, Visibility(sector, null, (0, 1000), (70, 1000)), ProfileKey());
        var texels = state.ProfileTexels; var revision = state.TerrainTextureRevision;
        foreach (var colour in new[] { 0xff9a6f9eu, 0xff22bb88u })
        {
            var previous = -1;
            foreach (var strength in new[] { 0d, .25, .5, .75, 1 })
            {
                // Isolate terrain from base FoV to measure the presentation layer itself.
                var coverage = strength * strength;
                var modified = state with { ProfileTexels = state.ProfileTexels.Select(t => t with { EffectiveCoverage = (float)coverage }).ToImmutableArray() };
                var pixels = TintPixels(modified, colour, 10000);
                var alpha = pixels[3]; Assert.True(alpha > previous); previous = alpha;
                if (strength == 0) Assert.All(TintPixels(modified, colour, 1000), b => Assert.Equal(0, b));
                Assert.InRange(Math.Abs(alpha - 255 * .40 * strength), 0, 2);
                for (var i = 3; i < pixels.Length; i += 4) Assert.Equal(alpha, pixels[i]);
                if (strength == 1)
                {
                    Assert.InRange(Math.Abs(pixels[2] - ((colour >> 16) & 255) * .40), 0, 2);
                    Assert.InRange(Math.Abs(pixels[1] - ((colour >> 8) & 255) * .40), 0, 2);
                }
            }
            var boundary = TintPixels(state, colour, 1000);
            var strongest = Enumerable.Range(0, 64 * 64).MaxBy(i => boundary[i * 4 + 3]) * 4;
            Assert.InRange(boundary[strongest + 3], 100, 215);
            var fraction = boundary[strongest + 3] / 255d;
            Assert.InRange(Math.Abs(boundary[strongest + 2] - ((colour >> 16) & 255) * fraction), 0, 3);
            Assert.InRange(Math.Abs(boundary[strongest + 1] - ((colour >> 8) & 255) * fraction), 0, 3);
        }
        Assert.Equal(texels, state.ProfileTexels); Assert.Equal(revision, state.TerrainTextureRevision);
    }

    [AvaloniaFact]
    public void TintStrengthChangesOnlyPresentationAndDefaultIsStrongerThanPrevious()
    {
        var sector = new GeoSector(Observer, 90, 70, 500000);
        var state = new EnvironmentalOverlayStateCoordinator().Update(Observer, sector,
            Visibility(sector, null, (0, 1000), (70, 1000)), ProfileKey());
        var texels = state.ProfileTexels;
        var previous = -1;
        foreach (var opacity in new[] { .15f, .25f, .40f, .55f })
        {
            var alpha = TintPixels(state, 0xff9a6f9e, 10000, opacity)[3];
            Assert.True(alpha > previous); previous = alpha;
            Assert.InRange(Math.Abs(alpha - opacity * 255), 0, 2);
            Assert.Equal(texels, state.ProfileTexels);
        }
    }

    private static byte[] TintPixels(EnvironmentalOverlayState state, uint colour, double distance, float opacity = .40f)
    {
        var point = WebMercator.FromWgs84(Angles.Destination(Observer, 90, distance));
        var parameters = EnvironmentalRenderParameters.Default with { ConeOpacity = 0, TerrainColourArgb = colour, TerrainTintOpacity = opacity };
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(point.X, point.Y, 2, 0, 64, 64), 64, 64, parameters);
        using var host = new EnvironmentalOverlayTestControl(state, frame, Colors.DeepPink) { Width = 64, Height = 64 };
        host.Measure(new Size(64, 64)); host.Arrange(new Rect(0, 0, 64, 64));
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64)); bitmap.Render(host);
        using var copy = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock(); bitmap.CopyPixels(buffer);
        var pixels = new byte[64 * 64 * 4];
        for (var row = 0; row < 64; row++) Marshal.Copy(buffer.Address + row * buffer.RowBytes, pixels, row * 256, 256);
        return pixels;
    }
}
