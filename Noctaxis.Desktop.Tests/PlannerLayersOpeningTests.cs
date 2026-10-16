using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    /// <summary>
    /// Opening Layers only shows the flyout. Profiling found no work hidden in it, and this keeps it so:
    /// no layer state, attribution, preference edit or map redraw request, and the same content shown each
    /// time rather than rebuilt. Each opening is timed from the press to the flyout's first frame.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningLayersOnlyShowsTheFlyout()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var map = window.FindControl<PlannerMapView>("PlannerMap")!;
            var runtime = map.CompositionForTesting.Runtime;
            int states = 0, attribution = 0, edits = 0, refresh = 0, layerProperties = 0;
            runtime.StateChanged += (_, _) => states++;
            runtime.AttributionChanged += (_, _) => attribution++;
            map.Layers.PreferencesEdited += (_, _) => edits++;
            map.Layers.PropertyChanged += (_, _) => layerProperties++;
            map.CompositionForTesting.Map.RefreshGraphicsRequest += (_, _) => refresh++;
            var layers = window.GetVisualDescendants().OfType<PlannerLayersControl>().Single();
            var button = layers.FindControl<Button>("LayersButton")!;
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            var content = flyout.Content;
            var measured = 0;
            layers.OpeningMeasured += (_, _) => measured++;

            for (var i = 0; i < 3; i++)
            {
                var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                using (window.CaptureRenderedFrame()) { }
                Assert.True(flyout.IsOpen);
                Assert.Same(content, flyout.Content);
                Assert.Equal((0, 0, 0, 0, 0), (states, attribution, edits, refresh, layerProperties));
                flyout.Hide();
                Dispatcher.UIThread.RunJobs();
                layerProperties = 0;
            }
            // Each opening was timed, the first frame after the opening itself.
            Assert.Equal(3, measured);
            var last = layers.LastOpening!.Value;
            Assert.True(last.FirstFrame >= last.Opened);
            Assert.True(PlannerLayersControl.SlowOpening > TimeSpan.Zero);
        }
        finally { Close(window); }
    }
}
