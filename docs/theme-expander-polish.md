# Palette and Expander header styling

## Cause

Noctaxis mapped ordinary Fluent Button resources but not Fluent Expander resources. The Expander header's ToggleButton template paints `Border#ToggleButtonBackground` directly on pointer-over and press using `ExpanderHeaderBackgroundPointerOver` and `ExpanderHeaderBackgroundPressed`. Those inherited Fluent system colours displaced the Noctaxis fill. Separately, collapsed headers inherited the generic ToggleButton SurfaceBorder while checked headers inherited Accent, explaining the border mismatch.

Reference: https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Themes.Fluent/Controls/Expander.xaml

## Changes

- `BuiltInPalettes.cs`: Neon retains dark surfaces and cyan selection/focus, adds persistent fuchsia SliderFill (#FF3DBD dark / #B3167A light), complementary plum/pink button hover/press and fuchsia card overlays. Dark Accent is #38E8FF. Existing semantic roles are reused; scientific/user-owned colours are untouched.
- Foxfire Dark: window #0D0E12, application #101014, surface #17171D, raised surface #202027. Inputs, cards, buttons, map chrome and ordinary text/borders are neutralised. Orange #FF8A3D remains Accent/SliderFill, #FFB15C is the hot accent, and the existing warm selected fill remains. Foxfire Light is unchanged.
- `FluentResourceBridge.cs`: maps header/chevron normal, hover, pressed and disabled resources. Collapsed hover/press mix SurfaceBackground with Accent by 10%/17%. Expanded fill uses SelectionBackground, with 7%/12% SelectionForeground washes for hover/press. These immutable brushes are generated once per theme switch.
- `App.axaml`: shared Expander-only selectors own expanded fill and selected text/chevron colour through resources. Enabled header borders use Accent in collapsed/expanded/hover/pressed states. Disabled headers use existing subdued resources. Original templates, padding, sizing, typography, border thicknesses and chevron behaviour remain.

The three new derived expanded-state resources are `ExpanderHeaderExpandedBackground`, `ExpanderHeaderExpandedBackgroundPointerOver` and `ExpanderHeaderExpandedBackgroundPressed`. Existing Fluent resource names are bridged for other states; no new authored palette schema is needed. No hard-coded black hover value remains in the Noctaxis Expander styling. High Contrast's intentional normal black surfaces/selection remain, while hover/pressed fills are non-black.

## Validation and captures

ExpanderPaletteTests covers 48 theme/appearance/colour-vision combinations, each with collapsed, hover, pressed, expanded, expanded-hover, expanded-pressed and both disabled states. It verifies actual template border fills and colours, distinct interactions, 4.5:1 enabled text contrast and 3:1 chevron contrast. Three additional palette tests check cyan/fuchsia usage and neutral Foxfire structural surfaces. The combined appearance/supporter/Settings selection passed 265 tests.

Twelve headless state captures were reviewed under `artifacts/appearance-polish/expander-{theme-id}-{Dark|Light}.png`, for builtin.noctaxis, builtin.highcontrast, supporter.noctaxis-neon, supporter.foxfire, supporter.deep-space and supporter.moonlit. These are rendered review artifacts, not pixel-golden tests. Native desktop interaction was not exercised. Existing neutral Expander content-panel borders were outside this header-only change and remain unchanged.

Final sequential Release validation: Core 331 passed / 1 skipped; Desktop 712 passed; solution 1043 passed / 0 failed / 1 skipped. The skip is the existing opt-in official live Terrarium check. Log: artifacts/expander-full-release.log. `git diff --check` passed (line-ending conversion notices only).
