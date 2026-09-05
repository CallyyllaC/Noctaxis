# Terrain data

Noctaxis has one canonical elevation pipeline: Mapzen/Tilezen Terrain Tiles in
Terrarium PNG format from the public `elevation-tiles-prod` S3 bucket.

## Provider contract

`TerrariumTerrainProvider` owns geographic tile addressing, acquisition,
persistent caching, PNG validation/decoding, bilinear sampling and raw-elevation
diagnostics. `TerrainSurfaceResolver` combines that one geometric DEM with the
existing ESA WorldCover classifier before `HorizonService`. WorldCover supplies
classification only; it is not an elevation provider. There is no
user-selectable DEM directory, secondary elevation DEM, provider fallback, or
hidden provider-specific path in the ViewModel or renderer.

Terrarium tiles are 256 x 256 Web-Mercator PNGs. Noctaxis uses zoom 12 by
default. At 53 degrees north this is about 23 metres per pixel; resolution varies
with latitude. Tilezen states that no new information is added after zoom 15.

Elevation is decoded exactly as:

```text
(R * 256 + G + B / 256) - 32768
```

Negative Terrarium values are valid raw data. The dataset is a composite
bare-earth product and includes bathymetry. Noctaxis never classifies water by
elevation sign: WorldCover non-water retains the raw elevation, including
negative land. Permanent-water samples at zero or positive elevation retain
their Terrarium elevation, preserving elevated lakes and reservoirs. Negative
permanent-water samples are resolved to approximate 0 m MSL for visibility,
while their raw bathymetry remains in diagnostics.

WorldCover 2021 covers mapped land areas rather than publishing empty open-ocean
tiles. An explicit WorldCover tile HTTP 404 within its mapped latitude extent is
therefore represented as `Ocean`; acquisition failures remain unavailable. A
class-80 pixel inside a supplied tile is `PermanentWaterUnspecified`. The model
already accepts explicit `Ocean` and `InlandWater` kinds, but no separate global
water-connectivity dataset is currently present. Consequently, a negative
class-80 inland-water pixel can still be conservatively treated as bathymetry;
an explicit future inland-water classifier can change that in the resolver
without changing `HorizonService`.

## Cache and failures

Tiles are stored under the normal application data root:

```text
EnvironmentalData/mapzen-terrarium/elevation-tiles-prod-undated/z12/{x}-{y}.png
```

Concurrent requests for one tile share acquisition and decoding. Downloads are
bounded, staged, validated and atomically promoted. A bounded 96-tile decoded
cache prevents horizon sampling from reopening PNGs. Missing, unavailable and
corrupt tiles remain explicit unavailable/error samples; they are never
converted into zero elevation.

WorldCover uses the same environmental cache and its existing 3-degree COG
acquisition coalescing. A profile classifies all radial coordinates in one
batch, grouped by classification tile, and selectively decodes each required
COG block once for that batch. It does not perform one WorldCover request or
decode per terrain sample.

Set `NOCTAXIS_TERRAIN_CACHE` only for the command-line terrain probe when a
specific shared cache root is wanted. The desktop application uses its normal
platform application-data directory.

## Sampling and observer height

Sampling is bilinear between pixel centres and crosses adjacent tile boundaries
without seams. If any contributing tile is unavailable, the interpolated sample
is unavailable rather than partially fabricated.

The observer surface value comes from the same resolved result exposed by Planner.
Camera height is added once by `HorizonService`. A manual ground-elevation
override remains authoritative until reset. A moved pin begins with unresolved
terrain state backed by a nullable value. The read-only elevation control stays
empty while resolving; real 0 m remains a valid resolved value. The previous
location elevation cannot leak into the next request. Latitude/longitude, rather
than a proximity bucket, define observer identity. A generation plus coordinate
check rejects late asynchronous results, and horizon cache keys retain the full
coordinate precision.

The existing adaptive horizon grid is retained: 15 m steps near the observer,
then progressively wider steps out to the requested range. Zoom 12 is close to
the common native information scale in the UK, so the first 15 m sample can
interpolate overlapping raster information; this refactor intentionally does
not also change the horizon policy. The developer minimap makes near-field
winning samples visible so a later resolution-aware sampling change can be
evaluated separately.

## Diagnostics

Enable **Terrain debug overlay** in Settings. The overlay deliberately presents
two different views:

- **Local terrain map** is a north-up geographic grid centred on the canonical
  observer. It shows the resolved physical surface, WorldCover water and
  bathymetry adjustments, the camera FOV wedge, selected bearing, winning
  horizon samples, scale and range. The default grid is a bounded 128 x 128
  sample over a 20 km radius. It is sampled in one bulk pass through the same
  production `TerrainSurfaceResolver` used by the horizon service, so Terrarium
  and WorldCover tile decoding and request coalescing continue to use their
  existing caches.
- **Terrain horizon polar view** is the existing circular bearing/distance
  diagnostic derived from the production horizon profile. Its rings are radial
  horizon-profile distance, not map geography.

Changing the observer immediately clears both stale products. Local-map work is
cancellable and guarded by the observer coordinate plus a generation number, so
a late result for an older pin cannot replace the current map. Changing only the
selected bearing or FOV redraws the overlays without resampling the terrain grid.

The copyable text report has separate **Local terrain map** and **Polar horizon
profile** sections, and contains raw Terrarium elevation, WorldCover class,
resolved surface, correction reason, provider/tile/pixel/interpolation details
and the current bearing's winning sample. The UI snapshot also includes observer
coordinates, refresh generation, profile timestamp, selected-bearing horizon,
pitch-independent zero-degree obstruction distance, target altitude and target
occultation. Plan-view hatching uses only that zero-degree geometric obstruction;
astronomical visibility separately compares the target altitude with the terrain
horizon.

The Planner FoV is composited as three independent layers. Its base cone always
extends to the existing fixed 500 km limit. Current-weather visibility changes
the underlying cone to grayscale beyond the reported distance without shortening
it. For each sampled FoV bearing, terrain hatching begins only at the first
intersection of the observer's horizontal ray and continues on the far side to
the fixed cone edge. Adjacent hit distances are linearly interpolated; a clear
bearing remains clear and is not replaced by a synthetic hit at maximum range.
The local terrain map draws this first-hit frontier as a thin red diagnostic line.
The copied diagnostics include the selected-bearing first hit, weather boundary,
fixed cone maximum, terrain horizon, target altitude and target occultation.

The console harness emits the same
observer summary followed by JSON:

```powershell
dotnet run --project Noctaxis.TerrainProbe -- 53.55865 -0.48052 1.7 50000
```

Official format and service documentation:

- https://github.com/tilezen/joerd/blob/master/docs/formats.md
- https://github.com/tilezen/joerd/blob/master/docs/use-service.md
- https://github.com/tilezen/joerd/blob/master/docs/attribution.md
