using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Makes tooltips (<see cref="ToolTip.TipProperty"/>) purely informational: the pointer passes through them to
/// the control they describe.
/// </summary>
/// <remarks>
/// By default a tooltip is its own popup window (on Windows and Linux), so moving onto a tooltip that overlaps
/// its control took the pointer out of the main window: the control lost its hover state and the tooltip
/// service closed the tooltip, only to reopen it when the pointer was "back". Here each tooltip's popup is
/// hosted in its window's overlay layer, where hit testing is ours, and the tooltip is not hit-testable.
/// Leaving the control still closes the tooltip, and keyboard- and automation-opened tooltips are unchanged.
/// Only popups whose content is a <see cref="ToolTip"/> are affected: flyouts, menus and drop-downs keep
/// their own interactive popups. Interactive content belongs in a flyout, never a tooltip.
/// <para>
/// A control inside another popup — an item in an open drop-down, which on Windows is its own native window —
/// has no overlay layer to host a tooltip in. Forcing one there made Avalonia throw ("Unable to create
/// IPopupImpl and no overlay layer is found for the target control"), which ended the application when the
/// pointer rested on a theme in the Theme drop-down. So the overlay is chosen each time a tooltip is placed,
/// and only where its control has one; anywhere else the tooltip is the platform's own popup, as Avalonia
/// would make it.
/// </para>
/// </remarks>
public static class InformationalToolTips
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        Popup.ChildProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            if (change.NewValue is not ToolTip tip) return;
            popup.ShouldUseOverlayLayer = CanUseOverlayLayer(popup.PlacementTarget);
            tip.IsHitTestVisible = false;
        });
        // A tooltip's popup is placed at its control before it opens, every time it opens.
        Popup.PlacementTargetProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            if (popup.Child is ToolTip) popup.ShouldUseOverlayLayer = CanUseOverlayLayer(change.NewValue as Visual);
        });
    }

    /// <summary>
    /// Whether a tooltip for <paramref name="target"/> can be hosted in an overlay layer: the target's own
    /// window has one. A control in a native popup, or one not yet placed, has none.
    /// </summary>
    internal static bool CanUseOverlayLayer(Visual? target) => target is null || OverlayLayer.GetOverlayLayer(target) is not null;
}
