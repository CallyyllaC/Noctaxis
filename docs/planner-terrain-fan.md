# Planner activation and terrain-depth fan

The activation section remains current. The finite depth-band presentation below is historical and has been superseded by [the cumulative terrain-envelope pass](terrain-envelope.md).

## Startup activation

The missing path was page activation. `ShowPlannerCommand` requested an observer refresh, but the tab binding directly changed `SelectedPageIndex` without invoking that command. `InitializeAsync` also unconditionally selected Locations after loading settings. A Planner page selected before initialization could therefore be reset, and a directly selected tab could stay dormant until observer interaction called `ScheduleObserverRefresh`.

`OnSelectedPageIndexChanged` and the end of initialization now share `ActivatePlannerIfReady`. It starts the existing observer/environment/minimap workflow once the session and equipment are loaded and Planner is active. Default startup on Locations still defers this first activation. Re-entering an already initialized Planner reuses its state. Normal observer requests count as initialization too. Saved/custom location navigation commits its observer before selecting Planner, avoiding an extra request for the old observer. Bearing and pitch remain derived camera inputs; lens changes retain the existing optical/frame and priority-profile behavior.

Production files: `Noctaxis.Desktop/ViewModels/MainViewModel.cs`; the startup regression and its terrain-capable fixture are in `PlannerActivationTests.cs` and `MainViewModelTests.cs`.

## Cone semantics

The preceding experiment sampled the positive horizontal horizon and tinted each affected bearing from its horizontal first obstruction to the cone's maximum range, independently of pitch. It was per-bearing internally, but its long solid shadows could read as a static whole-cone tint. The new production path consumes the existing `CameraTerrainDepth` rather than calculating a coverage fraction or another set of terrain casts.

`PlanTerrainFan` maps each existing camera-depth column to a bearing slice bounded by the midpoints to its neighboring columns, clipped to the exact horizontal FoV. It takes the distinct first-intersection depths from **positive-altitude camera rows only**. Those are the same rows/columns used by the camera-frame image:

- Negative/zero-altitude rows never create positive-terrain bands, even when the frame contains substantial ground.
- Changing pitch or vertical FoV changes the rows and intersections supplied by the existing camera-frame calculation. Pitching above a ridge removes its bands. Target altitude is not a fan input.
- Distinct visible depths form outward bands. A band starts at its depth and reaches the next visible depth. The final band stops at the next radial profile sample. If it is the final sampled point, half the preceding radial spacing supplies its finite footprint, capped at the fixed cone range. This is a presentation footprint convention, not a new visibility or terrain calculation.
- The same logarithmic near-light/far-dark palette as `TerrainFrameView` supplies tonal values, with 20% configured terrain hue. Band opacity is the configured tint opacity plus 0.25, capped at 0.85, to remain readable on a bright map.
- When the **entire vertical frame is at/below 0°** (`UpperAltitude <= 0`), the fan uses a separate warm solid ground-facing cue, accompanied by “Ground-facing · frame below horizontal horizon” beside the frame. It is not counted as ordinary obstruction and does not claim clear sky.
- Missing camera-depth data yields an empty presentation fan; the existing unavailable/resolving/disabled frame statuses retain their meanings. Full terrain profiles, including negative angles, remain intact.
Please perform a focused visual-contrast tuning pass on the current Noctaxis production terrain fan.

The current cumulative skyline-envelope geometry is now broadly correct and should be preserved.

Do not redesign the terrain fan semantics, horizon/profile generation, progressive terrain lifecycle, Planner activation, camera terrain-frame calculation, or terrain provider architecture.

The purpose of this task is specifically to improve the **readability of depth/terrain-height differences inside the cone**.

---

# Current problem

The terrain fan now shows coherent terrain structure and cumulative skyline regions, which is a significant improvement over the previous slab-like implementation.

However, the different terrain regions currently have **too little tonal contrast**.

On a bright OpenStreetMap background, many terrain layers appear as only slightly different shades.

This makes the cumulative depth/height structure difficult to read, especially with wide-angle lenses.

The live screenshots show that the geometry is now interesting and useful, but the terrain fan still looks too visually flat.

The problem appears to be predominantly **tone mapping**, not geometry.

---

# Preserve current semantics

Do not change the following current behaviour:

* per-bearing terrain rays
* cumulative visible skyline envelope
* terrain transitions beginning at their actual radial depth
* farther terrain replacing nearer terrain only when it rises above the existing angular skyline
* apparent terrain angular height controlling visual strength
* pitch changing which terrain enters or leaves the camera frame
* target altitude remaining independent of the terrain fan
* flat/nonpositive terrain not producing the old bogus 50% ground-coverage behaviour
* separate ground-facing state
* adjacent bearing continuity
* wide and telephoto FoV behaviour
* existing camera terrain-frame behaviour
* existing Planner startup activation
* existing progressive horizon lifecycle correction

This is a presentation tuning pass only unless a concrete defect is discovered.

---

# Why the current contrast is weak

The current tone scale appears to map apparent terrain altitude across a range related to the vertical camera frame.

For a wide lens, the vertical FoV may be very large.

For example, if the camera frame spans roughly 50° vertically, terrain features commonly encountered in real planning may only rise:

```text
+0.5°
+1°
+2°
+3°
+5°
+8°
```

above the normal horizon.

If tone is distributed approximately linearly across the full vertical frame, these useful low-angle terrain differences occupy only a very small part of the available tonal range.

The result is mathematically reasonable but visually weak.

We want stronger perceptual separation in the common low-angle range.

---

# Desired tonal behaviour

Keep apparent angular terrain altitude as the primary strength input.

Do not make physical distance directly control darkness.

Distance should continue to determine **where a terrain region begins**.

Apparent angular height should determine **how strong/dark it appears**.

The desired visual relationship is approximately:

```text
terrain just above horizon
    -> visible but light

~1–2°
    -> clearly stronger

~3–5°
    -> distinctly medium

~8–10°
    -> dark / visually dominant

very large positive terrain angle
    -> capped maximum strength
```

The exact numerical values may vary depending on vertical FoV and the existing terrain palette.

The important requirement is that ordinary low-angle terrain differences become easy to perceive.

---

# Use a nonlinear contrast curve

Please evaluate nonlinear mappings rather than using a simple linear horizon-to-frame-top interpolation.

Candidate mappings may include:

* power curves
* square-root-like curves
* logarithmic curves
* smooth perceptual curves
* another simple monotonic curve if it performs better visually

For example:

```text
normalized = clamp(angle / referenceAngle, 0, 1)

strength = pow(normalized, exponent)
```

where an exponent below `1.0` increases separation near the horizon.

Do not mechanically use this exact formula if another simple mapping is better.

The curve must remain:

* monotonic
* bounded
* deterministic
* easy to reason about
* easy to tune

Avoid introducing a complicated colour-science subsystem for this.

---

# Important semantic constraint

The contrast curve should not cause terrain tone to change simply because the camera pitch changed while the same terrain feature remains visible.

Pitch should affect:

> whether the terrain is visible inside the camera frame

not:

> the apparent physical identity of the terrain

A +4° ridge should retain approximately the same visual strength while visible, regardless of whether the camera is centred at 0°, +3°, or +6°, unless the existing framing logic legitimately changes which part of the terrain is visible.

Do not revert to a camera-relative dimmer-switch effect.

---

# Candidate comparison

Before selecting the final mapping, generate several candidate contrast curves using the **same terrain geometry and test data**.

Please compare at least:

1. existing/current mapping
2. mild nonlinear boost
3. medium nonlinear boost
4. strong nonlinear boost

If practical, render these side-by-side in one comparison image.

Use identical:

* observer
* terrain profile
* bearing
* FoV
* pitch
* base map/background
* terrain geometry

Only the contrast mapping should differ.

This allows the visual effect of the curve itself to be judged.

---

# Numerical comparison

For each candidate curve, report the resulting normalized/visual strength for representative terrain angles:

```text
+0.5°
+1°
+2°
+3°
+5°
+8°
+10°
```

and also the selected reference/max angle.

If the mapping adapts to vertical FoV, include examples for at least:

* wide kit lens
* moderate FoV
* telephoto FoV

We want to ensure the wide lens does not compress all useful terrain into nearly identical pale tones.

---

# Consider a practical reference angle

Inspect whether the current camera-frame top is the best denominator/reference for tonal mapping.

It may be visually better to use a capped or practical terrain reference angle.

For example, terrain above some useful apparent altitude may already be considered visually “strong enough,” rather than reserving the darkest shade for extremely steep terrain that is rarely encountered.

Possible concept:

```text
referenceAngle = min(frameTopRelevantRange, practicalTerrainAngle)
```

Do not adopt this blindly.

Evaluate whether a fixed or capped reference gives more stable readability across lenses.

If used, document the chosen rationale.

---

# Base cone visibility

The current green/base FoV cone may be competing visually with the terrain envelope.

Inspect whether reducing the base cone fill opacity slightly improves terrain readability.

If adjustment is useful:

* preserve the FoV outline
* preserve bearing/framing readability
* reduce only enough base fill to give terrain tones more luminance separation

Do not remove the base cone entirely unless there is a very strong visual reason.

The terrain fan should remain readable over bright OSM tiles.

---

# Palette behaviour

Keep the current terrain hue/theme unless a small luminance or saturation adjustment is necessary.

Focus primarily on **luminance/opacity contrast**, not hue changes.

Avoid turning depth levels into unrelated rainbow colours.

The desired result should still look like one coherent terrain overlay.

Do not introduce:

* hatching
* stripes
* stippling
* patterned fills
* per-band arbitrary colours

Solid tonal regions only.

---

# Opacity

Inspect the existing opacity calculation.

If all terrain regions currently have similar opacity and only slightly different colour/luminance, consider whether opacity can also contribute modestly to separation.

However:

* do not make opacity the sole depth cue
* do not make dark regions so opaque that the underlying map disappears
* cap opacity at a sensible level
* preserve map readability

The terrain overlay should feel strong enough to read but still clearly be an overlay.

---

# Wide-lens priority

Pay particular attention to the kit lens at wide focal lengths.

For a wide vertical FoV, most real-world terrain is clustered close to the horizon in angular terms.

The chosen mapping must preserve useful contrast between shallow terrain features in that regime.

The same curve should still behave sensibly for telephoto views.

Avoid lens-specific hardcoded values unless absolutely necessary.

Prefer a general mapping.

---

# Synthetic visual scenarios

Extend the current terrain-fan comparison capture to include contrast-sensitive cases.

Use the existing cumulative-envelope geometry.

Include at minimum:

## Shallow terrain sequence

```text
+0.5°
+1°
+2°
+3°
```

at increasing visible skyline distances.

This should visibly separate into multiple tonal levels.

## Moderate terrain

```text
+2°
+5°
+8°
```

The difference should be obvious without becoming black.

## Near low + far higher ridge

```text
near = +1°
far = +5°
```

The farther higher terrain should visibly replace the lighter near terrain.

## Wide FoV

Use a kit-lens-like wide frame where the vertical FoV is large.

The terrain must still have visible tonal differentiation.

## Telephoto FoV

Use a narrow frame and verify the mapping does not become excessively harsh.

## Realistic asymmetric terrain

One side shallow, one side high, one side mostly clear.

The visual structure should remain easy to parse.

---

# Live-map validation

If live/native validation is available, inspect at least one real cached location with shallow rolling terrain and one with more pronounced relief.

Look specifically for:

* terrain layers being distinguishable without staring closely
* shallow relief no longer blending into one flat shade
* stronger ridges clearly reading as more dominant
* OSM text and roads remaining visible beneath the overlay
* no terrain region becoming fully opaque
* no sudden tone discontinuity caused only by lens change
* no change to terrain geometry
* no change to skyline transition positions

If only synthetic/headless captures are available, state that clearly.

---

# Testing

Do not remove or weaken existing terrain-fan semantic tests.

Add targeted tests for the tone mapping itself.

At minimum verify:

1. terrain strength is monotonic with apparent positive terrain angle
2. +2° is visually stronger than +1°
3. +5° is visually stronger than +2°
4. maximum strength is bounded
5. negative/zero terrain remains ordinary clear terrain
6. pitch alone does not recolour the same visible terrain
7. distance does not directly change tonal strength
8. changing FoV does not produce invalid or unbounded strength values
9. base cone opacity remains within valid bounds
10. existing cumulative-envelope geometry remains identical before and after this pass

If practical, test the mapping function independently from rendering geometry.

---

# Performance constraints

This change should be essentially free compared with terrain calculation.

Do not:

* regenerate horizon profiles
* add terrain samples
* increase angular ray count
* introduce new terrain caches
* add GPU/SIMD architecture
* add heavy colour transforms
* allocate new large buffers unnecessarily

This should primarily be a different mapping from existing terrain-angle values to presentation strength.

---

# Final selection

Do not simply choose the strongest candidate.

Choose the curve that gives the best balance of:

* clear shallow-terrain separation
* readable stronger terrain
* retained map detail
* consistency across wide and telephoto FoVs
* stable appearance under pitch changes
* minimal visual harshness

Document why the selected curve was chosen.

---

# Final report

Provide:

## Current mapping

* exact current tone/opacity behaviour
* why shallow terrain differences are visually compressed

## Candidate curves

* formulas or mapping descriptions
* representative values at:

  * +0.5°
  * +1°
  * +2°
  * +3°
  * +5°
  * +8°
  * +10°
* comparison capture path

## Selected mapping

* chosen curve
* reference/max angle behaviour
* opacity changes if any
* base cone opacity changes if any
* rationale

## Validation

* synthetic scenarios
* live-map observations if performed
* wide vs telephoto behaviour

## Tests

* tests added/updated
* focused totals
* full solution totals
* failures/skips

## Preservation

Explicitly confirm that this pass did not alter:

* cumulative skyline geometry
* transition distances
* horizon/profile generation
* terrain lifecycle
* Planner activation
* target occultation
* terrain-frame calculations
* Terrarium/provider architecture

Treat this as a focused visual-contrast pass.

The target result is:

> **The terrain fan should preserve exactly the same geometry and meaning it has now, but differences between shallow, moderate, and strong visible terrain should be immediately readable on a bright map.**

The existing base/weather shader remains in place with a two-texel clear terrain input on the production fan path. Existing Skia drawing fills the cached geographic band polygons on top; no new shader, GPU pipeline, terrain provider, cache layer, or horizon algorithm was added. All polygons are clipped to the viewport. The earlier horizontal-mask helper and low-level shader tests remain as explicit nonproduction comparison/diagnostic coverage.

Production files: `Controls/PlanTerrainFan.cs`, `Controls/EnvironmentalOverlayModels.cs`, `Controls/EnvironmentalOverlayRenderer.cs`, `Controls/NoctaxisMapView.cs`, `ViewModels/MainViewModel.CameraFrame.cs`, and `Views/MainWindow.axaml` under `Noctaxis.Desktop`.

## Resolution, invalidation and limits

The fan retains the existing camera-depth horizontal sampling (at most approximately 1° per column) and its 72 vertical rows. A 24° frame uses 25 rays; a 4.5° frame uses 6. Each ray contributes only its unique visible depths, rather than 72 duplicate polygons. Geographic corners are rebuilt when the profile/frame/horizontal sector changes and only projected for pan/zoom. Equal depth contents reuse the current state even if a target assessment is a new object. Pitch invalidates the plan presentation via changed camera depth; it does not request another production horizon or minimap. No whole-cone coverage average is computed.

The synthetic capture has 111 bands for the asymmetric 72° wide ridge and 21 for the 12° tele ridge. Very shallow features smaller than one existing vertical depth pixel share the frame's resolution limit. Narrow tele fans are naturally thin on the map. The final radial footprint is approximate as described above; no claim of surveyed surface boundaries is made.

## Regression coverage and visual evidence

- `PlannerActivationTests`: preselected Planner at initialization and direct tab entry both request environment/minimap work; cone readiness and camera depth initialize without a pin change; re-entry does not duplicate the initial request. The tests failed before the activation fix.
- `PlanTerrainFanTests`: flat/negative profiles; separate ground-facing state; asymmetric columns; finite near/far layers; wide/tele sampling; pitch-above-ridge clearance; preserved occultation/profile values; viewport clipping.
- `PlanTerrainExperimentTests`, `TerrainAvailabilityTests`, and `CameraLiveTuningTests`: updated only the superseded unconditional pitch-independence expectations. Target-altitude independence, profile availability, frame calculations and lifecycle assertions remain.
- Existing `TerrainPriorityLifecycleTests`, cache/coalescing/cancellation, stale observer results and observer movement tests remain unchanged.

`artifacts/plan-terrain-experiment/terrain-fan-comparison.png` contains flat-wide, ridge-wide and ridge-tele rows at pitches −40°, 0°, +15° and +35°, with a camera-frame image directly above each plan-view panel. It was inspected after fixing an observed polygon-clipping issue. Flat terrain has no ordinary terrain bands; the ridge remains on the matching side of the cone with layered tones; high pitch clears it; downward pitch has the separate ground cue.

These are rendered offline synthetic cases against a bright neutral background, not live OSM or hardware captures. Set `NOCTAXIS_FAN_COMPARISON` to a PNG path and run `ExportOptionalTerrainFanComparison` to reproduce them.

## Validation

Final focused terrain/profile/rendering run: **313 passed, 0 failed** (178 Core; 135 Desktop). Its first attempt encountered the existing intermittent Avalonia dispatcher-ownership cleanup failure in `MinimapStrongerTonesSeparateLowReliefAndReduceLocalWeightForMountains`; the sequential rerun passed.

Final full sequential solution run, `dotnet test Noctaxis.slnx --no-restore --verbosity minimal`: **641 passed, 0 failed, 1 skipped** (Core: 331 passed and one opt-in live-terrain test skipped; Desktop: 310 passed). The comparison image was regenerated during this final run and visually inspected. `git diff --check` passed.

No changes were made to the progressive-horizon lifecycle fix or the Terrarium provider in this pass. Existing lifecycle, cancellation, coalescing, stale-result rejection and observer-movement tests remain in the passing suite. Native startup interaction and live map/hardware visual validation were not performed; startup validation uses the production view model with deterministic services.
