# Terrain fan local-contrast experiment

## Scope and baseline

The cumulative skyline fan and the selected absolute tone curve remain unchanged. This experiment adds a small terrain-space presentation residual to the existing absolute strength. It does not inspect map pixels, sharpen the final image, alter profile generation, or move any radial boundary.

The baseline is `TerrainFanTone.Strength(apparentAltitude)`, with the fixed 10° reference and exponent 0.75 selected in the preceding visual pass. The existing terrain hue, 65% default terrain layer opacity and 10% base cone fill are fixed throughout this experiment.

## Local model

`TerrainFanLocalContrast` operates over existing `PlanTerrainFan.Patches`:

```text
absolute = TerrainFanTone.Strength(patch.ApparentAltitudeDegrees)
reference = mean(absolute strengths of a very small semantic neighbourhood)
residual = clamp(absolute - reference, -0.25, +0.25)
display = clamp(absolute + amount * residual, 0, 1)
```

The amount is 0.18 in the selected candidate. Absolute terrain remains the dominant signal. A +1° isolated feature cannot outrank a broad +8° ridge; the local term is capped and contributes at most 18% of a bounded residual.

Bearing neighbours are patches whose angular centres are within 2.1° and whose radial envelopes overlap. Radial neighbours use the same small angular window and either overlap or touch a cumulative transition within 3.5% of the compared radial span. Combined mode uses the union of these relationships. Neighbours are defined in bearing/radial terrain coordinates, never screen pixels. Clear space has no patch and is not pulled into a synthetic bright or dark border.

This is a draw-time operation. The original `PlanTerrainPatch.Strength` remains the topology matching key, and each patch's existing apparent angle is only presentation metadata. `PlanTerrainFanLocalContrast` cannot add, remove or move a patch. Nonfinite and nonpositive angles resolve to zero absolute strength. Outputs are clamped and deterministic.

## Candidate comparison

`artifacts/plan-terrain-experiment/terrain-local-contrast-candidates.png` uses identical geometry, background, profile, observer, FoV, pitch, palette and opacity in all four columns:

- Current absolute strength.
- Bearing-only local residual at 18%.
- Radial-only local residual at 18%.
- Combined bearing/radial residual at 18%.

The rows are shallow +0.5/+1/+2/+3° progression, moderate +2/+5/+8°, near +1°/far +5°, asymmetric shallow/high/clear terrain, moderate-FoV shallow progression and telephoto shallow progression. The JSON beside the image records identical ray and polygon counts for every candidate. The companion `.base.png` retains the unchanged base opacity comparison from the preceding pass.

Combined mode is selected for production. Bearing-only improves lateral ridges but leaves same-bearing skyline levels close. Radial-only improves near/far transitions but does little for a side ridge or valley. Combined makes both structures easier to follow while remaining visibly softer than a global high-contrast treatment. No candidate produced a bright/dark halo, edge outline, one-degree zebra pattern or obvious DEM-noise amplification in the deterministic scenarios. The strong mountain remains dark but map labels and roads remain legible. This is a synthetic headless map-like background, not live OSM validation.

## Preservation

The geometry snapshot from the preceding pass still matches exactly. The current local-contrast tests preserve ray counts, patch coordinates, transition starts/ends and the unchanged topology strength. Local contrast is not part of terrain acquisition, the Terrarium provider, the progressive horizon lifecycle, Planner activation, camera frame, target occultation or availability state. Pitch continues to decide visibility; it does not directly become a contrast input. Physical distance is used only by the existing patch topology and neighbourhood relationship, not as a darkness gradient.

The existing single terrain-layer compositing remains in use. Opacity and base fill were not changed after the comparison. No terrain samples, rays, cache entries, GPU code or image-space convolution were added. The added work is a bounded pass over the existing few-dozen bearing patches and their small neighbourhoods. The projected-polygon raster test allows a small proportional number of edge pixels (at most 32 here), while still requiring zero clear holes; the baseline and selected palettes both satisfy it.

## Files changed

- `Noctaxis.Desktop/Controls/TerrainFanLocalContrast.cs`: isolated bounded bearing/radial/combined local residual.
- `Noctaxis.Desktop/Controls/EnvironmentalOverlayRenderer.cs`: selected combined mapping and comparison hook.
- `Noctaxis.Desktop.Tests/TerrainFanLocalContrastTests.cs`: boundedness, determinism, ridge/valley, equal-level stability, strong-mountain ordering, flat clear state, distance/pitch/FoV stability.
- `Noctaxis.Desktop.Tests/TerrainFanContrastCaptures.cs`: local candidate capture and numerical metadata.
- `Noctaxis.Desktop.Tests/PlanTerrainFanTests.cs`: same projected seam check exercised against baseline and selected local palette.

## Validation

The local-contrast focused run passed **384 tests** (216 Core, 168 Desktop), with zero failures or skips. Its TRX output is in `artifacts/plan-terrain-experiment/local-contrast-focused-final`.

The full serialized solution run passed **681 tests, 0 failed, 1 skipped** (Core: 331 passed/1 opt-in live Terrarium test skipped; Desktop: 350 passed). Desktop completed in 2 minutes 35 seconds. Its TRX output is in `artifacts/plan-terrain-experiment/local-contrast-full`. `git diff --check` passed.
