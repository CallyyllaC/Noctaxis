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
            // Hover and press also strengthen the border (plus a layout-neutral ring, see ThemeService).
            Alias("ButtonBorderBrush" + suffix, suffix is "PointerOver" or "Pressed" ? "StrongBorder" : "SurfaceBorder");
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
        // Fluent accent family (ProgressBar, CheckBox, ToggleSwitch, list highlights): the theme accent, never the OS accent.
        foreach (var (key, role) in new[] { ("SystemAccentColor", "Accent"), ("SystemAccentColorLight1", "AccentHover"), ("SystemAccentColorLight2", "Accent"),
                     ("SystemAccentColorLight3", "AccentPressed"), ("SystemAccentColorDark1", "Accent"), ("SystemAccentColorDark2", "AccentHover"), ("SystemAccentColorDark3", "AccentPressed") })
            resources[key] = resources[role + "Color"];
        Alias("SystemControlHighlightAccentBrush", "Accent");
        Alias("SystemControlHighlightListAccentLowBrush", "SelectionBackground");
        Alias("SystemControlHighlightListAccentMediumBrush", "SelectionBackground");
        Alias("SystemControlHighlightListAccentHighBrush", "SelectionBackground");
        Alias("HighlightBackground", "Accent");
        Alias("HighlightForeground", "AccentForeground");
        // DatePicker button and flyout.
        foreach (var (suffix, background, border) in new[] { ("", "InputBackground", "SurfaceBorder"), ("PointerOver", "ButtonHover", "StrongBorder"),
                     ("Pressed", "ButtonPressed", "StrongBorder"), ("Disabled", "ButtonDisabled", "SubtleBorder") })
        {
            Alias("DatePickerButtonBackground" + suffix, background);
            Alias("DatePickerButtonBorderBrush" + suffix, border);
            Alias("DatePickerButtonForeground" + suffix, suffix == "Disabled" ? "DisabledText" : "PrimaryText");
        }
        Alias("DatePickerFlyoutPresenterBackground", "ElevatedSurface");
        Alias("DatePickerFlyoutPresenterBorderBrush", "SurfaceBorder");
        Alias("DatePickerFlyoutPresenterHighlightFill", "SelectionBackground");
        Alias("DatePickerFlyoutPresenterSpacerFill", "SubtleBorder");
        Alias("DatePickerSpacerFill", "SubtleBorder");
        Alias("DatePickerSpacerFillDisabled", "SubtleBorder");
        // Scroll bars.
        resources["ScrollBarThumbBackgroundColor"] = resources["MutedTextColor"];
        resources["ScrollBarPanningThumbBackgroundColor"] = resources["MutedTextColor"];
        Alias("ScrollBarPanningThumbBackground", "MutedText");
        Alias("ScrollBarThumbFillPointerOver", "SecondaryText");
        Alias("ScrollBarThumbFillPressed", "PrimaryText");
        Alias("ScrollBarThumbFillDisabled", "DisabledText");
        Alias("ScrollBarTrackFill", "SurfaceBackground");
        Alias("ScrollBarTrackFillPointerOver", "SurfaceBackground");
        Alias("ScrollBarTrackStroke", "SubtleBorder");
        Alias("ScrollViewerScrollBarsSeparatorBackground", "SurfaceBackground");
        Alias("ScrollBarTrackStrokePointerOver", "SurfaceBorder");
        foreach (var suffix in new[] { "", "PointerOver", "Pressed" }) Alias("ScrollBarButtonArrowForeground" + suffix, "SecondaryText");
        Alias("ScrollBarButtonArrowForegroundDisabled", "DisabledText");
        // Placeholders, drop-down glyphs and expander content borders.
        foreach (var suffix in new[] { "", "PointerOver", "Focused" }) Alias("TextControlPlaceholderForeground" + suffix, "MutedText");
        Alias("TextControlPlaceholderForegroundDisabled", "DisabledText");
        Alias("ComboBoxPlaceHolderForeground", "MutedText");
        Alias("ComboBoxPlaceHolderForegroundFocusedPressed", "MutedText");
        foreach (var suffix in new[] { "", "Focused", "FocusedPressed" }) Alias("ComboBoxDropDownGlyphForeground" + suffix, "SecondaryText");
        Alias("ComboBoxDropDownGlyphForegroundDisabled", "DisabledText");
        Alias("ComboBoxBackgroundBorderBrushUnfocused", "SurfaceBorder");
        Alias("ComboBoxBackgroundBorderBrushFocused", "FocusBorder");
        foreach (var (suffix, border) in new[] { ("", "SurfaceBorder"), ("PointerOver", "StrongBorder"), ("Pressed", "StrongBorder"), ("Disabled", "SubtleBorder") })
            Alias("ComboBoxBorderBrush" + suffix, border);
        Alias("ExpanderContentBorderBrush", "SubtleBorder");
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
