# Planner map: terrain relief and environmental composition

The Planner map shows terrain relief, a restrained hillshade drawn from the Terrarium elevation data
that the terrain calculations use. Terrain and light pollution form an Environmental group between the
basemap's geography and its roads, boundaries and names. They change how the land looks without
covering the reference information.

## Composition

`MapComposition` orders registrations by semantic group, then by explicit order within the group, then
by id. `PlannerMapComposition` positions every native layer from that order: a registration's native
layers sit above those of every registration ordered before it. Nothing inserts at a fixed index.

| Group | Registration | Drawn by |
| --- | --- | --- |
| Basemap | `basemap` | the basemap's geography pass: land, land cover, water, buildings |
| Environmental | `terrain-relief` (order −20) | `TerrainReliefTileLayer` |
| Environmental | `light-pollution` (order −10) | `LightPollutionTileLayer` |
| Reference | `basemap-reference` | the basemap's reference pass: boundaries, roads, railways, labels |
| Planning | `environmental-shading`, `camera-framing`, `celestial-rays` | the map view's overlay |
| Interaction | `observer-pin` | the map view's overlay and input |

The physical order was previously: the whole vector basemap as one tile layer, then the light-pollution
layer, then the overlay control. Light pollution therefore covered roads and labels. It is now:
geography, terrain relief, light pollution, reference, then the overlay control.

`environmental-shading` used to be in the Environmental group, but it is the camera sector's
weather-and-terrain fill, drawn by the overlay together with the framing. It is now classed as Planning,
which is where it has always been drawn.

### Splitting the vector basemap

Mapsui draws OpenFreeMap as raster tiles, but Noctaxis renders those tiles itself from the vector
data (`BasemapTileRenderer`). So the split happens in the renderer, not with a filtered copy of a
style. `BasemapPass.Geography` draws the opaque background, land cover, water and buildings.
`BasemapPass.Reference` draws boundaries, transport and labels on a transparent background.
`BasemapPass.Full` draws both.

Every feature is drawn in exactly one pass, and reference over geography reproduces the full tile. Both
Planner layers share one `ThemedVectorTileSource`, so each vector tile is fetched and decoded once.
Attribution is unchanged. Buildings stay with the geography: drawn above the environmental layers they
would hide them across every town.

A basemap that cannot be split supplies no reference layers (`IMapsuiBasemap.CreateLayerSet` defaults
to the whole basemap as geography). Satellite imagery is such a basemap.

## Elevation source

`TerrariumTerrainProvider` implements `ITerrariumTileSource.GetTerrainTileAsync`, which returns
whole tiles along exactly the path elevation sampling takes:
- decoded-tile memory;
- failure back-off;
- `EnvironmentalTileCache`, the `TerrainDiskCache` governance and the download.

Production registers a second provider instance for the map, built with the same HTTP client
configuration, options and `IEnvironmentalTileCache`. A tile is downloaded and stored once, whichever
side asks first. Only decoded tiles are held separately, so browsing the map never evicts a tile a
horizon calculation is using. Map tiles count towards the user's terrain cache limit and are cleared
with it.

Relief is never shaded deeper than `DetailZoom`, the zoom the calculations sample (12). Deeper map
zooms enlarge the zoom-12 relief. This keeps the map on the terrain the calculations use. Terrarium's
deeper tiles are, over much of Britain, coarser sources enlarged in steps, and shading turned those
steps into visible blocks at scouting zooms.

The calculations themselves (sampling, horizon, obstruction, camera height, minimap, terrain frame)
are unchanged, and their regression fixtures pass unchanged.

## Hillshade

`Noctaxis.Core.Terrain.TerrainRelief` works in these steps:

- **Gradient.** It uses Horn's 3 × 3 gradient. The cell size is the true ground distance per pixel at
  each row's latitude (Web Mercator is conformal).
- **Light.** The light is a conventional synthetic sun in the north-west at 45°.
- **Output.** The output is relative to flat ground: 0 on level land, positive on slopes facing the
  light, and negative on slopes facing away. Flat land (the Fens) gains no texture.
- **Exaggeration.** A capped vertical exaggeration, 1.3^(12 − zoom) between 1 and 4, keeps hills
  recognisable at regional and national scale. This is not a user setting.
- **Sea level.** Elevations below sea level are raised to it. Terrarium includes bathymetry, and relief
  is about the land.
- **Invalid data.** A cell next to an invalid elevation stays flat.

`TerrainReliefTileSource` draws the relief as translucent white (lit) and black (shaded) over
transparency, capped at 70% alpha. The image needs no colour and does not depend on the theme, and it
reads under every palette and colour-vision mode. The theme adjusts only the layer's opacity (see
below).

### Tile edges

Each tile is shaded from a 258 × 258 grid padded with one sample from each of its eight neighbours
(`TerrainRelief.Pad`), so the gradient at an edge is the gradient of the land there.
- Longitude wraps.
- Beyond a pole, or where a neighbour is unavailable, the edge is extrapolated linearly. This continues
  the tile's own gradient rather than inventing a cliff or a flat rim.

Tests check that adjacent tiles meet without a step and that a plane shades uniformly to its edges.

### Caching

- **Derived relief.** Shaded grids are cached in memory, keyed by tile, algorithm version and
  terrain-cache generation. At most 192 grids are kept, about 48 MiB.
- **Clearing stored terrain** advances the generation, and derived relief from before it is discarded.
- **Rendered images** are held by the Mapsui tile layer's own cache.
- **No duplication.** Raw elevation is never copied.

## Behaviour

- **Street basemap:** relief is shown by default at 60%, beneath light pollution and the reference pass.
- **Satellite basemap:** relief is not drawn. Imagery already shows the terrain, and hillshade beneath
  opaque imagery would be invisible. `MapLayerDefinition.ShownOverImagery = false` gates it off
  whenever a satellite basemap is active, without changing the user's choice. The Layers panel says so.
  No satellite basemap is currently offered in the UI; the behaviour is tested with
  `RasterImageryBasemap`.
- **Light pollution:** the default opacity is now 10% (previously 50%). This is visible at regional and
  national scale, especially with a colour palette, and it leaves room for relief beneath. A stored
  opacity is kept as saved.
- **Themes:** `MapThemeTokens.TerrainReliefStrength` scales relief opacity. It is 1 normally and 0.5 in
  High Contrast, so land, water and the reference layer keep their full contrast. Roads, labels,
  planning lines and markers are all above the relief in every theme.
- **Controls:** Planner → Layers has a Terrain relief checkbox and an Opacity slider, consistent with
  light pollution. The choice is saved in `AppSettings.TerrainRelief`.
- **Export:** a plan image's map is the clean themed basemap with no Planner overlay or layer, so terrain
  relief is not part of it, consistent with the other map layers (see `docs/plan-images.md`).

## Failure and performance

Relief is supplementary. The following simply leave no relief:
- a missing, failed or corrupt tile;
- an exception from the source;
- a tile the map has left.

None of these throws. The map does not remember a null tile, so it is requested again when next in
view.

`TerrainReliefTileLayer` records the latest viewport. It abandons a tile before loading neighbours or
shading once the map has panned away from it or zoomed out past it. Coarser tiles, which the map shows
while zooming, stay wanted.

All decoding, shading and encoding run off the UI thread. PNG encoding uses fast settings.

Measured by the capture harness on the development machine, under parallel load:
- shading, including padding, takes about 8 ms per tile;
- encoding took about 25 ms with default PNG settings and about 6 ms with the fast ones.

`TerrainReliefTileSource.Metrics` reports computations, cache hits, abandoned and unavailable tiles,
and times.

## Verification

- **Core tests:**
  - `TerrainReliefTests`: flat ground, direction, steepness, bounds and invalid data, padding, seam
    continuity, sea level, exaggeration.
  - `TerrariumTerrainProviderTests`: map tiles share the calculations' download and disk cache;
    generation; key validation.
- **Desktop tests:**
  - `TerrainReliefMapTests`: group order, and native order independent of installation order; basemap
    replacement; satellite gating; source reuse and caching; enlargement beyond the detail zoom;
    failures; abandonment; the neutral image; preferences, theme strength and attribution; the Layers
    panel.
  - `TerrainReliefSettingsTests`: persistence, loading into a map bound before settings, the
    light-pollution default.
  - `VectorBasemapTests`: the geography and reference passes, and their composite equalling the full
    tile.
  - `TerrariumPlannerIntegrationTests`: production DI shares the elevation cache.
- **Captures:** `TerrainReliefCaptures` is an opt-in visual harness. Set
  `NOCTAXIS_TERRAIN_RELIEF_CAPTURES` to an output folder. It composites the real layers for mountainous,
  rolling, flat, urban and dark locations at local, regional and national zooms, in several themes.
  It uses temporary caches and reads an installed light-pollution atlas without writing.

## Left for later

- Relief is shaded at 256 samples per tile and enlarged on high-DPI screens. It is soft rather than
  sharp at the closest zooms, which suits the source data.
- Terrain relief has no satellite mode, because no satellite basemap is offered yet.
- Buildings are geography, so light pollution and relief tint them.
