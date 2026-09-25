# Local Lorenz numerical atlas — Phase 4A

## Source verification (19 September 2026)

Inspected the publisher's current [viewer JavaScript](https://djlorenz.github.io/astronomy/lp/overlay/dark.html),
[atlas information](https://djlorenz.github.io/astronomy/lp/) and
[scale explanation](https://djlorenz.github.io/astronomy/lp/colors.html).
The numerical directory's GitHub tree (not a clone) contained exactly 2,016 entries,
not truncated, totalling 48,514,648 compressed bytes at inspection. This is an observed
source snapshot, not a download-size promise in the UI. The app never queries GitHub.
All downloads use the publisher's `djlorenz.github.io/astronomy/binary_tiles/2025/` host.
HTTP failures, including 404, are errors; they are never ocean/NoData substitutes.

The viewer uses signed Int8 bytes, `128 * data[0] + data[1]` for the initial value,
row-start deltas against the preceding row start, and eastward deltas within a row.
Each gzip expands to exactly 360,001 bytes for 600×600 samples. The decoder validates
that size, gzip integrity, and rejects short/overlong input. It keeps int32 grid values
without changing signedness, radix or the publisher's reconstruction arithmetic.

The grid covers global longitude and **[-65°, +75°)** latitude, with 72×28 five-degree
tiles. Exactly +75° is outside the publisher's valid row range. Samples are cell-centred:
the first is half a 1/120° cell east/north of a tile's southwest edge. Nearest sampling
matches the publisher's one-based round(local×120 + 0.5) indexing. Bilinear sampling
uses global cell coordinates minus 0.5, crossing file boundaries and the date line.
The outer latitude half-cells clamp to the nearest valid row; coordinates outside the
domain return NoData and render transparent. Neither axis is inverted or shifted.

Numerical conversion is in one place:

```text
LPI = (5.0 / 195.0) × (exp(0.0195 × compressed) − 1.0)
```

LPI is artificial zenith brightness relative to the assumed natural background.
No Bortle conversion or extra sky-brightness metric is introduced. Metadata names David
Lorenz Light Pollution Atlas 2025 and links to the information page. No formal dataset
licence is asserted; the layer's licence metadata remains unset.

## Installation and storage

`LorenzInstallation` uses IUserDataPathProvider, so no Windows-only AppData path is embedded.
The dataset lives beneath:

```text
EnvironmentalData/LightPollution/Lorenz/2025/
  current                       # atomic pointer to a completed generation
  <generation-guid>/
    manifest.json
    overview.f32
    binary/binary_tile_<x>_<y>.dat.gz
```

Install/repair downloads all 2,016 files asynchronously with four workers and a reused
HttpClient. Transient network, timeout, 408, 429 and 5xx failures get up to two retries
with 0.5/1 second backoff. Files use `.part` names until validated. Compressed responses
are size-limited, and cancellation reaches network/decompression/file writes. Progress
reports accepted file count and measured compressed bytes; no estimated size is displayed.

The working generation is retained during repair. A replacement is completely decoded,
validated and given a completed manifest before the `current` pointer is replaced by a
same-directory rename. Cancellation/failure does not publish staging. Old generations
are removed after commit. Recursive removal is restricted to generated direct children
of the dataset directory, with reparse points rejected.

Manifest v1 records provider ID/name/year, signed-int8-delta source format, decoder/import
version, resolution, coverage, expected count, per-file compressed sizes, aggregate
compressed source bytes (`InstalledBytes`), and completion. `overview.f32` adds a fixed
29,030,400 bytes of numerical data. Normal detection parses this manifest and checks
expected filenames/sizes and overview length; it does not download, decompress, hash,
scrape or check for updates. This is structural validation, not full corruption detection.
Repair/reinstall explicitly downloads and validates every tile. A numerical read failure
invalidates availability rather than presenting a damaged tile as zero brightness.

## Numerical and rendering paths

`LightPollutionDataProvider` has no network dependency. Read sessions lease the current
generation, serialize cache access, and keep commit/removal from deleting open files.
The decoded LRU holds at most 24 int32 grids (34,560,000 payload bytes). Repeated reads
reuse these grids. Dispose releases subscriptions and clears retained data after active
read leases finish.

For zooms 0–4, installation creates a **numerical** overview: each 10×10 native block is
averaged in LPI space, producing a global 4,320×1,680 float32 grid at 1/12°. It is loaded
on first low-zoom use, not at startup. This avoids full-dataset decompression and source
tile-cache thrashing for a world view. It is independent of every palette and display
scale. The original compressed files remain authoritative. Zooms 5–8 sample the full
native grid. The overview is a documented low-zoom spatial average, not a normalization
change or a substitute for full-resolution numerical sampling.

`LightPollutionDisplayScale` uses these fixed stops:
0.01, 0.06, 0.11, 0.19, 0.33, 0.58, 1, 1.73, 3, 5.20, 9, 15.59, 27, 46.77.
Stop i maps to i/13. Between adjacent stops, it interpolates in log-LPI space. Values
at/below 0.01 map to zero and at/above 46.77 map to one. No viewport statistics affect
the result. `ILightPollutionColorMap` then maps that scalar; the only implementation is
opaque grayscale with round(255×scalar) in each RGB channel. NoData bypasses the palette
and produces alpha zero.

`LightPollutionRasterSource` implements the installed BruTile 6 async ITileSource API.
Mapsui 5.1 TileLayer consumes 256×256 encoded rasters; existing Skia encodes PNG in a
background task. There are no per-tile Avalonia controls, global bitmap or synchronous
render-callback disk reads. The source schema ends at zoom 8: its 65,536-pixel equatorial
world is about 1.52 samples per native longitudinal cell. Higher Planner zooms reuse
the highest native raster level. This adds no invented source detail or Navigator limit.

The rendered PNG LRU is independently capped at 64 tiles (under roughly 17 MiB for
uncompressed-sized RGBA PNGs). Keys include installation generation, XYZ, display-scale
version and palette ID. Mapsui's own tile cache is capped at 64, separately from numeric
and PNG caches. Numerical overview retention is 29,030,400 bytes. Temporary decode/render
buffers and native graphics cache overhead are additional, bounded working memory.
BruTile's GetTileAsync has no caller cancellation-token parameter; the source supplies
its own lifetime cancellation for background reads/preparation. Removal waits for read
leases, clears both application caches and native cache, and makes the layer unavailable.

## Composition, lifecycle and UI

The reusable NoctaxisMapView/probes retain their Phase 3 baseline stack. PlannerMapView
is a small production composition root which supplies the additional immutable definition.
The layer is Environmental, ordered before the camera-sector semantic registration,
supports visibility/opacity and opts those two fields into preferences. It defaults to
hidden, opacity 0.5 and Unavailable. MainWindow connects its local data service once.

InstallNativeOverlay installs the native layer once above OSM, below the unchanged
floating MapOverlay. Opacity/visibility use the Phase 3 adapter and do not replace the
native layer. Availability and active attribution update through the existing controller.
Attachment-scoped dataset notifications marshal to the UI dispatcher and reject callbacks
from older attachment generations. Cache invalidation does not rebuild composition.
Ordinary navigation performs no HTTP, source-directory scans or attribution recomputation.

Settings → Data identifies the source even while disabled, shows status and measured
progress, and provides source information, Install/Repair/Reinstall, Cancel and Remove.
Visibility and opacity follow the existing Save/Reset editor convention. AppSettings
stores only LightPollutionPreferences; the map restores them through MapLayerPreferences.
Availability/loading/installation progress are not persisted. Startup performs only local
detection. Existing location-card analytical light-pollution services are not repurposed
into new metrics in this map-only phase.

Terrain providers, caches, HorizonService, analytical FoV/obstruction and minimap logic
are untouched. ViewportRedrawScheduler, pin drag/commit, Render-priority invalidation,
render-time viewport reads and the existing activity-only timer remain intact. No pin
experiment was rerun.

### Cleanup failure and ownership fix

The reported broader-run failure was an Avalonia test-session ownership fault. The run
completed 191 tests, then the next `AvaloniaFact` failed during
`HeadlessUnitTestSession.EnsureIsolatedApplication`, before its test body, while the new
compositor tried to register with a render loop owned by another thread. The Phase 4A
dataset-command test had been declared as a plain `Fact`, even though installation change
notifications post status work to `Dispatcher.UIThread`. It could therefore create/use
Avalonia dispatcher state on an ordinary xUnit worker outside the isolated Avalonia test
session. Declaring that test as `AvaloniaFact` keeps the callback and compositor on the
session UI thread. The exact 192-test reproducer then passed; after adding the close
regression, the same broader class set passed 193/193.

The ownership audit also found a genuine production shutdown gap: MainWindow's `Closed`
handler was `async void`, so the window and Avalonia lifetime could finish before the map
binding had waited for active numerical reads and disposed Mapsui resources. MainWindow
now cancels the first close, coalesces duplicate close requests, cancels an active install,
awaits `PlannerMap.ReleaseLightPollutionAsync`, and only then performs the final close.
Final release detaches the installation callback, cancels raster work, disposes raster and
native map caches, awaits the provider's data gate, and clears numerical caches. The data
provider and map binding now expose only `IAsyncDisposable`; their former synchronous
fire-and-forget disposal entry points were removed. A regression test holds a real read
lease across two close requests, proves close cleanup remains pending, releases the lease,
awaits shutdown, and then removes the generation directory successfully. No sleep or
timeout increase is used to make cleanup succeed; the five-second `WaitAsync` is only a
deadlock guard on the asserted completion task.

## Phase 4B seam

Add LUT-backed implementations of ILightPollutionColorMap with distinct/versioned IDs
for Turbo, Viridis, Inferno, Magma and Cividis. Feed the selected implementation to the
raster source and invalidate only visual/native raster caches when selection changes.
Source files, the numerical overview, decoded grid cache, geographic interpolation,
installer and LPI conversion need no changes. No palette selector or other palette has
been implemented in this phase.

## Verification

Deterministic tests cover full synthetic signed decoding, malformed gzip/payload sizes,
LPI conversion, fixed normalization, grayscale endpoints, geographic boundaries/orientation,
date-line interpolation, bounded numerical retention, transparent NoData raster output,
offline cache use, full fake-HTTP installation/retry/progress/concurrency, cancellation,
failed/successful replacement, structural startup validation, removal/cache clearing,
map registration/order/state/attribution, Save/Reset/disk persistence and command busy/cancel.
Existing synchronization/composition/state tests remain unchanged.

An explicit probe lives at tools/Noctaxis.LightPollutionProbe. `--parity` reads three
already-downloaded source files and checks 15 samples against a literal port of the
publisher's JS loops. `--install <directory>` explicitly installs the full atlas and
records numerical samples and cold/cached raster timings. Neither is part of normal tests.
The real dataset and downloaded source snapshots are ignored artifacts, not bundled assets.

## Final results (19 September 2026)

The explicit real installation completed with `Complete: true`, all 2,016 expected binary
files, all 2,016 manifest entries, 48,514,648 measured compressed source bytes, and the
29,030,400-byte numerical overview. The installed atlas then sampled:

| Location | Nearest LPI | Bilinear LPI |
|---|---:|---:|
| London (51.5074, -0.1278) | 76.027958899 | 77.104366284 |
| Rural Wales (52.25, -3.75) | 0.158130565 | 0.158130565 |
| York (53.96, -1.08) | 11.446692470 | 11.402811608 |
| 0° boundary west (51.5, -0.00001) | 57.858205350 | 55.398559910 |
| 0° boundary east (51.5, +0.00001) | 55.644189274 | 55.394637948 |

The close west/east bilinear values demonstrate continuity across the tested source-file
boundary. A literal port of the publisher's JavaScript decoder matched the production
decoder at all 15 checked coordinates across real `1_1`, `36_24`, and `72_28` source
tiles, including negative signed-byte deltas, row starts, interior cells and edges.

The generated world (`london-z0.png`) and zoom-8 (`london-z8.png`) rasters were visually
inspected. The world output showed the expected global black-to-white distribution and
transparent out-of-coverage latitude; the zoom-8 output showed a continuous grayscale
brightness field. The low-zoom image came from the numerical overview, so this inspection
does not introduce a palette-dependent overview.

Measured probe timings for raster preparation were 219.7384 ms cold / 1.7151 ms cached at
z0, 41.0541 / 0.0271 ms at z4, and 116.1525 / 0.0399 ms at z8. These are single-run local
tile preparation measurements. They are not native map pan/zoom latency, sustained frame
times, GPU timings, or monitor frame-pacing evidence.

Release verification completed as follows:

- Phase 4A focused: **47 passed, 0 failed, 0 skipped**.
- Planner/map/window lifecycle: **9 passed, 0 failed, 0 skipped**.
- Broader cleanup reproducer: **193 passed, 0 failed, 0 skipped**.
- MapLayerState / MapComposition / overlay synchronization / viewport / pin: **28 passed,
  0 failed, 0 skipped**.
- Full desktop project: **775 passed, 0 failed, 0 skipped**.
- Full sequential solution: **1,106 passed, 0 failed, 1 skipped** (331 Core + 775 Desktop;
  the existing opt-in live Terrarium integration test was skipped).

The preserved Phase 3 files for map composition/state tests, overlay synchronization,
viewport scheduling and terrain providers still match their pre-Phase-4A SHA-256 snapshot.

Known limits remain deliberate: latitude +75° is outside the publisher's grid; low zooms
use the documented 10×10 LPI-space average; startup validation is structural rather than
a full decompression/hash pass; the publisher does not state a formal dataset licence;
and no native pan/zoom frame-performance capture was made. Only grayscale exists. Phase 4B
colour maps and selection remain unimplemented.
