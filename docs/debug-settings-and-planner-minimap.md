# Debug Settings and permanent Planner terrain minimap

This pass reorganises the desktop UI and its local-map request ownership. Terrain calculations, source policy, geodesy, cache capacities, and startup-time behaviour are unchanged.

## Settings and diagnostics

Debug is the last tab in the existing Settings navigation. The existing persisted `TerrainDebugOverlay` preference is labelled **Enable terrain diagnostics** there; it retains the normal Save settings / Reset unsaved changes workflow and requires no settings migration.

Debug contains:

- Current observer, terrain status, generation and profile timestamp.
- The existing terrain horizon polar view.
- Selected camera bearing, first horizontal obstruction, terrain horizon angle, target altitude and visibility.
- Expandable surface-source details with readable labels for raw terrain elevation, resolved surface elevation, land-cover classification, water correction, observer ground, camera height and final observer elevation.
- Expandable cache/performance details: completed-profile capacity, hits, misses, evictions, existing timing counters and terrain disk-cache usage.
- Advanced tile/cell/resolution, correction reason, observer datum and local-map generation/state.
- The existing detailed copy-snapshot action. Its output retains the full raw diagnostic information and uses current observer/generation data.

The ordinary terrain-calculation toggle remains in Appearance. Persistent terrain cache controls remain in Data. The Planner's large diagnostic overlay is removed; its concise inspector terrain summary and ordinary terrain/weather presentation remain.

The polar control receives no profile when diagnostics are off. Human-readable diagnostic properties return a disabled status without analysing a profile in that state. Full raw-snapshot formatting is no longer bound to a continuously updated text block: it is invoked by the copy action. No additional diagnostic sampling was introduced.

## Permanent geographic minimap

The terrain canvas is 208 × 208 display pixels, inside a compact bordered panel at the main Planner map's top-left with a 12-pixel margin. The refresh strip is placed beside it; the inspector toggle and bottom map attribution retain their separate positions. The minimap container is not hit-testable.

It shows resolved physical terrain, existing water colouring, a central observer marker, camera FoV/direction, north and the existing range/scale. Dense winning-sample markers and first-hit frontier lines are omitted from this normal product view. Detailed first-hit information remains in Debug.

The existing north-up geographic projection, 20 km radius and 128 × 128 grid are unchanged. No new service, dataset or cache was added. The existing `TerrainDebugMapService` and snapshot type names are retained to avoid a broad mechanical API migration. Their bounded immutable snapshot already contains the necessary product data; its arrays are approximately 1.1 MiB at the normal grid size, so this pass does not introduce a separate product/debug copy.

## Lifecycle and reuse

Local-map scheduling depends on terrain being enabled, observer latitude/longitude and terrain source/cache generation. It no longer depends on the diagnostics preference. A repeated request for the same observer/source keeps the existing pending or completed map instead of clearing and re-requesting it. The original one-entry service cache remains in use.

Observer movement immediately clears the previous raster and starts resolving the current observer. Existing cancellation, generation and canonical-observer checks reject late completions. Terrain disabling clears the raster and starts no map work. A completed result with no valid terrain cells, or an exception, ends resolving and exposes the unavailable state.

Normal status wording is **Resolving terrain…**, **Terrain calculations disabled**, or **Terrain unavailable**. Ready renders the raster. No raw telemetry is printed inside the minimap.

FoV and bearing use the same camera properties as the Planner. Changes to lens/FoV, time, target, weather, bearing and the diagnostics preference preserve the raster; only the overlay needs to redraw. Camera-height/manual-elevation changes do not invalidate this geographic raster.

## Verification coverage

New tests cover Debug navigation and control placement, one permanent minimap, diagnostics-independent map requests, raster reuse, late observer completion using controllable tasks without sleeps, unavailable/error completion, and headless rendering of ready/resolving/disabled/unavailable states. The rendering test also checks that bearing changes alter the rendered overlay without replacing the snapshot.

Existing disabled-production integration tests continue to verify zero terrain provider/cache work while disabled, including manual elevation edits. Existing geographic orientation, polar reactivity, terrain rendering and stale-result tests remain in place.

One existing toggle test exposed worker-thread UI mutation during the full run. It now runs in Avalonia's headless UI context, matching production dispatch; its stale-result assertions are unchanged.

Visual inspection covered deterministic headless minimap captures. It was not a live desktop or GPU-driver validation.

Final verification:

| Check | Result |
| --- | --- |
| Focused Desktop UI / terrain / polar tests | 127 passed |
| Targeted rerun after UI-context correction | 7 passed |
| Solution restore | Passed; dependencies up to date |
| Release solution build | Passed; 0 warnings, 0 errors |
| Full Core suite | 281 passed, 1 opt-in live test skipped |
| Full Desktop suite | 253 passed |
| git diff --check | Passed; line-ending conversion notices only |

The first full Desktop run had the UI-context test failure described above (252 passed, 1 failed). After correcting that test's execution context, the complete suite passed. No assertion was removed or relaxed.
