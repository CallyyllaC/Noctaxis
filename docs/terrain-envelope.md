# Cumulative plan-view terrain envelope

The geometry and visibility rules below remain current. Draw-time contrast has subsequently been tuned in [the terrain fan contrast pass](terrain-fan-contrast.md); its nonlinear palette replaces the linear display strengths described here without changing topology.

## Previous behaviour and scope

The previous fan collected distinct first-hit camera-row depths independently for each bearing cell. Each depth made a constant-radius slab, and the last slab ended at the next radial sample (or an estimated footprint). Distance controlled both position and shade. Independent boundaries and arbitrary terminal footprints produced detached blocks.

This pass changes presentation only. The Planner activation fix, progressive horizon lifecycle fix, Terrarium acquisition, minimap, production/debug separation and camera terrain-frame calculation are preserved. Hashes captured before/after confirm `MainViewModel.cs`, `TerrainProviders.cs` and `CameraTerrainFrame.cs` are unchanged during this pass. Existing working-tree changes from earlier tasks remain in place.

## Skyline and frame semantics

`PlanTerrainFan.Build` uses the bearings and vertical bounds of the existing `CameraTerrainDepth`, and walks `TerrainHorizonProfile.SightlineAt` at those same bearings. It uses existing apparent angles and distances; it performs no elevation acquisition or additional terrain casts.

For each near-to-far sightline, a running maximum starts at 0°. Nonfinite/invalid distances and angles are ignored. A farther point must exceed the full foreground angular maximum to create a new skyline level. Nonpositive terrain never creates an ordinary envelope. Negative terrain remains untouched in the production profile and camera image.

Visible transitions begin at their sampled radial distance. Their strength persists outward until another visible skyline level replaces it, then ultimately to the existing 500 km cone limit. This is a cumulative shadow metaphor, not the geographic footprint of a surface or an availability measurement.

Points below the entire camera frame do not contribute. A peak above the frame is **not automatically invisible**: its foreground surface may still intersect positive camera rows. In that case the builder uses the highest positive `CameraTerrainDepth` row whose first intersection belongs to that sample. That visible row supplies its presentation angle, while the original full peak remains the occlusion threshold for farther terrain. If no positive camera row intersects the above-frame sample, it creates no new presentation level.

The explicit regression uses a foreground point at **2 km/+15°**, a farther point at **9 km/+5°**, and frame **−10°…+10°**. The foreground fills the top positive row at **+9.861111°**. Before the correction all 25 fan rays were empty; afterward each contains one transition at 2 km continuing to 500 km. The hidden 9 km point creates no transition. This also appears in the last capture row.

When `UpperAltitude <= 0`, the existing separate warm ground-facing cue and explanatory frame text remain. No ordinary skyline envelopes are built in that state.

## Tone and connected geometry

Tone is based on apparent angular altitude, never elevation above sea level or radial distance. The horizon-anchored strength scale is `clamp(visibleAngle / (verticalFoV / 2), 0, 1)`. Half the vertical FoV is full strength, corresponding to the top of a level camera. This deliberately keeps an unchanged visible ridge's tone stable under pitch changes. Elevated frames can contain saturated strengths; the top of every shifted frame is not renormalized, because that would make pitch itself a colour input. A clipped foreground uses its actual visible camera-row angle.

The renderer maps strength between named light/dark grayscale constants, retaining 20% of the configured terrain hue. It uses solid fills only.

Adjacent rays join by cumulative strength levels rather than by transition index. For each pair, the ordered union of strengths defines matching radial frontiers. A missing level terminates at the cone edge, tapering across only that adjacent angular interval. The region between successive frontiers is a polygon; repeated endpoints naturally form triangles. Thus unequal transition counts and transitions to clear bearings do not require matching array lengths, detached strips or a cone-wide average.

Long radial sides retain the existing 10 km geographic drawing subdivision, with shared subdivision positions. This samples geometry only. Pan/zoom reprojects cached geographic coordinates. The source terrain stays at its existing angular resolution.

Visual validation found that separately compositing translucent polygons accumulated opacity on shared raster edges, producing fine dark striping. The existing Skia renderer now fills one temporary terrain layer opaquely and composites its configured opacity once. This removes the stripes without modifying skyline semantics. A pixel-level test compares 13,495 interior pixels against the expected single-composite tone: the old draw had 2,412 errors exceeding eight red-channel levels; the corrected draw meets the regression limit of fewer than ten (allowing projected boundary pixels), with no clear holes. Viewport clipping remains tested.

## Performance and invalidation

Typical horizontal counts remain 73 rays for 72°, 25 for 24°, and 6 for 4.5°. The builder inspects each existing radial sample once per ray, plus at most 72 existing camera rows when validating an above-frame peak. Neighbor connections sort/merge their skyline strengths; no finer terrain angles, provider calls, new cache or speculative GPU code were added.

The deterministic capture contains five radial samples per bearing: 365 sample visits for a wide frame, 125 for normal, and 30 for tele. At level pitch, the layered wide scene builds 258 polygons/5,246 vertices; the unequal scene builds 270/5,294; tele builds 15/550; the above-frame scene builds 30/2,472. In the final focused capture, these small synthetic builds took about 0.2–4.2 ms each, including coordinator work. These are fixture observations, not live-profile performance claims; production visit counts are ray count multiplied by existing sightline length.

The existing coordinator retains equal-depth/profile/sector reuse. Target-only changes reuse the fan. Pitch or lens changes rebuild the derived frame/fan from existing terrain; pitch does not request a new horizon or DEM tiles. Pan/zoom and colour changes retain geographic geometry. No unbounded presentation cache was introduced.

## Files changed in this pass

- `Noctaxis.Desktop/Controls/PlanTerrainFan.cs`: cumulative skyline levels, authoritative visible foreground rows, angular strengths and connected geographic polygons.
- `Noctaxis.Desktop/Controls/EnvironmentalOverlayModels.cs`: fan fingerprint includes angular strength instead of depth colour input.
- `Noctaxis.Desktop/Controls/EnvironmentalOverlayRenderer.cs`: angular tones and single terrain-opacity composition.
- `Noctaxis.Desktop.Tests/PlanTerrainFanTests.cs`: cumulative extent, hidden terrain, above-frame foreground, pitch/angle tone, adjacency, unequal transitions, wrapped bearings, wide/normal/tele resolution and raster regression.
- `Noctaxis.Desktop.Tests/CameraLiveTuningTests.cs`: updated outward-shadow assertion and expanded comparison capture/metrics.
- `docs/terrain-envelope.md` and historical note in `docs/planner-terrain-fan.md`.

## Evidence and limits

`artifacts/plan-terrain-experiment/terrain-envelope-comparison.png` shows seven rows: flat wide, single ridge wide, multiple layers wide, hidden farther terrain wide, unequal transitions wide, layered tele, and above-frame foreground normal. Columns are pitches 0°, +32°, +40° and −40°. Each camera image sits directly above its plan panel on a bright neutral background. The layers at +32° retain only the high ridge; +40° clears the terrain; −40° shows the separate ground-facing cue. The single/hidden rows have the same envelope. Adjacent boundaries are coherent, with no detached terminal slabs or accumulated edge tint. The tele fan is naturally thin at this map scale.

The preceding finite-band image remains at `artifacts/plan-terrain-experiment/terrain-fan-comparison.png`; it uses different fixtures and is historical context, not a pixel-identical before/after comparison. The new PNG's adjacent JSON records counts and timings. Set `NOCTAXIS_FAN_COMPARISON` to an output PNG path and run `ExportOptionalTerrainFanComparison` to regenerate both.

These are deterministic headless Skia/Avalonia captures, not live native-map validation. Native application control was unavailable in this session. The fan shares the camera frame's bearing interpolation and its 72-row limit for clipped foreground. It summarizes sampled skyline structure; it does not reconstruct terrain between radial samples. Clear-to-obstructed contours interpolate only within neighboring source bearings.

## Validation

The final focused run passed **355 tests** (216 Core, 139 Desktop), with no failures or skips. It regenerated the inspected comparison image. Its TRX files are in `artifacts/plan-terrain-experiment/envelope-focused-final`.

The first full solution attempt passed 331 Core tests (one live test skipped) and 320 Desktop tests, with one existing Desktop tint test failing during Avalonia dispatcher-ownership cleanup. No production code was changed in response. A full rerun explicitly serializes both MSBuild projects and xUnit collections:

```powershell
dotnet test Noctaxis.slnx --no-restore -m:1 --logger trx --results-directory artifacts/plan-terrain-experiment/envelope-full-rerun --verbosity minimal -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

The full serialized rerun passed **652 tests, 0 failed, 1 skipped**: Core 331 passed/1 skipped; Desktop 321 passed. Desktop completed in 2 minutes 30 seconds. TRX evidence is in `artifacts/plan-terrain-experiment/envelope-full-rerun`; the first attempt remains separately recorded in `envelope-full-final`.

Existing availability, Planner activation, camera-frame/occultation, progressive-profile, cancellation, coalescing and stale-result tests are included. In particular, `CancelledWideCameraWaiterCannotLeaveACompletedProfileWithReservedBearings` still verifies complete 360-bearing publication and wide/tele frame availability; producer cancellation still prevents cache publication. The live Terrarium test remains opt-in. `git diff --check` passed.
