using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

public sealed class PlannerPinNoOpInteractionTests
{
    [AvaloniaFact]
    public void PressAndReleaseOnPinWithoutMovingDoesNotCommitAndLaterObserverChangeRecentres()
    {
        var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        view.MapControlForTesting.Map!.Layers.Clear(); // No basemap tile fetching in tests.
        var window = new Window { Width = 800, Height = 600, Content = view };
        var commits = 0;
        view.CoordinateCommitted += (_, _) => commits++;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var pin = view.TranslatePoint(view.PinScreenPointForTesting!.Value, window)!.Value;

            window.MouseDown(pin, MouseButton.Left);
            window.MouseUp(pin, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, commits);
            // The no-op must not leave the view believing a pin commit is still in flight:
            // an unrelated observer change (search result, saved location) re-centres the map.
            view.Observer = new GeoCoordinate(-35, 149);
            Dispatcher.UIThread.RunJobs();
            var centred = view.PinScreenPointForTesting!.Value;
            Assert.Equal(view.Bounds.Width / 2, centred.X, 1.0);
            Assert.Equal(view.Bounds.Height / 2, centred.Y, 1.0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DraggedPinStillCommitsItsNewCoordinate()
    {
        var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        view.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = 800, Height = 600, Content = view };
        GeoCoordinate? committed = null;
        view.CoordinateCommitted += (_, coordinate) => committed = coordinate;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var pin = view.TranslatePoint(view.PinScreenPointForTesting!.Value, window)!.Value;

            window.MouseDown(pin, MouseButton.Left);
            window.MouseMove(pin + new Vector(30, 20), RawInputModifiers.LeftMouseButton);
            window.MouseUp(pin + new Vector(30, 20), MouseButton.Left);

            Assert.NotNull(committed);
            Assert.NotEqual(view.Observer.Latitude, committed.Value.Latitude);
        }
        finally { window.Close(); }
    }
}
