# Terrain fan contrast tuning

## Previous mapping

The fan used `clamp(angle / (verticalFoV / 2), 0, 1)` directly as its display strength. With a 50° vertical frame, +1° and +5° occupied only 4% and 20% of the tonal range. A narrow 3° frame, conversely, reached maximum at +1.5°. This compressed common wide-lens terrain and saturated telephoto terrain early.

The renderer then interpolated grayscale from 0.85 (light) to 0.18 (dark), mixed in 20% of the configured terrain hue, and composited the complete terrain layer once. Opacity was `clamp(configuredTerrainOpacity + 0.25, 0, 0.85)`: 65% by default, 40–80% across the existing normalized slider range. Base cone fill was 10%. Those palette, opacity and base-fill values remain unchanged.

## Candidate comparison

All four columns in `artifacts/plan-terrain-experiment/terrain-contrast-candidates.png` draw the **same production state and geographic polygons** for each scenario. Observer, profile, bearing, FoV, pitch, background, terrain hue and opacity are identical. Only the strength function changes.

- Current: `clamp(angle / (verticalFoV / 2), 0, 1)`.
- Mild: `clamp(angle / 10°, 0, 1)^0.9`.
- Medium: `clamp(angle / 10°, 0, 1)^0.75`.
- Strong: `clamp(angle / 10°, 0, 1)^0.5`.

| Apparent angle | Current wide (50° vertical) | Mild | Medium, selected | Strong |
|---|---:|---:|---:|---:|
| +0.5° | 0.020 | 0.067 | 0.106 | 0.224 |
| +1° | 0.040 | 0.126 | 0.178 | 0.316 |
| +2° | 0.080 | 0.235 | 0.299 | 0.447 |
| +3° | 0.120 | 0.338 | 0.405 | 0.548 |
| +5° | 0.200 | 0.536 | 0.595 | 0.707 |
| +8° | 0.320 | 0.818 | 0.846 | 0.894 |
| +10° | 0.400 | 1.000 | 1.000 | 1.000 |

The old reference/max angles were 25° wide, 10° moderate and 1.5° tele. Their representative strengths were:

| Angle | Current moderate (20° vertical) | Current tele (3° vertical) |
|---|---:|---:|
| +0.5° | 0.050 | 0.333 |
| +1° | 0.100 | 0.667 |
| +2° | 0.200 | 1.000 |
| +3° | 0.300 | 1.000 |
| +5° | 0.500 | 1.000 |
| +8° | 0.800 | 1.000 |
| +10° | 1.000 | 1.000 |

The new candidate columns use the same fixed 10° reference for every lens; no FoV-specific numerical table is needed for them. These tables describe visible angular inputs. The unchanged framing calculation still decides what is visible, including the existing positive-row rule for a foreground peak above the frame.

## Selection

Selected **medium: exponent 0.75, fixed 10° reference**. Compared with the current mapping, the shallow 0.5/1/2/3° sequence has visibly stronger separation in a wide frame. The 2/5/8° scene is distinct without turning black. The strong square-root candidate darkens the weakest terrain more than needed and leaves less of the light end available. Medium preserves a clearer light-to-dark hierarchy and readable synthetic road lines and labels.

A fixed practical angle avoids reserving the darkest tone for a rarely encountered +25° wide-lens ridge, and avoids recolouring a feature merely because the lens or pitch changes. Maximum strength is reached at +10° and capped thereafter. Nonpositive and nonfinite inputs return zero. Distance is not an input.

`terrain-contrast-candidates.base.png` compares the medium curve with base fill at 10%, 7% and 4%. The reduction chiefly changed the colour cast and added little useful layer separation. The existing 10% base fill and FoV outline are preserved. Terrain opacity is also preserved: luminance supplies the extra separation, while the existing single-composite opacity retains map detail and avoids edge accumulation.

## Geometry and architecture preservation

The original normalized `PlanTerrainBand.Strength` is also a topology key used to match adjacent rays. Changing that function could merge or split levels and violate the geometry requirement. It therefore remains unchanged. Each existing polygon now carries a colour-only `ApparentAltitudeDegrees` value, taken from its contributing skyline level; the renderer applies `TerrainFanTone.Strength` only at draw time.

Where the original topology had already merged saturated levels, the polygon keeps the lowest contributing visible angle. This preserves the existing polygon count and boundaries rather than adding new subdivisions for colour. The tone adjustment does not recover detail already merged by that unchanged topology.

The coordinator fingerprint includes the angular metadata, so an angular colour change cannot be lost when the old topology coordinate is saturated. Reuse for equal profile/frame data and target-only updates remains intact. An internal renderer constructor accepts an alternate mapping solely to render deterministic candidate comparisons; no user setting or production/debug branch was added.

A pre-change geometry snapshot covers 48 combinations of wide/normal/tele FoV, four pitches and four flat/shallow/moderate/above-frame profiles. It includes every ray, transition, original topology strength, polygon coordinate, ground outline and sample count. Its SHA-256 is unchanged after the contrast pass:

`F2042013CCA56C8DB3676A415C41C04CBB4A017268A5895BD5531F078367F2AF`

Before/after hash files are `contrast-geometry-before.txt` and `contrast-geometry-after.txt` beside the captures. Separate file hashes confirm `MainViewModel.cs`, `TerrainProviders.cs` and `CameraTerrainFrame.cs` are unchanged in this pass. Cumulative skyline geometry, transition distances, horizon generation, profile lifecycle, Planner activation, target occultation, terrain-frame calculations and the Terrarium/provider architecture remain intact.

The cost is one bounded power evaluation per drawn polygon plus small angular metadata prepared with the existing geometry. Ray count, radial samples, polygon count, geographic subdivision, pan/zoom reprojection and render-layer buffers are unchanged. No terrain acquisition, cache, GPU pipeline or heavy colour transform was added.

## Files and tests

- `Noctaxis.Desktop/Controls/TerrainFanTone.cs`: named reference and exponent; independent bounded tone function.
- `Noctaxis.Desktop/Controls/PlanTerrainFan.cs`: angular colour metadata, with unchanged topology calculations.
- `Noctaxis.Desktop/Controls/EnvironmentalOverlayRenderer.cs`: draw-time curve and internal comparison mapping hook.
- `Noctaxis.Desktop/Controls/EnvironmentalOverlayModels.cs`: angular metadata in the presentation fingerprint.
- `Noctaxis.Desktop.Tests/TerrainFanContrastTests.cs`: monotonicity, low-angle separation, bounds, invalid/nonpositive input, distance/pitch/lens stability, opacity bounds and exact pre-change geometry hash.
- `Noctaxis.Desktop.Tests/TerrainFanContrastCaptures.cs`: four-candidate comparison, numerical JSON and base-fill comparison.
- `Noctaxis.Desktop.Tests/PlanTerrainFanTests.cs`: existing seam test retained for both old and selected palettes. Expected colour now uses the actual projected polygon instead of an approximate polar boundary interpolation; the same pixel-error limit is retained. This avoids misclassifying legitimate contrast boundaries as seams.

The candidate image contains shallow wide, moderate wide, near-low/far-high wide, asymmetric wide, shallow moderate and shallow tele scenarios. The background is a deterministic bright map-like fixture with roads, a park and labels; it is not an OpenStreetMap tile capture. Native live-map validation was unavailable. `terrain-contrast-selected.png` additionally regenerates the existing seven-scenario envelope/frame comparison using the selected production curve.

To regenerate, set `NOCTAXIS_CONTRAST_CAPTURE` to the candidate PNG path and `NOCTAXIS_FAN_COMPARISON` to the selected PNG path, then run the corresponding optional export tests. The numerical comparison is saved beside the candidate PNG as JSON.

## Validation totals

The focused run passed **371 tests** (216 Core, 155 Desktop), with zero failures or skips. It also regenerated all comparison artifacts and the post-change geometry hash. TRX results are in `artifacts/plan-terrain-experiment/contrast-focused`.

The full sequential solution run passed **668 tests, 0 failed, 1 skipped** (Core: 331 passed/1 skipped; Desktop: 337 passed). The only skip is the opt-in live Terrarium test. Desktop finished in 2 minutes 31 seconds. Both projects and xUnit collections were serialized; no Avalonia cleanup failure occurred in this full run. TRX results are in `artifacts/plan-terrain-experiment/contrast-full`.

```powershell
dotnet test Noctaxis.slnx --no-restore -m:1 --logger trx --results-directory artifacts/plan-terrain-experiment/contrast-full --verbosity minimal -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

All existing terrain-frame, availability, skyline, target-independence, Planner activation, complete-profile, cancellation, stale-result and coalescing tests remain included. No semantic regression tests were removed. Geometry hashes match exactly, preserved-file hashes match, and `git diff --check` passed.
