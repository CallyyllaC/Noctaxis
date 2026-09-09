# Noctaxis application review — 9 September 2026

## Scope and result

This pass audited the current user-facing XAML and its workflows before changing UI structure. The starting checkout already contained extensive unfinished terrain, camera, startup, minimap and cache work. That work was preserved; the full working tree was tested together. This report attributes only this pass's changes to this pass.

The Terrarium/Mapzen terrain pipeline, first-obstruction semantics, horizon calculations, camera depth/frame behaviour, terrain colours and rendering defaults were not redesigned. No terrain provider or platform dependency was added.

The audit and persistence classification are in [application-review-audit.md](application-review-audit.md). Verification combines regression tests, real production service workloads with synthetic HTTP, headless Skia rendering and a short native Windows process sample. It is not a complete interactive native/GPU or long-session profile.

## UI/UX

- Kept Locations / Planner / Settings, location cards, primary/visible target selection, precise camera controls, read-only ground elevation, horizon/weather summaries and existing support placeholder.
- Removed the redundant first-run “Save current location” button; the current-location action and add card remain.
- Renamed ordinary WSF maintenance actions to “Refresh settlement imagery”; provider attribution remains intact.
- Replaced “canonical” sensor wording with an explanation of millimetres and field of view. Sensor and focal-range labels now include units. Corrected bearing/pitch accessibility names.
- Renamed wedge shading to camera overlay opacity. Separated terrain planning from camera overlay appearance and collapsed fine rendering controls under “Advanced overlay appearance”.
- Collapsed bulk image repair controls under “Repair saved map images” and changed the action row to wrap.
- Clarified that target visibility applies to Planner; retained the existing shared live state rather than introducing a competing settings copy.
- Explained current-time startup in Time settings. No display, equipment, source or cache defaults were arbitrarily reset.

Headless captures cover all three top-level pages and eight Settings tabs at 1440 × 900, using the real XAML shell without a populated view model. These check basic layout and wording, not real selected values, catalogue rows or native map tiles. The tall combined minimap/terrain-frame panel remains a constrained-window concern; its lower content is clipped in the shell capture. A responsive pass should validate it with populated data and actual map sizing before changing production frame presentation.

## Startup and persisted defaults

The existing launch path already replaces the saved planning instant with the current rounded machine-local hour. This pass found and fixed a concrete DST defect: leniently resolving a rounded wall time could choose the wrong occurrence of the repeated autumn hour or jump ahead by more than an hour. Rounding now considers valid neighbouring local-hour instants, both occurrences of an overlap, and skips nonexistent hours; ties round forward.

Equipment, units, zone preference, configured targets, visibility, camera/display settings, feature toggles, diagnostics preference, saved locations and last custom coordinate retain their existing persistence. Planning time remains serialized for compatibility but is session state, not a startup preference. Derived terrain/weather/render state continues to refresh through production services. Existing manual elevation compatibility is retained.

Concurrent `JsonUserDataStore.SaveAsync` calls reproduced a shared temporary-file collision. A per-store semaphore now serializes atomic writes, and cancellation is checked before replacing the last complete file. This protects the singleton store used by the application; it is not a multi-process writer lock.

## Production and diagnostics

Production still owns the upper-left `LocalTerrainMap`, camera terrain frame, FoV, horizon/obstruction calculations, map interaction and shared terrain services. The existing minimap remains independent of the diagnostics setting. Existing tests verify its observer-change cancellation, late-result rejection, ready/unavailable/disabled states and raster reuse across presentation changes.

New explicit `Noctaxis.Desktop.Diagnostics` ownership:

- `TerrainDebugMiniMap`: moved polar diagnostic control; consumes the existing production profile.
- `TerrainConeTopologyDiagnostics`: moved Debug-build topology writer; its opt-in behaviour is unchanged.
- `TerrainSampleRenderer`: extracted raw sample/ray drawing and labels from the production map control. The map supplies its existing projection and path renderer, so there is no second map projection or terrain pipeline.

`MainWindow` binds the polar view from the diagnostic namespace. `MainViewModel.TerrainDiagnostics.cs` remains the existing diagnostic presentation adapter. The historical `TerrainDebugMapService` / snapshot and view-model property names remain compatibility debt: those shared types also serve production and are not disabled with diagnostics. This pass deliberately avoids a broad mechanical rename across unrelated in-progress terrain work.

## Hardening and tests

New regression cases cover:

1. Three startup-time cases across autumn overlap and spring gap; two failed before the fix.
2. Weather timeout becomes an error result; caller cancellation still propagates. Timeout failed before the fix.
3. Location-search timeout becomes a user-facing failure; caller cancellation still propagates. Timeout failed before the fix.
4. Search cache evicts old distinct queries while reusing recent results.
5. Concurrent persistence writes complete without collision, and a cancelled save preserves the last complete state. Failed before the fix.
6. Encoded tile cache LRU eviction, byte bounds, oversized payload handling and active buffer lifetime.
7. Diagnostic renderer ownership versus production minimap ownership.
8. Location reload preserves unchanged cards and cancels removed cards. Failed before the fix.
9. Search reset rejects a late result from a provider ignoring cancellation and clears the loading indicator.

Three opt-in profiling harness methods were also added. Like the existing benchmark harnesses, they return without running the workload unless their output environment variable is set; those are included in the test runner's pass count and are not additional regression assertions.

Existing suites cover terrain/FoV/first-hit semantics, negative-land versus water resolution, cache/invalidation/cancellation, provider failures, geometry/rendering, equipment, locations, astronomy, exports, startup persistence and view-model transitions. No existing assertion was removed to make an implementation pass. An existing test file became partial to reuse its HTTP/service fixture, and a diagnostic-control namespace import changed.

| Run | Passed | Failed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Initial Core | 321 | 0 | 1 | 322 |
| Initial Desktop | 285 | 0 | 0 | 285 |
| Final Core | 329 | 0 | 1 | 330 |
| Final Desktop | 291 | 0 | 0 | 291 |
| Final total | 620 | 0 | 1 | 621 |

The runner total increased by 14: 11 regression cases and three opt-in profiling harness methods. `git diff --check` passed.

Debug and Release solution builds passed with zero warnings/errors. Release tests rebuild the changed projects. The skipped test is the opt-in official Terrarium live integration test. Source-relative XAML assertions, existing terrain/render regression suites and the new lifetime tests are included. TRX files are under `artifacts/application-review/verified`.

## Performance and memory

Measurements are observations, not stable CI timing thresholds. No SIMD, unsafe code, GPU architecture, extra terrain concurrency or numerical shortcut was introduced.

| Workload | Before | After / decision |
| --- | --- | --- |
| Actual production tile-fetch path, 512 distinct synthetic 100 KB responses | 51,324,760 bytes retained after GC; 512 HTTP calls | 16,770,944 bytes retained in isolated rerun; 512 calls; recent tile reused. Cache has a 16 MiB payload cap. About 67% less retained process memory in this workload. |
| Actual location-search provider, 2,000 synthetic responses | 719,368 bytes retained | 58,264 bytes retained; cache bounded to 128 queries, expired entries removed on insertion. About 92% lower retained memory in this workload. |
| Sun: 30 position + daily-path calculations, changing observer/time | 293 ms, 9.2 MB allocated | No algorithm change; subsequent warm run 114 ms. Not claimed as an optimisation. |
| Moon: same workload | 79 ms, 11.6 MB allocated | 80 ms in later run; unchanged. |
| M31: same workload | 533 ms, 134 MB allocated | 722 ms later; significant allocation candidate, but no numerical/caching rewrite without a focused trace. |
| Real headless MainWindow construction/show | 2,672 ms first measured run | 1,472 ms later; warmup and capture workload differ, so no startup speedup claim. |
| Native Windows startup, approved new process, existing local settings/caches | No before comparison | At 15 seconds: 281,407,488-byte working set, 255,836,160 private bytes, 13,656 ms total CPU. Responding in all 15 sampled seconds. Only this newly launched process was stopped. |

Tile fetch time was 62 ms before and 118 ms in the isolated final sample. This change is for bounded retention, not demonstrated speed. Both versions allocate about 103 MB over the synthetic workload; it does not eliminate HTTP payload allocation or large objects, but prevents the cache from retaining every tile indefinitely. Timing and total-process GC measurements are noisy, and do not imply a native heap or GPU resource bound.

Location-card reload previously recreated cards and restarted thumbnail requests even with unchanged coordinates. The regression workload demonstrated this. It now updates and reuses existing cards; deleted cards cancel work, unsubscribe attribution events and dispose their bitmap. Clipboard export also disposes its temporary bitmap and input stream after clipboard flush.

### Whole-application inspection matrix

| Area | Evidence / disposition |
| --- | --- |
| Startup and disk state | Native process sample, headless shell timing, startup/persistence tests; concurrent writes fixed. |
| Main map interaction / redraw / terrain rendering | Existing real Skia renderer and geographic regression suites run. Diagnostic sample drawing extracted without changing production mathematics. No interactive GPU frame-time claim. |
| Terrain generation / caches / cancellation / locking | Existing production tests and checked-in benchmark tooling reviewed; bounded completed profiles, source invalidation and waiter cancellation retained. Earlier terrain benchmark reports are historical, not fresh measurements from this pass. |
| Minimap updates | Existing observer-generation and raster-identity tests run; no new sampling pipeline. |
| FoV / observer / target / date-time updates | Existing view-model refresh/invalidation tests, new DST tests and measured production astronomy paths. No speculative extra parallelism or redraw caching. |
| Location cards / saved loading / image loading | New reuse/removal test; bitmap lifetime fixed; production encoded tile path measured and bounded. |
| Weather / network failures | Existing geographic weather cache is capped at 32; timeout and caller-cancellation tests added. No live forecast latency claim. |
| Search / fallback paths | Existing location fallback tests retained; query cache bounded; cancellation-ignoring late results rejected. |
| Serialization / async / IO | Temporary-file concurrency reproduced and fixed; no change to persisted format. No repeated serialization optimisation asserted. |
| Allocation / large objects / retention | Tile/search GC measurements plus astronomy allocated bytes. Native process totals are sampled; no long-running GC-root or GPU-retention trace. |
| Concurrency / subscriptions | Existing terrain coalescing/limits retained; card subscriptions cleaned up on removal. No unsafe keyed-lock removal or extra worker pools. |

### Remaining risks and rejected changes

- Native process “Responding” is a coarse OS observation, not a frame-time guarantee. A populated interactive map/observer/weather session, Linux run, GPU capture and multi-hour retention trace remain unverified.
- The thumbnail service's per-location gate dictionary remains lifetime-scoped and grows with distinct location IDs. Unlike encoded image buffers these are small entries, but repeated create/delete sessions merit a dedicated bounded or reference-counted gate test before changing locking semantics.
- Application exit still uses an async event handler, and initialization is fire-and-forget. Successful store-level tests do not establish guaranteed persistence during OS shutdown or initialization-failure presentation. These need application-lifetime tests rather than an untested shutdown rewrite.
- Production diagnostic presentation still has some historical debug naming. Mechanical API renaming was rejected as churn across the unfinished terrain work.
- M31 allocations warrant tracing, but adding a celestial cache without proving complete date/zone/observer invalidation would risk stale numerical results.
- No indefinite caching, pooling of externally held arrays, SIMD rewrite or new GPU dependency was justified by these measurements.

## Files changed by this pass

Modified (some already contained unrelated edits):

- `Noctaxis.Core/Time/PlannerStartupTime.cs`
- `Noctaxis.Core/Locations/LocationServices.cs`
- `Noctaxis.Core/Persistence/JsonUserDataStore.cs`
- `Noctaxis.Core/Weather/OpenMeteoWeatherProvider.cs`
- `Noctaxis.Core.Tests/PlannerStartupTimeTests.cs`
- `Noctaxis.Core.Tests/PersistenceAndWeatherTests.cs`
- `Noctaxis.Desktop/Controls/NoctaxisMapView.cs`
- `Noctaxis.Desktop/Services/LocationMapThumbnailService.cs`
- `Noctaxis.Desktop/ViewModels/LocationViewModels.cs`
- `Noctaxis.Desktop/Views/MainWindow.axaml`
- `Noctaxis.Desktop/Views/MainWindow.axaml.cs`
- `Noctaxis.Desktop/Views/LocationsPage.axaml`
- `Noctaxis.Desktop.Tests/EnvironmentalOverlayTests.cs`
- `Noctaxis.Desktop.Tests/LocationMapThumbnailServiceTests.cs`

Moved (old paths removed, namespace updated):

- `Noctaxis.Desktop/Controls/TerrainDebugMiniMap.cs` → `Noctaxis.Desktop/Diagnostics/TerrainDebugMiniMap.cs`
- `Noctaxis.Desktop/Controls/TerrainConeTopologyDiagnostics.cs` → `Noctaxis.Desktop/Diagnostics/TerrainConeTopologyDiagnostics.cs`

Created:

- `Noctaxis.Desktop/Diagnostics/TerrainSampleRenderer.cs`
- `Noctaxis.Desktop/Services/TileMemoryCache.cs`
- `Noctaxis.Core.Tests/ApplicationHardeningTests.cs`
- `Noctaxis.Desktop.Tests/ApplicationHardeningTests.cs`
- `Noctaxis.Desktop.Tests/ApplicationReviewProfileTests.cs`
- `Noctaxis.Desktop.Tests/LocationLifetimeTests.cs`
- `docs/application-review-audit.md`
- `docs/application-review-report.md`
- `docs/application-review-artifacts.txt`

The artifact manifest lists every generated review JSON, PNG and TRX path. Build `bin`/`obj` outputs are ordinary generated outputs. Other modified/untracked files in the starting checkout were preserved and are not claimed as this pass's changes.

## Reproduction

```powershell
dotnet test Noctaxis.slnx -c Release --no-restore --logger trx --results-directory artifacts/application-review/verified
dotnet build Noctaxis.slnx -c Debug --no-restore
$env:NOCTAXIS_REVIEW_PROFILE = 'H:\Noctaxis\artifacts\application-review\repeat'
dotnet test Noctaxis.slnx -c Release --no-restore --filter 'FullyQualifiedName~ProfileProduction|FullyQualifiedName~ProfileAndCaptureDesktopPages'
```

The profile workloads use synthetic HTTP and temporary test assets. They do not fetch official terrain/weather data. The separate native startup sample used the normal local application settings and caches with explicit approval.
