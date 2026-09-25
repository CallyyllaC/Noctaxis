# Terrain fan performance investigation

This pass preserves the working terrain semantics and changes only local-contrast preparation and its renderer lifetime. Earlier lifecycle, cumulative-envelope, palette, startup and availability changes remain in the working tree.

## Measurement method

`TerrainFanPerformanceTests` is an opt-in, repeatable harness using the production framing calculator, fan builder, coordinator, projection and Skia renderer. Each stage has two warm-up iterations and ten measured iterations. JSON contains mean, median, worst, total elapsed time, managed bytes per update, geometry complexity and preparation counts. The final comparison uses Release builds. The baseline draw hook invokes the unchanged original `Enhance` implementation; both paths use identical geometry, palette, viewport and profile in the same binary. Original Debug measurements established the bottleneck before production edits; they are retained separately, not mixed with Release results.

The real scenario is Blaenau Ffestiniog (53.00562745652107, -3.9519223528039116), acquired through the existing Terrarium/WorldCover resolver and horizon service using a read-only view of the existing disk cache. It has 360 completed bearings and 157,320 sightline points. A nearby observer requires a new profile; returning to the original coordinate verifies service cache reuse. No network downloads or alternate production profiles/caches were introduced. A separate 360-bearing synthetic stress profile supplies 24 rising features per bearing.

The drawing target is an 800×600 CPU Skia bitmap. Native computer interaction/presentation profiling is unavailable in this session. These measurements are **not native FPS**, GPU timings, input-to-display latency, or a claim that 60 Hz has been achieved. Managed allocation counters exclude native Skia allocations. The headless Avalonia slider test separately measures bound input through derived camera depth, with real control bindings and a synthetic single ridge; it excludes screen presentation.

## Final Release measurements

The cached wide case uses 72° horizontal / 50° vertical FoV. Means below are milliseconds; each is ten updates. Baseline and optimized reports are `artifacts/plan-terrain-experiment/performance-cached-final-release.baseline.json` and `performance-cached-final-release.json`. Those files retain every stage's median, worst and allocation counts.

| Cached wide stage | Original mean | Optimized mean | Optimized median / worst | Managed bytes, original → optimized |
|---|---:|---:|---:|---:|
| Bearing depth calculation | 1.49 | 1.47 | 1.52 / 2.09 | 3,311,085 → 3,311,085 |
| Pitch depth calculation | 0.55 | 0.51 | 0.49 / 0.62 | 93,888 → 93,888 |
| Fan construction | 6.48 | 5.48 | 5.52 / 6.55 | 3,925,712 → 3,925,712 |
| Contrast calculation / preparation | 393.19 | 19.46 | 19.46 / 19.53 | 3,378,048 → 198,336 |
| Projection and path construction | 3.51 | 3.58 | 3.50 / 4.18 | 302,320 → 302,320 |
| Base shader alone | 196.13 | 192.18 | 191.41 / 197.53 | 672 → 672 |
| Static redraw | 585.52 | 199.24 | 199.80 / 201.09 | 3,771,616 → 393,568 |
| Pan/zoom redraw | 578.69 | 198.88 | 198.09 / 207.96 | 3,771,616 → 393,568 |
| Bearing through Skia | 586.84 | 227.35 | 228.62 / 236.56 | 14,178,877 → 11,025,044 |
| Pitch through Skia | 595.19 | 202.20 | 203.78 / 212.07 | 7,795,552 → 4,417,504 |
| FoV through Skia | 632.95 | 232.19 | 232.62 / 243.90 | 14,402,041 → 11,189,677 |

The wide contrast reduction is 95.1% in time and 94.1% in managed allocation. Static drawing improves 66.0%, bearing updates 61.3%, pitch updates 66.0%, and FoV updates 63.3% in this CPU harness. Differences in unchanged depth/fan/projection stages are run variation, not claimed optimizations.

| Cached FoV | Rays | Transitions | Patches / paths / terrain draws | Projected points | Original candidate comparisons | Indexed candidates |
|---|---:|---:|---:|---:|---:|---:|
| 72° | 73 | 1,914 | 3,779 | 22,172 | 14,277,062 | 2,367,516 |
| 24° | 25 | 834 | 1,595 | 8,732 | 2,542,430 | 1,010,350 |
| 4.5° | 6 | 86 | 30 | 610 | 870 | 870 |

Each draw also has one base rectangle and one terrain opacity composite. No extra bitmap is allocated by terrain code per draw; the existing SaveLayer remains. Indexed counts include conservative angular candidates subsequently rejected before radial arithmetic. Static/pan/zoom redraws perform zero neighbour comparisons after preparation.

At 24°, bearing-through-Skia improves 264.30 → 201.91 ms and static drawing 262.85 → 192.30 ms. At 4.5°, bearing is 194.86 → 193.01 ms: contrast was negligible, so no meaningful telephoto frame-time victory is claimed. The base shader dominates that workload. The synthetic wide profile independently measures original contrast at 322.66 ms and final preparation at 13.52 ms; its final JSON pair is `performance-final-release[.baseline].json`.

Repeated identical state costs 0.02 ms and 64 managed bytes in the cached wide case, with zero fan-ray evaluations or tone preparations. Ten wide bearing updates evaluate 730 fan rays and prepare ten tone fields. Ten small wide pitch changes evaluate 730 rays but retain the unchanged output fan and prepare zero tone fields. Pan/zoom and static redraws evaluate zero fan rays and prepare zero tones. These counts describe the actual chosen camera sweep, not a promise that arbitrary pitch changes retain identical geometry.

Cold production horizon service over cached DEM data: 1,557.27 ms; repeated same profile: 0.697 ms; nearby new profile: 1,064.44 ms; switch back: 0.069 ms. Both profiles have 360 completed bearings and valid terrain. See `performance-cached-final-release.json.acquisition.json`.

Headless bound controls: 56 pitch changes average 1.11 ms (median 0.46, worst 24.12); six bearing changes average 0.87 ms (median 0.69, worst 1.78). Every input performs one derived calculation; all 62 together perform zero profile-generation, environment-request or planning-snapshot updates. See `performance-input-final-release.json`. These UI timings have a different, simple terrain fixture and are not added to the cached mountain numbers.

## Ranked causes and implementation

1. **Repeated all-pairs local contrast dominated terrain presentation.** The cached wide fan has 3,779 patches, requiring 14,277,062 candidate comparisons for each original redraw. The old implementation also allocated LINQ iterators and neighbour arrays and recalculated the absolute power curve for neighbours repeatedly. Pan/zoom and static redraws repeated this work despite unchanged terrain geometry.
2. **Changed-fan preparation still repeated identical candidate sorting.** The first indexed implementation sorted the same bearing-bucket candidate list once per patch. Release measurements justified sorting it once per bucket instead.
3. **The residual CPU raster baseline is expensive independently of terrain.** The harness measures the base shader alone separately. This is an existing full-viewport spherical/weather shader cost, not evidence of a new DEM or horizon rebuild. It cannot be extrapolated to GPU rendering. No shader, weather or map architecture changes were made on that evidence.

`TerrainFanLocalContrast.Prepare` computes absolute tones once, groups candidates by bearing, retains conservative adjacent buckets at floating-point boundaries, sorts each bucket's candidate indices once, and applies the original inclusive bearing/radial rules. Neighbour sums retain original patch order. It is not claimed to be asymptotically linear for arbitrarily dense radial terrain or narrow FoVs; it removes irrelevant distant-bearing comparisons and repeated work.

`SkiaEnvironmentalOverlayResources` retains one fan reference and one prepared tone array. Identical fan redraws, pan, zoom and recolouring reuse those tones. Replacement releases the previous presentation data; renderer disposal clears it. No terrain cache, alternate profile, debounce, task scheduler or background computation was added. Projection and SKPath creation remain per draw because they were much smaller measured costs and depend on the viewport.

## Invalidation and thread ownership

| Change | Profile work | Camera depth / fan / presentation |
|---|---|---|
| Bearing | None | Derived depth and fan can change; prepare tones for a changed retained fan |
| Pitch | None | Derived depth and fan are evaluated; identical resulting fan state can retain existing tones |
| Focal length or lens | None | FoV changes depth and fan; same terrain profile |
| Target altitude / time, holding camera direction fixed | No terrain reacquisition for time alone | Target assessment can change; equal camera depth retains fan semantics |
| Pan / zoom | None | Reproject existing polygons; no fan or tone preparation |
| Repeated identical state | None | Coordinator retains fan; no tone preparation |
| Observer location / elevation | Profile may change | Existing generation, cancellation and stale-result rules remain authoritative |
| Return to cached observer | Existing horizon cache | Reuse cached profile; derive current camera presentation |

If target tracking changes camera bearing as time changes, a fan update is expected: the camera direction itself changed. Target independence does not mean ignoring an actual camera movement.

Camera-depth derivation runs synchronously in the view-model getter, with one cached result for matching inputs. Geographic fan construction runs in map presentation preparation. Tone preparation, geographic projection, native path creation and filling run in the custom Skia callback under its existing resource lock. Native callback thread scheduling and Avalonia presentation coalescing were not measured. Multiple property notifications must not be mistaken for multiple presented frames. The existing 62-input bound-slider regression checks exactly one derived calculation per input and zero profile generation/environment/snapshot refreshes; no arbitrary throttling was introduced.

## Smoothing decision

No smoothing was shipped. The measured CPU rendering path still exceeds a 30/60 Hz presentation budget independently of the optimized terrain work, and native GPU presentation could not be measured. The prerequisite for accepting a cosmetic render cost has therefore not been established. No claim is made about measured linear/smoothstep overhead: those prototypes and comparison captures were not produced. The existing approximately 1° sampling and hard angular treatment remain unchanged; no blur, extra rays, altered radial boundaries or invented extrema were added.

## Regression protection

Remaining limitations: a changed wide cached fan still allocates substantially while interpolating sightlines and constructing geographic polygons (about 11 MB for a bearing update through the entire measured pipeline). This pass removes the repeated contrast garbage but does not claim allocation-free interaction. Native GPU cost, compositor frame counts, display latency, network cache-miss acquisition and physical lens/location-switch interaction were not measured. Focal-length/lens behaviour is covered by production FoV-path measurements and existing view-model regression tests; pan/zoom measurements change the real projection inputs rather than driving a native map gesture. These distinctions matter when assessing the remaining live responsiveness.

New tests compare prepared and original tones across modes, FoVs, pitches and amounts, including floating-point bucket edges. A raster test requires byte-identical original/optimized output and a deterministic managed-allocation bound. A coordinator/renderer test requires reuse for identical state, target-altitude and viewport changes and replacement for observer changes. The optional real-profile harness also compares every prepared tone against the original implementation.

The pre-existing geometry SHA comparison, above-frame foreground occlusion case, negative/zero availability cases, pitch/bearing tests, startup activation, 360-bearing priority cancellation, stale-result rejection and coalescing tests are retained unchanged. Full solution validation is sequential.

The optional raster comparison is `artifacts/plan-terrain-experiment/performance-equivalence.png`: original on the left, optimized on the right. The test asserts identical pixel bytes before saving the comparison. This is an equivalence capture, not a smoothing comparison or native screenshot.

Focused Release terrain/rendering/profile/cancellation validation: **393 passed, 0 failed, 0 skipped** (Core 216, Desktop 177). The explicitly enabled benchmark/equivalence/input run passed all ten selected tests.

Full sequential Release solution suite: **690 passed, 0 failed, 1 skipped** (691 total: Core 331 passed / 1 skipped, Desktop 359 passed). The skipped test is `TerrariumLiveIntegrationTests.OfficialTerrariumSampleIsPhysicallyPlausible`, which requires the existing explicit live-test opt-in. TRX files are under `artifacts/plan-terrain-experiment/performance-focused` and `performance-full`. `git diff --check` passed. An early diagnostic benchmark run hit its two-minute hang timeout; later unrestricted benchmark runs completed successfully, so that timeout is not treated as evidence of a terrain defect.

Files changed specifically for this pass:

- `Noctaxis.Desktop/Controls/TerrainFanLocalContrast.cs`: indexed bulk preparation; original evaluator retained for equivalence.
- `Noctaxis.Desktop/Controls/EnvironmentalOverlayRenderer.cs`: one-fan tone reuse and preparation counter.
- `Noctaxis.Desktop.Tests/TerrainFanPerformanceTests.cs`: optional real/synthetic stage and acquisition benchmarks.
- `Noctaxis.Desktop.Tests/TerrainFanPreparedContrastTests.cs`: seven equivalence, boundary, allocation and reuse cases.
- `Noctaxis.Desktop.Tests/CameraLiveTuningTests.cs`: optional timings around existing bound-slider regression.
- `Noctaxis.Desktop.Tests/TerrainAvailabilityTests.cs`: make its existing read-only cache helper accessible to the benchmark.
- `docs/terrain-fan-performance.md`: this report.

## Reproduce

In PowerShell, set `NOCTAXIS_FAN_PERFORMANCE` to an absolute JSON output path for synthetic measurements, and/or `NOCTAXIS_FAN_CACHED_PERFORMANCE` for the cached mountain case. Each writes a corresponding `.baseline.json`. Set `NOCTAXIS_CAMERA_INPUT_PERFORMANCE` for bound-slider timings and `NOCTAXIS_FAN_PERFORMANCE_CAPTURE` for the byte-identical before/after raster comparison.

```powershell
dotnet test Noctaxis.Desktop.Tests -c Release --no-restore -m:1 --filter 'FullyQualifiedName~TerrainFanPerformanceTests|FullyQualifiedName~TerrainFanPreparedContrastTests|FullyQualifiedName~CameraSlidersShareNumericStateAndOnlyCalculateDerivedFrames' -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

Unset optional output variables for normal regression runs. The cached benchmark requires the existing Blaenau Ffestiniog DEM cache; cache misses are reported, not downloaded or fabricated.

## Preservation

This pass does not change cumulative skyline, transition distances, apparent-altitude metadata, local-contrast meaning, terrain profiles/acquisition, camera frame or pitch semantics, target occultation, flat/ground-facing states, Planner activation, progressive horizon lifecycle, Terrarium/provider architecture or weather. No existing regression threshold was relaxed.
