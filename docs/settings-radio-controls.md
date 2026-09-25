# General settings radio groups and text-size slider

This pass changes control types and scoped styling only. Theme architecture, palette values, persistence schema, runtime layout invalidation, calculation settings and measurement conversion logic are unchanged.

| Setting | Control |
|---|---|
| Theme | Existing catalogue-driven ComboBox |
| Mode | Native System / Dark / Light radio buttons |
| Colour vision | Native None / Protanopia / Deuteranopia / Tritanopia radio buttons |
| Text size | Slider, 100–200%, snapping in 10% steps, live current percentage and endpoint labels |
| Measurement system | Native Metric / Imperial / UK radio buttons, retained in General's Units section |

Each group uses an ordinary WrapPanel and native RadioButton GroupName exclusivity. Options have natural widths and consistent margins; groups remain horizontal when they fit and wrap when needed. No horizontal radio scrolling or custom interaction control was introduced. At 720px and 200% text, colour-vision options wrap; at 1440px they still fit horizontally. Existing vertical Settings scrolling accommodates content beyond the viewport.

The small two-way RadioChoiceConverter ignores unchecked transitions so they cannot overwrite the selected option. A colour-vision enum proxy selects the existing option object; it stores no duplicate preference. Mode, vision and text size still use ApplyAppearanceAsync. Tests verify immediate persistence and unchanged planning session/request counts. Theme remains separate from mode and catalogue-driven.

Units bind directly to SettingsUnits. Selection remains an unsaved editor change until Save settings; tests save a radio-selected UK value through the existing command. All three existing measurement values and their conversion semantics are retained.

Radio buttons retain native accessible labels, filled/empty circular selection, keyboard focus and Space activation. Scoped Fluent radio brushes reference existing semantic resources, removing Fluent-blue selection from High Contrast without modifying palettes. A focus outline uses the existing FocusBorder and StateFocusThickness resources, distinct from the selected radio marker. Headless tests verify focus and keyboard selection; native screen-reader and complete OS keyboard traversal remain unverified.

Validation exercises four family/mode palettes at 720px and 1440px, each at 100%, 150% and 200%. It checks labels against measured text bounds, option/container bounds, normal-width horizontal rows, narrow wrapping, exclusivity, immediate percentage updates, keyboard 10% slider steps, persisted appearance and units, and zero additional calculation/environment requests before deliberate Save settings. Existing appearance regression tests and Settings input-coverage checks are retained.

## Representative captures

- [settings-radio-builtin.noctaxis-Dark-1440-100.png](H:/Noctaxis/artifacts/appearance-polish/settings-radio-builtin.noctaxis-Dark-1440-100.png)
- [settings-radio-builtin.noctaxis-Dark-1440-200.png](H:/Noctaxis/artifacts/appearance-polish/settings-radio-builtin.noctaxis-Dark-1440-200.png)
- [settings-radio-builtin.noctaxis-Light-1440-100.png](H:/Noctaxis/artifacts/appearance-polish/settings-radio-builtin.noctaxis-Light-1440-100.png)
- [settings-radio-builtin.highcontrast-Light-1440-200.png](H:/Noctaxis/artifacts/appearance-polish/settings-radio-builtin.highcontrast-Light-1440-200.png)
- [settings-radio-builtin.noctaxis-Dark-720-200.png](H:/Noctaxis/artifacts/appearance-polish/settings-radio-builtin.noctaxis-Dark-720-200.png)
- [settings-radio-builtin.highcontrast-Light-1440-100.png](H:/Noctaxis/artifacts/appearance-polish/settings-radio-builtin.highcontrast-Light-1440-100.png)
- [settings-radio-focus-builtin.highcontrast-Light-1440.png](H:/Noctaxis/artifacts/appearance-polish/settings-radio-focus-builtin.highcontrast-Light-1440.png)

The 100% captures include Measurement system in its existing General/Units section. The existing headless capture tests also regenerate their previous matrices; no native visual validation is implied.

## Files changed in this pass

- [Noctaxis.Desktop/Views/MainWindow.axaml](H:/Noctaxis/Noctaxis.Desktop/Views/MainWindow.axaml)
- [Noctaxis.Desktop/Controls/RadioChoiceConverter.cs](H:/Noctaxis/Noctaxis.Desktop/Controls/RadioChoiceConverter.cs)
- [Noctaxis.Desktop/ViewModels/MainViewModel.Appearance.cs](H:/Noctaxis/Noctaxis.Desktop/ViewModels/MainViewModel.Appearance.cs)
- [Noctaxis.Desktop.Tests/SettingsRadioTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/SettingsRadioTests.cs)
- [Noctaxis.Desktop.Tests/MainViewModelTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/MainViewModelTests.cs)
- [Noctaxis.Desktop.Tests/AppearancePersistenceTests.cs](H:/Noctaxis/Noctaxis.Desktop.Tests/AppearancePersistenceTests.cs)
- [docs/settings-radio-controls.md](H:/Noctaxis/docs/settings-radio-controls.md)

## Final validation

Focused Settings/appearance validation: 124 passed, 0 failed, 0 skipped. Sequential Release solution validation: Core 331 passed, 0 failed, 1 skipped (332 total); Desktop 475 passed, 0 failed, 0 skipped; solution total 806 passed, 0 failed, 1 skipped (807 total). `git diff --check` passed (exit 0), recorded in artifacts/settings-radio-diff-check.log. The skip is the opt-in live Terrarium integration test. Full output is in artifacts/settings-radio-full-release.log.
