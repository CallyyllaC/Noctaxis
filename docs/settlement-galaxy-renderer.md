# Settlement galaxy renderer V1

The saved-location renderer uses the embedded immutable preset at
`Noctaxis.Desktop/Styles/noctaxis_galaxy_style_v1.json`. Its canonical identity is
the SHA-256 of the canonical serialized preset. The hash is calculated once and
cached with the immutable style instance rather than being repeated for each star.

## Production data and layers

WSF 3D Building Fraction is the settlement-density source. Building Height has a
bounded, mild influence on settlement mass and deterministic star selection. Pass 7
brightness itself uses the selected fixed class gains. WSF cell coordinates plus
dataset/version form stable object identities; stars are illustrative settlement
samples and are not literal buildings. Bulk OSM building requests are not restored.
OSM/Overpass remains the source for road and waterway geometry.

The behavioral pass order follows the authoritative Python bundle: hierarchy,
luminance-preserving colour zoning, luminosity, density-peak hero core, cloud
underlay, positive wisps, shared-field WSF-derived stars, overlapping star chroma,
satellites, density-suppressed ambience, map integration, and positive outer
falloff. Real roads and water are then drawn above astronomy, Pass 13 tonemapping
is applied, and the location pin is drawn absolutely last.

The compositor contains no dark dust, negative halo, subtractive or multiplicative
mask, colour-detection mask, pin patch, or dark cluster outline.

## Determinism and caching

Stable variation uses the first 64 bits (little-endian) of SHA-256 over the seed
namespace, WSF cell identity, location ID, viewport centre/zoom/dimensions, preset
version and settings hash. Raster accumulation and component ranking use fixed
row-major traversal and explicit tie-break ordering.

Thumbnail metadata schema 9 stores renderer identity
`settlement-galaxy-passes-1-14`, renderer version 3, thumbnail style version 15,
settlement-overlay version 8, preset version 1, the canonical settings hash, explicit
settlement-rendered state, and a hash of the actual map/vector/WSF/style inputs.
A renderer/style mismatch invalidates only the derived thumbnail. Card loading
recomposes that stale thumbnail locally from the source map, OSM geometry and
compressed WSF viewport derivative, without making acquisition requests.

## Image-processing acceleration

The density blur, peak-neighbourhood dilation, local tonemapping blur, and hot
bitmap-compositing loops use a shared image-processing backend. On supported
Windows x64 systems, OpenCV supplies its native optimised CPU dispatch and thread
pool for the large kernels. Renderer-owned BGRA buffers are composited directly
instead of crossing the managed/native boundary for each pixel.

Native-library load failures degrade to deterministic managed implementations of
the same kernels, so a missing optional acceleration path does not prevent saved
locations from opening. GPU/OpenCL execution is deliberately not part of the V1
cache contract: device-dependent floating-point paths could make decoded pixels
vary between machines. The acceleration backend therefore is not included in the
style identity, while settlement-overlay version 8 invalidates thumbnails made by the older
pixel implementation without invalidating WSF, road, water, or source-map data.

## Pass diagnostics

Development tools can call `ProcessSettlementDebug(...)` with an explicit output
directory. It writes numbered PNGs for Passes 1 through 13 plus density, broad
density, component labels, hero-core mask, cloud field, star impulses and outer
falloff. Normal thumbnail generation never creates a diagnostic writer or writes
these artifacts.
