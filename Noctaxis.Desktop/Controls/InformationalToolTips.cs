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
            popup.ShouldUseOverlayLayer = true;
            tip.IsHitTestVisible = false;
        });
    }
}
