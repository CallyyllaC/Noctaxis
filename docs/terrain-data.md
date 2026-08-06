# Terrain data

Noctaxis does not download, redistribute, or silently assume elevation data.
Supply a folder of SRTM `.hgt` tiles in **Settings**. The original seven-letter
tile names identify the southwest integer-degree corner:

- `N50W003.hgt` covers latitude 50–51° N and longitude 3–2° W.
- `S01E036.hgt` covers latitude 1–0° S and longitude 36–37° E.

Both common formats are supported:

- 1201×1201 signed 16-bit samples (SRTM3)
- 3601×3601 signed 16-bit samples (SRTM1)

Samples are big-endian and rows run north to south. Noctaxis validates the
exact file length, reads signed elevations, treats `-32768` as void, and uses
bilinear interpolation. A void cell can be interpolated from remaining valid
neighbours; an entirely void neighbourhood is missing coverage.

For each requested azimuth the provider traces great-circle destinations from
the observer, samples elevations, and retains the greatest apparent vertical
angle. Optional Earth-curvature correction subtracts `distance² / (2R)`.
Profiles run in a cancellable background calculation and are cached by observer
and trace settings. The default trace uses 360 bearings, 250 m distance steps,
and a 50 km range.

If the observer elevation is zero and DEM coverage exists, the DEM elevation is
used. A non-zero saved observer elevation takes precedence. Missing files or
coverage produce an explicit flat-horizon state; they do not prevent planning.

Terrain-clear and terrain-set times are intersections between the sampled
astral path and interpolated terrain profile. They are labelled estimates
because both the path and DEM are sampled and SRTM does not represent trees,
buildings, or short-range local obstructions reliably.
