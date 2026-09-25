# Plan-view terrain experiment

Historical report: the unconditional pitch-independence experiment below is superseded by [the planner terrain-depth fan](planner-terrain-fan.md). The profile lifecycle correction remains in place.

## Existing behaviour and audit

The production map uses `EnvironmentalOverlayRenderer`, one continuous shader draw with a cached 512 × 1 RGBA float texture (8 KiB of pixel data), plus three geographic cone outline paths. It does not build the legacy `BuildCameraOverlay` terrain polygons. The texture resampling interpolates already calculated samples; it does not perform 512 terrain casts. Retaining this small texture preserves smooth distance/intensity presentation and avoids an unnecessary renderer rewrite.

Previously the map consumed `FramingVisibilityAssessment.EffectiveTerrainObstructions`. `MainViewModel.DerivedCameraFraming` keys this assessment on terrain, weather, target altitude, bearing, horizontal/vertical FoV, pitch and coverage threshold. `FramingVisibilityCalculator` samples approximately every degree and refines transitions to 0.125°. With a camera frame, those samples include camera-dependent coverage and first meaningful obstruction distances. Consequently pitch changed both the texture fingerprint and its visible result. Framing visibility notifications also discarded the map outline/state even when only camera coverage changed.

The underlying horizon request in `PlanningService.CreateTerrainRequest` depends on observer, camera height, manual ground elevation and terrain enablement. Observer refresh loads the environment and priority terrain; astronomy refresh reuses the environment. Camera controls only recalculate the derived camera frame, as the existing live slider regression verifies.

## Small, reversible changes

- `Noctaxis.Desktop/Controls/EnvironmentalOverlayModels.cs`: production-profile slice selection and diagnostic sample count, reusing the existing coordinator state. Samples use native profile bearings inside the FoV plus exact endpoints, including north wrap. Only finite, strictly positive terrain horizon angles contribute, with the existing `TerrainObstructionAt(bearing)` horizontal first-hit distance. No DEM, horizon, sightline or occultation algorithm changed.
- `Noctaxis.Desktop/Controls/NoctaxisMapView.cs`: passes the existing production horizon into that coordinator; ignores camera-only coverage notifications for map geometry/redraw and pitch/threshold-only settings changes for map redraw. Hidden visibility treatment remains hidden.
- `Noctaxis.Desktop.Tests/PlanTerrainExperimentTests.cs`: nonpositive/positive, shallow/capped intensity, pitch/target independence, native sample counts, bearing/FoV slicing and preserved camera/occultation/first-hit checks.
- `Noctaxis.Desktop.Tests/CameraLiveTuningTests.cs`: retains the coverage-to-shader and camera-depth tests, changes the production-map expectation to stable texture revision/alpha, and adapts the optional capture harness for synthetic flat/positive terrain comparisons.

No added deadband. Named styling constants define a 5° full-strength angle and 0.45 minimum positive strength:

`strength = 0.45 + 0.55 * sqrt(clamp(angle / 5°, 0, 1))`

Nonpositive/unavailable terrain has zero strength. The existing texture field named `EffectiveCoverage` carries squared presentation strength on the production map path, so the unchanged shader's square root produces this curve. It no longer denotes physical camera coverage on that path; camera-frame assessments retain their original coverage meaning. At the default 40% tint setting, positive terrain uses approximately 18–40% opacity (about 28% at 1°). The existing solid frontier and weather composition remain unchanged. No patterns, additional configuration, concurrency, GPU implementation or terrain cache were added.

## Invalidation after the change

| Event | Underlying terrain profile | Plan slice / texture | Map / FoV redraw |
| --- | --- | --- | --- |
| Observer position/elevation/height | Existing environment request/cache rules | Updated | Yes |
| Production terrain/source update | Existing service update | Updated | Yes |
| Camera bearing | Reused | New horizontal slice | Yes |
| Horizontal FoV/focal length | Reused | New horizontal slice | Yes |
| Pitch / frame threshold | Reused | Unchanged | No explicit map invalidation for these inputs |
| Target altitude alone | Reused | Unchanged | Snapshot/celestial repaint may still occur |
| Planning time | Environment reused by astronomy refresh | Unchanged if bearing/profile unchanged | Celestial and weather repaint may occur |
| Weather visibility | Reused | Same terrain texture | Weather and outline updated |
| Pan / zoom / size / tint styling | Reused | Same terrain texture | Cached state drawn with updated projection/style |

A target-following camera legitimately changes bearing as planning time or the target changes; that selects a different slice. Weather visibility can also change with planning time. These dependencies remain. Pitch and target altitude still invoke camera-frame/target assessment work outside the plan view; removing that would change features explicitly outside this experiment. A compositor repaint may still draw the cached texture even without an explicit terrain invalidation.

## Measured regression evidence

- Integer-aligned 24° FoV: 25 native samples including endpoints.
- Fractional 24° FoV: 26 samples. Six combinations of three pitches and two target altitudes: **26 total sample evaluations, one overlay rebuild, one profile revision**, identical state object.
- Bearing then FoV changes: 25 + 25 + 11 = **61 sample evaluations**, three overlay rebuilds, unchanged profile revision.
- Real map control test: six pitches retain texture revision and rendered probe alpha; camera coverage and depth continue changing.
- The existing 62-change live slider test verifies no planner generation, environment request or core recalculation increase while derived camera frames update.

## Visual evidence and limits

Captures: `artifacts/plan-terrain-experiment/before.png` and `after.png`. Both use the same synthetic observer, bearing 90°, FoV 70°, pitches 0/15/30/45°, and a bright #F2EFE9 background. Rows represent 1° and 0° horizons. The before capture runs the original assessment-fed renderer path; the after capture supplies the production profile. Camera-frame previews are included above each map panel.

Inspected both captures: previously even the flat horizon was tinted when pitched low, and tint disappeared as pitch increased. Now the positive terrain row is stable across pitches and the flat row has no terrain tint. The shallow tint and quiet solid frontier are visible on the bright background. These are offline synthetic captures, not actual flat/mountain locations or a live OpenStreetMap visual acceptance test. Readability over dense map labels and very narrow FoVs still needs live review. Native angular spacing necessarily limits boundary precision; existing interpolation is retained rather than claiming finer terrain detail.

To regenerate, set `NOCTAXIS_TINT_COMPARISON` to an absolute PNG path and run the `ExportOptionalPitchComparison` test. Set `NOCTAXIS_PLAN_BEFORE=1` only for the before capture.

## Validation

Focused terrain/environmental/framing/geographic suite: 176 Core + 122 Desktop passed (298 total).
Full solution (`dotnet test Noctaxis.slnx --no-restore --verbosity minimal`): 331 Core passed, one live integration test skipped; 302 Desktop passed. Total: **633 passed, 1 skipped, 0 failed**. The live Terrarium integration test remains opt-in; deterministic tests do not establish live provider or hardware results. `git diff --check` passed.
