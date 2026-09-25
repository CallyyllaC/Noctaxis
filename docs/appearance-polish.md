# Appearance polish validation

Completed 2026-09-13 from the existing working tree. This report supersedes the foundation report's custom-renderer/text-scale limitations and test totals. Architecture and scientific calculation ownership remain unchanged. Final capture review required no further production changes.

## Root causes and corrections

| Issue | Concrete cause and fix |
|---|---|
| Text grows but clips until resize | Dynamic font resources changed text measurements while cached ancestor/template measurements remained stale. At 100→110%, headers retained 91/117px widths; explicit invalidation produced 97/127px. ThemeService now publishes an appearance layout revision on scale changes. A Window attached property coalesces one Loaded-priority dispatcher callback after the resource/binding batch, invalidates descendant and window measure/arrange, then updates layout. No timer, resize, page switch or global window registry. |
| Settings navigation | Stale TabItem bounds prevented the existing wrapping header panel from recomputing rows. Correct invalidation restores wrapping; all eight labels fit their measured bounds at 200%. |
| Light renderer leaks | HorizonGraph painted literal #0F151F and used parent-relative Bounds; TerrainFrameView used #101D30 for empty/letterbox chrome; LocalTerrainMap used #0A1018 for empty chrome. ChromeDrawingControl now caches dynamic surface/text/font properties. HorizonGraph paints local Bounds.Size and anchors axis labels separately. Scientific pixels remain independently coloured. |
| Footer inset | Outer main TabControl's default content padding inset the Planner by 12px per side. Padding=0 fixes actual container geometry. PlannerTimeline starts at window X=0 and equals window width in all three themes and six scales. |
| Image-card washout | Fluent's pointer-over ContentPresenter background won over Button.Background; the card pointer-over style also assigned WindowBackground. Scoped locationCardBody presenter styles now preserve transparency and use CardHoverOverlay/CardPressedOverlay. Ordinary buttons keep their normal state aliases. |
| Flat High Contrast states | Selection and unselected tabs shared chrome resources; card focus reused selection accent; selected list template painting retained Fluent blue. Selection now uses the SelectionBackground/Foreground pair, with explicit presenter state ownership. Hover borders are 2px, pressed 3px, selected 4px; focus is a separate 3px FocusBorder ring (cyan in None). Current cards have a checkmark and Current location label; favourites retain filled/hollow stars. Disabled controls keep subdued content and no interaction outline. Primary uses inverted accent fill, secondary an outline. |
| Terrain chrome sizing | Fixed control heights combined raster and scalable labels. LocalTerrainMap now reserves external title/radius/compass/scale chrome around its 208×208 logical raster; frame data remains within its 208×160 preferred surface. Empty status text wraps and measures naturally. TerrainPreviewPanel stacks or uses two 208px columns with a 12px gap when height is constrained and width permits. Legend swatch/label pairs wrap together. |
| Modes seem inert | Variants target shared status/focus roles. Ordinary surfaces, selection and decorative colours intentionally stay stable. Protanopia and Deuteranopia intentionally use the same authored palette. Screens without these semantic roles can look identical. |

Live regression sequences include 100→110→120→175→100→150→200→100 and Planner/page sequences through all six scales. Automatic layout matches a forced fresh measure without changing window dimensions. Exact 175% is retained; normalisation no longer rounds factors to tenths. The UI step remains 10%.

## Architecture, fallback, persistence and lifetime

ThemeCatalogue retains stable builtin.system, builtin.dark, builtin.light and builtin.highcontrast IDs, with None, Protanopia, Deuteranopia and Tritanopia modes. System resolves platform Light/Dark, with Dark when unavailable. Unknown/unavailable themes fall back to Dark; missing modes fall back to None; missing/invalid individual tokens inherit Dark with diagnostics. Built-ins are ungated; no supporter themes were added.

All three authored built-in palettes explicitly contain all 47 required tokens in all 12 theme/mode combinations. InheritedTokens is empty for every built-in. No unexpectedly inherited ordinary surface token was found: the Light leaks were renderer literals and geometry, not incomplete palettes. Malformed future-theme fallback remains tested.

ThemeService retains one replaceable dictionary, immutable brushes/Color counterparts, ten typography roles and one disposable platform subscription. Custom controls cache dynamic properties rather than resolving resources per pixel. The 120-switch measurement recorded 160.2339ms total, 4,413,968 allocated bytes, 37,880 retained bytes after collection, one dictionary and 175 resources. This is a bounded headless fixture, not a native memory benchmark.

Appearance changes still use ApplyAppearanceAsync and the existing nested Settings.Appearance state.json persistence path. They bypass calculation-bearing ApplySettingsAsync. Regression tests retain the same planning session and request counts: zero additional Planner, terrain, weather or astronomy requests. Theme-only terrain changes retain source/bitmap identity and raster build count. User terrain obstruction/tint colours, explicit framing colours, opacity/thickness and saved data are not rewritten.

The foundation migration remains 151 direct XAML colour literals and all 86 explicit XAML font-size literals, plus card gradients and map attribution styling. This polish adds two card overlay tokens and themed custom-renderer chrome. Existing large-text card reflow/height, date-picker width, scrolling dialogs/settings and wrapping navigation are retained.

## Exact colour-vision resource coverage

Hex values below are foreground roles in Success / Warning / Error / Information order.

| Theme | None | Protanopia = Deuteranopia | Tritanopia |
|---|---|---|---|
| Dark | #83D9B1 / #E8C96A / #FF9A9F / #9ACBFF | #79D3FF / #FFE080 / #FFB66D / #D7B7FF | #7DE0CA / #FFE5B4 / #FF9CBF / #BDCFFF |
| Light | #17613F / #705000 / #A21C37 / #19588F | #00628B / #705000 / #914B00 / #664499 | #00665B / #705000 / #A21C47 / #354D9F |
| High Contrast | #8CFFB1 / #FFFF00 / #FFA8BA / #8DDFFF | #79D3FF / #FFE080 / #FFB66D / #D7B7FF | #7DE0CA / #FFE5B4 / #FF9CBF / #BDCFFF |

None background roles (same order): Dark #142B24/#302817/#3B1820/#172A40; Light #E0F4E9/#FFF2C9/#FFE7EC/#E4F1FF; High Contrast all #000000. Non-None status backgrounds all become SurfaceBackground: Dark #131A25, Light #FFFFFF, High Contrast #000000. None FocusBorder is Dark #B8AEFF, Light #59409B, High Contrast #00FFFF; non-None FocusBorder equals that mode's Success.

Thus Dark changes nine palette roles; Light changes eight (Warning foreground stays #705000); High Contrast changes five (four background roles already equal black). Generated Color counterparts and aliases consuming FocusBorder change correspondingly. Protanopia↔Deuteranopia changes no resource values. Selection/Accent and decorative chrome do not change by mode.

| Classification | Sources and non-colour cues / boundary |
|---|---|
| Ordinary chrome | Surfaces, typography, button/list/tab states and renderer margins follow theme. Selection has fill/border/underline, focus a separate ring. No decorative hue changes to exaggerate mode differences. |
| Semantic UI | Success/Warning/Error/Information and focus use variants. Validation/delete/limit messages are labelled text; terrain direction is text; favourites use a star; current location has an explicit checkmark label. Diagnostic captures label all four statuses and add check/exclamation/cross/information symbols. |
| Specialist data | CelestialPalette and MainViewModel.CelestialColour, object paths/timeline series, weather/environment colours, terrain water/no-data/depth/sky pixels and diagnostic palettes remain outside variant coverage. Primary versus secondary paths retain solid/dashed styling and object names. These cues do not establish full perceptual accessibility of every scientific series. Broad recolouring requires a separate scientific-palette review. |
| User-owned | Explicit terrain/framing colours and associated preferences are preserved verbatim. |
| Decorative | Card imagery/gradients, shadows and already-safe branding remain independent. Card hover is a subtle alpha overlay, not a full-screen colour filter. |

Tests validate deterministic complete palettes, four distinct status values and existing text/status contrast checks (4.5:1; focus 3:1). Distinct hex values and luminance contrast are not a colour-vision simulation or clinical/perceptual certification. The twelve semantic comparison captures demonstrate the intentional scope.

## Fluent bridge ownership inventory

All aliases below globally follow semantic Noctaxis tokens for ordinary controls. Image-backed card presenters explicitly own their backgrounds; these aliases must not repaint card imagery. No additional global Fluent keys were added merely to change specialist visuals. Unlisted Fluent template keys retain Fluent defaults; this is not a claim that every native/template state has been restyled.

| Keys (suffix expansion is exhaustive) | Mapping |
|---|---|
| ButtonBackground{empty,PointerOver,Pressed,Disabled} | ButtonBackground, ButtonHover, ButtonPressed, ButtonDisabled |
| ButtonForeground{same four} | PrimaryText, except DisabledText |
| ButtonBorderBrush{same four} | SurfaceBorder |
| AccentButtonBackground{same four} | Accent, except disabled ButtonDisabled |
| AccentButtonForeground{same four} | AccentForeground, except DisabledText |
| AccentButtonBorderBrush{same four} | Accent |
| SliderTrackFill{same four} | SliderTrack |
| SliderTrackValueFill{same four}, SliderThumbBackground{same four} | SliderFill |
| TabItemHeaderForeground{Unselected,Selected,UnselectedPointerOver,SelectedPointerOver,UnselectedPressed,SelectedPressed,Disabled} | Selected states SelectionForeground; DisabledText for disabled; otherwise PrimaryText |
| TabItemHeaderBackground{same seven} | Selected states SelectionBackground; unselected pointer-over ButtonHover; otherwise ApplicationBackground |
| TabItemHeaderSelectedPipeFill | Accent |
| TextControlSelectionHighlightColor | SelectionBackground |
| TextControlForeground, TextControlForegroundDisabled | PrimaryText, DisabledText |
| TextControlBackground, TextControlBorderBrush, TextControlBorderBrushFocused | InputBackground, SurfaceBorder, FocusBorder |
| SystemControlFocusVisualPrimaryBrush, SystemControlFocusVisualSecondaryBrush | FocusBorder, WindowBackground |

Scoped styles additionally own selected list/combobox presenters, checked toggles and card state borders. Existing Fluent checkboxes/progress indicators are not represented as fully customised by this inventory; their checkmark/progress structure remains a non-colour cue.

## Validation and captures

| Suite | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Expanded focused | 78 | 0 | 0 | 78 |
| Release Core | 331 | 0 | 1 | 332 |
| Release Desktop | 440 | 0 | 0 | 440 |
| Sequential solution | 771 | 0 | 1 | 772 |

Full command: `dotnet test Noctaxis.slnx -c Release --no-restore -m:1 -v minimal`, exit 0. The final full run includes the final source-encoding correction and all focused cases. Focused totals comprise 69 appearance cases, seven card layout cases and two existing terrain/minimap regressions. The only skip is TerrariumLiveIntegrationTests.OfficialTerrariumSampleIsPhysicallyPlausible, an opt-in live-provider check. Earlier reproductions intentionally failed for stale header measures and opaque hover backgrounds; no final validation failures remain.

Logs: artifacts/appearance-polish-focused.log; artifacts/appearance-polish-full-release.log; artifacts/appearance-polish-repro.log. `git diff --check` passed (exit 0); output is recorded in artifacts/appearance-polish-diff-check.log.

The capture index lists every produced image: 186 current polish PNGs, six before-hover PNGs and 35 foundation PNGs. Current groups: 27 isolated card states, 24 real Locations states with synthetic image fixture, 18 Planner captures, six live Settings header captures, 72 Settings section captures, 27 dialog captures, twelve semantic/state matrices. Scales are 100/110/120/150/175/200 for Planner and Settings, and 100/150/200 for common dialogs. Return-to-100 overwrites the initial 100 capture; numerical logs/tests retain transition evidence.

Visual review covered all six Planner scales across three themes, the six live navigation scales, all twelve semantic matrices, and representative Locations/large-text Settings/dialog captures. Terrain/frame labels and grouped legends fit at 100/150/200; footer edges are correct in all themes. Scroll viewports deliberately show only part of long content at 200%; action buttons remain outside the scrolling content. Headless empty/white map canvases and uniform synthetic depth images are fixture limitations, not assertions about native maps.

Native Windows accessibility trees, screen readers, keyboard traversal across all controls, OS theme notifications, mixed DPI, very small windows, live data/GPU composition and long-running native memory behaviour remain unverified. No claim of universal layout compliance or full scientific colour-vision accessibility is made.

## Files

The foundation report contains its complete file inventory. The following are the polish files, including files shared with foundation work; unrelated pre-existing terrain/tools/image changes are preserved and not attributed to appearance polish.

- [Noctaxis.Core/Domain/AppearancePreferences.cs](H:/Noctaxis/Noctaxis.Core/Domain/AppearancePreferences.cs)
- [Noctaxis.Desktop/App.axaml](H:/Noctaxis/Noctaxis.Desktop/App.axaml)
- [Noctaxis.Desktop/Themes/AppearanceLayout.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/AppearanceLayout.cs)
- [Noctaxis.Desktop/Themes/ThemeService.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/ThemeService.cs)
- [Noctaxis.Desktop/Themes/BuiltInPalettes.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/BuiltInPalettes.cs)
- [Noctaxis.Desktop/Themes/FluentResourceBridge.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/FluentResourceBridge.cs)
- [Noctaxis.Desktop/Themes/ThemeCatalogue.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/ThemeCatalogue.cs)
- [Noctaxis.Desktop/Controls/ChromeDrawingControl.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/ChromeDrawingControl.cs)
- [Noctaxis.Desktop/Controls/TerrainPreviewPanel.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/TerrainPreviewPanel.cs)
- [Noctaxis.Desktop/Controls/HorizonGraph.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/HorizonGraph.cs)
- [Noctaxis.Desktop/Controls/LocalTerrainMap.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/LocalTerrainMap.cs)
- [Noctaxis.Desktop/Controls/TerrainFrameView.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/TerrainFrameView.cs)
- [Noctaxis.Desktop/Views/MainWindow.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/MainWindow.axaml)
- [Noctaxis.Desktop/Views/LocationsPage.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/LocationsPage.axaml)
- [Noctaxis.Desktop.Tests/AppearancePolishTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePolishTests.cs)
- [Noctaxis.Desktop.Tests/AppearancePolishTerrainTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePolishTerrainTests.cs)
- [Noctaxis.Desktop.Tests/AppearancePolishPageTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePolishPageTests.cs)
- [Noctaxis.Desktop.Tests/WindowPolicyTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/WindowPolicyTests.cs)
- [docs/appearance-polish.md](H:/Noctaxis/docs/appearance-polish.md)
- [docs/appearance-polish-captures.md](H:/Noctaxis/docs/appearance-polish-captures.md)

[Foundation report and file inventory](H:/Noctaxis/docs/appearance-foundation.md) · [Complete capture list](H:/Noctaxis/docs/appearance-polish-captures.md)
