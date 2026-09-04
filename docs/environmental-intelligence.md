# Environmental Intelligence architecture

Environmental Intelligence is a shared `Noctaxis.Core.Environment` subsystem. Consumers request only the layers they need through `ILocationEnvironmentService`, or use the focused provider interfaces for high-volume work such as horizon profiles and saved-location settlement rasters. A thumbnail request does not fetch aurora or VIIRS data, and a horizon request does not fetch settlement data.

## Source roles

| Source | Noctaxis role | Initial provider |
| --- | --- | --- |
| Mapzen/Tilezen Terrain Tiles / Terrarium PNG | Canonical raw terrain elevation | `ITerrainElevationProvider` |
| ESA WorldCover 2021 v200 | Surface classification and land-cover context | `ILandCoverProvider` |
| WSF 3D v02 | Building fraction and average building height | `ISettlementDataProvider` |
| VIIRS night lights | Artificial-light radiance | `ILightPollutionProvider` |
| NOAA SWPC OVATION and planetary Kp | Global aurora intensity and geomagnetic activity | `IAuroraProvider` |
| OSM / Overpass | Lightweight road and waterway vectors only | existing `IMapFeatureDataService` |

Terrarium is sampled once on its Web-Mercator grid and remains the sole elevation source. `TerrainSurfaceResolver` combines raw Terrarium values with WorldCover classification before observer and horizon calculations. Negative non-water land remains negative. Positive permanent-water elevation is retained; negative permanent-water bathymetry is resolved to approximate mean sea level while its raw value remains diagnostic. Missing classification retains raw Terrarium rather than fabricating a water surface. WSF height is building-specific context and is not added to terrain elevation.

WSF 3D is acquired through DLR's catalogued WCS coverages
`land__WSF3D_V02_BUILDINGFRACTION` and `land__WSF3D_V02_BUILDINGHEIGHT`.
The public direct-download directory is not treated as a predictable tile API, and WMS
portrayal rasters are not used as scientific inputs. Each one-degree request is accepted only
when its GeoTIFF is a georeferenced EPSG:4326 single-band scientific raster with the expected
sample type, bounds, nodata value and numeric range. Building Fraction is converted from
0..100 percent to 0..1, while stored Building Height is converted immediately with its documented
0.1 gain (`142` becomes `14.2 m`). Raw stored values never enter settlement rendering.

## Horizons

`IHorizonService` uses a progressive fixed-slot profile and samples the sole Terrarium
provider through the central surface resolver on one shared geographic coordinate grid. WorldCover
classifies the complete grid in one grouped batch. The default reusable profile contains 360 bearings
with 437 adaptive radial samples: 15 m through 1 km, 40 m through 5 km, 100 m
through 20 km and 250 m thereafter to 50 km. Each `TerrainHorizonSample` carries
the terrain angular-horizon value, feature distance, optional WorldCover classification,
and the retained radial sightline needed to calculate base terrain obstruction and optional
target occultation.

Observer sightlines start at the resolved physical surface elevation plus 1.7 m camera height,
added exactly once. A manual ground override remains supported. If Terrarium is
unavailable, the caller-supplied unresolved fallback is explicit. Earth curvature subtracts `distance² / (2R)`
using the standard 7/6 effective-Earth-radius refraction model.

After an observer change, the base camera bearings and both interpolation
neighbours are priority work. That partial profile is production quality and
can drive terrain hatching while bounded background workers fill the remaining
one-degree slots in 24-bearing chunks. A later camera rotation claims only
uncomputed slots; completed slots are never discarded. Radial distances,
inverse distances, curvature drops, angular-distance trigonometry and bearing
trigonometry are precomputed once. The radial hot loop compares slopes and
converts only each winning slope to a horizon angle.

The terrain angular horizon is the maximum apparent terrain angle. The
base plan-view obstruction is the first horizontal intersection
or meaningful rising-terrain boundary and is independent of camera pitch.
Target-altitude occultation is a separate query against the same profile.

`IPlannerEnvironmentService` combines the horizon, observer elevations,
WorldCover and a bounded WSF observer sample into one immutable snapshot cached
by observer. Bearing, FOV, time, weather and map viewport changes reuse it.

## Shared source-tile cache

Static environmental source tiles live below the standard Noctaxis application-data root:

```text
EnvironmentalData/
  {source-id}/
    {source-version}/
      {layer}/
        {tile-id}.{extension}
```

The cache is global to the application, not keyed by saved-location ID. Nearby locations therefore reuse overlapping Terrarium, WorldCover and WSF tiles. Writes are staged to a unique temporary file, validated, then atomically moved into place. Invalid cached files are rejected and reacquired; cancellation propagates; retry is bounded for HTTP 429 and transient server errors.

The WSF cache uses deterministic one-degree chunks internally even though those names are not
remote download URLs. Its `v02-wcs-scientific-v1` cache namespace separates validated WCS data
from any older direct-download assumptions. GeoTIFF metadata carries dimensions, CRS, bounds,
sample encoding and nodata; the namespace identifies the source/normalization contract and file
timestamps retain retrieval time. An atomically written adjacent metadata JSON records those
fields plus acquisition source, normalization scale/unit and retrieval time for diagnostics.

The on-disk cache is complemented by source-separated bounded decoded-raster
caches (Terrarium 96 tiles and WSF 8 layer tiles). They use
single-flight decode and approximate LRU eviction, so nearby observer moves
reuse immutable raster grids without permitting unbounded DEM memory growth.

`SavedLocationMaps/{location-id}` remains the home of location-specific source mosaics, vector overlays, metadata, derived thumbnails and a compressed viewport-resampled `settlement-field.bin.gz` used for network-free restyling. Canonical environmental source tiles are never copied there.

## Saved-location settlement rendering

Production generation now follows:

```text
WSF Building Fraction + mildly bounded Building Height
  -> Web-Mercator viewport resampling
  -> deterministic settlement mass field
  -> deterministic connected-component ranking
  -> pin-containing (or nearest) main component
  -> covariance/PCA main and satellite geometry plus density maxima
  -> V1 positive-light ambience, zoning, hierarchy, clouds, wisps and falloff
  -> deterministic three-field chromatic illustrative stars
  -> existing OSM roads
  -> existing OSM waterways
  -> positive-light tonemapping
  -> location pin
```

Synthetic stars are seeded with SHA-256 from WSF dataset/version/cell identity, location ID, viewport and the canonical style identity. They are stable for identical input but do not claim to be literal buildings. The glow compositor uses screen-additive light only; it has no subtractive darkening, road-colour masks, trenches, shadow masks, black cluster outlines or pin cut-out. The immutable default preset is embedded from `Styles/noctaxis_galaxy_style_v1.json`.

Bulk OSM building requests are no longer registered in the production dependency graph. They were expensive, unnecessarily detailed for a density visualisation, and prone to Overpass rate limiting. Obsolete per-location building artefacts are not consumed by the application. OSM road and waterway acquisition, cache compatibility and drawing remain active.

Thumbnail metadata schema 9 records WSF provider/version/status, explicit settlement-rendered state,
styled-input identity, renderer version, preset version and canonical settings hash. Settlement derivative
schema 2 guarantees normalized fraction and height-in-metres fields; schema 1 derivatives are
ignored without deleting user or map data. Thumbnail style version 14 invalidates old derived
images without invalidating the reusable map or OSM-vector inputs.

Settlement status preserves the distinction between valid populated coverage (`Available` or
`Cached`), valid zero-fraction coverage (`Empty`), an explicitly absent upstream tile
(`TileAbsent`), network/service failure (`SourceUnavailable`) and a structurally or scientifically
invalid response (`InvalidRaster`). All failure states still produce a usable saved-location map
without fabricated settlement light or an OSM-building fallback.

## Current foundation status

- WorldCover: functional tile acquisition, shared caching and one grouped
  selective GeoTIFF-block classification pass per terrain profile. Full 3-degree
  10 m rasters are not expanded into application memory. An explicitly absent
  tile inside the mapped latitude extent identifies open ocean; service failures
  remain unavailable.
- Planner: current-coordinate terrain state and directional terrain obstruction
  feed the compact sidebar. Thirteen
  bearings across the horizontal FOV feed a bounded radial lookup texture used by
  one Skia runtime-effect draw. The geographic cone remains full length; terrain
  hatching and weather desaturation stay independent and composable. Pan and zoom
  update only renderer transforms and never regenerate terrain or hatch geometry.
- VIIRS: real provider/model boundary with explicit unavailable status until a composite is acquired; no fabricated radiance.
- NOAA SWPC: live OVATION nearest-cell intensity and separately labelled planetary Kp, with timestamp models and a short memory expiry. Kp is not presented as local aurora probability.
- Location transfer: versioned saved-location JSON with full location/planning metadata. Environmental tiles and thumbnails are intentionally excluded and are rebuilt through normal services after import.

Future UI should display availability from provider results rather than treating provider failures as page-opening failures.
