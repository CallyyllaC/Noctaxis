# Persistent terrain cache management

## User controls and defaults

Settings → Data → Terrain cache shows current file bytes, the applied limit and file count. The maximum uses GiB (binary units), defaults to **2 GiB = 2,147,483,648 bytes**, and follows the existing Save settings / reset-editor flow. Saving a reduced limit triggers eviction immediately without restarting. Zero disables persistent retention; the editor supports up to 1 TiB.

The normal persisted setting is `Settings.TerrainCacheLimitBytes` in `%APPDATA%/Noctaxis/state.json` (on this machine, `C:/Users/CGain/AppData/Roaming/Noctaxis/state.json`). Old state files without this property default to 2 GiB. Prefer the UI; external edits are read on startup, as with the existing settings store.

Clear terrain cache uses the existing confirmation dialog, explaining that terrain will be downloaded again and saved locations/settings remain unchanged. Confirming cancels current Planner terrain work, invalidates source-memory and horizon generations, waits for leased reads/writes, deletes governed files, then refreshes an active observer through the normal Planner pipeline. No observer movement, Planner reopen or application restart is needed.

## Scope

Only these source trees under `%APPDATA%/Noctaxis/EnvironmentalData` are governed, across their versions/layers:

- `mapzen-terrarium/` — currently `elevation-tiles-prod-undated/z{zoom}/{x}-{y}.png`.
- `esa-worldcover/` — currently `2021-v200/land-cover/{N|S}{latitude}{E|W}{longitude}.tif`.

All persistent files within those owned trees count, including future derived artefacts placed there. Unknown filenames have no inferred geography and receive lowest geographic retention priority. There is no new derived on-disk cache in this pass. Staged `.tmp` downloads are not valid retained entries; abandoned staging files are reconciled when no operation owns a file. Reparse-point directories are not traversed during reconciliation.

WSF settlement data, weather, saved map images, catalogue data, equipment, settings and saved locations are outside the policy and are not cleared.

## Accounting and metadata

`TerrainDiskCache` maintains file path, actual `FileInfo.Length`, geographic footprint, logical access recency and active lease count. There is no database or persisted index to corrupt. Startup/lazy reconciliation reconstructs the metadata from deterministic paths and filesystem timestamps. Explicit reconciliation and clear also detect externally added/deleted files.

Insertion and deletion update tracked byte totals incrementally. Individual terrain samples do not trigger directory scans or eviction. Decoded Terrarium hits update logical recency in memory. Actual disk acquisition/read leases refresh a file timestamp when released; this gives a restart baseline without touching the filesystem on every elevation sample. In-memory hit recency since the last disk access is not persisted across restart.

The Settings display reads an immutable usage snapshot without taking the filesystem-maintenance lock. Notifications are coalesced onto the UI dispatcher. Reconciliation, limit maintenance and clear run asynchronously; no directory scan is performed synchronously by the Settings display.

Diagnostics expose bytes, limit, persistent entry count, automatic eviction count/bytes, successful clears and oversized acquisitions. Normal logs summarize eviction/clear and explain oversized non-retention. Per-file cache-hit/decoded messages remain debug-level where already implemented.

## Eviction ordering

1. Highest distance from the nearest current saved location first.
2. Oldest access for equal distances.
3. Deterministic path order for exact ties.

With no saved points, geographic priorities are equal and the policy becomes LRU. Saved points are supplied from current application state, including after saved-location changes. Derived nearest-distance priorities carry a revision and are recomputed when the saved set changes. They are not permanently attached to where an entry was originally acquired.

Terrarium bounds are derived from Web Mercator z/x/y. WorldCover bounds cover its three-degree raster footprint. Distance is spherical: points inside a footprint have zero distance; otherwise the closest parallel or meridian edge is used, including longitude wrapping. A large WorldCover raster containing a saved point therefore remains valuable even when its centre is distant. No GIS dependency was added.

The open observer is not a permanent preservation point. All assets remain evictable once no active read/acquisition owns them. Even assets containing saved points are evicted by LRU if retaining all of them would exceed the budget.

## Hard limit, active files and oversized assets

At quiescence, retained file bytes must be at or below the limit. Successful insertions, limit changes and reconciliation trigger eviction. Active leases span source acquisition, validation, promotion and decode/selective reading; no file is deleted underneath those operations.

Temporary over-budget storage can exist while those files are actively leased. If the highest eviction-priority file is leased, eviction waits for its release rather than deleting more valuable nearby files. Release reruns eviction. No entire horizon calculation or permanently open observer holds a lease: the leases cover file operations only.

An asset larger than the entire budget is usable while leased and is deleted when released. Repeated reads within the same owning lease reuse it, with no internal download/evict retry loop. A later independent request for an oversized WorldCover asset can require another download because it was intentionally not retained; increasing the limit avoids that trade-off. Terrarium can still reuse its decoded memory data until clear or normal decoded-cache eviction. Oversized acquisitions are counted and logged.

Filesystem deletion failures are surfaced rather than falsely reporting that the hard limit or clear succeeded. Clear reports an error in Settings if removal is denied. The policy coordinates readers/writers in the application's cache instance; it is not a cross-process locking protocol for separately running Noctaxis instances sharing the same directory.

## Clear and memory invalidation

Clear advances the persistent generation, invalidates memory caches and cancels HorizonService work. It waits for existing file leases without holding the metadata lock across the asynchronous wait. An acquisition nested inside an existing lease may finish, so clear cannot deadlock a reader it is waiting for. After draining and deletion, generation/invalidation advances again before new work proceeds. Old-generation lease requests are rejected.

Memory affected:

- Terrarium decoded raster entries and recent failure entries.
- WorldCover cached tile-path/absence results. Its selective TIFF reader has no separate long-lived decoded raster cache.
- HorizonService completed profiles and active sessions. A completion from a previous horizon generation cannot repopulate the completed-profile cache.

Displayed immutable snapshots can remain valid while work drains. Planner then replaces them through its normal refresh and stale-result protection. WorldCover also checks cached paths under a file lease and reacquires normally if automatic eviction removed a previously known file.

## Deterministic tests

`TerrainDiskCacheTests` covers:

- 800 bytes plus a 400-byte insertion under a 1,000-byte limit: **800 bytes retained after eviction**, verified against physical file lengths.
- Farthest-first eviction overriding recency; a changed saved set changes priority without regenerating files.
- Multiple saved points, WorldCover containing a saved point, spherical/wrapped footprints.
- Equal-distance and no-saved-point LRU, including a recency update.
- Concurrent insertions and a subsequent limit reduction, with final usage within each limit.
- Clear during a controllable download: bytes remain readable while leased, clear waits, then deletes, rejects an old generation and allows new acquisition. No sleeps establish ordering.
- Oversized 120-byte entry with a 100-byte budget: four reads under one lease acquire once; **zero bytes retained after release**.
- Clear preserves an unrelated WSF file; reconciliation repairs externally deleted/added file accounting; a new manager recovers usage without an index.
- Settings default and JSON round-trip.

The size figures above distinguish retained storage from temporary leased files: the 1,000-byte scenario temporarily has 1,200 bytes during insertion before eviction, and the oversized scenario temporarily has 120 bytes while in use. Concurrent work can temporarily hold the sum of its active assets; it is checked at or below 1,000 bytes once operations finish, then at or below 500 after reduction.

Additional tests exercise a real Terrarium PNG through the persistent/decoded cache, clear, and reacquisition at the same coordinate; HorizonService invalidation at the same observer; Settings save, cancelled/confirmed clear, unchanged saved locations/settings and active-observer refresh. The existing settings-binding coverage guard includes both new controls. Production-DI fixture tests now explicitly use a temporary application-data root so they never manage the user's real cache.

## Measurements

Opt-in harness: `NOCTAXIS_RUN_CACHE_BENCHMARK=1`, filter `CachePolicyMeasurements` in Core tests. It uses 980 synthetic 4 KiB terrain files and 20 synthetic 1 MiB WorldCover files in representative directory layouts: **1,000 assets, 24,985,600 payload bytes** before timed insertions. These are filesystem/policy measurements, not DEM decoding or network benchmarks.

One local Release run:

| Saved points | Insert below limit | Insert + eviction after priority change | Insert + eviction, unchanged priorities |
|---:|---:|---:|---:|
| 0 | 3.65 ms | 13.37 ms | 5.97 ms |
| 10 | 4.38 ms | 13.14 ms | 3.49 ms |
| 100 | 3.25 ms | 70.74 ms | 5.66 ms |
| 250 | 4.83 ms | 174.21 ms | 4.69 ms |

Reconciliation: **86.72 ms**, about **1,375,040 allocated bytes**. Approximate additional retained manager metadata: **501,240 bytes**. Clear: **364.39 ms** for roughly 1,000 files after the insertion/eviction sequence. Empty directory entries are not file payloads and are left in place.

The first mixed-fixture run recomputed geographic priorities on successive insertion/release passes and took 376.56 ms in its 250-point case. Revisioned priority reuse removed that repeated calculation. These single-run observations motivated a small cache-policy change; they are not controlled benchmark speedup claims, percentile statistics or CI pass/fail thresholds. Large saved-set changes still require an O(entries × saved points) priority rebuild during maintenance; steady-state insertions reuse existing priorities. No heavyweight spatial index, GPU/SIMD terrain processing or DEM redesign was introduced.

## Verification

Restore and the standard `dotnet build Noctaxis.slnx -c Release --no-restore` passed with zero warnings/errors. The running-app output lock from the earlier regression task no longer blocked the final standard build. Full standard-output Release results: **Core 252 passed / 1 opt-in live theory skipped; Desktop 243 passed**. `git diff --check` passed.

The external live terrain suite remains opt-in and was not used to test cache eviction or clear. All destructive test operations used unique temporary directories. No manual clear was performed on the user's real terrain cache, and native Settings visual inspection was not claimed.
