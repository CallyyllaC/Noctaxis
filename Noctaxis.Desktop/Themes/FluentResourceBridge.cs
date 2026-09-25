using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Noctaxis.Desktop.Themes;

/// <summary>Fluent template compatibility aliases; palette authors only supply semantic tokens.</summary>
internal static class FluentResourceBridge
{
    public static void Apply(ResourceDictionary resources)
    {
        void Alias(string key, string role) => resources[key] = resources[role];
        foreach (var (suffix, background) in new[] { ("", "ButtonBackground"), ("PointerOver", "ButtonHover"), ("Pressed", "ButtonPressed"), ("Disabled", "ButtonDisabled") })
        {
            Alias("ButtonBackground" + suffix, background);
            Alias("ButtonForeground" + suffix, suffix == "Disabled" ? "DisabledText" : "PrimaryText");
            Alias("ButtonBorderBrush" + suffix, "SurfaceBorder");
            Alias("AccentButtonBackground" + suffix, suffix == "Disabled" ? "ButtonDisabled" : "Accent");
            Alias("AccentButtonForeground" + suffix, suffix == "Disabled" ? "DisabledText" : "AccentForeground");
            Alias("AccentButtonBorderBrush" + suffix, "Accent");
            Alias("SliderTrackFill" + suffix, "SliderTrack");
            Alias("SliderTrackValueFill" + suffix, "SliderFill");
            Alias("SliderThumbBackground" + suffix, "SliderFill");
        }
        foreach (var state in new[] { "Unselected", "Selected", "UnselectedPointerOver", "SelectedPointerOver", "UnselectedPressed", "SelectedPressed", "Disabled" })
        {
            var selected = state.StartsWith("Selected", StringComparison.Ordinal);
            Alias("TabItemHeaderForeground" + state, state == "Disabled" ? "DisabledText" : selected ? "SelectionForeground" : "PrimaryText");
            Alias("TabItemHeaderBackground" + state, selected ? "SelectionBackground" : state.EndsWith("PointerOver", StringComparison.Ordinal) ? "ButtonHover" : "ApplicationBackground");
        }
        Alias("TabItemHeaderSelectedPipeFill", "Accent");
        Alias("TextControlSelectionHighlightColor", "SelectionBackground");
        Alias("TextControlForeground", "PrimaryText");
        Alias("TextControlForegroundDisabled", "DisabledText");
        Alias("TextControlBackground", "InputBackground");
        Alias("TextControlBorderBrush", "SurfaceBorder");
        Alias("TextControlBorderBrushFocused", "FocusBorder");
        Alias("SystemControlFocusVisualPrimaryBrush", "FocusBorder");
        Alias("SystemControlFocusVisualSecondaryBrush", "WindowBackground");
        // Expander paints its own template Border, bypassing ordinary Button aliases.
        // Resolve washes once per theme switch, never during rendering.
        void Wash(string key, string background, string foreground, double amount)
        {
            var a = ((ISolidColorBrush)resources[background]!).Color;
            var b = ((ISolidColorBrush)resources[foreground]!).Color;
            byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
            resources[key] = new ImmutableSolidColorBrush(Color.FromRgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B)));
        }
        Alias("ExpanderHeaderBackground", "SurfaceBackground");
        Wash("ExpanderHeaderBackgroundPointerOver", "SurfaceBackground", "Accent", .10);
        Wash("ExpanderHeaderBackgroundPressed", "SurfaceBackground", "Accent", .17);
        Alias("ExpanderHeaderExpandedBackground", "SelectionBackground");
        Wash("ExpanderHeaderExpandedBackgroundPointerOver", "SelectionBackground", "SelectionForeground", .07);
        Wash("ExpanderHeaderExpandedBackgroundPressed", "SelectionBackground", "SelectionForeground", .12);
        Alias("ExpanderHeaderBackgroundDisabled", "ButtonDisabled");
        foreach (var suffix in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            Alias("ExpanderHeaderBorderBrush" + suffix, suffix == "Disabled" ? "SubtleBorder" : "Accent");
            Alias("ExpanderHeaderForeground" + suffix, suffix == "Disabled" ? "DisabledText" : "PrimaryText");
            Alias("ExpanderChevronForeground" + suffix, suffix == "Disabled" ? "DisabledText" : "PrimaryText");
            resources["ExpanderChevronBackground" + suffix] = Brushes.Transparent;
            resources["ExpanderChevronBorderBrush" + suffix] = Brushes.Transparent;
        }
    }
}
