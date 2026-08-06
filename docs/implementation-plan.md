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
7. SRTM `.hgt` support is implemented directly: filename lookup, resolution
   detection, big-endian signed samples, bilinear interpolation, cancellable
   ray tracing, optional curvature, and memory/disk-independent profile cache.
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
5. Add flat and SRTM terrain providers with asynchronous profile generation
   and terrain-adjusted crossings.
6. Add Open-Meteo field configuration, geographic caching, forced refresh, and inspector states.
7. Add PNG save/clipboard scouting-card export, embedded metadata, and settings dialogs.
8. Complete deterministic tests, documentation, accessibility/error states,
   restore/build/test, and a smoke launch.

## Explicit constraints

- No web server, database, accounts, telemetry, mobile target, WebView, or 3D.
- Weather and terrain remain optional; missing credentials/data are visible
  normal states rather than startup errors.
- Network access is used only for map tiles and Open-Meteo calls.
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
