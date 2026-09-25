# Light Pollution ultrawide stabilization

## Scope and evidence

This pass addresses retention and completion of the existing local Light Pollution
overlay. Numerical decoding, sampling, normalization, palettes, installation, source
attribution, layer ordering, Layers UI and event-driven pin synchronization are unchanged.

The measurement appendix is [light-pollution-viewport-measurements.md](light-pollution-viewport-measurements.md).
It records every stage at 1920×1080, 2560×1440, 3440×1440 and 3840×2160,
including required and dispatched levels, cache occupancy/eviction, repeated requests,
numerical cache activity, read waiting, raster/PNG preparation and publication latency.
Raw JSON, timings, request lists and images are under `artifacts/lp-viewport/`.

The exact path is London (−0.1278°, 51.5074°), z8 initial; east 512 z8 pixels;
south 384 z8 pixels; z7; z7.5; return to the original z8 view. Each viewport uses the
same centres and resolutions. The probe drains actual native `FetchJob.FetchFunc`
tasks in batches of up to four and samples the native render selection between batches.
It never treats `Busy == false` as publication completion. All data access is local;
the installation client throws if an HTTP request is attempted.

The initial probe incorrectly inferred the intermediate tile level from logarithmic
resolution distance. The corrected probe records the level and tile list passed through
Mapsui's own data-fetch strategy. At z7.5 Mapsui selects **level 8**. The old `before/`
outputs are superseded by `baseline-final/`; they are not used in the appendix.

## Root causes

1. **Native retention was too small.** The native cache used min32/max64; crossing
   64 evicted 33 entries, including required current and fallback tiles. The fetch
   tracker does not automatically requeue a tile merely because it was evicted after
   publication. Missing detailed tiles and blank regions therefore remained after
   every scheduled fetch had finished. The initial required sets are 58, 94, 118 and
   216 tiles. Intermediate-zoom sets reach 121, 196, 251 and 406 tiles.
2. **A second native limit truncated cold requests.** Mapsui 5.1.0's
   [FetchTracker](https://raw.githubusercontent.com/Mapsui/Mapsui/5.1.0/Mapsui.Tiling/Fetcher/FetchTracker.cs)
   caps cache-missing dispatches at 256 per update. In the baseline 4K intermediate
   step, 396 required tiles were absent, but only 256 level-8 fetches were dispatched;
   140 misses were not scheduled. The appendix distinguishes this from cache eviction.
3. **The 64-entry rendered-raster LRU amplifies repeat work, but is not an independent
   cause of missing published features.** A probe-only native-capacity ablation kept
   the raster LRU at 64. All four paths had zero missing detailed tiles and zero
   repeated XYZ requests, despite substantial raster-LRU eviction. Native features
   retain their own reference to the same encoded byte array. Raster eviction does
   not invalidate a published feature. With the original native cache, revisiting an
   evicted raster often regenerates it; no separate raster-cache enlargement is needed
   to fix display coverage.
4. **Serialized numerical access materially increases cold backlog.** Four raster
   jobs share one read lease, so three can wait while one samples and encodes. Recorded
   wait totals/means and generation/PNG times are in the appendix. This contributes
   latency, but cannot explain holes remaining after all native publications. At 4K
   initial load, visible native eviction already occurs before numerical-cache eviction.
   The numerical cache remains 24 grids and its existing lease/disposal design is retained.

Fallback retention is necessary: z8 requires levels 8/7/6/5 concurrently, and z7
requires 7/6/5/4. Counting only current-level screen tiles would still under-size the cache.
No empty raster results, palette-revision rejections or raster failures occurred in the
real-data paths. The existing palette publication race is a separate dependency ordering
issue and its assertion/synchronization was not weakened.

The 24-grid cache is a secondary source of churn, not the demonstrated coverage fault.
Across the final paths it recorded 36/54/83/163 decompressions and 12/30/59/139 evictions;
the baseline recorded 50/67/110/181 decompressions. Decompression time was not isolated
from sampling, so these measurements do not establish a separate wall-time benefit from
increasing its capacity. In the final 4K path, generation/sampling took 20.44 s and PNG
encoding 10.74 s; aggregate lease waiting was 46.52 s across concurrent jobs, while actual
fetch wall time was 31.28 s. Source-completion-to-native-publication p95 was at most
0.122 ms across those stages. Thus the serialized preparation path contributes real
backlog; delayed native publication is not the measured multi-second bottleneck.

The ablation used probe-only access to Mapsui's cache, with capacities 147/246/316/468
derived from the first corrected run's distinct dispatched XYZs. At 4K the complete
required union was subsequently found to be 496 because the baseline's 256 limit had
prevented dispatching some candidates. Even the 468-entry ablation had no required-tile
evictions or repeated fetches; it evicted 28 obsolete tiles. It is diagnostic evidence,
not the production sizing policy. Full request-list unions are used below.

## Exact fix

`LightPollutionTileCachePolicy` delegates tile-level selection and rendering to the
existing Mapsui strategies. Through their public cache extension point it sets native
retention **before dispatch** to the high-water mark of:

```
distinct(current required tiles ∪ previous required tiles) + active-fetch allowance
```

The allowance is Mapsui's configured per-layer fetch concurrency (four here). The
existing 64-entry floor remains. The measured peak adjacent-view unions are
147/246/316/496, yielding capacities **151/250/320/500**. Capacity is capped at **512**,
corresponding to a 128 MiB allowance for potential 256×256 RGBA native tile images.
It does not grow without bound during travel. Min and max are equal at the chosen
capacity, avoiding the old half-cache purge. The high-water mark is retained for the
layer's lifetime to avoid resize/zoom oscillation. Required entries are touched through
the normal cache lookup path before new work is dispatched.

`LightPollutionTileLayer` also implements a finite continuation through the existing
`IFetchableSource` interface. If the previous native pass left deferred required tiles,
the layer replans the latest viewport only when the caller reports zero active native
fetches and actual cache inspection confirms those tiles are still absent. Mapsui's
[DataFetcher](https://raw.githubusercontent.com/Mapsui/Mapsui/5.1.0/Mapsui/Fetcher/DataFetcher.cs)
removes an active job after awaiting its fetch function, so this includes native cache
publication. There is one extra pass at the present 512/256 limits, not an unbounded
retry loop. A new viewport resets the finite budget; disposal stops further dispatch.
The dependency's global limit and source code are unchanged.

No background task, dispatcher callback, timer, event subscription, arbitrary sleep or
new cancellation owner was introduced. The source's 64-entry cache and serialized
numerical lease are unchanged. Optional diagnostics add counters/timestamps only when
the probe enables them; there is no permanent per-tile logging.

## Verification

Five new behavioral regressions were run before the fix: **0 passed, 5 failed**.
They require actual current-level features, not fallback coverage. After the fix those
same five passed. Additional coverage exercises a new viewport while four older native
jobs are gated in flight, and finite completion when no dataset is available.

The focused Release filter passed **95/95, 0 failed, 0 skipped**, including the six
viewport cases then present. The subsequently added unavailable-dataset bounded-completion
case passed separately **1/1, 0 failed, 0 skipped**. Thus the focused evidence covers
96 passing cases across those two runs. Artifacts: `focused.trx` and `bounded-failure.trx`.

The full Desktop Release suite passed **811 passed, 0 failed, 0 skipped** in 6 m 17 s
(`desktop-final.trx`). No production changes were needed during final verification.
The sequential Core result is **331 passed, 0 failed, 1 skipped**. The skip is the existing
`TerrariumLiveIntegrationTests.OfficialTerrariumSampleIsPhysicallyPlausible` opt-in test,
which requires `NOCTAXIS_RUN_LIVE_TERRAIN_TESTS=1` to access official tiles.
The sequential Desktop result is **811 passed, 0 failed, 0 skipped**. The full sequential
solution total is **1,142 passed, 0 failed, 1 skipped** (1,143 cases including the opt-in
skip). The Core run took 24 s and Desktop approximately 6 minutes. Results were checked
against the individual TRX outcomes, not inferred from a build or console exit alone.

Commands used:

```powershell
dotnet test Noctaxis.Desktop.Tests/Noctaxis.Desktop.Tests.csproj -c Release --no-restore --settings artifacts/phase4b/serial.runsettings --logger 'trx;LogFileName=desktop-final.trx' --results-directory artifacts/lp-viewport
dotnet test Noctaxis.slnx -c Release --no-restore --maxcpucount:1 --settings artifacts/phase4b/serial.runsettings --logger trx --results-directory artifacts/lp-viewport/solution-final
```

The final installed-data SHA-256 comparison matched **all 2,018 files** against the
snapshot taken before the final native-renderer repeat: **2,016 source tiles, manifest
and numerical overview; 0 changed, 0 added, 0 missing**. Evidence is in
`dataset-before.json`, `dataset-after.json` and `dataset-comparison.json` under
`artifacts/lp-viewport/`. No download, rewrite or reinstall was performed.

Final verification required no further production changes. This stabilization pass is complete.

The real-data probe was repeated after the fix. Both fixed paths have zero missing
current-level tiles, zero uncovered tile centres and zero evictions of currently required
native tiles at every stage. Remaining native evictions concern obsolete tiles; returning
to the original viewport dispatches no new raster work. The first fixed run reduced
repeated XYZs from 146/318/433/738 to **17/24/25/53**. The appendix uses the final native
renderer repeat rather than mixing timing samples between runs.

`after-native/*-native.png` uses Mapsui's actual Skia `MapRenderer`, with the existing
0.5 layer opacity. `*-detail.json` records measured encoded retention and memory estimates.
The separate diagnostic mosaics use full opacity and are not colour-comparison references.
Native images were visually inspected at all four sizes: 1080p east-pan, 1440p return,
ultrawide intermediate zoom and 4K intermediate zoom. The previously missing 1080p/4K
rectangles are filled, without persistent detail gaps. All 24 stages additionally have
recorded exact detailed-tile coverage, independent of this visual sample.

## Interpretation and limits

Read-wait totals sum concurrent waits; they are not wall time. Raster generation excludes
lease waiting and PNG encoding, but includes numerical sampling/decompression. Numerical
misses equal successful source-grid decompressions in these error-free runs. Numerical
hits count individual grid lookups (up to four per bilinear output pixel), not XYZ fetches.
The raster-start clock begins when the source worker starts, so it excludes its initial
thread-pool queue delay; stage wall and final viewport-to-publication measurements include
the pending native queue. Read waiting includes the small lease-acquisition overhead. Publication
is measured through completion of the native fetch function, so source-to-publication
values are upper bounds including scheduling overhead. Stage wall time excludes image
export; final repeat JSON additionally records viewport-to-publication distributions.

These are deterministic native planner/source/CPU-renderer measurements, not physical
ultrawide monitor testing, GPU memory measurements, live gesture frame pacing or FPS.
The gated overlap regression checks pending-work behavior separately; the measured path
drains between stages. Cold views still require real raster generation and can take
seconds. Source-grid churn on wider zoom transitions is recorded, not redesigned away.

The memory appendix includes actual unique PNG bytes and numerical arrays plus a
conservative decoded-native-image estimate. It is not total process memory: renderer
caches, framebuffers, object overhead and transient encoding buffers are additional.
The fixed raster and numerical cache bounds are retained. Viewports/rotations whose
required set exceeds 512 are outside this verified budget; the cache stays bounded and
does not promise full-detail retention for arbitrarily large surfaces. No basemap,
histogram, palette, UI or next-phase feature was started.

| Viewport | Native capacity high-water | Estimated retained cache memory |
|---|---:|---:|
| 1920×1080 | 151 | 106.33 MiB |
| 2560×1440 | 250 | 135.17 MiB |
| 3440×1440 | 320 | 155.95 MiB |
| 3840×2160 | 500 | 208.74 MiB |

These estimates use the measured arrays and conservative decoded-image allowance defined
above; they are **not process RSS or GPU memory measurements**.

## Preserved behavior

This task did not change Lorenz source files, installation format, decoder, LPI conversion,
numerical overview, display normalization, colour-map LUTs, rendered-raster cache capacity
(64), decoded numerical cache capacity (24), Light Pollution Settings/Layers UX,
MapComposition architecture, terrain calculations, minimap, or observer/pin synchronization.
The decoder and numerical sampling code were compared with the pre-investigation copies;
their only surrounding edits are opt-in cache diagnostics. Recorded hashes also confirm
the palette/display, binding/composition, map view, redraw scheduler and Layers UI files
are unchanged from the investigation baseline. Existing unrelated worktree edits remain.
