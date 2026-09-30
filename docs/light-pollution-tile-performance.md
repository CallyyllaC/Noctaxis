# Light pollution tile performance

The light-pollution layer visibly filled in after the basemap and terrain relief. This records what
was measured, what was responsible, and what changed.

## Method

`EnvironmentalLayerProfile` (Desktop tests) is opt-in: set `NOCTAXIS_LAYER_PROFILE` to an output folder.
- **Layers.** It drives `LightPollutionTileLayer` and `TerrainReliefTileLayer` through Mapsui's own
  fetch interface, four concurrent jobs, as the map runs them.
- **Views.** A 3440 × 1440 (2× device-pixel) view over northern England: initial, pan east, pan south,
  zoom out, zoom in, then back to the start. After that come an interrupted pan (a view overtaken by
  the next one after its first batch) and, for light pollution, a palette change.
- **Light pollution** reads the installed atlas without writing. Each stage is split into:
  - atlas lease wait;
  - source-tile read and decompression;
  - sampling (including the intensity scale);
  - palette mapping;
  - PNG encoding;
  - Mapsui's PNG decode of the published tile.
- **Terrain relief** uses a temporary cache. A first pass fills it over the network. The comparable
  pass is disk-warm with nothing decoded, like the atlas.

Per-tile stage times are CPU time on one of the four workers. Wall times are what the map waits.
Runs vary by up to about ±50% on the development machine, and the first stages include JIT warm-up.

## Before

| Stage | Wall ms | Tiles | Lease wait ms/tile | Sample ms/tile | Palette ms/tile | Encode ms/tile |
|---|---|---|---|---|---|---|
| initial | 3166 | 41 | 116 | 52 | 2.3 | 21 |
| pan east | 807 | 17 | 66 | 31 | 1.6 | 15 |
| pan south | 935 | 16 | 87 | 33 | 1.5 | 24 |
| zoom out | 4687 | 79 | 89 | 34 | 1.5 | 24 |
| return | 674 | 12 | 83 | 34 | 1.5 | 21 |
| interrupted pan | 1256 | – | – | – | – | – |

Other costs were minor:
- Source decompression: 64 ms in total for 18 files.
- Mapsui's PNG decode: 0.01–0.2 ms per tile.

For comparison, terrain relief from a warm disk covered the initial view (137 tiles) in 1082 ms and a
pan in 390–540 ms.

## What was responsible

- **The atlas lease.** Each tile held the atlas read lease (`LorenzInstallation.DataGate`, a
  single-entry semaphore) for its whole generation *and* PNG encode. The four concurrent fetches
  therefore ran one at a time. The lease wait of 65–116 ms per tile was queueing behind other tiles,
  and wall time was the sum of every tile's work.
- **Per-pixel sampling.** Each pixel did four awaited lookups through the LRU cache of decoded source
  tiles.
- **PNG encoding** used the default, thorough compression.
- **Not palette colouring.** Palette mapping was 1.5–2.3 ms of a 55–80 ms tile (about 3%). A GPU
  shader or deferred colouring could not have made a visible difference, so neither was introduced.
  Opacity was already applied by Mapsui at draw time, and is unchanged.

## Changes

- **Lease.** `LightPollutionDataProvider.ReadSession.ReadRegionAsync` gathers the few source tiles a
  map tile needs, under the lease. The lease is then released. The result, a `Region`, is immutable
  source data sampled synchronously, and `Region.SampleGrid` works row and column terms out once
  rather than per pixel. `LightPollutionRegionTests` checks the values are identical to
  `ReadSession.SampleAsync`. The cases cover a Greenwich crossing, a region several source tiles wide,
  the antimeridian, the atlas's northern edge and the overview.
- **Encoding.** PNGs are encoded with fast settings (Sub filter, zlib level 1), as for terrain relief.
- **Palette changes.** A palette change that arrives while a tile is being prepared recolours that
  tile's sampled intensities instead of sampling it again. A palette change in Settings regenerates
  the visible tiles: 184 ms for a 41-tile view, measured.

## After

| Stage | Wall ms | Tiles | Lease wait ms/tile | Sample ms/tile | Palette ms/tile | Encode ms/tile |
|---|---|---|---|---|---|---|
| initial | 649–756 | 41 | 3 | 34–42 (JIT warm-up) | 8–10 | 4 |
| pan east | 160–215 | 17 | 0.1 | 21–37 | 5 | 3 |
| pan south | 130–192 | 16 | 0.1 | 16–35 | 5–6 | 4 |
| zoom out | 427–753 | 79 | 0.3 | 13–25 | 2–4 | 4 |
| return | 53–56 | 12 | 0.1 | 11 | 2 | 4 |
| palette change (41 tiles) | 184 | 41 | – | 12 | 2 | 2 |
| interrupted pan | 128–148 | – | – | – | – | – |

Palette times rise with four workers contending for the CPU; they remain small.

Light pollution now matches or beats terrain relief at every comparable stage:

| Stage | Light pollution (ms) | Terrain relief, disk-warm (ms) |
|---|---|---|
| initial | 649 | 1091 |
| pans | 160–192 | 391–607 |
| zoom out | 753 | 772 |
| return | 56 | 860 |

In the native app, a pan was complete within 300 ms for every layer: the capture 300 ms after the pan
was identical to the settled view. After a zoom, the layer still arriving at 300 ms was the basemap's
reference pass, not light pollution.

## Caching, coalescing and staleness compared with terrain relief

- **Caching.** Light pollution keeps decoded source tiles (24), encoded tiles (64, keyed by palette and
  data generation) and Mapsui's tile cache. Terrain relief keeps shaded grids and Mapsui's tile cache.
- **Coalescing.** Mapsui never has two requests for one tile of a layer in flight, and both sources
  serve repeats from their caches.
- **Staleness.** Mapsui only dispatches tiles for the latest view, so an interrupted pan wastes only the
  four jobs already running. At about 20 ms each after these changes, abandoning them as terrain relief
  does would gain nothing measurable, so no abandonment was added.
