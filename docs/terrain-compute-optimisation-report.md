# Terrain computation optimisation — 5 September 2026

## Scope and continuation

Continued the existing working tree and `TerrainComputeBenchmarkTests.cs`; no previous work was discarded. The file already contained an opt-in offline PNG benchmark and its initial run had completed. It was extended with signed sample counts, submitted/completed batch counts, a verified zero-sample warm-cache check, layout measurements, and a separate tiled-TIFF classification experiment. The original matrix remains intact.

The regression baseline was Core **150 passed / 1 live skip**, Desktop **144 passed**. Production changes began only after the completed baseline JSON and Markdown report were saved. Persistent cache policy and the earlier uncommitted cache/UI work were preserved. No new UI, shader, persistent-cache policy or terrain sampling-policy changes were made.

## Reproduction and evidence

```powershell
$env:NOCTAXIS_COMPUTE_BENCHMARK_OUTPUT = 'H:\Noctaxis\docs\terrain-compute-final.json'
dotnet test Noctaxis.Core.Tests/Noctaxis.Core.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~TerrainComputeBenchmarkTests
Remove-Item Env:NOCTAXIS_COMPUTE_BENCHMARK_OUTPUT
./scripts/Compare-TerrainComputeBenchmarks.ps1
```

Without the environment variable, the timing harness performs no benchmark work. Timings never determine test success. Its unique temporary directory is deleted afterwards; it never accesses the user's environmental cache or performs network acquisition. Fixtures are prepared before measurement. The supplemental TIFF reader test runs after the main matrix, not concurrently.

* [Baseline report](terrain-compute-baseline.md) and [raw baseline](terrain-compute-baseline.json).
* [Complete final comparison](terrain-compute-comparison.md), including every scenario, operation and worker count; [machine-readable CSV](terrain-compute-comparison.csv).
* [Classification-only comparison](terrain-compute-classification-comparison.md) and [raw measurements](terrain-compute-negative-only.json).
* [Rejected serial-acquisition experiment](terrain-compute-bulk.json), [bounded parallel acquisition](terrain-compute-bulk-parallel.json), [preload/cancellation experiment](terrain-compute-cancellation.json).
* [Final raw measurements](terrain-compute-final.json), [measured value-type sizes](terrain-compute-final.json.layout.json), [selective TIFF experiment](terrain-compute-final.json.worldcover.json).

Each matrix cell has one warm-up and three measured repetitions on this .NET 10 machine. The tables use separate medians for time, allocation and counters. GC counters are process-wide, not stack-attributed allocation traces. The reported final run had no concurrent build/test activity. Earlier experiments and all repetitions remain available; timings vary appreciably between runs.

The named locations are workload origins, **not surveyed geographic terrain**: the production zoom-12 Terrarium provider decodes a repeated deterministic PNG fixture. Brigg and mountain pixels are non-negative; coast has positive/negative halves; open water is negative throughout. Classification in the main matrix is a constant permanent-water test double. This isolates CPU/managed allocation work but does not measure real WorldCover COG/network latency. The separate TIFF experiment uses the actual WorldCover provider and selective tiled reader against a small compressed fixture.

`360` means the existing default 50 km, 360-bearing, 437-distance profile (157,320 ray samples plus the observer). `FoV13` is a separate 13-bearing request using the same default range; it is not a measurement of progressive FoV-ready latency. The 500 km presentation-cone maximum was not changed. `observer` deliberately measures the explicit diagnostic API, including classification; production HorizonService uses the new calculation-only observer API. Cold calculation means a fresh decoded cache after the observer measurement, with PNG files already on disk. `warm` verifies no new terrain samples. Maps are 128 × 128 at 20 km, followed by an identical request. Rapid movement starts four distinct horizon operations and requests cancellation after 2 ms each.

## What was already optimised

HorizonService already precomputes radial distances, curvature, inverse distances, radial sine/cosine and bearing sine/cosine. Horizon angle and first horizontal obstruction already share the same sightline samples. Neither was rewritten. Terrarium decoded storage was already `float[]`; geodesy and interpolation remain double precision. WorldCover already groups coordinates by asset and opens one selective reader per asset per batch; it does not retain a full decoded raster. Horizon execution was already bounded, with 24-bearing work batches.

## Retained changes and measured justification

1. **Negative-only production classification.** Read the raw batch first, classify only negative coordinates, then scatter classifications back in input order and apply the unchanged resolver. All-negative batches reuse the original coordinate/classification arrays directly; mixed batches allocate a compact negative-coordinate array. Duplicates retain existing semantics without a new deduplication cache. Non-negative samples receive an explicit `NonNegativeTerrainClassificationNotRequired` reason, not an invented land/ocean label. Negative samples with unavailable classification still retain raw elevation and the existing fallback reason.

   The explicit single-sample diagnostic API and explicitly preclassified bulk API remain available. The diagnostic map still requests all labels. HorizonService uses calculation-only observer resolution and classification per bounded bearing batch. Classification time is still recorded through the existing ambient stage diagnostics and is included within surface sampling time; these overlapping stage times must not be added together.

2. **True bulk Terrarium sampling.** Convert each coordinate once to a pixel/fraction descriptor. Collect contributing tile keys, acquire each once per bounded group, then interpolate synchronously from resident immutable tile references into the original output positions. Groups are capped at 1,024 coordinates and at most 96 tile references, even for scattered input. Tile acquisition uses four bounded workers. No Task, interpolation result record, diagnostic string or persistent-cache descriptor is created per bulk sample. Four-corner weight order, zero-weight threshold, missing/corrupt-neighbour behavior, date-line wrapping and double arithmetic match the single-point reference.

   Existing GetTileAsync, decode coalescing, failure retry and acquisition/decode leases remain in use. Persistent logical recency is touched once per group tile, not per point. This does not redesign TerrainDiskCache. Single-coordinate callers retain their existing API.

3. **Preload discovery allocation and cancellation.** Replace per-coordinate four-key arrays/LINQ enumeration with one HashSet, retaining the same four discovery keys. Check cancellation every 256 coordinates before acquisition. Bulk interpolation checks every 256 samples; acquisition checks at group/tile boundaries; resolver and bearing checks remain bounded. Shared in-flight tile acquisition intentionally remains cancellation-independent, preserving coalescing for other callers.

4. **One-entry local-map reuse.** Cache only the most recent complete immutable snapshot, keyed by normalized observer, normalized range/resolution and terrain generation. The injected terrain cache supplies production generation identity. Incomplete terrain or classification remains retryable. Generation changes and observer/grid changes miss the cache. The service's resolver/source identity is fixed for its lifetime. Snapshots still own immutable arrays; no pooled or mutable provider storage is exposed.

## Classification-only results

These are the isolated first-stage results, before bulk sampling changes (six horizon workers):

| Profile | Classified before → after | Time before → after (ms) | Interpretation |
|---|---:|---:|---|
| Brigg | 157,321 → 0 | 317.25 → 283.53 | Eliminates positive classification |
| Coast | 157,321 → 87,351 | 352.68 → 275.03 | Only negative inputs classified |
| Open water | 157,321 → 157,321 | 317.36 → 273.91 | No classification-count reduction |
| Mountain | 157,321 → 0 | 330.54 → 342.45 | Slight timing regression in this stage |

The classification-only allocation table is linked above. It showed small coastal/open-water allocation increases from partition/scatter buffers; the final all-negative fast path removes those unnecessary buffers. No blanket speedup is claimed for classification alone. Its exact count reduction and the subsequent combined results justify retaining it.

Deterministic tests cover 10,000 non-negative samples → zero requests; 1,000 samples with 50 negative → exactly those 50 coordinates; all-negative input; exact output ordering; duplicate negative values; unavailable classification; positive permanent water; explicit debug classification; and a positive HorizonService profile with zero classification calls.

Two existing label-dependent fixtures were adapted to negative land, where labels are still required: the batching test now asserts exactly fifteen 24-bearing calls for all 24,120 samples, and environmental propagation still asserts BuiltUp labels and settlement-failure isolation. No numeric terrain assertion or resolver policy test was removed. Explicit positive-water resolver tests remain unchanged.

## Allocation, batching and parallelism findings

The principal measured reductions are the aggregate removal of per-coordinate async/cache/diagnostic work and preload temporary arrays. Counter measurements do not establish individual stack percentages. Terrarium's decoded-cache touch count drops from roughly 160,000 to 2,300–2,600 for full profiles. Persistent tile acquisition/decode counts remain broadly similar; this optimisation primarily removes lookup overhead, not cold tile demand. Exact load/hit counts vary with concurrent cache eviction and are included in the comparison.

The serial-acquisition bulk experiment reduced allocation but regressed Brigg full-profile time from 317 to 490 ms (coast 389 ms, water 428 ms, mountain 466 ms). It was rejected. Four bounded tile workers recovered performance: the intermediate run measured 179/182/188/216 ms for Brigg/coast/water/mountain. The final matrix records all 1/3/6 horizon-worker results; a horizon-worker setting of 1 does not disable the provider's independent bounded tile workers. No claim of a globally single-threaded benchmark is made. No new task-per-ray or task-per-sample scheduling was introduced, and the default horizon worker count was retained.

The final same-observer profile hit still performs no sampling but allocates about 11 KiB in existing cache-key/work wrappers. Sub-millisecond hit timings fluctuate; their percentage changes are not meaningful speed claims. Repeated map requests instead reuse the exact immutable snapshot and allocate 176 measured bytes with no provider calls.

No ArrayPool was added: the largest remaining allocation source is decoded tiles and stable output data, while bulk descriptors are at most 32 KiB per invocation. Pooling stable snapshots would violate ownership. No float conversion was needed because tiles already use floats. No ValueTask API migration was needed because the dense bulk loop is now synchronous after tile acquisition. No SIMD, explicit intrinsics or GPU compute path was introduced. The measurements do not isolate a sufficiently dominant regular arithmetic loop to justify them; JIT disassembly/auto-vectorisation was not measured, and no unsupported claim about JIT vectorisation is made. Portable scalar execution remains valid.

## WorldCover and hardware audit

The actual WorldCover path downloads whole official COG files (subject to the existing acquisition limit), then selectively reads required internal TIFF blocks; it does not decode the whole raster per request. It groups by asset and resolves/opens once per asset per call. Fifteen bearing batches can reopen the same asset fifteen times. The supplemental fixture compares that cost directly over 157,320 coordinates: consult the raw results for all repetitions. Smaller batches have more allocation from reader setup and repeated block reads; their timing on a small compressed TIFF must not be extrapolated to full official COGs. No complex new COG cache/subsystem was added.

The Atlantic classification failure was not reproduced or diagnosed in this offline pass. No HTTP failure was reinterpreted as ocean and no synthetic classification was added to production. The existing GPU presentation shader was left untouched. No GPU upload/dispatch/readback benchmark was run because the retained improvements concern irregular provider/cache work, not an established GPU-suitable bottleneck.

## Cancellation and invalidation limits

The rapid scenario measures actual elapsed time/allocation and batch submissions/completions. Baseline runs classified all four full profiles before cancellation; the final runs stop before ray batches are submitted, apart from the observer's required negative classification. It does not count individual operations abandoned inside a cancelled batch, stale UI results, or the exact CPU work after the cancellation signal. Existing stale-result regression tests remain the evidence for rejection of obsolete UI results. A new deterministic test cancels during preload discovery and proves at most 256 coordinate reads and zero tile loads.

Existing profile identity uses normalized latitude/longitude/elevation plus the complete terrain request (camera height, manual datum, range, sampling and curvature policy), with generation invalidation from cache clearing. Weather, target, time, pitch, bearing and vertical FoV are not profile-key fields. Planner keeps astronomy/presentation refresh separate; map refresh is observer-scoped and overlay redraws do not request a new grid. These paths were audited rather than rewritten. The benchmark records cache hit/miss status and verifies zero provider samples on hits; no new UI invalidation-reason framework was added.

There are existing ownership limits outside the direct HorizonService benchmark: PlannerEnvironmentService coalesces snapshot builds with a cancellation-independent lifetime, so cancelling a wait does not guarantee cancellation of that shared build. This pass does not claim otherwise. Existing completed horizon/environment dictionaries also have no cardinality bound; no additional profile cache was introduced here.

## Active memory estimate

These are storage estimates, not measured process working-set peaks. Runtime layout measurements are 24 bytes per coordinate, 112 per sightline sample, 16 per nullable elevation and 8 per nullable classification.

* Decoded Terrarium cache: 96 × 256 × 256 × 4 = **24 MiB** raster payload, plus small entries/objects.
* Default 50 km completed profile: 157,320 × 112 = **16.8 MiB** sightline payload plus bearing metadata. Every additional retained distinct profile adds approximately this amount; there is no global steady-state ceiling while existing profile dictionaries grow.
* One 128 × 128 immutable diagnostic map: approximately **1.1 MiB** array payload plus tile strings/metadata. Its new cache holds only one snapshot.
* Full-profile coordinate array: **3.6 MiB** transient. Up to five normal background bearing batches have roughly 4–6 MiB combined resolver/coordinate output buffers; an additional priority slot can be active. Bulk descriptor buffers are at most 32 KiB each, with small bounded tile dictionaries/key arrays.
* Bulk references keep at most 96 decoded tiles alive per active invocation, including tiles evicted from the shared decoded cache. Typical overlap substantially reduces this, but a conservative six-slot disjoint-tile envelope adds up to **144 MiB** raster payload beyond the shared cache. This is bounded per active invocation, not a new retained cache.
* WorldCover selective readers hold request/pixel/group arrays and internal compressed/uncompressed block buffers, not a complete raster. Actual block dimensions and concurrent classification batches determine peak memory; the small fixture does not establish an official-COG peak.
* Persistent-manager metadata is unchanged. The preceding cache-management report measured approximately 0.5 MiB for 1,000 assets; that metadata benchmark was not repeated here.

For one completed default profile plus map and full decoded cache, terrain payload is approximately **42 MiB**, excluding reader/manager/runtime overhead. During generation, ordinary overlapping tile workloads add coordinate/batch buffers; a conservative disjoint six-slot reference envelope is around **195–200 MiB before WorldCover/runtime overhead**. A local-map build adds roughly 1–2 MiB of owned/transient arrays plus at most 24 MiB of tile references and reader buffers; existing profile payload remains resident. Allocation totals in the benchmark are cumulative and must not be presented as peak live RAM.

## Verification and next work

Restore passed. The normal Release solution build passed with **0 warnings / 0 errors**; no process was terminated and no final isolated-output workaround was necessary. Full Core: **264 passed / 1 opt-in live skip**. Full Desktop: **243 passed**. Focused runs were performed after classification, each bulk experiment, preload changes and map reuse; full suites include the final retryability fix. The final opt-in benchmark passed. The final whitespace check result is recorded with delivery.

The most significant remaining measured CPU/allocation candidate is repeated PNG decode under the 96-tile cache: full-profile preload can evict tiles before their bearings consume them. Final cold profiles still load hundreds of tiles, and each load allocates a 256 KiB float raster. A next pass should measure priority-aware/working-set-aware preload scheduling before changing cache capacity. Real cached WorldCover COG profiling should accompany that work. The existing unbounded completed-profile dictionaries and shared-build cancellation ownership deserve a separately scoped memory/lifetime review. No SIMD or GPU rewrite is recommended from this evidence.

## Final measured summary

Six horizon workers; independent medians. All remaining operations and worker counts are in the complete comparison linked above.

| Scenario | Operation | Before ms | After ms | Time delta % | Before MiB allocated | After MiB allocated | Allocation delta % |
|---|---|---:|---:|---:|---:|---:|---:|
| Brigg | 360 | 317.2515 | 162.2543 | -48.86 | 298.365 | 178.667 | -40.12 |
| Brigg | map128 | 43.8787 | 19.4842 | -55.6 | 24.875 | 12.387 | -50.2 |
| Brigg | mapRepeat | 33.2946 | 0.005 | -99.98 | 15.574 | 0 | -100 |
| Brigg | rapid | 153.4552 | 52.786 | -65.6 | 85.364 | 16.007 | -81.25 |
| Brigg | warm | 0.0249 | 0.0306 | 22.89 | 0.011 | 0.011 | 0.36 |
| Coast | 360 | 352.6845 | 188.1923 | -46.64 | 293.567 | 177.235 | -39.63 |
| Coast | map128 | 49.3439 | 22.2589 | -54.89 | 23.663 | 11.557 | -51.16 |
| Coast | mapRepeat | 34.6801 | 0.0056 | -99.98 | 15.437 | 0 | -100 |
| Coast | rapid | 194.3303 | 61.4899 | -68.36 | 85.445 | 15.983 | -81.29 |
| Coast | warm | 0.0235 | 0.037 | 57.45 | 0.011 | 0.011 | 0 |
| Mountain | 360 | 330.5426 | 194.7882 | -41.07 | 326.858 | 207.449 | -36.53 |
| Mountain | map128 | 44.3 | 20.7148 | -53.24 | 25.91 | 13.427 | -48.18 |
| Mountain | mapRepeat | 32.8445 | 0.0037 | -99.99 | 15.563 | 0 | -100 |
| Mountain | rapid | 144.9078 | 60.2446 | -58.43 | 85.366 | 16.014 | -81.24 |
| Mountain | warm | 0.0233 | 0.025 | 7.3 | 0.011 | 0.011 | 0 |
| OpenWater | 360 | 317.3567 | 159.8774 | -49.62 | 294.96 | 175.205 | -40.6 |
| OpenWater | map128 | 46.256 | 21.3201 | -53.91 | 25.321 | 13.184 | -47.93 |
| OpenWater | mapRepeat | 37.5954 | 0.0033 | -99.99 | 15.554 | 0 | -100 |
| OpenWater | rapid | 176.9245 | 61.2542 | -65.38 | 85.446 | 16.014 | -81.26 |
| OpenWater | warm | 0.0273 | 0.0288 | 5.49 | 0.011 | 0.011 | 0.36 |

Final selective-TIFF medians: one batch 140.62 ms / 71,489,776 bytes; fifteen batches 104.63 ms / 78,688,712 bytes. This measures the small local fixture only; the additional allocation is approximately 6.87 MiB.

Final focused Core: 162 passed / 1 live skip. `git diff --check`: exit 0, no whitespace errors (only repository LF/CRLF notices).
Final focused Desktop: 144 passed.
