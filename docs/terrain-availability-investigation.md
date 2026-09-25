# Terrain-frame availability investigation

Historical lifecycle/availability report. The lifecycle fix below is retained; the earlier pitch-independent map presentation has since been superseded by [the planner terrain-depth fan](planner-terrain-fan.md).

The failure is now reproduced deterministically in the progressive horizon lifecycle. The preceding plan-view experiment remains intact; the correction is confined to producer ownership and completion publication.

## Ownership trace

- `EnvironmentalOverlayStateFactory.PlanSamples` applies the positive-horizon test only while constructing map presentation samples. It neither replaces nor edits `TerrainHorizonProfile.Samples` or their sightlines.
- `MainViewModel.TerrainFrameDepth` reads `DerivedCameraFraming.CameraDepth` from `CurrentTerrain`, not from the map coordinator, texture or mask.
- `CameraTerrainFrame.Scan` accepts finite negative, zero and positive angles. `FramingVisibilityCalculator` creates depth when any coarse camera column has a resolved horizon; it does not require positive angles, positive coverage or a nonempty obstruction mask. A fully sky-facing camera still has a valid depth frame.
- `TerrainFrameStatus` shows disabled when calculations are disabled; no status when depth exists; resolving while ground work is pending/running; otherwise unavailable when depth is absent. Absence can come from no current snapshot/profile, no coverage or no usable sightline in the camera slice. The exact cause of the reported instance remains unknown.
- The minimap becomes Ready when its independently sampled raster contains valid surface elevations. This is independent of angle and obstruction. A ready raster does not prove the separate radial-profile operation succeeded; a true radial acquisition failure can legitimately leave the raster populated and the frame unavailable.
- `EnvironmentWorkState` uses coverage and environmental data state, not angle. Horizon creation retains negative slopes and angles. No global clamping/filtering was found.
- The actual hole came from `HorizonService.ProgressiveSession`: the wide camera priority request claimed its required bearings, while background workers skipped claimed slots. `BuildCompleteAsync` waited only for those workers and then published `IsComplete = true`; a cancelled priority waiter could also strand its claimed slots. The result was a reusable profile with `286/360` completed bearings and an unresolved `60–133°` sector.

## New regression coverage

`Noctaxis.Desktop.Tests/TerrainAvailabilityTests.cs` adds view-model/render tests for full profiles at −3°, 0° and +3°. Each test holds the minimap Ready, renders the full camera depth, checks profile/sightline values remain intact, checks negative/zero map texels are clear, verifies positive map texels are obstructed, and changes pitch through −20°, 0°, +20° while retaining the same map state. It then removes the actual profile and verifies the frame genuinely becomes unavailable despite the independent populated minimap. Existing disabled/pending and pitch-independence tests remain intact.

The lifecycle regression test fails against the old implementation with `Expected: 360, Actual: 286` and passes after the correction.

## Read-only cached terrain probe

The optional test uses the actual Terrarium decoder, WorldCover provider, surface resolver, horizon service and minimap service against existing local cache files. Its test-only cache adapter never invokes acquisition or modifies cache files. No provider or production cache implementation changed.

At the saved flat-area observer, the initial probe produced 360 valid horizon bearings (340 positive across the whole circle) and 16,384 valid minimap cells. The frame was available at each tested pitch, including the saved pitch/bearing.

A second probe selected a genuinely nonpositive sector at bearing 173.5°, FoV 0.5°. The map texture was entirely clear, the coordinator rebuilt once, and all four camera frames were available with different counts of terrain pixels. This verifies the simultaneous clear-map/available-frame case with real cached elevations, but does not reproduce the user's unavailable panel. Evidence is in `artifacts/plan-terrain-experiment/availability.json` and `availability-clear.json`.

To run the optional probe, set `NOCTAXIS_AVAILABILITY_OUTPUT` to a JSON destination and `NOCTAXIS_AVAILABILITY_LATITUDE` / `NOCTAXIS_AVAILABILITY_LONGITUDE` to a cached flat-area observer; run `InspectOptionalCachedFlatLocation`. It requires at least two adjacent nonpositive horizon bearings and fails explicitly if the cached data cannot supply the case.

## Lifecycle correction and deterministic captures

`BuildCompleteAsync` now waits for every bearing-ready producer before setting `IsComplete` or caching the profile. The priority wait is split into a producer operation using the session token and a caller-facing `WaitAsync(callerToken)`, so cancellation of a superseded UI waiter cannot abandon reserved bearings. Producer cancellation still cancels the whole generation and prevents cache publication.

`artifacts/plan-terrain-experiment/priority-before.json` records the old behavior: `IsComplete=true`, `completedBearings=286`, ranges `0–59 valid`, `60–133 unresolved`, `134–359 valid`. `priority-after.json` records the corrected behavior: `IsComplete=true`, `completedBearings=360`, range `0–359 valid`. The companion regression test uses a 74° priority request to model a wide startup FoV and then verifies the cached profile is complete without changing lens or observer.

## Validation

- Focused terrain/rendering/framing/profile suite: 178 Core + 127 Desktop passed.
- Final sequential full solution run: 331 Core + 302 Desktop passed, one network-dependent live integration test skipped, zero failures. The read-only cached-location probe was explicitly enabled and passed in this run.
- An earlier full run had one Avalonia dispatcher-ownership cleanup failure in an existing minimap test. The sequential rerun passed. An overlapping probe build also encountered a test-executable lock; it was rerun after the first suite completed.
- `git diff --check` passed. No live GUI reproduction of the reported unavailable state was obtained.
