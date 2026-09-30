using Avalonia;
using Avalonia.Controls;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Content placed on the trailing side of the application navigation strip, beside the page names.
/// </summary>
/// <remarks>
/// The navigation is a <see cref="TabControl"/>, which has one content slot and it belongs to the
/// selected page. This attached property gives the strip itself somewhere to put shell-level content —
/// today the shared search field — so that content stays in the window's markup, where it can be read
/// and bound, instead of being written into a style's control template.
/// </remarks>
public static class NavigationHost
{
    public static readonly AttachedProperty<object?> TrailingProperty =
        AvaloniaProperty.RegisterAttached<TabControl, object?>("Trailing", typeof(NavigationHost));

    public static object? GetTrailing(TabControl control) => control.GetValue(TrailingProperty);

    public static void SetTrailing(TabControl control, object? value) => control.SetValue(TrailingProperty, value);
}
