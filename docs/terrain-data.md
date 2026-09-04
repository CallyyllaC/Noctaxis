# Terrain data

Noctaxis has one canonical elevation pipeline: Mapzen/Tilezen Terrain Tiles in
Terrarium PNG format from the public `elevation-tiles-prod` S3 bucket.

## Provider contract

`TerrariumTerrainProvider` owns geographic tile addressing, acquisition,
persistent caching, PNG validation/decoding, bilinear sampling and diagnostics.
Every consumer receives the same `ITerrainElevationProvider` instance through
dependency injection. There is no user-selectable DEM directory, secondary
surface DEM, provider fallback, or hidden provider-specific path in the
ViewModel or renderer.

Terrarium tiles are 256 x 256 Web-Mercator PNGs. Noctaxis uses zoom 12 by
default. At 53 degrees north this is about 23 metres per pixel; resolution varies
with latitude. Tilezen states that no new information is added after zoom 15.

Elevation is decoded exactly as:

```text
(R * 256 + G + B / 256) - 32768
```

Negative values are valid. The dataset is a composite bare-earth terrain product
and includes bathymetry, so an ocean observer can have a negative provider
elevation. Noctaxis does not classify water by sign and does not clamp negative
terrain to zero or -500 m.

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

Set `NOCTAXIS_TERRAIN_CACHE` only for the command-line terrain probe when a
specific shared cache root is wanted. The desktop application uses its normal
platform application-data directory.

## Sampling and observer height

Sampling is bilinear between pixel centres and crosses adjacent tile boundaries
without seams. If any contributing tile is unavailable, the interpolated sample
is unavailable rather than partially fabricated.

The observer ground value comes from the same provider result exposed by Planner.
Camera height is added once by `HorizonService`. A manual ground-elevation
override remains authoritative until reset. A moved pin begins with unresolved
terrain state, so the previous location elevation cannot leak into the next
request.

The existing adaptive horizon grid is retained: 15 m steps near the observer,
then progressively wider steps out to the requested range. Zoom 12 is close to
the common native information scale in the UK, so the first 15 m sample can
interpolate overlapping raster information; this refactor intentionally does
not also change the horizon policy. The developer minimap makes near-field
winning samples visible so a later resolution-aware sampling change can be
evaluated separately.

## Diagnostics

Enable **Terrain debug overlay** in Settings. The overlay shows a local plan-view
terrain sample map centred on the observer, elevation ramp, camera FOV wedge,
cardinal bearings, winning horizon samples, zoom-12 tile grid, observer tile ID,
range and missing samples. It is built from the production horizon profile and
does not perform independent terrain I/O.

The copyable text report contains observer provider/tile/pixel/interpolation
details and the current bearing's winning sample. The console harness emits the
same observer summary followed by JSON:

```powershell
dotnet run --project Noctaxis.TerrainProbe -- 53.55865 -0.48052 1.7 50000
```

Official format and service documentation:

- https://github.com/tilezen/joerd/blob/master/docs/formats.md
- https://github.com/tilezen/joerd/blob/master/docs/use-service.md
- https://github.com/tilezen/joerd/blob/master/docs/attribution.md
