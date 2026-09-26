# Colour-vision accessibility

Colour-vision support is part of the theme system, not a separate palette. Every theme family
(Noctaxis, High Contrast, Neon, Foxfire, Deep Space, Moonlit) has Light and Dark palettes, and each
palette has four variants: None, Protanopia, Deuteranopia and Tritanopia. That makes 48 combinations,
and all of them can be switched live.

```
AppearancePreferences (family, Light/Dark, colour-vision mode)
  → ThemeCatalogue.Resolve
  → BuiltInPalettes variant (ApplySemanticVariant)
  → ThemeService: brushes, <token>Color and CelestialColourSet in one replaceable dictionary
  → FluentResourceBridge (Fluent control keys)
  → DynamicResource consumers
```

The consumers are the XAML UI, the chart, the target-list keys, the map overlays, the basemap, the
Mapsui canvas and the minimap. Drawing code only names semantic roles; it never names a theme family.

## What a colour-vision variant changes

A variant starts from the family's own colours. It changes only what separation needs, so it still
looks like its theme.

| Roles | Change |
|---|---|
| `Success`, `Warning`, `Error`, `Information` | Separate authored sets for each deficiency, Light/Dark and High Contrast. |
| Status backgrounds | A light wash of the status colour over the surface (High Contrast keeps plain surfaces). |
| `CelestialSun`, `CelestialMoon`, `CelestialDeepSky1`–`5` | Separate authored sets for each deficiency and Light/Dark. |
| `MapWater`, `MapVegetation`, `MapBuiltUp` | Nudged only where a pair collapses for that family (`MapAreaOverrides`). |
| `MapPinActivity`, `MapBearing` | Changed only where needed: High Contrast (protanopia) and Neon (deuteranopia). |

Accent, selection, focus, surfaces, text, chrome and the other map roles keep the family values.
`ColourVisionAccessibilityTests.ColourVisionVariantsKeepTheFamilyIdentity` enforces this.

## Meaning never rests on colour alone

| Meaning | Colour | Non-colour cue |
|---|---|---|
| Primary vs secondary target | Celestial role | The primary path or ray is thick and solid. |
| Which celestial object | Celestial role | Each role has its own dash pattern and marker (disc, ring, square, triangles, diamond, cross). The chart labels each path at its highest point ("Sun", "Moon", catalogue ID). The target list shows the same line-and-marker key (`CelestialLegendGlyph`). |
| Below the horizon | `ChartBelowHorizon` | The line is thinner and dashed, and sits under the horizon line. |
| Beyond the weather visibility distance | Greyed cone | A dashed frontier arc at the visibility distance, drawn in the basemap label colour. Framing edges beyond it are dotted. |
| Terrain obstruction | User-chosen tint | A solid frontier edge (unchanged). It differs by pattern from the dashed weather frontier. |
| Terrain frame centre vs threshold | Fixed colours on the grayscale raster | The centre line is dashed and thin; the threshold line is solid and thicker. A legend sits beside the view. |
| Status messages | Status role | An icon: circled i, circled tick, triangle, octagon (`StatusText`). |
| Hover and press | Fill change | The border thickens to 2 px (3 px when pressed in High Contrast) and strengthens (`StrongBorder`). |

## Measured thresholds

The colour science is CIEDE2000 on colours simulated with the Machado et al. (2009) model at full
severity, plus WCAG 2 contrast. `ColourScience` in the test project implements it. Each palette is
judged by the viewer it is meant for: a colour-vision variant under its own deficiency, and None with
normal vision.

| Check (all 48 combinations) | Threshold | Result |
|---|---|---|
| Status pairs | ΔE ≥ 10 | ≥ 16.0 in every variant |
| Status text on its background and on the surface | ≥ 4.5:1 | pass |
| Celestial pairs | ΔE ≥ 10 | ≥ 15.0 in variants, ≥ 25 in None |
| None celestial colours seen with any deficiency | ΔE ≥ 7.5 | 8.0 (the previous colours measured 1.9) |
| Celestial lines on the chart surface and on map land | ≥ 3:1 | pass (previously 1.3–1.7:1 on Light) |
| Below-horizon path line | ≥ 3:1 | pass |
| Map area and road pairs | ΔE ≥ min(5, 0.85 × the family's normal-vision ΔE) | pass |
| Pin ring/activity and field-of-view/bearing | ΔE ≥ 10 | pass |
| Focus ring on every surface | ≥ 3:1 | pass |

Basemap areas are deliberately subtle, so the map rule is relative: a deficiency must not make a pair
harder to tell apart than the family's own design is for normal vision. Land and built-up differ by
lightness, which colour-vision deficiencies do not affect.

## Light Pollution

The default colour map, Grayscale, carries no hue. Turbo's description now says it is not
colour-vision safe. When a colour-vision mode is active and Turbo is selected, Settings shows a notice
with a **Use Cividis** button. The user's choice is never changed for them.

## Intentionally literal colours

- The terrain-frame depth raster and its Near/Far/Sky legend swatches: grayscale data, not theme
  chrome.
- The centre and threshold lines on that raster: fixed tints over grayscale, told apart by pattern and
  weight.
- The `TerrainDebugMiniMap`, `TerrainSampleRenderer` and debug topology colours: diagnostics only.
- The ground-facing fan outline in the environmental overlay: a rare, geometry-labelled state.
- The obstruction tint: a user setting.
- Saved-location image processing and the settlement glow: artwork for the location cards.
- Location-card shadows and glows in `App.axaml`: decorative, and they carry no meaning.
- Light Pollution colour maps: scientific scales chosen by the user.
- Satellite imagery: never recoloured.

## Verification

- `ColourVisionAccessibilityTests` checks the full colour-science matrix, role definitions, identity
  preservation and per-deficiency variants.
- `ColourVisionRuntimeTests`:
  - The visible Mapsui canvas pixel equals `MapSurface` with no tiles drawn. This test fails with the
    old white `Map.BackColor`.
  - A live sweep of all 48 combinations through `ThemeService` leaves no stale resource. It covers
    the chart, target-list glyph, status icon, map overlay, Mapsui background and Fluent accent.
  - The Fluent progress bar uses the theme accent.
- `EnvironmentalOverlayTests`:
  - A theme switch recolours overlays without rebuilding the environmental state or the basemap
    layers.
  - The weather limit is a dashed frontier.

Remaining work needs people rather than numbers: checking with colour-vision-deficient users, and
judging marker and label clutter on dense real sessions.
