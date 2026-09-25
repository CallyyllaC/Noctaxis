# Light Pollution colour maps — Phase 4B

## Scope and pipeline

Phase 4B adds selectable presentation palettes to the existing local Lorenz 2025
overlay. The unchanged path is numerical source → LightPollutionDataProvider → LPI →
LightPollutionDisplayScale → normalized scalar. ILightPollutionColorMap converts that
scalar to opaque RGB, and the existing background raster source encodes the tile.
NoData bypasses palette evaluation and remains transparent.

The display scale still has the same 14 stops, stop i / 13 mapping, log interpolation,
and fixed 0.01 / 46.77 saturation limits. Grayscale retains rounded 255 × scalar.
No LPI maths, geographic sampling, numerical overview, installer or dataset format
changed. SHA-256 checks against the start-of-task snapshot confirm that the numerical
provider, decoder/format, installer, terrain provider and viewport scheduler files are
unchanged. Terrain and pin systems were untouched.

## Definitions and redistribution

| Persisted ID | Display name | Definition |
|---|---|---|
| `grayscale` | Grayscale | Existing linear black → white mapping |
| `turbo` | Turbo | Google Turbo, Anton Mikhailov's table contributed to Matplotlib |
| `viridis` | Viridis | Standard Smith / van der Walt / Firing table |
| `inferno` | Inferno | Standard Smith / van der Walt table |
| `magma` | Magma | Standard Smith / van der Walt table |
| `cividis` | Cividis | Standard Nuñez / Anderton / Renslow table distributed by Matplotlib |

The five chromatic tables come from the established
[Matplotlib v3.10.8 `_cm_listed.py`](https://github.com/matplotlib/matplotlib/blob/v3.10.8/lib/matplotlib/_cm_listed.py).
The inspected source SHA-256 is
`DDAD3698F5129CEB1792A445371286C08BC9080298E657B3054AEA19C9659EF9`.
All 256 RGB triplets per palette retain the upstream decimal values, rather than
approximating the progression with a few stops. Arrays occupy about 30 KiB of double
payload. Each scalar is clamped, multiplied by 255, and adjacent entries are linearly
interpolated in RGB before rounding channels to bytes. Alpha is always 255.
NaN scalar input maps to the low endpoint; geographical NoData is handled separately.

[Matplotlib's licence](https://matplotlib.org/stable/project/license.html) permits
redistribution with its licence/copyright and a change summary. These are bundled in
`Noctaxis.Desktop/ThirdParty/Matplotlib-LICENSE.txt`, copied to application output, and
referenced by `THIRD_PARTY_NOTICES.md`. The adaptation is table extraction into C# arrays
and interpolation. The upstream licence page records Turbo's direct contribution by
its author. The [BIDS authors](https://bids.github.io/colormap/) also publish Viridis,
Inferno and Magma under CC0. No plotting/scientific runtime package was added.

## Selection, persistence and UI

LightPollutionColorMaps is a six-entry immutable singleton catalogue. It resolves exact,
stable textual IDs; unknown, missing, empty, null or malformed IDs resolve to Grayscale.
The colour-map interface carries ID, display name and version (currently 1). No enum
ordinal or colour-map object is persisted.

Settings → Data → Light Pollution has a named Colour map ComboBox. Its options always
show text. Selection edits the settings draft; Save writes `LightPollution.PaletteId`
and updates the effective map preference. Reset restores the palette draft to Grayscale,
as requested, without changing the saved/effective preference until Save. Other settings
retain their existing Reset behavior. Restart restores the saved palette. Visibility,
opacity and application theme are independent.

Example persisted preference:

```json
"LightPollution": { "IsVisible": true, "Opacity": 0.5, "PaletteId": "viridis" }
```

The optional field defaults to Grayscale for Phase 4A state. A small field converter
consumes non-string JSON values safely, preserving the rest of the user's settings.
Unknown string values are resolved by the desktop catalogue; the next Save writes the
effective stable ID. Option order has no persistence meaning.

## Rendering and cache ownership

Saved preference → NoctaxisMapView styled preference → LightPollutionMapBinding.SetPalette
→ singleton resolution → LightPollutionRasterSource.SetColorMap. Initial configuration
also applies an already-bound preference. The palette is captured once for each tile;
there is no per-pixel string resolution, LINQ, heap allocation, or registry construction.

Only the source's bounded rendered-PNG LRU and the existing LP native TileLayer cache
are cleared. The visual cache key retains installation generation, XYZ and display-scale
version, and now includes stable palette ID plus version. Same ID/version is a no-op.
The numerical LRU, loaded overview, downloaded files, manifest and installation generation
are retained. No MapComposition rebuild or native LP layer replacement occurs on selection.
Basemap and other overlay state and attribution remain unchanged.

Palette changes can race an existing raster job. A small visual-state lock protects the
PNG cache and selected palette. A tile prepared for an obsolete revision retries using
the same numerical lease/cache. Mapsui can also publish a feature *after* the source task
returns: the retained LightPollutionTileLayer (a TileLayer subclass) tags raster features
with their palette revision and excludes obsolete features from painting. If a native
fetch batch is still busy at selection time, the binding clears its cache when the
batch drains and issues the new fetch/redraw once. Otherwise it requests one map refresh
immediately. No sleeps, polling, replacement layers or unowned async tasks were introduced.

Hidden, unavailable or detached layers update the palette and invalidate visuals without
requesting new raster generation. Attachment remains idempotent; queued callbacks check
the attachment generation, and final disposal removes the added DataChanged subscription.
The source now explicitly implements BruTile's `ILocalTileSource` marker: Mapsui's default
fetch path requires this subtype, although Phase 4A's direct probe calls did not exercise
that requirement. The source still has no HTTP dependency.

## Deterministic tests

New tests cover all six palettes' exact endpoint and middle-row RGB checkpoints,
clamping, determinism, alpha, LUT interpolation and zero measured per-pixel allocations;
registry singleton identity, names and fallback; malformed field and actual disk-state
fallback without loss of visibility/opacity; Save/reload/Reset/theme independence;
rendered cache hits, palette separation and same-value no-op; numerical object retention,
overview retention and stable installation metadata; NoData transparency for every palette;
an intentionally blocked pixel mapping concurrent with a palette change; native layer
identity, attribution, other-layer state, redraw count, hidden/unavailable behavior,
detach/reattach; and actual Mapsui native tile fetching before/after selection.

Existing Phase 4A tests and map synchronization tests were retained. New settings-input
coverage is declared for the ComboBox. No unrelated assertions or timeouts were weakened.

## Final Release verification

The Phase 4B focused Release set passed **79/79**. This includes the new palette,
registry, persistence, cache, stale-render, native-layer and Mapsui-fetch tests, the
Phase 4A tests, and the requested map/lifecycle synchronization tests.

The full Desktop Release project passed **798/798**.

The full sequential solution passed **1,129 tests, 0 failed, 1 skipped**:

- Core: **331 passed, 0 failed, 1 skipped**;
- Desktop: **798 passed, 0 failed, 0 skipped**;
- total: **1,129 passed, 0 failed, 1 skipped**.

The one skipped test is the existing opt-in live Terrarium integration test. The
solution run used a verification-only runsettings file with one xUnit worker and
test-collection parallelism disabled. This is necessary because ordinary parallel
Avalonia sessions have an existing infrastructure failure: two parallel Desktop runs
failed during `HeadlessUnitTestSession.EnsureIsolatedApplication` while constructing a
new compositor, before entering an unrelated EnvironmentalOverlay test body. The
failing test changed between runs; each failing test passed in isolation, and the
serialized run passed every Desktop test. No palette assertion or Phase 4B code was
involved in those setup failures, so no unrelated test or timeout was changed.

The installed real atlas remained unchanged: generation `cdf315db843b4bf1b66fdf01188095e4`,
2,016 binary files, manifest and overview (2,018 files total), with the before/after
SHA-256 comparison passing for every file. No source data was re-downloaded. Protected
Phase 3 map/overlay/viewport/terrain files also retain their baseline hashes. There is
no outstanding Phase 4B implementation work required by the verification results.

## Real visual and performance evidence

`tools/Noctaxis.LightPollutionProbe --palettes <existing-atlas-root>` has an HTTP handler
that throws on any request. It requires an existing installation and never imports or
downloads one. Before/after hashes matched for **all 2,018 installed files**: 2,016 source
tiles, the manifest and the numerical overview. **No source data was re-downloaded.**

Ignored output: `artifacts/lorenz-real-install/phase4b/` contains 24 individual PNGs
(world z0, London z8, rural Wales z8, mixed UK z6 × six palettes), four labelled comparison
sheets, six joined adjacent-tile boundary PNGs, and `timings.json`. The run log is
`artifacts/phase4b/probe-final.log`.

All four comparison sheets and all six boundary joins were visually inspected. The
gradients are continuous and recognizable, channels are correctly ordered, world NoData
remains transparent, and the Greenwich join has no visible seam. The same geographic
bright/dark features occupy the same pixels across palettes. High-end saturation remains
the Phase 4A fixed-scale behavior; Turbo's high end is dark red, not white. No unexpected
clipping or transparency change was observed.

Times below are milliseconds, medians of five **cold visual-cache** generations followed
by cached retrieval. Numerical caches and runtime/JIT were warmed to isolate the cost of
switching palettes; these are not cold-process/disk timings. Mapping and encoding columns
are separate seven-repeat medians for the London case.

| Palette | World z0 cold / cached | London z8 cold / cached | Map 65,536 scalars | Encode London PNG |
|---|---:|---:|---:|---:|
| Grayscale | 16.28 / 0.0512 | 47.75 / 0.0146 | 0.292 | 11.20 |
| Turbo | 15.07 / 0.0345 | 53.03 / 0.0153 | 0.731 | 22.88 |
| Viridis | 14.42 / 0.0322 | 52.79 / 0.0129 | 0.742 | 21.82 |
| Inferno | 16.93 / 0.0456 | 56.44 / 0.0144 | 0.734 | 23.01 |
| Magma | 16.16 / 0.0472 | 54.10 / 0.0129 | 0.737 | 22.52 |
| Cividis | 18.70 / 0.0214 | 52.88 / 0.0148 | 0.743 | 20.75 |

The high-zoom difference was investigated: chromatic mapping adds about 0.45 ms per tile,
while coloured PNG encoding takes about 21–23 ms versus grayscale's 11 ms. The coloured
London PNGs are 39,901–51,742 bytes versus grayscale's 20,403 bytes. Compression of the
different RGB image data is the dominant measured extra cost, not expensive palette
resolution or per-pixel allocations. Samples include normal timing noise; these are local
raster preparation measurements, **not map FPS, GPU or monitor frame-pacing measurements**.

## Deliberate limits

There is no gradient editor, map legend, inline selector preview, automatic theme selection,
alternate scale, Bortle conversion, cursor sampling, statistics, atlas updating or basemap
evolution. Optional preview/legend polish was left out to keep the settings change small.
Native interactive window visual QA and pan/zoom frame pacing were not measured; native
tile integration was exercised by deterministic headless tests. Phase 4A's geographic,
structural-validation and low-zoom overview limitations still apply. Work stops at Phase 4B.
