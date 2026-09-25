# Appearance model normalisation

The existing appearance service and calculation isolation are retained. This report supersedes the identity model in the earlier foundation/polish reports.

## Model before and after

Previously SelectedThemeId combined Dark, Light, System and High Contrast identities. Preferences now independently store ThemeFamilyId × AppearanceMode × ColourVisionMode × TextScale. Theme options contain only Noctaxis and High Contrast; Mode contains System, Dark and Light. The existing text-size control, typography and 100–200% live layout behaviour are unchanged.

The default remains Noctaxis Dark to preserve the previous default. System is a mode, never a catalogue family. Legacy IDs are recognised only by compatibility normalisation; the resolver and platform observer use the explicit mode.

## Families and palettes

| Stable family ID | Name | Dark | Light |
|---|---|---|---|
| builtin.noctaxis | Noctaxis | Existing dark palette | Existing light palette |
| builtin.highcontrast | High Contrast | Existing black-dominant palette | Authored white-dominant palette |

ThemeDefinition now contains family identity, display name, supporter flag, description, schema version and Dark/Light ThemePaletteDefinition values. Each palette supplies its base tokens and authored colour-vision variants. Both modes of both families support None, Protanopia, Deuteranopia and Tritanopia. All 16 combinations explicitly contain all 47 required tokens and report zero inherited tokens.

The existing semantic colour-vision function prepares the internal built-in variants once. Protanopia and Deuteranopia deliberately share their authored semantic set. Light variants use the existing dark-foreground semantic set. No scientific palettes or full-screen filters were added.

## High Contrast Light

Surfaces are white, ordinary text and borders black, muted text #333333 and disabled text #595959. Selection/current and primary actions use black fill with white text. Hover and pressed surfaces are #E0E0E0/#C0C0C0, with 2px/3px state outlines. Selection retains 4px borders; focus retains a separate 3px ring, #005FCC in None. Colour-vision modes replace the focus hue with their semantic focus colour, independently of black selection.

Current-location check/label, filled versus hollow favourite star, selected tab underline, inversion and disabled interaction treatment remain intact. Image cards keep their images on hover; High Contrast Light hover overlay is transparent and pressed overlay is translucent black. This palette was authored deliberately using the existing Light vocabulary and explicit high-contrast overrides, not RGB inversion.

Existing state-matrix, contrast, image-hover, live page/dialog and terrain-chrome tests now cover both HC modes. Reviewed captures include HC Light normal/hover/pressed/selected/focused/disabled controls, current/favourite cards, and Settings at 100% and 200%.

## Persistence migration

The existing AppSettings.Appearance object in state.json remains the sole storage path. Its converter reads the legacy SelectedThemeId field and writes only ThemeFamilyId, AppearanceMode, ColourVisionMode and TextScale. It retains the store's enum serialisation convention and case-insensitive property reading.

| Legacy SelectedThemeId | ThemeFamilyId | AppearanceMode |
|---|---|---|
| builtin.system | builtin.noctaxis | System |
| builtin.dark | builtin.noctaxis | Dark |
| builtin.light | builtin.noctaxis | Light |
| builtin.highcontrast | builtin.highcontrast | Dark unless an explicit persisted mode exists |

Colour vision and text scale survive migration. New ThemeFamilyId takes precedence if both fields exist. Real-store tests load each old shape, preserve explicit terrain colour, save the new shape and reload it. Missing appearance settings retain the existing Dark default. No migration rewrites saved-location content or creates another store.

## System behaviour and lifetime

System resolves the selected family using the current OS Light/Dark preference; unavailable platform information resolves Dark. Platform changes update live on the UI dispatcher only while Mode is System. Explicit Dark/Light ignore them; queued notifications recheck Mode before applying. OS changes leave family, colour vision, text scale and persisted preferences unchanged.

IAppearancePlatform is a small preference/event boundary because Avalonia disallows application implementations of its platform interface. The production adapter forwards the platform event, attaching one subscription and removing it when ThemeService unsubscribes. Deterministic tests drive the same service path with a fake preference source for both families and all three modes, verify resource counts through repeated switches, and verify unsubscription on disposal. Native OS notification delivery remains unverified.

One replaceable dictionary per ThemeService, cached dynamic renderer properties and the existing layout revision mechanism remain. The adapter creates no global window registry or per-pixel resource lookups.

## Fallback and supporter boundary

Unknown/unavailable families resolve Noctaxis while retaining requested mode. A malformed future family without Light uses its own Dark palette and reports a diagnostic; the effective Fluent base becomes Dark too. Missing colour-vision variants use None with diagnostics. Missing/invalid individual tokens retain the previous Noctaxis Dark token fallback and report inherited keys. Undefined numeric appearance modes normalise to System; absent/unparseable persisted modes use the existing Dark default. Built-ins require no fallback.

A future supporter family supplies one stable ID, one availability/entitlement decision and both palettes. Controls never query entitlement. Dark/Light/System do not require separate entitlements or IDs. No supporter theme, external theme loader or network entitlement integration was added.

## Preservation

All family/mode/vision updates still call ApplyAppearanceAsync and persist independently of calculation-bearing ApplySettingsAsync. Tests exercise both families, all three modes and all four vision modes while retaining the same session/snapshot, planning/environment request counts and explicit #123456 terrain colour. Appearance changes generate zero additional Planner, terrain, weather or astronomy requests.

This pass does not alter terrain generation/obstruction/rendering semantics, astronomy, weather, FoV, saved-location data or user-owned presentation colours. Existing terrain edits in the working tree remain unrelated and preserved. No renderer or typography redesign was performed.

## Validation

Focused command: `dotnet test Noctaxis.Desktop.Tests -c Release --no-restore -m:1 --filter "FullyQualifiedName~Appearance|FullyQualifiedName~ResponsiveCardGrid|FullyQualifiedName~TerrainDebugOverlay_IsOptionalBoundAndCopyable|FullyQualifiedName~PlannerMinimapContainerAllowsWheelTrackpadAndPanInputThroughToMapsui" -v minimal`.

Focused: 105 passed, zero failed/skipped, including persistence migration, platform preference, HC Light states, completeness/contrast and existing layout/input regressions. The final sequential command `dotnet test Noctaxis.slnx -c Release --no-restore -m:1 -v minimal` passed (exit 0), including the final internal palette-storage simplification.

| Suite | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Core | 331 | 0 | 1 | 332 |
| Desktop | 467 | 0 | 0 | 467 |
| Full solution | 798 | 0 | 1 | 799 |

The sole skip is the opt-in TerrariumLiveIntegrationTests.OfficialTerrariumSampleIsPhysicallyPlausible. `git diff --check` passed (exit 0); existing line-ending conversion notices are informational. No final test failures remain.

Logs: artifacts/appearance-model-focused.log and artifacts/appearance-model-full-release.log. Captures use headless Avalonia, synthetic images and an injected platform preference source. Native screen readers/keyboard workflows, mixed DPI, OS event delivery and GPU/live-provider rendering remain outside this validation. Specialist scientific colour-vision coverage remains unchanged.

[Representative capture list](H:/Noctaxis/docs/appearance-model-captures.md)

## Files changed in this pass

- [Noctaxis.Core/Domain/AppearancePreferences.cs](H:/Noctaxis/Noctaxis.Core/Domain/AppearancePreferences.cs)
- [Noctaxis.Desktop/Themes/ThemeCatalogue.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/ThemeCatalogue.cs)
- [Noctaxis.Desktop/Themes/BuiltInPalettes.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/BuiltInPalettes.cs)
- [Noctaxis.Desktop/Themes/ThemeService.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/ThemeService.cs)
- [Noctaxis.Desktop/Themes/AppearancePlatform.cs](H:/Noctaxis/Noctaxis.Desktop/Themes/AppearancePlatform.cs)
- [Noctaxis.Desktop/ViewModels/MainViewModel.Appearance.cs](H:/Noctaxis/Noctaxis.Desktop/ViewModels/MainViewModel.Appearance.cs)
- [Noctaxis.Desktop/Views/MainWindow.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/MainWindow.axaml)
- [Noctaxis.Desktop.Tests/AppearanceTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearanceTests.cs)
- [Noctaxis.Desktop.Tests/AppearanceFamilyTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearanceFamilyTests.cs)
- [Noctaxis.Desktop.Tests/AppearancePersistenceTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePersistenceTests.cs)
- [Noctaxis.Desktop.Tests/AppearancePolishTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePolishTests.cs)
- [Noctaxis.Desktop.Tests/AppearancePolishPageTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePolishPageTests.cs)
- [Noctaxis.Desktop.Tests/AppearancePolishTerrainTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePolishTerrainTests.cs)
- [Noctaxis.Desktop.Tests/MainViewModelTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/MainViewModelTests.cs)
- [docs/appearance-model.md](H:/Noctaxis/docs/appearance-model.md)
- [docs/appearance-model-captures.md](H:/Noctaxis/docs/appearance-model-captures.md)
