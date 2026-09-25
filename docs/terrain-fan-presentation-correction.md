# Terrain presentation correction and native GPU validation

The ordered sliding-window local-contrast preparation is preserved. The renderer now assigns each pixel exactly one existing angular/radial patch and composites its prepared tone once. Terrain acquisition, profile availability, lifecycle/cancellation, cumulative skyline transitions, patch geometry, pitch behaviour, camera terrain frame, Planner activation, and weather calculations were not changed in this pass.

## GPU encoding failure and correction

The first unified field used unnormalised RGBAF32 table values, including 500,000-metre distances and patch indices, through a runtime-effect child shader. That child returns `half4`; the software result was valid, but the native sampling/precision boundary produced an empty terrain field. Consequently, the early fast timings from that encoding are invalid and excluded from this report.

`TerrainFanRaster` now packs fixed-point integer bytes into normalised RGBA8888 channels. The shader recovers the bytes and reconstructs values with full float arithmetic. Distances use 1/1024 metre storage units, angular offsets use 1e-7 degree units, and tone values use 1e-9 units, followed by normal shader float precision. Integer group indices remain exact. This is a transient renderer presentation table, not another terrain profile or acquisition cache. It retains one fan, is replaced/disposed with that fan, and is released when terrain is removed or ground-facing.

The renderer's inverse projection also uses stable spherical expressions. Software regression tests caught a large near-zero error in the portable `atan` approximation during development; stable range/angle evaluation removed it. These expressions only locate a pixel within the existing patch partition. Neither `PlanTerrainFan` nor its transition distances and altitude metadata were changed.

The final probe validates GPU readback **before** collecting interaction timings, then validates the changed fan again afterward. Both checks use the real window's `GRContext`. The complete profile has 360 bearings; kit-lens FoV is 72.5853° × 51.9785°.

## Native callback results

All values below are milliseconds. The baseline is the previous valid native path renderer, `native-kit-gpu2.json`; the corrected result is `native-correction.json`, with `gpuValidatedBeforeTiming=true` and successful pre/post GPU captures. Medians use the central pair for even sample counts.

| Interaction | Before mean / median / worst | Corrected mean / median / worst | Mean change |
|---|---:|---:|---:|
| Bearing | 112.32 / 129.80 / 156.26 | 13.35 / 11.30 / 24.46 | −98.97 |
| Pan | 24.89 / 24.83 / 34.11 | 0.60 / 0.60 / 0.73 | −24.29 |
| Zoom | 18.06 / 20.60 / 24.58 | 0.49 / 0.48 / 0.70 | −17.57 |
| Pitch | 20.19 / 19.66 / 24.56 | 0.68 / 0.51 / 1.38 | −19.52 |

Bearing tone preparation averages 9.34 ms, versus the earlier 91.18 ms. Pan, zoom and pitch perform zero tone preparation. GPU completion averages 0.20–0.44 ms across these corrected interactions; the base shader stage averages 0.25–0.35 ms. These measure the custom overlay callback and explicit GPU completion, not map-tile latency, compositor presentation or monitor frame pacing. Preflight warms the renderer, so static startup timings are not presented as a cold-start comparison.

Managed allocation per native callback fell from approximately 588 KB to 109 KB for bearing, 399 KB to 1.18 KB for pan, 398 KB to 1.13 KB for zoom, and 386 KB to 1.11 KB for pitch. These counters exclude native Skia/driver memory.

## Preserved contrast optimisation

The reference `Enhance` evaluator is retained. The ordered window preserves patch-order accumulation, inclusive bearing neighbourhoods, radial overlap/touch definitions, residual clamping, amount, and absolute tone curve. It avoids bucket construction and candidate sorting for production's ordered patches, and skips the redundant fractional-overlap calculation when Combined mode already accepts a positive overlap. Unordered inputs retain the indexed fallback.

The native fan replay contains 3,702 patches. Indexed preparation visits 2,318,364 candidates, performs 1,121,352 radial checks and accepts 102,754 neighbours; the ordered version visits only those 1,121,352 angular candidates. Setup is O(N), followed by O(E) candidate checks; dense same-bearing input can still have quadratic E. There is no bearing-keyed cache or background calculation.

The after-interaction diagnostic replay measured:

| Indexed replay sub-stage | First replay | Typical warm replay |
|---|---:|---:|
| Absolute tones and output arrays | 0.220 ms | 0.137–0.173 ms |
| Bearing buckets | 0.400 ms | 0.065–0.113 ms |
| Candidate indexing/arrays | 0.091 ms | 0.021–0.040 ms |
| Candidate sorting | 0.107 ms | about 0.098 ms |
| Predicates and neighbourhood accumulation | 42.086 ms | 17.861–18.267 ms |

The candidate loop dominates, not sorting. Radial checks and summation are measured together to avoid a clock call on every candidate; their separate counts are recorded. There is no LINQ or fingerprint calculation in the production preparation loop. Renderer reuse uses fan identity, with no cross-fan neighbourhood retention. This staged replay is warmer and structurally instrumented differently from the historical 91 ms callback, so its milliseconds must not be presented as an exact subdivision of that historical sample.

Preparation allocation fell from 193,352 to 88,920 bytes in this replay. The ordered path allocates three double arrays (24N + array headers), with no per-patch candidate lists or sorting buffers. All eight native replay comparisons reported maximum tone error **0**. Regression coverage also compares wide/normal/telephoto FoV, pitches, asymmetric and unequal radial transitions, wrapped bearings, floating-point bucket edges, and unordered input. Prepared/reference renders remain byte-identical.

## Shared-edge ownership and artifact evidence

The original polar partition had `polarOverlap=0` and `polarMax=1`. Independently projected paths nevertheless overlapped and left gaps. The correction removes path rasterisation from the positive terrain field. One angular-group lookup and one radial-band lookup select a single existing tone; one fragment composites it. Meaningful tone transitions remain crisp. No polygon expansion, overlap epsilon, smoothing, blur, opacity reduction or extra terrain sampling is used.

The same deterministic 258-patch fixture produces:

| Scale | Old overlapping path pixels | Old clear gaps | New shared-edge overdraw / alpha errors | New clear gaps / internal cracks |
|---|---:|---:|---:|---:|
| 1× | 42,888 | 174 | 0 / 0 | 0 / 0 |
| 2× | 53,799 | 86 | 0 / 0 | 0 / 0 |
| 4× | 54,489 | 55 | 0 / 0 | 0 / 0 |
| 8× | 32,898 | 0 | 0 / 0 | 0 / 0 |
| 16× | 11,338 | 0 | 0 / 0 | 0 / 0 |

After-renderer overdraw is structurally zero: one fragment per pixel, corroborated by constant-alpha readback. The old count is geometric `SKPath.Contains` overlap, not an assertion that all those pixels were visibly alpha-darkened. The fixture retains all 258 semantic patches and all 5,160 original corner coordinates unchanged. The corrected field emits zero projected vertices and zero patch paths, using one rectangle draw. There are no new polygon/triangle primitives to become invalid or degenerate; a separate legacy connected-sliver count was not collected. Tests find zero interior wrong-tone pixels and zero coverage outside the FoV. At 1×/4×, one/two pixels differ at numerical tone boundaries within the 0.05-pixel probe neighbourhood; none are clear cracks or alpha seams.

The existing seam test now uses the original polar `Contains` and reference `Enhance` rules as its oracle: the defective overlapping paths cannot define expected ownership. Its tone tolerance was tightened from eight to two channel levels; the zero-gap assertion remains.

## Native pixel readback

The complete real terrain fan, rendered on the native GPU into 512×384 surfaces, is non-empty at every scale. Preflight coverage counts are 83,236 / 118,377 / 179,768 / 196,608 / 196,608. Post-interaction counts are 83,069 / 118,320 / 179,799 / 196,608 / 196,608. **Every scale has zero GPU/software coverage differences and zero alpha errors.**

Post-interaction GPU/software colour differences above two channel levels affect four pixels at 2× and one at 4×. Each matches an existing semantic tone within 0.05 pixel of a boundary, including a taper corner that requires diagonal probing. Interior colour differences are zero. Preflight has two corresponding boundary pixels at 2× and none at the other scales. These small backend precision differences remain explicitly recorded; GPU/software output is not claimed byte-identical.

## Files and captures

Production presentation files changed in this work: `Noctaxis.Desktop/Controls/TerrainFanLocalContrast.cs`, `EnvironmentalOverlayRenderer.cs`, and new `TerrainFanRaster.cs`. Diagnostics extend `Noctaxis.Desktop/Diagnostics/ViewportRenderProbe.cs` and `tools/Noctaxis.ViewportProbe` with staged preparation replay and native GPU readback. Tests extend `TerrainFanPreparedContrastTests.cs`, strengthen `PlanTerrainFanTests.cs`, and add `TerrainFanRasterTests.cs`. All earlier unrelated working-tree changes remain intact.

Artifacts are under `artifacts/viewport-investigation`:

- `native-correction.json`: validated preflight, timings, preparation replay and post-interaction readback.
- `correction-comparison.json`: combined before/after metrics, generated by `tools/Summarize-TerrainCorrection.ps1`.
- `correction/fan-before-{1,2,4,8,16}x.png` and `fan-after-{1,2,4,8,16}x.png`: matching production-palette comparisons.
- `correction/metrics-{1,2,4,8,16}x.json`: deterministic pixel metrics.
- `native-correction.preflight.gpu-field-{1,2,4,8,16}x.png` and `native-correction.gpu-field-{1,2,4,8,16}x.png`: actual GPU readbacks before/after interactions.
- `native-invalid-half-*.json`: rejected empty-field measurements, excluded from performance conclusions.

## Final validation

Software/reference/pixel and five-scale artifact rerun: **19 passed, 0 failed**. Native GPU validation passed before and after timed interactions. Final focused Release suite: **216 passed, 0 failed, 0 skipped** (Core 66, Desktop 150). Full sequential Release rerun: **702 passed, 0 failed, 1 skipped, 703 total** (Core 331 passed/1 skipped; Desktop 371 passed). The skip is the explicitly opt-in Terrarium live integration test. `git diff --check` and the added-file trailing-whitespace check pass.

Final TRX evidence is in `gpu-encoding-pixels-final`, `gpu-encoding-focused-final`, and `gpu-encoding-full-final` under the artifact directory. The final focused and full commands use Release, `-m:1`, `xUnit.ParallelizeTestCollections=false`, and `xUnit.MaxParallelThreads=1`. The full command is `dotnet test Noctaxis.slnx -c Release --no-restore -m:1 --logger trx --results-directory artifacts/viewport-investigation/gpu-encoding-full-final --verbosity minimal -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1`.

An earlier focused attempt encountered an Avalonia dispatcher error in minimap test cleanup; its isolated rerun and the subsequent complete 702-pass solution run passed without production changes. Another earlier attempt hit the existing asynchronous weather stale-result test; the complete run also passed it. These observations are retained rather than concealed by reporting only successful runs.

Neither failure recurred in the final 216-test focused or 703-test full runs. No production change was made for either intermittent test failure.

The small GPU/software differences at existing tone boundaries remain a precision limitation. Visible tonal steps from the existing patch values also remain at large zoom; smoothing them would change the requested presentation semantics. Callback timing does not establish end-to-end map responsiveness or display frame rate.
