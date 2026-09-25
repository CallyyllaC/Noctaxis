using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctaxis.Desktop.Themes;

/// <summary>Invalidates cached template measurements after a batch of typography resource changes.</summary>
public sealed class AppearanceLayout : AvaloniaObject
{
    public static readonly AttachedProperty<int> RevisionProperty =
        AvaloniaProperty.RegisterAttached<AppearanceLayout, Window, int>("Revision");
    private static readonly AttachedProperty<bool> PendingProperty =
        AvaloniaProperty.RegisterAttached<AppearanceLayout, Window, bool>("Pending");
    public static int GetRevision(Window window) => window.GetValue(RevisionProperty);
    public static void SetRevision(Window window, int value) => window.SetValue(RevisionProperty, value);
    static AppearanceLayout() => RevisionProperty.Changed.AddClassHandler<Window>((window, _) =>
    {
        if (window.GetValue(PendingProperty)) return;
        window.SetValue(PendingProperty, true);
        // Complete the resource/binding batch first. Loaded is a dispatcher layout phase, not a delay.
        Dispatcher.UIThread.Post(() =>
        {
            window.SetValue(PendingProperty, false);
            foreach (var child in window.GetVisualDescendants().OfType<Layoutable>())
            {
                child.InvalidateMeasure();
                child.InvalidateArrange();
            }
            window.InvalidateMeasure();
            window.InvalidateArrange();
            window.UpdateLayout();
        }, DispatcherPriority.Loaded);
    });
}
