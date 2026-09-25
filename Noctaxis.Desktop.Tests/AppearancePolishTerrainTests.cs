using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed partial class EnvironmentalOverlayTests
{
    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)] [InlineData("builtin.noctaxis", AppearanceMode.Light)] [InlineData("builtin.highcontrast", AppearanceMode.Dark)] [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    public void AppearancePolishTerrainChromeReflowsAndKeepsRasterAndFooterBounds(string id, AppearanceMode appearance)
    {
        var service = ((App)Application.Current!).Themes!;
        service.Apply(new(id, AppearanceMode: appearance));
        var window = new MainWindow { Width = 1440, Height = 1000 };
        window.Show();
        var pages = (TabControl)((Grid)window.Content!).Children[1]; pages.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var mini = window.GetVisualDescendants().OfType<LocalTerrainMap>().Single();
        var frame = window.GetVisualDescendants().OfType<TerrainFrameView>().Single();
        mini.Map = ReliefMap(new GeoCoordinate(53, -1), (x, y) => 100 + x * 10 + Math.Sin(y) * 30);
        mini.LoadState = TerrainDebugMapLoadState.Ready;
        frame.Depth = new CameraTerrainDepth([350, 0, 10], -10, 10, Enumerable.Repeat(1000d, 3 * 72).ToImmutableArray());
        window.GetVisualDescendants().OfType<HorizonGraph>().Single().Snapshot = Snapshot(mini.Observer);
        try
        {
            foreach (var scale in new[] { 1d, 1.1, 1.2, 1.5, 1.75, 2, 1 })
            {
                service.Apply(new(id, TextScale: scale, AppearanceMode: appearance)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                AppearancePolishTests.Capture(window, $"planner-{id}-{appearance}-{scale * 100:0}");
                Assert.Equal(208, mini.RasterBounds.Height, 3);
                Assert.Equal(208, mini.RasterBounds.Width, 3);
                Assert.Equal(10 * scale, mini.ChromeFontSize, 3);
                Assert.Equal(Color.Parse(service.EffectiveTheme.Palette["SurfaceBackground"]), ((ISolidColorBrush)frame.ChromeBackground).Color);
                var chrome = window.FindControl<Border>("PlannerTerrainMinimap")!;
                Assert.False(chrome.IsHitTestVisible);
                foreach (var child in chrome.GetVisualDescendants().OfType<ChromeDrawingControl>())
                {
                    var position = child.TranslatePoint(new Point(), chrome)!.Value;
                    Assert.True(position.Y + child.Bounds.Height <= chrome.Bounds.Height + 1, $"{child.GetType().Name} exceeds chrome at {scale}");
                }
                var footer = window.FindControl<Border>("PlannerTimeline")!;
                Assert.Equal(0, footer.TranslatePoint(new Point(), window)!.Value.X, 3);
                Assert.Equal(window.Bounds.Width, footer.Bounds.Width, 3);
            }
            var originalMap = mini.Map; var originalBitmap = mini.DisplayBitmap; var builds = mini.RasterBuildCount;
            service.Apply(new("builtin.light")); Dispatcher.UIThread.RunJobs();
            using var bitmap = new RenderTargetBitmap(new PixelSize(208, (int)mini.Bounds.Height)); bitmap.Render(mini);
            Assert.Same(originalMap, mini.Map); Assert.Same(originalBitmap, mini.DisplayBitmap); Assert.Equal(builds, mini.RasterBuildCount);
        }
        finally { window.Close(); service.Apply(new()); }
    }
}
