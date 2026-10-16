using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Controls.Docking;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed partial class EnvironmentalOverlayTests
{
    /// <summary>
    /// The Terrain frame, laid over the map's bottom-left, takes the theme's surface and stays wholly on the
    /// map at every text size, its drawn parts inside its own card; and the timeline still spans exactly
    /// the map's column.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)] [InlineData("builtin.noctaxis", AppearanceMode.Light)] [InlineData("builtin.highcontrast", AppearanceMode.Dark)] [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    public void AppearancePolishTerrainFrameReflowsOnTheMapAndKeepsTheFooterBounds(string id, AppearanceMode appearance)
    {
        var service = ((App)Application.Current!).Themes!;
        service.Apply(new(id, AppearanceMode: appearance));
        var window = new MainWindow { Width = 1440, Height = 1000 };
        window.Show();
        var pages = (TabControl)((Grid)window.Content!).Children[1]; pages.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var frame = window.GetVisualDescendants().OfType<TerrainFrameView>().Single();
        var map = window.FindControl<PlannerMapView>("PlannerMap")!;
        frame.Depth = new CameraTerrainDepth([350, 0, 10], -10, 10, Enumerable.Repeat(1000d, 3 * 72).ToImmutableArray());
        window.GetVisualDescendants().OfType<HorizonGraph>().Single().Snapshot = Snapshot(map.Observer);
        try
        {
            foreach (var scale in new[] { 1d, 1.1, 1.2, 1.5, 1.75, 2, 1 })
            {
                service.Apply(new(id, TextScale: scale, AppearanceMode: appearance)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                AppearancePolishTests.Capture(window, $"planner-{id}-{appearance}-{scale * 100:0}");
                Assert.Equal(Color.Parse(service.EffectiveTheme.Palette["SurfaceBackground"]), ((ISolidColorBrush)frame.ChromeBackground).Color);
                var card = window.FindControl<Border>("PlannerTerrainFrame")!;
                var cardAt = card.TranslatePoint(new Point(), map)!.Value;
                Assert.True(cardAt.X >= 0 && cardAt.Y >= 0 && cardAt.Y + card.Bounds.Height <= map.Bounds.Height + 1,
                    $"the Terrain frame should stay on the map at {scale}");
                foreach (var child in card.GetVisualDescendants().OfType<ChromeDrawingControl>())
                {
                    var position = child.TranslatePoint(new Point(), card)!.Value;
                    Assert.True(position.Y + child.Bounds.Height <= card.Bounds.Height + 1, $"{child.GetType().Name} exceeds the card at {scale}");
                }
                // The timeline is docked under the map and runs on under the Inspector, to its right edge.
                var footer = window.GetVisualDescendants().OfType<PlannerPanelHost>().Single(host => host.PanelId == "timeline");
                var inspector = window.GetVisualDescendants().OfType<PlannerPanelHost>().Single(host => host.PanelId == "inspector");
                Assert.Equal(map.TranslatePoint(new Point(), window)!.Value.X, footer.TranslatePoint(new Point(), window)!.Value.X, 3);
                Assert.Equal(inspector.TranslatePoint(new Point(inspector.Bounds.Width, 0), window)!.Value.X,
                    footer.TranslatePoint(new Point(footer.Bounds.Width, 0), window)!.Value.X, 3);
            }
        }
        finally { window.Close(); service.Apply(new()); }
    }
}
