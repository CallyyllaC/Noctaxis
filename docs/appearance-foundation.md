# Noctaxis appearance foundation — implementation and validation report

## Architecture and ownership

`ThemeService` owns one replaceable application resource dictionary and one Avalonia platform colour-change subscription. Controls consume DynamicResource tokens. `ThemeCatalogue` resolves stable IDs, checks cosmetic availability, and overlays semantic colour-vision variants. `ThemeDefinitionValidator` detects missing/invalid required palette entries and missing mode declarations. Definition metadata includes stable ID, display name, supporter flag, supported modes, description and schema version 1.

There are 45 semantic palette tokens, plus Color counterparts, gradient colours, typography and Fluent compatibility aliases. The resolver starts with Dark fallback resources, then overlays the selected palette and semantic variant. Brushes are immutable and created at switching time. FluentResourceBridge maps template-state keys to the shared vocabulary without replacing Fluent templates. Button, tab, slider, text-input and focus resources are included. Template-key references were checked against [Avalonia's Fluent source](https://github.com/AvaloniaUI/Avalonia/tree/master/src/Avalonia.Themes.Fluent/Controls); application behaviour is validated against the installed Avalonia 12.1.0 packages.

The audit recorded 261 colour-source occurrences across 19 files, including 165 in UI XAML, and 86 explicit XAML font sizes. These are source-pattern counts, not exhaustive counts of every numeric Skia colour constructor. Migration replaced 151 direct XAML colour literals and all 86 font-size literals, plus card-gradient colours and C# map attribution styling. Closely related local shades were consolidated. Image effects/shadows, specialist data colours and diagnostics are deliberately outside ordinary theme ownership; see appearance-audit.md for the original per-file inventory.

## Built-ins and variants

| Stable ID | Behaviour | Window / primary text / accent |
|---|---|---|
| builtin.dark | Default, preserves the blue-grey Dark appearance with consolidated shades | #0B0F17 / #E8ECF2 / #B4A9FF |
| builtin.light | Authored light surfaces and dark text | #F4F6FA / #172334 / #59409B |
| builtin.highcontrast | Authored black/white theme with yellow accent and cyan standard focus | #000000 / #FFFFFF / #FFFF00 |
| builtin.system | Resolves OS light/dark using Avalonia platform settings; unavailable platform information falls back to Dark | Effective Light or Dark palette |

All built-in palettes support None, Protanopia, Deuteranopia and Tritanopia. High Contrast is a theme, not an additional flag. Protanopia/Deuteranopia share a deliberately selected blue/gold/orange/purple status set; Tritanopia uses teal/gold/rose/blue. Light has darker foreground equivalents. Variant status backgrounds use the theme surface, and focus uses the first variant colour. There is no completed-image RGB filter.

These variants currently cover shared success/warning/error/information and focus resources. They do **not** recolour existing celestial paths, weather/data visualisations or terrain. Some overlapping celestial series remain dependent on colour; labels and solid/dashed primary distinctions are retained. Further specialist palette work is deferred rather than claimed complete.

## Text size and layouts

The persisted factor is 1.0–2.0 in 0.1 increments, independently of DPI and colour vision. Ten semantic typography sizes scale through resources; tab headers also respond. Headless page/dialog construction passes at 100%, 120%, 150% and 200%.

Layout changes include wrapping text and Settings tabs, a wider date selector at larger text, naturally measured card heights, larger card minimum height, and full-width card text with an opaque backing above 120%. Edit/confirmation dialog content can scroll. The pin readout was moved out of the minimap's footprint. Remove-camera/lens buttons now have accessible names. Delete-button opacity no longer suppresses contrast.

The full-suite minimap interaction regression was corrected by restoring its original non-hit-testable wrapper and removing the new scroll wrapper. Wheel, trackpad and pan input continue through to Mapsui. The fixed-size terrain/minimap renderers themselves are unchanged: dense minimap content at large text/small viewport heights remains a native layout validation limitation. This report does not claim universal 200% layout compliance at every window size.

## Persistence, runtime and fallbacks

Appearance is nested in the existing `AppSettings.Appearance` record in the existing `state.json`, with `SelectedThemeId`, `ColourVisionMode` and `TextScale`. On Windows the normal manual path is `%APPDATA%/Noctaxis/state.json`. Existing files without Appearance use Dark / None / 100%. There is no second settings store. Preference names follow the existing serializer's property casing.

Changing an appearance control updates visible resources and saves immediately. `ApplyAppearanceAsync` changes only Appearance and bypasses `ApplySettingsAsync`; it does not schedule astronomy, weather, horizon or terrain work. Tests verify unchanged session/snapshot references, unrelated settings and calculation request counts. Save failures are reported in the existing status message.

Unknown/unavailable IDs resolve to Dark; unavailable cosmetic IDs cannot break startup. An absent requested mode falls back to standard and logs a reason. Missing individual palette entries inherit Dark; invalid colour entries retain inherited values and report a reason. Invalid mode numbers and non-finite/out-of-range text scales normalise safely. The selected stable ID can be retained while its effective fallback is displayed.

System changes are dispatched to the UI thread only while System is selected. No MainWindow recreation or restart is required. Platform observation relies on Avalonia's platform implementation; native OS preference changes were not exercised here.

## Supporter boundary and adding themes

Register a ThemeDefinition with a unique stable ID, palette, metadata and all mode declarations in the catalogue. Supply cosmetic availability through the catalogue predicate; the default grants no supporter entitlement. Controls never query supporter status. The shipped catalogue contains no sample supporter theme or network entitlement integration. Accessibility remains available regardless of that predicate.

A future dark-based cosmetic palette can be registered without editing controls. The current Fluent base/Light semantic variant selection explicitly recognises builtin.light; a future light-based supporter theme will need base-variant metadata in the catalogue/service before it can be fully supported. The schema field is informational, not an external theme-file loader.

## Preservation and lifetime

Terrain obstruction colour, terrain tint strength, framing opacity/line thickness and other explicit presentation preferences remain user-owned. Theme and colour-vision changes do not mutate them. Terrain generation, rasterisation, tone/local contrast, profile/horizon services, providers, weather/astronomy calculations and FoV semantics were not modified by this task. Existing terrain changes were already in the working tree. The only C# map edit in this task styles attribution through cached dynamic properties.

Repeated switching retains one dictionary and one platform subscription. Disposal unsubscribes and removes the owned dictionary. The 120-switch headless measurement is in artifacts/appearance/switch-stress.json. The recorded run took 198.09 ms total, allocated 3,902,632 bytes and retained 37,992 additional bytes after collection; the dictionary held 166 entries. It measures a small bound window, not a native application's complete memory footprint. Rendering does not rebuild theme dictionaries or perform per-pixel resource lookup.

## Validation

Final sequential command: `dotnet test Noctaxis.slnx -c Release --no-restore -m:1 -v minimal` (exit 0).

| Suite | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Core | 331 | 0 | 1 | 332 |
| Desktop | 414 | 0 | 0 | 414 |
| Solution | 745 | 0 | 1 | 746 |

The skip is the opt-in official Terrarium live integration test. There are 43 appearance-focused cases; the focused run also included seven card-layout cases (50 passed). The post-correction run included three existing regressions (53 passed, no failures/skips). An initial full run found the minimap pass-through regression and the outdated General-control inventory test; both are corrected, with the fresh full run above passing.

`git diff --check` passed (exit 0); only existing line-ending conversion notices were emitted. Focused appearance tests cover resolution, all 12 theme/mode combinations, fallback/availability, persistence/legacy settings, text bounds, live resources, construction, independent user state and contrast. Ordinary text/status pairs target 4.5:1; focus targets 3:1. All tested token combinations pass. Map imagery and specialist visualisations are not asserted to satisfy ordinary text contrast rules.

Native keyboard/screen-reader workflows, OS theme notifications, GPU map/terrain rendering, real terrain availability and native long-running memory behaviour remain unverified. Headless captures use test-double planning data and do not establish physical/live-provider correctness. Custom renderer typography remains outside the migrated XAML typography set.

## Captures and logs

`artifacts/appearance/` contains 35 PNGs: Locations, Planner and Settings for Dark/Light/High Contrast at 100%; Dark's three colour-vision modes; Dark at 120%, 150% and 200%; and eight Settings sections at 200%. Page indices are 0 Locations, 1 Planner, 2 Settings. These artifacts are local, not published screenshots.

Logs: artifacts/appearance-tests.log (50 checks including card layout), artifacts/appearance-validation.log (53 including the three corrected regression cases), artifacts/appearance-full-release.log (sequential solution), artifacts/appearance-restore.log (successful restore after sandbox network denial). Capture generation is opt-in through NOCTAXIS_APPEARANCE_CAPTURES; its test returns without rendering when unset.

## Files changed by this appearance task

Some listed files already had unrelated changes; those changes were preserved. Other pre-existing terrain tests, renderers, diagnostics, documentation, tools and images are not attributed to this task.

- [Noctaxis.Core/Domain/Models.cs](H:/Noctaxis/Noctaxis.Core/Domain/Models.cs)
- [Noctaxis.Core/Domain/AppearancePreferences.cs](H:/Noctaxis/Noctaxis.Core/Domain/AppearancePreferences.cs)
- [Noctaxis.Desktop/App.axaml](H:/Noctaxis/Noctaxis.Desktop/App.axaml)
- [Noctaxis.Desktop/App.axaml.cs](H:/Noctaxis/Noctaxis.Desktop/App.axaml.cs)
- [Noctaxis.Desktop/Controls/LocationSearchControl.axaml](H:/Noctaxis/Noctaxis.Desktop/Controls/LocationSearchControl.axaml)
- [Noctaxis.Desktop/Controls/NoctaxisMapView.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/NoctaxisMapView.cs)
- [Noctaxis.Desktop/Controls/ResponsiveCardGridPanel.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/ResponsiveCardGridPanel.cs)
- [Noctaxis.Desktop/Controls/LocationCardTextGrid.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/LocationCardTextGrid.cs)
- [Noctaxis.Desktop/ViewModels/MainViewModel.cs](H:/Noctaxis/Noctaxis.Desktop/ViewModels/MainViewModel.cs)
- [Noctaxis.Desktop/ViewModels/MainViewModel.Appearance.cs](H:/Noctaxis/Noctaxis.Desktop/ViewModels/MainViewModel.Appearance.cs)
- [Noctaxis.Desktop/Views/ConfirmationDialog.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/ConfirmationDialog.axaml)
- [Noctaxis.Desktop/Views/LocationSearchDialog.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/LocationSearchDialog.axaml)
- [Noctaxis.Desktop/Views/LocationsPage.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/LocationsPage.axaml)
- [Noctaxis.Desktop/Views/MainWindow.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/MainWindow.axaml)
- [Noctaxis.Desktop/Views/SavedLocationEditDialog.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/SavedLocationEditDialog.axaml)
- [Noctaxis.Desktop/Themes/BuiltInPalettes.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/BuiltInPalettes.cs)
- [Noctaxis.Desktop/Themes/FluentResourceBridge.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/FluentResourceBridge.cs)
- [Noctaxis.Desktop/Themes/ThemeCatalogue.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/ThemeCatalogue.cs)
- [Noctaxis.Desktop/Themes/ThemeService.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/ThemeService.cs)
- [Noctaxis.Desktop.Tests/MainViewModelTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/MainViewModelTests.cs)
- [Noctaxis.Desktop.Tests/AppearanceTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearanceTests.cs)
- [Noctaxis.Desktop.Tests/AppearancePersistenceTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePersistenceTests.cs)
- [Noctaxis.Desktop.Tests/AppearanceCaptures.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearanceCaptures.cs)
- [docs/appearance-audit.md](H:/Noctaxis/docs/appearance-audit.md)
- [docs/appearance-foundation.md](H:/Noctaxis/docs/appearance-foundation.md)

