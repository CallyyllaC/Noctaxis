using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Mapsui;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// S-V03: a followed map re-centres when the observer moves, never when only the observer's elevation is
/// resolved for the same latitude and longitude.
/// </summary>
public sealed class MapFollowObserverTests
{
    private static (NoctaxisMapView View, Window Window) Show()
    {
        var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        view.MapControlForTesting.Map!.Layers.Clear(); // No basemap tile fetching in tests.
        var window = new Window { Width = 800, Height = 600, Content = view };
        // As MainViewModel does: a committed pin becomes the observer, at first with no resolved ground.
        view.CoordinateCommitted += (_, coordinate) => view.Observer = coordinate with { ElevationMetres = 0 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

    private static void DragPin(NoctaxisMapView view, Window window)
    {
        var pin = view.TranslatePoint(view.PinScreenPointForTesting!.Value, window)!.Value;
        window.MouseDown(pin, MouseButton.Left);
        window.MouseMove(pin + new Vector(30, 20), RawInputModifiers.LeftMouseButton);
        window.MouseUp(pin + new Vector(30, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static MPoint PanEast(NoctaxisMapView view, double metres)
    {
        var navigator = view.MapControlForTesting.Map.Navigator;
        var panned = new MPoint(navigator.Viewport.CenterX + metres, navigator.Viewport.CenterY);
        navigator.CenterOnAndZoomTo(panned, navigator.Viewport.Resolution, 0);
        Dispatcher.UIThread.RunJobs();
        return panned;
    }

    private static MPoint Centre(NoctaxisMapView view) =>
        new(view.MapControlForTesting.Map.Navigator.Viewport.CenterX, view.MapControlForTesting.Map.Navigator.Viewport.CenterY);

    [AvaloniaFact]
    public void ElevationResolvedAfterPinDragDoesNotRecentreAPannedMap()
    {
        var (view, window) = Show();
        try
        {
            DragPin(view, window);
            var committed = view.Observer;
            Assert.NotEqual(53, committed.Latitude);
            var panned = PanEast(view, 50_000);
            var pinBefore = view.PinScreenPointForTesting!.Value;

            // Terrain resolves the ground: same place, new elevation.
            view.Observer = committed with { ElevationMetres = 123 };
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(panned.X, Centre(view).X, 1e-6);
            Assert.Equal(panned.Y, Centre(view).Y, 1e-6);
            Assert.Equal(pinBefore.X, view.PinScreenPointForTesting!.Value.X, 0.5);
            Assert.Equal(pinBefore.Y, view.PinScreenPointForTesting!.Value.Y, 0.5);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ElevationOnlyChangeWithoutAPinCommitAlsoKeepsThePannedView()
    {
        var (view, window) = Show();
        try
        {
            var panned = PanEast(view, 20_000);

            view.Observer = view.Observer with { ElevationMetres = 250 };
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(panned.X, Centre(view).X, 1e-6);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ObserverMovedElsewhereStillRecentresAFollowedMap()
    {
        var (view, window) = Show();
        try
        {
            DragPin(view, window);
            view.Observer = view.Observer with { ElevationMetres = 123 };
            PanEast(view, 50_000);

            // A search result or saved location: a real move, which the followed map shows.
            view.Observer = new GeoCoordinate(-35, 149, 600);
            Dispatcher.UIThread.RunJobs();

            var centred = view.PinScreenPointForTesting!.Value;
            Assert.Equal(view.Bounds.Width / 2, centred.X, 1.0);
            Assert.Equal(view.Bounds.Height / 2, centred.Y, 1.0);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MapThatDoesNotFollowIsNotRecentredByAMove()
    {
        var (view, window) = Show();
        try
        {
            view.FollowObserver = false;
            var panned = PanEast(view, 50_000);

            view.Observer = new GeoCoordinate(-35, 149);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(panned.X, Centre(view).X, 1e-6);
        }
        finally { window.Close(); }
    }
}
