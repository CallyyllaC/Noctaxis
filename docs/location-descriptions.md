# Location descriptions

When the Planner pin is placed on the map, Noctaxis names it from geographic data it already holds,
for example *Near the River Ancholme, outside Saxby*. This replaced reverse geocoding with the public
Nominatim service. A description is a contextual label. The exact coordinate stays authoritative for
every calculation, and a saved location's own name is never changed by one.

## Pipeline

| Stage | Code | Responsibility |
| --- | --- | --- |
| Discovery | `IGeographicFeatureSource`, `OpenFreeMapGeographicFeatureSource` | Named candidate features plus anonymous evidence (built-up land, open water), with coverage |
| Spatial reasoning | `GeographicSpatialAnalysis` | Distance, bearing, containment and bank side on a local tangent plane |
| Ranking | `LocationDescriptionEngine` | One relationship per feature, scored by kind, size and distance; selects a primary and a secondary |
| Composition | `LocationDescriptionComposer` | Localised text from `Places_Description_*` resources |
| Service | `LocationDescriptionService` | Remembers nearby answers (15 m); never remembers an unavailable one |

`MainViewModel` asks after a 700 ms debounce. A result for a pin that has since moved, or that a
saved location has replaced, is discarded. Results stay structured (`LocationDescription`,
`FeatureRelationship`), so later uses can read the relationship, distance, bearing and confidence
rather than parsing text.

## Data read

The OpenFreeMap source reads the 3×3 zoom-13 tiles around the pin (settlements, watercourses, lakes,
peaks, ridges, reserves, built-up land) and the 2×2 zoom-14 tiles nearest it (landmarks, which the
tiles hold only from zoom 14). That is at most 13 tiles, four at a time, through the shared
`VectorTileRepository`, so tiles the Planner has drawn are reused and work offline. Coverage is
reported as complete, partial or unavailable, and nothing beyond the tiles' reach is described.

## Supported now

- **Features:** cities, towns, villages, hamlets, suburbs and neighbourhoods (place points); named
  rivers, canals and streams; named lakes (a lake's label point matched to its water polygon); peaks
  and ridges; named parks and reserves; castles, monuments, attractions and museums; places of worship.
- **Relationships:** *In* (built-up land within a settlement's core distance, or inside a reserve),
  *By* (watercourses and lakes), *On the west/east/north/south bank of*, *Outside* (within a
  settlement's core distance but not on built-up land), *Near*, and *North-west of …* (eight compass
  points).
- **Human scale:** each kind has its own reach. A named river is *near* up to 700 m, a stream only up
  to 200 m. A town reaches farther than a hamlet. Containment in a large designated area is weighted
  down, so a nearby river outranks it.
- **Combination:** one feature phrase and one settlement phrase, the feature first. The second phrase
  never repeats the first name.

## Rules that keep it honest

- Only named features are described, and only names in the data. Nothing is invented or guessed.
- A bank side is claimed only on a straight, unclipped run of the course, at least about 20 m from the
  generalised centreline, and not when the position is in the water. Otherwise *By* is used.
- No compass direction is given from a line's clipped end. No description is made beyond the coverage
  radius. When nothing qualifies, the Planner keeps showing coordinates. An absent feature is never
  treated as proof that none exists.

## Limits and next steps

- Vector tiles are generalised for display, to roughly one pixel at their zoom (about 10 m at zoom
  13). Distances are good for labels, not for measurement.
- Settlements are points: "in" relies on mapped residential land or buildings, and tiles hold no
  administrative areas.
- Woodland and land cover carry no names in OpenMapTiles. Named woods, longer river context beyond
  the tile block, landmark polygons, riverbank polygons and richer multi-feature phrasing need further
  sources. Each can be added as another `IGeographicFeatureSource`, or as a new feature kind with its
  own profile, without changing the engine.
