using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mapsui;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

/// <summary>Live theme and colour-vision switching through the real ThemeService.</summary>
public sealed class ColourVisionRuntimeTests
{
    private static (byte R, byte G, byte B) Pixel(WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        var offset = y * buffer.RowBytes + x * 4;
        byte At(int channel) => Marshal.ReadByte(buffer.Address, offset + channel);
        return buffer.Format == PixelFormats.Rgba8888 ? (At(0), At(1), At(2)) : (At(2), At(1), At(0));
    }

    [AvaloniaFact]
    public void MapCanvasVisiblyShowsTheThemeSurfaceWhereNoTileIsDrawn()
    {
        var service = ((App)Application.Current!).Themes!;
        var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        // No basemap tiles: what shows is Mapsui's own background, as while loading or offline.
        view.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = 320, Height = 240, Content = view };
        try
        {
            service.SetSupporterEntitlement(true);
            window.Show();
            foreach (var (id, appearance, mode) in new[]
                     {
                         (ThemeCatalogue.DefaultId, AppearanceMode.Dark, ColourVisionMode.None),
                         (ThemeCatalogue.DefaultId, AppearanceMode.Light, ColourVisionMode.Deuteranopia),
                         ("supporter.foxfire", AppearanceMode.Dark, ColourVisionMode.Protanopia),
                         ("builtin.highcontrast", AppearanceMode.Dark, ColourVisionMode.Tritanopia),
                         ("supporter.moonlit", AppearanceMode.Light, ColourVisionMode.None)
                     })
            {
                service.Apply(new(id, mode, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                var surface = Color.Parse(service.EffectiveTheme.Palette[MapThemeTokens.Surface]);
                using var frame = window.CaptureRenderedFrame()!;
                // A corner away from the pin, the attribution and any overlay.
                var (r, g, b) = Pixel(frame, 12, 12);
                Assert.True(Math.Abs(r - surface.R) <= 1 && Math.Abs(g - surface.G) <= 1 && Math.Abs(b - surface.B) <= 1,
                    $"{id}/{appearance}/{mode}: visible canvas #{r:X2}{g:X2}{b:X2}, map surface {surface}");
                Assert.Equal(new Mapsui.Styles.Color(surface.R, surface.G, surface.B, surface.A), view.MapBackColorForTesting);
            }
        }
        finally
        {
            window.Close();
            service.SetSupporterEntitlement(false);
            service.Apply(new());
        }
    }

    [AvaloniaFact]
    public void EverySwitchAcrossTheMatrixLeavesNoStaleSemanticColour()
    {
        var service = ((App)Application.Current!).Themes!;
        var graph = new HorizonGraph { Width = 200, Height = 100 };
        var glyph = new CelestialLegendGlyph { Role = CelestialRole.DeepSky2 };
        var status = new StatusText { Kind = StatusKind.Error, Text = "Something failed" };
        var map = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        map.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = 640, Height = 480, Content = new StackPanel { Children = { graph, glyph, status, map } } };
        try
        {
            service.SetSupporterEntitlement(true);
            window.Show();
            foreach (var variant in AppearanceTests.Variants)
            {
                var (id, mode, appearance) = ((string)variant[0], (ColourVisionMode)variant[1], (AppearanceMode)variant[2]);
                service.Apply(new(id, mode, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                var p = service.EffectiveTheme.Palette;
                var label = $"{id}/{appearance}/{mode}";
                Assert.Equal((id, mode), (service.EffectiveTheme.Definition.Id, service.EffectiveTheme.Mode));
                foreach (var (key, value) in p)
                {
                    Assert.True(Application.Current!.TryFindResource(key, out var resource), $"{label}: {key} missing");
                    Assert.Equal(Color.Parse(value), Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color);
                }
                Assert.True(Application.Current!.TryFindResource(CelestialThemeTokens.ColourSet, out var published));
                var expected = CelestialColourSet.FromPalette(p);
                var set = Assert.IsType<CelestialColourSet>(published);
                Assert.Equal(expected.Objects, set.Objects);
                Assert.Equal((expected.BelowHorizon, expected.TerrainFill, expected.TerrainOutline), (set.BelowHorizon, set.TerrainFill, set.TerrainOutline));
                // Custom-drawn consumers follow at once, without a restart.
                Assert.Equal(expected.Objects, graph.CelestialColours.Objects);
                Assert.Equal(expected.Objects, map.CelestialColoursForTesting.Objects);
                Assert.Equal(expected[CelestialRole.DeepSky2], glyph.ColourForTesting);
                Assert.Equal(Color.Parse(p["Error"]), Assert.IsAssignableFrom<ISolidColorBrush>(status.TextBlockForTesting.Foreground).Color);
                Assert.Equal(Color.Parse(p["Error"]), Assert.IsAssignableFrom<ISolidColorBrush>(status.IconForTesting.Foreground).Color);
                var surface = Color.Parse(p[MapThemeTokens.Surface]);
                Assert.Equal(new Mapsui.Styles.Color(surface.R, surface.G, surface.B, surface.A), map.MapBackColorForTesting);
                // Fluent controls take the theme accent and surfaces instead of the platform defaults.
                Assert.True(Application.Current.TryFindResource("SystemAccentColor", out var accent));
                Assert.Equal(Color.Parse(p["Accent"]), accent);
                Assert.True(Application.Current.TryFindResource("DatePickerButtonBackground", out var datePicker));
                Assert.Equal(Color.Parse(p["InputBackground"]), Assert.IsAssignableFrom<ISolidColorBrush>(datePicker).Color);
            }
        }
        finally
        {
            window.Close();
            service.SetSupporterEntitlement(false);
            service.Apply(new());
        }
    }

    [AvaloniaFact]
    public void FluentProgressBarUsesTheThemeAccentInsteadOfThePlatformAccent()
    {
        var service = ((App)Application.Current!).Themes!;
        var progress = new ProgressBar { Value = 50, Width = 200, Height = 6 };
        var window = new Window { Width = 300, Height = 200, Content = progress };
        try
        {
            window.Show();
            foreach (var appearance in new[] { AppearanceMode.Dark, AppearanceMode.Light })
            {
                service.Apply(new(ThemeCatalogue.DefaultId, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(Color.Parse(service.EffectiveTheme.Palette["Accent"]), Assert.IsAssignableFrom<ISolidColorBrush>(progress.Foreground).Color);
            }
        }
        finally
        {
            window.Close();
            service.Apply(new());
        }
    }
}

public sealed partial class EnvironmentalOverlayTests
{
    [AvaloniaFact]
    public void ThemeSwitchRecoloursOverlaysWithoutRebuildingEnvironmentOrBasemap()
    {
        var service = ((App)Application.Current!).Themes!;
        var guide = new CameraFramingGuide(105, 70, 70, 140, CameraFramingDirectionSource.PrimaryTarget);
        var sector = new GeoSector(Observer, 105, 70, 500_000);
        var view = new NoctaxisMapView
        {
            Observer = Observer, Snapshot = Snapshot(Observer), FramingGuide = guide,
            FramingVisibility = Visibility(sector, 20_000, (0, 100), (70, 100)),
            FramingSettings = new CameraFramingSettings(ShadingOpacityPercent: 10, LineThickness: 1.25),
            ShowCameraOverlay = true, ShowCelestialOverlays = true
        };
        var layers = view.MapControlForTesting.Map!.Layers.ToArray();
        var window = new Window { Width = 400, Height = 400, Content = view };
        try
        {
            window.Show();
            var centre = WebMercator.FromWgs84(Observer);
            view.MapControlForTesting.Map.Navigator.CenterOnAndZoomTo(new MPoint(centre.X, centre.Y), 10, 0);
            Dispatcher.UIThread.RunJobs();
            using var rendered = new RenderTargetBitmap(new PixelSize(400, 400), new Vector(96, 96));
            rendered.Render(view.OverlayForTesting);
            var state = view.EnvironmentalStateForTesting;
            Assert.NotNull(state);
            var before = view.CelestialColoursForTesting;
            service.Apply(new(ThemeCatalogue.DefaultId, ColourVisionMode.Deuteranopia, AppearanceMode: AppearanceMode.Light));
            Dispatcher.UIThread.RunJobs();
            rendered.Render(view.OverlayForTesting);
            Assert.NotEqual(before.Objects, view.CelestialColoursForTesting.Objects);
            Assert.Same(state, view.EnvironmentalStateForTesting);
            Assert.Equal(layers, view.MapControlForTesting.Map.Layers.ToArray());
        }
        finally
        {
            window.Close();
            service.Apply(new());
        }
    }

    [AvaloniaFact]
    public void WeatherVisibilityLimitIsADashedFrontierNotOnlyAGreyShift()
    {
        const int size = 64;
        var sector = new GeoSector(Observer, 90, 70, 500_000);
        var state = new EnvironmentalOverlayStateCoordinator(profileTextureWidth: 32)
            .Update(Observer, sector, Visibility(sector, 1000, (0, null), (70, null)), ProfileKey());
        // Centred on the visibility distance: the frontier crosses the middle column top to bottom.
        var point = WebMercator.FromWgs84(Angles.Destination(Observer, 90, 1000));
        var parameters = EnvironmentalRenderParameters.Default with { WeatherMarkArgb = 0xffffffff };
        var frame = EnvironmentalOverlayMath.CreateFrame(new Viewport(point.X, point.Y, 2, 0, size, size), size, size, parameters);
        using var host = new EnvironmentalOverlayTestControl(state, frame, Colors.DeepPink) { Width = size, Height = size };
        host.Measure(new Size(size, size)); host.Arrange(new Rect(0, 0, size, size));
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size)); bitmap.Render(host);
        using var copy = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock(); bitmap.CopyPixels(buffer);
        int Mark(int x, int y)
        {
            var offset = y * buffer.RowBytes + x * 4;
            return Math.Min(Marshal.ReadByte(buffer.Address, offset + 2), Math.Min(Marshal.ReadByte(buffer.Address, offset + 1), Marshal.ReadByte(buffer.Address, offset)));
        }
        var marked = Enumerable.Range(0, size).Count(y => Enumerable.Range(size / 2 - 3, 7).Max(x => Mark(x, y)) > 150);
        // Dashed: long runs of marks and gaps along the frontier...
        Assert.InRange(marked, 15, size - 15);
        // ...and nothing like it away from the frontier, where the fill stays continuous.
        Assert.All(Enumerable.Range(0, size), y => Assert.True(Mark(4, y) < 60 && Mark(size - 5, y) < 60));
    }
}
