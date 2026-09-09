# Completed terrain result lifetime — 6 September 2026

## Continuation and scope

The resumed working tree already contained `CompletedResultCache`, four-entry HorizonService and PlannerEnvironmentService integrations, generation fences, diagnostics and six deterministic tests. Inspection and a fresh test build confirmed those edits compiled and the six tests passed. They were retained. Completion work clarified the horizon active-session cleanup method name, added cancellation and independent-working-set tests, and completed verification. The accepted compute optimisations and all unrelated working-tree edits remain intact.

## Cache audit

| Cache | Key / retained value | Previous lifetime | Final treatment |
|---|---|---|---|
| HorizonService completed profiles | Existing normalized coordinate/request string; immutable profile with sightlines, about 16.8 MiB for the default 50 km request | Unlimited dictionary; cleared by generation invalidation | Four completed entries, LRU |
| PlannerEnvironmentService snapshots | Normalized observer plus full TerrainProfileRequest; snapshot references profile and settlement data | Lazy tasks retained successful snapshots indefinitely; faults removed when observed | Separate active builds and four-entry completed LRU; generation invalidation registered through IEnvironmentalTileCache |
| HorizonService active sessions | Same profile key; progressive work/tasks | Removed after completion/failure; generation invalidation detaches/cancels old work | Unchanged active/coalescing policy; no completed-LRU eviction of active work |
| Diagnostic terrain map | Normalized observer/grid/generation; about 1.1 MiB default immutable map | Already one completed snapshot | Unchanged |
| Terrarium decoded raster cache | Tile key; 256 KiB float raster | Existing 96-tile bounded cache | Unchanged |
| WSF decoded coverage | Chunk/layer; variable scientific raster | Existing eight-entry decoded cache | Unchanged |
| WorldCover asset results | Asset name; path/status, not decoded full rasters | No count bound; generation clear removes entries | Lightweight metadata retained unchanged, as requested |
| Provider failure maps / tile-cache lock map | Tile/chunk/path; failure metadata or semaphore | No global count bound; failures expire on access, generation clear where supported | Unchanged; not completed observer profiles |

WorldCover full decoded raster data is not retained in its asset-result dictionary. Selective TIFF block dictionaries and Terrarium bulk tile-reference dictionaries are operation-local, not long-lived completed-result caches. Persistent terrain-manager metadata remains governed by the existing disk policy and is untouched.

One additional **inspection finding remains outside this two-cache change**: WSF's `AcquireSingleFlightAsync` removes its entry in a waiter's `finally` only if the shared task has already completed. If its waiter cancels before acquisition finishes, a later completed result can remain referenced by that dictionary until that key is requested again. The result can contain a raster. This cancellation edge was not changed or reproduced in this surgical pass; it prevents claiming that every possible terrain-adjacent retained reference is globally bounded. Provider failure/asset metadata likewise remains count-unbounded. No claim of a strict process-memory limit is made.

## Implementation and safety

Both services use the same small internal completed-result helper: a dictionary maps keys to linked-list nodes, with the most recently used node at the front. Hits move their node to the front; insertions add at the front; overflow removes the tail. Capacity is four in each service. Eviction only drops reusable references; it never disposes, cancels or mutates returned profiles/snapshots.

The helper protects list/dictionary/counter updates with one lock. Service generation state and active environment entries use their existing/service gate. Lock ordering is service gate followed by completed-cache gate; no path acquires them in reverse order. There is no await/provider work under the completed-cache lock. Byte estimation reads bearing counts once at insertion, without traversing individual terrain samples. No per-eviction logging was added.

Horizon publication occurs once, before successful profile-task completion, with the captured generation checked under the generation gate. Existing completion cleanup removes the active session and cannot reinsert an evicted profile. The profile key and computation semantics are unchanged.

Environment builds remain lazy and coalesced, independent of individual caller cancellation as before. Completion/failure removes its active entry even when a caller stops waiting. Identity-checked removal prevents an old build from removing a replacement. Successful results enter the completed LRU only if their generation still matches. Invalidation clears completed entries and detaches old active entries; old callers may still hold their results, but late completion cannot repopulate the current-generation cache. Production cache-clear notifications reach the environment service through its injected IEnvironmentalTileCache; HorizonService retains its existing invalidation wiring.

`CompletedCacheDiagnostics` exposes count, capacity, cumulative hits/misses/evictions and approximate retained array bytes for both services. Environment also exposes active build count. A miss means the completed LRU did not satisfy a request; that request may still coalesce with active work. Clear resets retained count/bytes; cumulative counters remain available.

## Memory ceiling

* Horizon completed cache: at most **4 profiles**, approximately **67.2 MiB** of default sightline payload plus metadata.
* Environment completed cache: at most **4 snapshots**, each potentially retaining a profile. The default settlement association is only a 1 × 1 pair of float arrays; the profile dominates.
* Usually the two caches share profile objects, so they do not duplicate the arrays. Independent recency/direct horizon requests can leave different sets; the regression test demonstrates **8 distinct profiles** across them. For default requests this is approximately **134.4 MiB** of profile payload plus metadata.
* Counts, not bytes, are capped. Larger custom requests retain larger profiles. Active builds, current UI/caller references, decoded rasters and delayed GC collection remain additional memory owners. Summing both diagnostic byte estimates can double-count shared profiles.

No extra deduplication layer, disk persistence, cache-capacity changes elsewhere, UI changes or scheduling changes were introduced.

## Deterministic tests

Eight `TerrainResultCacheTests` cover:

1. Horizon A/B/C/D, hit A, insert E: B is evicted; A performs zero provider work; evicted B recalculates and then becomes a hit; previously returned B remains valid.
2. Forty distinct observer snapshots: both completed counts remain bounded, retained-byte estimates stay bounded for equal-size requests and completed environment builds leave the active dictionary.
3. Equivalent environment LRU behavior; evicted snapshots rerun settlement association.
4. A controlled blocked build remains active/coalesced while other completed entries are evicted, then completes normally.
5. Horizon clear removes completed data and rejects an old-generation completion from a deliberately cancellation-ignoring provider.
6. Environment invalidation through a cache notification prevents late old-generation completion from repopulating the cache; new requests work.
7. Cancelling a waiter preserves the shared build and its eventual completed-cache result without leaving an active entry.
8. Independent service working sets can retain eight distinct profiles, making the reported memory estimate explicit.

All scheduling-sensitive tests use completion sources, not sleeps or timing thresholds. Focused baseline before implementation: **152 passed / 1 live skip**. Final focused terrain/visibility suite: **160 passed / 1 live skip**.

## Final verification

- Release solution build: passed, 0 warnings and 0 errors.
- Full Core suite: 272 passed, 1 opt-in live skip.
- Full Desktop suite: 243 passed.
- `git diff --check`: passed, exit 0; only LF/CRLF conversion notices.
