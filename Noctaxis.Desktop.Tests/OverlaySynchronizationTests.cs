using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.Tests;

public sealed class OverlaySynchronizationTests
{
    [Fact]
    public void WorkerBurstQueuesOneInvalidationUntilRenderAndUsesNoIdleWork()
    {
        var queued = new Queue<Action>();
        var invalidations = 0;
        var scheduler = new ViewportRedrawScheduler(() => false, queued.Enqueue, () => invalidations++);
        Assert.False(scheduler.Request());
        scheduler.Attach();
        Assert.True(scheduler.Request());
        for (var i = 0; i < 100; i++) Assert.False(scheduler.Request());
        Assert.Single(queued);
        queued.Dequeue()();
        Assert.Equal(1, invalidations);
        Assert.False(scheduler.Request());
        scheduler.RenderStarting();
        Assert.Empty(queued);
        Assert.True(scheduler.Request());
        queued.Dequeue()();
        Assert.Equal(2, invalidations);
    }

    [Fact]
    public void UiUpdatesAreImmediateAndOldPostedWorkCannotCrossRenderOrAttachment()
    {
        var queued = new Queue<Action>();
        var invalidations = 0;
        var onUi = false;
        var scheduler = new ViewportRedrawScheduler(() => onUi, queued.Enqueue, () => invalidations++);
        scheduler.Attach();
        scheduler.Request();
        scheduler.Detach();
        scheduler.Attach();
        scheduler.Request();
        queued.Dequeue()();
        Assert.Equal(0, invalidations);
        queued.Dequeue()();
        Assert.Equal(1, invalidations);
        scheduler.RenderStarting();
        scheduler.Request();
        scheduler.RenderStarting(); // unrelated redraw already consumed latest viewport
        onUi = true;
        scheduler.Request();
        Assert.Equal(2, invalidations);
        queued.Dequeue()();
        Assert.Equal(2, invalidations);
    }

    [AvaloniaFact]
    public void NavigationInvalidatesImmediatelyWithoutTimerAndIdleDoesNotRedraw()
    {
        var view = new NoctaxisMapView();
        Assert.False(view.AnimationTimerEnabled);
        view.MapControlForTesting.Map!.Layers.Clear();
        var probe = new OverlaySynchronizationProbe(false, view.Observer);
        view.SyncProbe = probe;
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var bitmap = new RenderTargetBitmap(new PixelSize(800, 600));
            bitmap.Render(view.OverlayForTesting);
            var before = Count(probe, "invalidate");
            var observer = view.Observer;
            var navigator = view.MapControlForTesting.Map.Navigator;
            navigator.CenterOnAndZoomTo(new(1000, 2000), 200, 0);
            Assert.Equal(before + 1, Count(probe, "invalidate"));
            Assert.False(view.AnimationTimerEnabled);
            navigator.CenterOnAndZoomTo(new(5000, 6000), 100, 0);
            Assert.Equal(before + 1, Count(probe, "invalidate"));
            Assert.True(Count(probe, "coalesced") > 0);
            bitmap.Render(view.OverlayForTesting);
            Assert.Equal(observer, view.Observer);
            var expected = PlannerPinProbe.Project(observer, navigator.Viewport);
            Assert.Equal(expected.X, view.PinScreenPointForTesting!.Value.X, 8);
            for (var i = 0; i < 100; i++) view.TickAnimationForTesting();
            Assert.Equal(before + 1, Count(probe, "invalidate"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WorkerUpdatesMinimapAndDetachCancelsCallbacksWhileActivityRestartsOnAttach()
    {
        var view = new NoctaxisMapView { Observer = new GeoCoordinate(53, -1) };
        var map = view.MapControlForTesting.Map!;
        map.Layers.Clear();
        var probe = new OverlaySynchronizationProbe(false, view.Observer);
        view.SyncProbe = probe;
        var window = new Window { Width = 800, Height = 600, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var bitmap = new RenderTargetBitmap(new PixelSize(800, 600));
            bitmap.Render(view.OverlayForTesting);
            await Task.Run(() => map.Navigator.CenterOnAndZoomTo(new(1000, 2000), 100, 0));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(100 * Math.Cos(53 * Math.PI / 180), view.GroundMetresPerPixel, 8);
            Assert.Equal(map.Navigator.Viewport.Height, view.ViewportHeightPixels);
            view.PinActivity = PlannerPinActivity.CoreLoading;
            Assert.True(view.AnimationTimerEnabled);
            var activityBefore = Count(probe, "invalidate");
            view.TickAnimationForTesting();
            Assert.Equal(activityBefore + 1, Count(probe, "invalidate"));
            bitmap.Render(view.OverlayForTesting);
            await Task.Run(() => map.Navigator.CenterOnAndZoomTo(new(3000, 4000), 200, 0));
            window.Content = null;
            var detached = Count(probe, "invalidate");
            Assert.False(view.AnimationTimerEnabled);
            Dispatcher.UIThread.RunJobs();
            view.TickAnimationForTesting();
            Assert.Equal(detached, Count(probe, "invalidate"));
            map.Navigator.CenterOnAndZoomTo(new(5000, 6000), 300, 0);
            Assert.Equal(detached, Count(probe, "invalidate"));
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.AnimationTimerEnabled);
            Assert.Equal(300 * Math.Cos(53 * Math.PI / 180), view.GroundMetresPerPixel, 8);
            view.PinActivity = PlannerPinActivity.None;
            Assert.False(view.AnimationTimerEnabled);
        }
        finally { window.Close(); }
    }
    private static int Count(OverlaySynchronizationProbe probe, string kind) => probe.Samples.Count(s => s.Kind == kind);
}
