# Planner map theming

The Planner map takes its colours from semantic theme roles. Map code names a role
(`Themes/MapThemeTokens.cs`); each theme family's Light and Dark palettes supply the values.
The composition stack (Basemap, Environmental, Planning, Interaction), the physical render order
and all visibility/settings gates are unchanged.

## The street basemap is themed vector data

The street basemap used to be OSM raster tiles: the same bright image in every theme. A raster
cannot follow a theme without destructive tinting, so the street/topographic basemap is now
vector data that Noctaxis draws itself.

| Part | Implementation |
|---|---|
| Data | OpenFreeMap vector tiles, OpenMapTiles schema, OSM data, max zoom 14. The versioned tile URL is read from `https://tiles.openfreemap.org/planet` (TileJSON). |
| Fetch/cache | `OpenFreeMapTileFetcher`: gzip-aware and de-duplicated. Tiles and the last TileJSON are kept in `BasemapCache/openfreemap/<tileset version>/`, so viewed areas draw offline. The previous tileset is removed once a newer one is seen. 404 means an absent tile (bare land); other failures are retried later. |
| Decode | `VectorTile`: a small in-repo Mapbox Vector Tile (protobuf) decoder. Mapsui 5.1, Mapsui.Tiling/Nts and BruTile 6 have no vector-tile decoding or style evaluation, so no package could provide it. |
| Draw | `BasemapTileRenderer`: SkiaSharp at 2x pixel density, from a `BasemapStyle` (semantic roles only, no family names). Zooms beyond 14 enlarge the matching part of the z14 tile. |
| Deliver | `ThemedVectorTileSource` (BruTile `ILocalTileSource`) inside `ThemedBasemapTileLayer` (Mapsui `TileLayer`) at the Basemap slot, so ordering and overlays are exactly as before. |
| Theme switch | `NoctaxisMapView` binds one colour per basemap role. A switch coalesces into one `PlannerMapComposition.ApplyBasemapStyle`, which swaps the style, discards rendered images and re-renders from decoded vector data. There is no download, no layer rebuild and no terrain or environment work. Tiles are tagged with their style version, so a tile rendered just before a switch is hidden and refreshed. |
| Satellite | `BasemapKind.Satellite` with `RasterImageryBasemap`. It is not `IThemeableBasemap`, so imagery is shown exactly as supplied and a later return to the vector basemap adopts the current theme. No satellite provider is registered and no basemap picker exists yet. |

`OpenStreetMapBasemap` (raster) still exists but is no longer the default. Location thumbnails
and the scouting-card export use OSM independently and are unchanged.

## Roles

| Token | Controls | Follows Light/Dark |
|---|---|---|
| `MapLand` | Basemap background; also `MapSurface`, so nothing flashes while tiles load | yes |
| `MapBuiltUp` | Residential, commercial and industrial landuse; aeroway areas | yes |
| `MapBuilding` | Building footprints (z13+) | yes |
| `MapWater` | Water areas, rivers, canals and streams; wetland as a tint | yes |
| `MapVegetation` | Wood/forest, grass, parks and cemeteries (graded opacity) | yes |
| `MapRoadMinor` | Secondary roads down to paths | yes |
| `MapRoadMajor` | Motorways and trunks. Primary roads are 35% of the way from minor to major, so the family hue marks the hierarchy rather than flooding the map. | yes |
| `MapRoadCasing` | Road outlines (major from z10, minor from z13) | yes |
| `MapRail` | Rail and transit (dashed) | yes |
| `MapBoundary` | Country and state boundaries (dashed) | yes |
| `MapLabel`, `MapLabelHalo` | Place, peak and POI names with halos | yes |
| `MapWaterLabel` | Sea, lake and bay names (italic) | yes |
| `MapPoi` | Peak and POI markers. POIs from z16, top-ranked only, 12 per tile. | yes |
| `MapSurface` | NoctaxisMapView background and Mapsui `Map.BackColor` (Mapsui paints an opaque white canvas by default) | yes |
| `MapChromeBackground`, `MapChromeBorder`, `MapChromeText` | Attribution; `Border.mapChrome` (refresh strip, pin preview); `Button.mapChrome` (Toggle inspector, Layers); minimap card border | yes |
| `MapPinFill`, `MapPinOutline` | Planner pin, and the minimap observer | no |
| `MapPinActivity` | Pin loading arc over a `MapPinFill` halo | no |
| `MapViewFill`, `MapViewStroke`, `MapBearing` | Minimap field-of-view wedge and bearing line | no |

Colour-vision variants re-author only the roles that lose a hue distinction for their deficiency
(water, vegetation and built-up areas, and two symbols); see `docs/colour-vision-accessibility.md`.
Celestial rays, framing edges and the environmental cone take their colours from the celestial roles
(`CelestialThemeTokens`), which follow the theme and colour-vision mode.

Family intent for the dark basemaps:

- **Noctaxis:** neutral blue-black land with restrained purple on trunk roads and borders.
- **Neon:** neutral land and teal water, controlled cyan trunk roads, fuchsia dashed borders and POIs.
- **Foxfire:** neutral charcoal land with muted copper trunk roads and cool cyan borders.
- **Deep Space:** indigo roads and starlight-gold POIs.
- **Moonlit:** slate throughout.

Light basemaps use pale land with darker roads (casings carry them) and dark labels. High Contrast
prioritises legibility: black/white land, amber (dark) or near-black (light) major roads, and
maximal label contrast.

## Limitations

- Road names along lines are not drawn.
- Labels are placed per tile and only where they fit wholly inside it, so a label that would
  straddle a tile edge is omitted rather than cut.
- Label text uses the platform default typeface, preferring `name:latin`.

## Verification

`MapThemeTokenTests` covers every family, mode and colour-vision variant:

- Roles are complete and authored.
- Every basemap role differs between Light and Dark; dark land is dark and light land is light.
- The canvas matches the land.
- Labels meet ≥4.5:1 on halo and land; POIs ≥3:1, boundaries ≥2:1 and rail ≥1.8:1 on land.
- Areas are distinguishable, and road salience keeps its hierarchy (High Contrast is stricter).
- No accent wash, and land stays neutral.
- The earlier chrome, pin and minimap rules still hold.

`VectorBasemapTests` covers:

- Decoding of every value type, and rejection of truncated tiles.
- Pixel-exact semantic roles and a live restyle of the same data.
- Zoom gating, overzoom quadrants, and label roles without edge clipping.
- A theme change re-renders with no new fetch or decode, and deep zooms share one download.
- Unavailable tiles are retried; absent and invalid tiles are handled.
- TileJSON template resolution, gzip, the disk cache and offline restart, and 404/500 mapping.
- Live `ThemeService` switches restyle the basemap with identical layers and source, and never
  download a tile twice.
- Satellite imagery is untouched.

Real OpenFreeMap tiles (London z12/z15, northern England z9, Channel z6) were rendered offline in
five themes to tune the hierarchy; native in-app rendering still needs a visual pass.
