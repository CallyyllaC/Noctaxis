# Saved-location preview rendering

The image on each saved-location card is rendered by Noctaxis for the current theme family,
Light/Dark mode and colour-vision mode. It is no longer a fixed-colour picture baked when the
location's map was cached.

## Pipeline

1. **Cached assets:** `LocationMapThumbnailService`. Per location it caches, for its 896×504 zoom-13
   frame:
   - `landscape.bin.gz` (`SavedLocationLandscapeCodec`): water, vegetation and built-up coverage,
     rasterised from OpenFreeMap vector polygons at scene mask resolution (`OpenFreeMapLandscapeSource`);
   - `features.json`: motorway/trunk/primary/secondary roads and rivers, canals and streams from the same
     tiles (`OpenFreeMapMapFeatureDataService`), clipped per tile and joined across tile edges;
   - `settlement-field.bin.gz`: the WSF 3D settlement field (unchanged).

   The tiles come from the shared `VectorTileRepository` the Planner basemap reads, so an area already
   viewed is acquired offline and each tile is read once for both outputs. A frame reads at most 15
   tiles (refused above 24), four at a time, and is all-or-nothing: an unavailable tile, or tiles that
   report no data, fail the acquisition and the previous assets stay as they were.
2. **Scene:** `Mapping/Preview/LocationPreviewScene`. A theme-independent description built from those
   assets once per location.
   - **Area coverage:** water, vegetation and built-up areas from the landscape. Locations saved before
     metadata schema 10 have an OpenStreetMap Standard raster (`source.png`) instead, still classified by
     colour.
   - **Line work:** roads and waterways, in pixels of the source viewport.
   - **Settlement:** a smoothed density field and a widely blurred bloom.
   - **Settlement lights:** deterministic, seeded by the location.
   - **The site:** the saved location, where the instrument is centred.

   `LocationPreviewSceneLoader` builds a scene from a location's directory, checking each file's
   recorded content hash. It caches the result as `preview-scene.bin` (`LocationPreviewSceneCodec`),
   keyed by those inputs: a cold build takes about 150–250 ms, a cached load about 3 ms.
3. **Style:** `LocationPreviewStyle.FromPalette`, from the resolved theme palette. It uses the Planner
   basemap roles (`MapLand`, `MapWater`, `MapVegetation`, `MapBuiltUp`, the roads, `MapLabel`) and three
   preview roles in `LocationCardTokens`: `LocationPreviewGlow` (settlement light),
   `LocationPreviewHaze` (the lens edge) and `LocationPreviewInstrument` (the family accent; the label
   colour in High Contrast). ThemeService publishes it as the `LocationPreviewStyle` resource.
4. **Atmosphere:** `LocationPreviewAtmosphere`, derived from the scene on first use, in memory only.
   It is seeded by the location, so every theme and export of a place shows the same sky.
   - Two nebula fields: one gathers around the settlement light, one fills the open sky.
   - A background starfield.
   - Up to three meaningful off-site light sources (`LocationPreviewLightSources`), each with a bearing,
     relevance-based strength, angular spread and distance. Lit settlement cells are grouped into
     connected clusters. The cluster at the site, clusters within 12% of the scene height of the site
     and clusters that are too small are ignored. The rest are ranked by relevance: their light, each
     part reduced with its distance from the site. Light the site itself sits in outshines lesser light
     elsewhere, so an off-site source needs at least half its relevance. A source is added only if its
     bearing is at least 90° from every source already chosen, and only while it has at least 15% of
     the strongest source's relevance. Clusters are never averaged into one direction. A cluster's
     bearing is the 30° window with the most relevant light, so sprawl that wraps around the site
     points at its densest part. Spread is the weighted 10th–90th percentile of the cluster's
     bearings, so brightness alone does not change it. A site inside its own light, such as a village
     centre, has no sources.
5. **Renderer:** `LocationPreviewRenderer`. Pure, deterministic Skia with no UI dependency. It draws in
   layers:
   - **Ground:** land and areas.
   - **Atmosphere:** nebula and starfield; not in High Contrast.
   - **Structure:** waterways, roads as navigation traces, settlements as star clusters.
   - **Device falloff:** the lens edge; not in High Contrast.
   - **Instrument.**

   Areas, nebula and settlement light are mixed at scene resolution and drawn scaled; everything else is
   drawn at output resolution. A 2× card preview takes about 30–45 ms off the UI thread.
6. **Card:** `LocationPreviewImage`. It renders off the UI thread at its pixel size, and only when the
   scene, theme or size changes.

## Tone by theme

| Tone | Used for | Treatment |
|---|---|---|
| Dark | Dark families | Deep space. Roads are luminous navigation traces, with a soft track and a fine core. Settlements are star clusters in the family's map point colour, the brightest with small glints, over an additive bloom. The nebula gathers around the lights. Stars show only within the instrument's field, and the edges fall away through a lens. |
| Light | Light families | A tinted rather than white edge, faint tinted cloud, cased roads, and settlements as a wash and fine stipple. It stays a designed artefact, not fog. |
| High Contrast | Both HC palettes | No nebula, stars, bloom or lens. Full-strength cased structure, crisp density, and a solid instrument in the label colour. |

The instrument is the same in every tone:
- a scan ring broken at the cardinals, with bearing ticks every 10° and longer ones every 30°;
- a north chevron;
- a fine inner acquisition ring and short cardinal crosshairs;
- lock-on brackets that mark the saved site itself (there is no map pin in the preview);
- a light sector for each light source: a lit section of the scan ring centred on the source's bearing.
  Its length shows the angular spread (clamped to 14–80°). Strength only changes opacity, the glow
  track and a modest line weight. Each sector has a small outward-pointing caret just outside the ring
  at the source bearing. Sectors use the theme's glow colour, or the instrument colour in High
  Contrast. There is no indicator when there is no meaningful off-site light.

## Accessibility

- **Text safety:** stars and the open-sky nebula thin out through the lens, so nothing bright sits in
  the outer field where card text can overlap the preview.
- **Colour-vision variants:** the map roles already carry the per-deficiency adjustments, so previews
  follow each variant.
- **Distinctions don't rest on hue.** Settlement is glow and points, roads are cased lines, and water
  is an area.
- **Text sits on a card-surface scrim, never on the image.** `LocationPreviewRendererTests` checks
  every theme × mode × colour-vision combination: the name and metadata stay at least 4.5:1 over the
  worst preview pixel under the scrim.

## Export and comparison

`LocationPreviewExport.RenderMatrix` / `WriteMatrix` render a scene for every family × Light/Dark ×
colour-vision mode from the production `ThemeCatalogue`, without a window or screen capture. It is the
starting point for batch export and visual comparison.

## Migration from raster and Overpass data (metadata schema 10)

- Schema 9 assets (`source.png` from OpenStreetMap raster tiles, Overpass `features.json` schema 1) stay
  complete and load offline; bulk "Regenerate map images" (`AcquireMissing`) treats them as held and
  downloads nothing for them.
- A requested acquisition (single regenerate, a moved location, a new save) writes a complete vector
  generation (`SourceFormat = openfreemap-landscape-v1`); `source.png` is deleted only after that
  commit. A failed or cancelled acquisition leaves every file untouched.
- `RefreshFeatures` replaces only `features.json`, so a legacy raster can be paired with OpenFreeMap roads.
- The scene cache key includes the source hash, so a new landscape rebuilds `preview-scene.bin`; theme
  changes never touch assets or the scene.

### Comparison with the raster pipeline (October 2026)

Measured on four real saved locations (old raster + Overpass vs new vector data, same WSF field), plus
new-only urban and coastal frames:

- Lincolnshire farmland, river and village frames: near-identical previews. Mask IoU 0.6–0.7 for
  water and vegetation, 0.4–0.55 for built-up land (vector built-up follows mapped residential land and
  buildings rather than raster grey; generalisation at zoom 13 softens small parcels).
- Snowdonia (woodland, moor, quarry town): raster vegetation 55 % against vector 13 %. The raster
  figure was mostly OpenStreetMap Carto's national-park tint, read as vegetation; its "built-up" was
  largely slate quarries. The vector landscape shows only mapped heath, grass and woodland. This is a
  correction, not a loss, and no fill is fabricated to reproduce it.
- Coast (Embleton Bay) and cities (Hull, Lincoln): the coastline comes from OpenMapTiles' ocean polygons,
  and cities show dense built-up land; no old capture exists to compare against, since that would need the
  retired endpoint.

## Not yet done

- Terrain relief is not part of the scene yet.
- Minor roads are not drawn (the preview keeps its earlier classes); the tiles hold them if wanted.
