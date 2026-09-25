# Appearance foundation audit (before resource migration)

The working tree already contains terrain changes. They are outside this pass. Counts below are source occurrences (hex literals and colour factory calls; XAML font sizes), not unique colours. Transparent fills, image scrims and shadow effects are counted where hexadecimal; inherited Fluent resources are not.

## Ownership

A/E: App.axaml, Views and XAML controls contain local window/card/input/text/border/focus and map chrome colours. These migrate to shared dynamic semantic resources; close/delete/status and favourite treatments retain meaning.
B: HorizonGraph, celestial paths, weather charts, environmental overlays and map symbols encode data. They are not accents. Text labels and primary/secondary dashed paths already provide secondary cues. Complex chart/category palette adaptation is recorded separately; terrain-related colours are expressly protected.
C: CameraFramingSettings.TerrainObstructionColour, TerrainTintStrengthPercent, line thickness and overlay opacity are explicit preferences. Appearance updates must only replace three appearance fields, never normalise or overwrite these settings. Celestial colours currently derive from categories/order, not user overrides.
D: Diagnostics renderers and debug rays use dedicated local diagnostic colours. They stay independent. Diagnostic panel chrome and text can share UI resources.
E: Multiple near-identical blue-grey text/borders/surfaces are accidental local variants and will consolidate to semantic roles.

| Source | Colour occurrences | Typography literals | Ownership |
|---|---:|---:|---|
| `Noctaxis.Desktop/App.axaml` | 56 | 9 | A/E: UI theme |
| `Noctaxis.Desktop/Controls/CameraOverlayColourPolicy.cs` | 1 | 0 | B/C/D: protected rendering, user or diagnostic |
| `Noctaxis.Desktop/Controls/CelestialPalette.cs` | 7 | 0 | B: semantic/data |
| `Noctaxis.Desktop/Controls/EnvironmentalOverlayRenderer.cs` | 1 | 0 | B/C/D: protected rendering, user or diagnostic |
| `Noctaxis.Desktop/Controls/HorizonGraph.cs` | 9 | 0 | B: semantic/data |
| `Noctaxis.Desktop/Controls/LocalTerrainMap.cs` | 14 | 0 | B/C/D: protected rendering, user or diagnostic |
| `Noctaxis.Desktop/Controls/LocationSearchControl.axaml` | 4 | 3 | A/E: UI theme |
| `Noctaxis.Desktop/Controls/NoctaxisMapView.cs` | 9 | 0 | B: semantic/data |
| `Noctaxis.Desktop/Controls/TerrainFanRaster.cs` | 1 | 0 | B/C/D: protected rendering, user or diagnostic |
| `Noctaxis.Desktop/Controls/TerrainFrameView.cs` | 4 | 0 | B/C/D: protected rendering, user or diagnostic |
| `Noctaxis.Desktop/Diagnostics/TerrainDebugMiniMap.cs` | 24 | 0 | B/C/D: protected rendering, user or diagnostic |
| `Noctaxis.Desktop/Diagnostics/TerrainSampleRenderer.cs` | 7 | 0 | B/C/D: protected rendering, user or diagnostic |
| `Noctaxis.Desktop/Services/SavedLocationMapImageProcessor.cs` | 12 | 0 | B: semantic/data |
| `Noctaxis.Desktop/ViewModels/MainViewModel.cs` | 7 | 0 | B: semantic/data |
| `Noctaxis.Desktop/Views/ConfirmationDialog.axaml` | 1 | 1 | A/E: UI theme |
| `Noctaxis.Desktop/Views/LocationSearchDialog.axaml` | 2 | 2 | A/E: UI theme |
| `Noctaxis.Desktop/Views/LocationsPage.axaml` | 29 | 16 | A/E: UI theme |
| `Noctaxis.Desktop/Views/MainWindow.axaml` | 72 | 54 | A/E: UI theme |
| `Noctaxis.Desktop/Views/SavedLocationEditDialog.axaml` | 1 | 1 | A/E: UI theme |

## Accessibility findings and boundaries

Location favourites have star and text cues; selected location has a thicker border. Visibility has status text; primary paths are solid and secondary paths dashed. Weather values and target names are textual. Colour-only differences within overlapping celestial/data series remain a follow-up; this pass must not rewrite specialist visualisations or terrain. Icon buttons require accessible names. Focus needs a shared visible border across palettes. Fixed card heights, constrained descriptions, horizontal settings rows and a narrow Planner sidebar need scrutiny at 200%.

Native screen-reader and keyboard workflow validation remains separate from headless construction/captures. Headless images cannot establish GPU map/terrain correctness.
