# External maps

Planner's **Open in Maps** action uses the current pin coordinates. Choose the provider in Settings > General > External map provider, then **Save settings**. Reset unsaved changes restores the saved provider. Existing state files default to OpenStreetMap. Previously saved Google Street View preferences load as Google Maps.

`AppSettings.ExternalMapProvider` uses the existing JSON user-data store. `ExternalMapService` validates coordinates and constructs URLs independently of the Planner, then delegates to the existing `IExternalUriLauncher` / `ShellExternalUriLauncher` (system shell, Windows and Linux). The Planner command disables for invalid coordinates and reports launch failures through the existing status message. No browser launch occurs in tests.

Coordinates use invariant-culture G17 formatting. URL templates (lat/lon are substituted with coordinates):

| Provider | URL |
| --- | --- |
| OpenStreetMap | `https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=16/{lat}/{lon}` |
| Google Maps | `https://www.google.com/maps/search/?api=1&query={lat}%2C{lon}` |
| Mapillary | `https://www.mapillary.com/app/?lat={lat}&lng={lon}&z=16&focus=map` |

OpenStreetMap marks and centres the coordinate. Google Maps searches the coordinate. Mapillary opens the map at zoom 16; users select available imagery, which may not exist near the pin. This feature does not query coverage or select a specific Mapillary image.

References: [OSM marker URLs](https://wiki.openstreetmap.org/wiki/Template:MapLink/doc), [Google Maps URLs](https://developers.google.com/maps/documentation/urls/get-started), [Mapillary web map](https://www.mapillary.com/app/).

No manual UI or live Windows/Linux browser-launch validation was performed.
