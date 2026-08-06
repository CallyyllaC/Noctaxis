# Architecture

## Projects

`Noctaxis.Core` is the single backend/domain library retained from the starting
solution. It owns WGS84 coordinates, Noda Time instants, target/catalogue
models, astronomy and optical calculations, terrain/weather boundaries and
implementations, persistence, planning orchestration, and export rendering.

`Noctaxis.Desktop` is the Avalonia 12 executable. It owns view models, views,
native dialogs, Mapsui/Web Mercator adaptation, and display rendering. It does
not calculate astronomy, parse weather, read DEMs, or persist domain state.

`Noctaxis.Core.Tests` tests deterministic backend behaviour. The small
`Noctaxis.Desktop.Tests` project tests planning-session transitions without
starting an Avalonia lifetime.

## Data and dependency flow

The UI edits one immutable `PlanningSession` containing the committed observer,
UTC instant, timezone, primary target, visible target list, and lens. `PlanningService` resolves the enabled catalogue targets
and coordinates three cancellable operations: daily astral path, terrain
  profile, weather, and Sun/Moon context. It returns one immutable `PlanningSnapshot` consumed by
the map, inspector, chart, and exporters.

Map and date/time interaction use explicit preview state. The planning pin is a
cached Avalonia overlay projected from its committed WGS84 coordinate. Panning
changes only the Mapsui viewport; a pin drag updates preview coordinates and its
release commits one coordinate and cancels obsolete planning work. Date/time sliders preview the primary position
on a background task and commit after settling. Lens edits recalculate only FOV.

Top-level navigation is Locations, Planner, and Settings. `LocationsViewModel`
owns reusable cards and favourite/recent projections without duplicating a
planning session. A shared location-search control and `LocationSearchViewModel`
are used by Planner and the Custom-location dialog. `ILocationResolver`,
`IDeviceLocationProvider`, `IDeviceLocationAvailabilityService`, and
`ILocationSearchProvider` keep platform, fallback, and geocoding concerns out of
views.

## Coordinates and time

Core geography is always WGS84 latitude/longitude. The desktop map adapter is
the only code that converts to EPSG:3857 Web Mercator. Bearings are clockwise
from true north and angles are normalised to `[0, 360)`.

Instants are Noda Time `Instant` values. A local date/time becomes an instant
only through `ITimeZoneResolver`; DST gaps are resolved leniently and local day
bounds can therefore be 23, 24, or 25 hours. Manual/saved zones are supported.
Noctaxis never claims to infer a geographic timezone when it has not done so.

## External boundaries

- `IAstronomyService` wraps Astronomy Engine. Sun/Moon ephemerides, event
  search, illumination, refraction, and stateless J2000-to-of-date vector rotation
  stay behind it. No reusable custom-star slot or permanent Astronomy Engine body
  is used. `OpenNgcTargetCatalogue` parses validated, embedded upstream OpenNGC
  CSV resources; Sun and Moon are the only non-OpenNGC system entries.
- `ITerrainHorizonProvider` has flat and SRTM implementations. Elevation access
  is separately replaceable to allow deterministic synthetic tests.
- `IWeatherProvider` is implemented by an `HttpClientFactory` typed Open-Meteo
  client. Provider DTOs are isolated and mapped into domain records. A separate
  clock-driven geographic cache uses great-circle distance, forecast hour, and
  a fixed ten-minute age; forced manual/export requests bypass it.
- `ILocationSearchProvider` is an independent Open-Meteo geocoding client with
  isolated DTO mapping, cancellation, recent-query caching, and GeoNames
  attribution. `ITargetSearchService` searches the bundled OpenNGC data offline.
- `IUserDataStore` performs versioned, atomic per-user JSON persistence and
  quarantines malformed files.
- `IScoutingCardExporter` produces one Skia-rendered PNG byte stream for both
  save and clipboard destinations. A standard PNG text chunk contains a
  versioned `NoctaxisExportMetadata` JSON payload; no UI view model is serialized.

The dependency-injection composition root is `Noctaxis.Desktop/App.axaml.cs`.
Interfaces exist only at external or meaningfully replaceable boundaries.

Settings are staged directly in the main Settings tab. General, Weather, and
Celestial objects are internal settings sections; no settings window or editable
map-source configuration exists. Celestial configuration, visible state, order,
and default primary target are persisted separately from the full catalogue.

## Persistence and secrets

`state.json` lives below the platform application-data directory in a
`Noctaxis` folder. A temporary sibling is flushed then moved over the previous
file. Malformed data is renamed with a `.corrupt-<timestamp>` suffix and clean
defaults are returned. Legacy API-key, tile URL, and attribution properties are
ignored during deserialization. Weather needs no credential or stored secret.
