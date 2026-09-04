# Noctaxis implementation plan

## Existing repository

The starting solution contains a single `net10.0` class library named
`Noctaxis.Core`. That project name is retained as the sole domain/backend
layer. The placeholder `Class1` type will be removed when the first domain
models are added.

## Project structure

- `Noctaxis.Core` — immutable domain and session models, astronomy and lens
  calculations, terrain and weather providers, JSON persistence, export data,
  and replaceable boundary interfaces.
- `Noctaxis.Desktop` — Avalonia 12 desktop executable, MVVM view models,
  Mapsui adapter, custom path/horizon rendering, native file/folder dialogs,
  card rendering, and dependency-injection composition root.
- `Noctaxis.Core.Tests` — deterministic unit and integration-style tests for
  calculations, parsers, persistence, terrain, weather, and astronomy.
- `Noctaxis.Desktop.Tests` — focused view-model transition tests with fake
  providers and no UI or network dependency.

## Major decisions

1. All persisted and calculated instants use Noda Time `Instant`; user-facing
   date/time values are resolved through an explicit IANA/Windows-compatible
   time-zone service. Automatic geographic lookup is not claimed. A saved
   location override is used when present and the machine zone is the honest
   fallback.
2. WGS84 latitude/longitude is the only geographic representation in core.
   Mapsui/Web Mercator conversion is isolated in `Noctaxis.Desktop`.
3. Astronomy Engine is wrapped by `IAstronomyService`. Deep-sky horizontal
   conversion uses sidereal time from Astronomy Engine and tested spherical
   coordinate conversion; Sun/Moon ephemerides, illumination, and events use
   the library directly.
4. `PlanningSession` is the coherent input state. `PlanningSnapshot` is an
   immutable calculated result. The main view model debounces expensive daily
   path/terrain/weather refreshes while current-position calculations remain
   responsive.
5. Mapsui's Avalonia-12-specific stable package is used for native desktop map
   input and OpenStreetMap raster tiles. Attribution is permanently visible.
   Tile source and required attribution are fixed to prevent invalid provider
   configuration and attribution removal.
6. The horizon chart is a small native Avalonia custom control: terrain is
   drawn first, then below/above-horizon target paths and event markers. No 3D
   renderer is introduced.
7. The original local-folder SRTM `.hgt` implementation provided filename
   lookup, interpolation and curvature. It is superseded by the Environmental
   Intelligence plan below; terrain decoding remains behind the shared Terrarium provider.
8. Open-Meteo is an `HttpClientFactory` typed client with isolated DTO mapping.
   A testable geographic cache reuses same-hour forecasts within a configurable
   radius for less than ten minutes; forced refreshes bypass and update it.
9. State and saved locations use versioned JSON files in the platform's user
   application-data folder. Writes use a temporary sibling followed by atomic
   replacement/move. Corrupt files are quarantined and defaults remain usable.
10. Scouting cards are PNG-only at the UI. SkiaSharp renders a single byte
    stream used for save and clipboard; an Apache-licensed PNG library writes
    the versioned structured export model into `Noctaxis.ExportData` metadata.

## Delivery sequence

1. Scaffold desktop and test projects, centralise stable package versions, and
   establish a compiling dark-themed shell.
2. Implement the map/time/observer/Sun/Moon vertical slice and path chart.
3. Add an offline OpenNGC-backed deep-sky catalogue and lens/FOV overlay.
4. Add saved locations and planning-session persistence.
5. Add the initial flat/local-SRTM terrain provider and terrain-adjusted
   crossings. This delivery step is historical and is superseded below.
6. Add Open-Meteo field configuration, geographic caching, forced refresh, and inspector states.
7. Add PNG save/clipboard scouting-card export, embedded metadata, and settings dialogs.
8. Complete deterministic tests, documentation, accessibility/error states,
   restore/build/test, and a smoke launch.

## Explicit constraints

- No web server, database, accounts, telemetry, mobile target, WebView, or 3D.
- Weather and terrain remain optional; missing credentials/data are visible
  normal states rather than startup errors.
- Network access is capability-scoped to map/geocoding/weather services and the
  explicitly documented environmental source-tile providers.
  Automated tests never call external services.

## NOC-011 through NOC-019 interaction batch

- Add Locations/Planner/Settings navigation with Locations as the zero-cost
  homepage, reusable location cards, shared geocoding search, and explicit
  Custom-location resolution sources.
- Add lightweight preview coordinates, settled commit, cancellation, and timing logs.
- Persist enabled celestial objects and primary target; search the embedded
  catalogue locally and render common multi-object map/horizon models.
- Replace dense inspector rows with labelled, scrollable, collapsible sections;
  clarify observer elevation and isolate advanced sensor dimensions.
- Add focal presets and preview/commit date/time sliders. Lens edits update FOV
  only; slider previews do not start weather or terrain work.

## NOC-020 through NOC-031 correction batch

- Embed staged General, Weather, and Celestial-object settings directly in the
  Settings tab; remove the obsolete modal and informational Map section.
- Keep the observer pin geographically anchored. Map panning is viewport-only;
  explicit pin dragging previews locally and commits once on release.
- Model device-location availability independently from resolution and disable
  unsupported or permanently denied states without startup permission requests.
- Use one debounced search control for Planner and the cancellable Custom dialog;
  edit saved names and optional notes in a separate validated dialog.
- Import and validate the OpenNGC J2000 CSV catalogue, add local combined
  filters, perform stateless direct coordinate transformation, and enforce a
  persisted maximum of eight visible configured objects.

## Saved-location settlement glow replacement

1. Retain the existing `features.json` road/water acquisition, validation,
   caching, projection, and Skia line styles. The semantic line overlay remains
   independent of settlement data.
2. Supersede the building-star/habitation-point format with a versioned compact
   building-centre document containing OSM type/id, latitude/longitude, building
   kind, and levels. Store shared and per-location centre data under new cache
   identities so schema-v1 housing assets cannot be read as settlement input.
3. Add focused density, geometry, and composition types. Port the reference's
   2x bilinear splat, deterministic building weights, Gaussian scales,
   percentile normalisation, 7x7 binary closing, pin-containing 4-connected
   component, weighted covariance/PCA, component-relative ellipses, and up to
   five separated real-density peaks.
4. Composite only screen-additive halo, body, core, and lobe emissions using the
   reference palette and gains. No settlement operation may subtract, multiply,
   mask, shadow, outline, moat, or colour-detect map pixels.
5. Compose in this order: fully styled base artwork, continuous settlement glow,
   existing roads, existing waterways, and the current-location pin last.
6. Add deterministic, projection/density, component-selection, sparse-input,
   additive-only, semantic ordering, pin ordering, and legacy-cache rejection
   tests. Verify with Release build and the complete deterministic test suite.

## Environmental Intelligence foundation

1. Create a shared `Noctaxis.Core.Environment` subsystem with explicit layer
   availability, capability-scoped requests, source/version identities, a
   common application-level tile cache, and reusable raster sampling.
2. Replace the active user-folder SRTM path with Mapzen/Tilezen Terrarium as terrain
   elevation. It supports point and grouped batch sampling through the shared
   source-tile cache.
3. Add a horizon service that calculates terrain angles/distances and preserves
   explicit unavailable samples.
4. Add WSF 3D Building Fraction/Height sampling and feed a resampled WSF field
   into saved-location settlement geometry. Generate deterministic cosmetic
   stars from dataset/cell seeds; do not use WSF height as added elevation.
5. Retire bulk Overpass buildings from normal saved-map generation while
   retaining the existing OSM road/water query, cache, projection, and drawing.
6. Add WorldCover coordinate classification, a VIIRS provider boundary with
   explicit unavailable results, a global NOAA SWPC aurora/Kp provider, and a
   capability-selective `LocationEnvironmentService`.
7. Add versioned saved-location import/export containing user/planning metadata
   only. Environmental source tiles and derived thumbnails are deliberately
   excluded and rebuilt through normal services.
8. Test Terrarium decoding and boundaries, no-data interpolation, shared atomic
   cache behavior, horizons, WSF weighting/determinism/failure behavior,
   transfer round trips, and existing saved-map refresh/pin/vector behavior.

## Planner terrain and environmental pipeline

1. Expose one Planner environment snapshot containing the observer coordinate,
   Terrarium terrain, ESA WorldCover and WSF 3D availability, observer ground
   elevation, and a reusable 360-degree horizon
   profile. Keep weather and astronomy on their existing independent paths.
2. Retain radial terrain sightline samples in the horizon profile so
   camera visibility can find the first intersection at the camera's current
   elevation angle. Use 360 bearings, a 50 km analysis radius, 250 m radial
   sampling, Earth-curvature correction, and a 1.7 m default observer height
   above sampled ground. Keep these values centralised in the terrain request.
3. Calculate one terrain horizon from resolved physical-surface values. Terrarium
   remains the sole elevation source; WorldCover permanent-water classification
   corrects negative bathymetry without changing negative land or positive water.
4. Batch-classify the complete shared horizon coordinate grid through WorldCover
   using selective GeoTIFF point reads, avoiding per-sample downloads and full
   in-memory expansion of 3-degree 10 m rasters. Associate a bounded WSF sample with the current observer for
   availability and future building hooks; do not add WSF height to terrain.
5. Keep static environmental snapshots keyed by observer location and source
   configuration. Coalesce concurrent tile requests, remove failed in-memory
   loads so they can retry, and isolate terrain failure from other environmental sources.
6. Version Planner refreshes as well as cancelling them. A result may update the
   UI only when it still belongs to the current refresh generation and observer.
   Camera bearing/FOV, date/time, weather, map pan and map zoom reuse the static
   profile; only observer movement requests a different environmental snapshot.
7. Replace terrain-clear/set sidebar placeholders with source description,
   current coordinates, ground and surface states, plus ground, surface and
   effective first-obstruction distances for the current camera bearing.
8. Sample 13 bearings across the horizontal FOV and pass their effective first
   intersections to the geographic overlay builder. Draw a variable diagonal
   hatch region beyond terrain while keeping the full 500 km cone and weather
   styling composable. Do not modify celestial target rays or add map terrain,
   land-cover, building, contour, hillshade or horizon-line visualisations.
9. Add deterministic tests for interpolation, dual/effective obstruction,
   missing-source degradation, observer height, curvature, profile reuse,
   observer invalidation, stale async protection, cache failure, multi-bearing
   FOV sampling and asymmetric hatch geometry; verify the complete Release suite.

## Terrain obstruction correctness follow-up

1. Instrument observer datum selection and each stored radial sightline with the
   source elevations, curvature correction, apparent angles, sample counts and
   horizon-producing feature. Provide CSV/debug exports without continuous UI
   logging.
2. Replace the default 250 metre uniform radial grid with a source-resolution-
   appropriate adaptive grid: 15 m through 1 km, 40 m through 5 km, 100 m
   through 20 km and 250 m through 50 km. Keep a small 15 m observer-cell
   exclusion radius and retain explicit uniform sampling only for deterministic
   tests and specialised callers.
3. Use the same centrally resolved Terrarium/WorldCover physical-surface value in Planner and horizon calculation,
   with camera height added once and an explicit manual override when requested.
4. Separate base plan-view terrain obstruction from astronomical target
   occultation. Horizon angles remain angular maxima; cone/sidebar obstruction
   uses the first non-negative apparent-terrain intersection and never depends
   on target altitude. Target-vs-horizon clearance remains an independent
   diagnostic assessment.
5. Add compact current-bearing horizon angles and datum-confidence text to the
   Planner sidebar. Continue to feed 13 directional base obstructions into the
   existing asymmetric hatch geometry without changing the 500 km cone,
   weather composition or celestial rays.
6. Add deterministic adaptive-sampling, narrow-feature, western-longitude HGT
   and GeoTIFF, datum-disagreement, angular-profile, near/far ridge, bearing,
   camera-pitch and cache-reuse tests. Use the cached Blaenau Ffestiniog tiles
   only for a development diagnostic run, not as a network-dependent unit test.

## Planner observer refresh UX

1. Replace the monolithic Planner loading flag with one generation-scoped
   refresh state containing the astronomy, celestial-overlay, base-camera,
   weather, ground-horizon and surface-horizon work states. Derive staged
   progress, core-ready, partial-ready and failure status from that state.
2. Move the observer pin immediately, invalidate observer-dependent geographic
   geometry immediately, and begin astronomy, static environment and weather
   work concurrently. Only commit results whose generation and observer still
   match the active refresh.
3. Commit astronomy before celestial rays, then commit the base camera sector.
   Allow weather and terrain to enrich that already-visible camera geometry in
   whichever order they finish; optional unavailable/error results resolve the
   refresh rather than leaving it loading.
4. Use an astronomy-only refresh scope for date/time and celestial-selection
   changes, retaining the matching static environmental snapshot. Keep lens,
   bearing and FOV updates local so they resample existing state without a
   terrain request. Preserve drag-preview and single commit-on-release behavior.
5. Replace the footer's tiny indeterminate slider with a non-reflowing staged
   progress strip over the top of the Planner map. Add a subtle pin-centred
   activity arc, with a quieter state once core Planner geometry is ready and
   optional environment/weather enrichment remains.
6. Add deterministic orchestration tests for immediate pin movement,
   generations, stale astronomy/environment rejection, ordered core commits,
   late optional enrichment, provider failure resolution, cached completion,
   pan/zoom and camera-setting isolation, temporal terrain reuse, rapid pin
   placement and drag commit behavior. Verify the full Release suite.

## Unified local-horizon visibility

1. Reuse the cached terrain/surface radial sightlines as the sole elevation input
   for both camera-cone terrain state and selected-target local visibility. Keep
   data acquisition in the Environmental Intelligence providers and expose a
   deterministic, UI-agnostic running-horizon calculator.
2. Represent each bearing as an immutable horizon altitude plus ordered visible
   and terrain-occluded distance intervals. Continue after each obstruction so
   a farther feature can rise above the running horizon envelope.
3. Sample cone edges inclusively using `ceil(FOV / detail)` equal segments. Add
   a persisted 1-45 degree camera-framing setting with a 10 degree default;
   changing it rebuilds only derived cone state and never reloads terrain.
4. Apply the same horizon calculator to current-target azimuth/altitude and show
   astronomical-below, terrain-blocked, marginal, clear, and terrain-unavailable
   states without changing astronomical rise/transit/set events.
5. Keep 500 km as the shared hard result/geometry ceiling, extend the adaptive
   policy with 500 m, 1 km and 2 km long-range bands, and use the standard 7/6
   effective-Earth-radius refraction model.
6. Render every occluded interval through the existing geographic cone overlay,
   tapering between neighbouring angular samples. Strengthen the hatch using a
   contrast-safe underlay plus dark/light luminance stripes so it remains clear
   over desaturated weather regions and varied map tiles.

## Planner sidebar, observer altitude and equipment ownership

### Current source-of-truth audit

- `PlanningSession.Observer.ElevationMetres` is the persisted ground-elevation
  input exposed by the Planner. The terrain engine currently prefers provider
  ground data but does not retain whether the sidebar value is an override.
- `TerrainProfileRequest.ObserverHeightAboveGroundMetres` is the existing camera
  ray-origin height and defaults to 1.7 metres. `HorizonService` adds it once to
  the selected observer ground datum and exposes the resulting absolute camera
  elevation on `TerrainHorizonProfile`.
- `PlanningSession.Lens` is the calculation input for sensor width/height,
  focal length and orientation. `LensCalculator` is the sole FoV implementation.
- `AppSettings.SelectedTimeZoneId` owns global timezone behaviour, while saved
  locations and the active session retain their resolved location timezone for
  remote astronomical planning.
- `MainViewModel.CelestialObjects` is the shared configured/selected target
  collection. Planner and Settings currently expose separate transient search
  models over that same collection.
- Terrain/weather values already have separate view-model properties; only the
  current `Conditions` visual groups them together.

### Implementation sequence

1. Add a small observer-elevation state model with provider ground, optional
   manual ground override and one effective-altitude calculation. Persist it on
   the active session/saved location without changing legacy JSON readability.
2. Move the existing 1.7 metre camera height into `AppSettings`, pass it and the
   optional manual override through the existing environment/terrain request,
   and retain the terrain engine as the only place that raises the ray origin.
3. Add validated camera/lens equipment records to `AppSettings`. Migrate an
   empty equipment collection from the legacy session sensor dimensions and
   focal length using stable defaults, then persist selected profile identifiers
   on the plan while continuing to derive `LensConfiguration` for calculations.
4. Add Settings equipment editors and camera-height input. Bind Planner framing
   to saved camera/lens selections, clamp zoom focal lengths, fix primes, and
   continue deriving read-only FoV through `LensCalculator`.
5. Remove Planner timezone, catalogue search/filter, sensor editing, focal
   shortcuts and overlay toggles. Rename/split the requested accordions without
   redesigning their existing content.
6. Add deterministic domain, persistence, view-model and structural XAML tests;
   rerun targeted suites, the full Release suite and `git diff --check`.
