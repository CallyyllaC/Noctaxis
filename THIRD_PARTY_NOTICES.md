# Third-party notices

Noctaxis uses the following direct runtime dependencies. Copyright and full
licence texts remain available from the linked projects and NuGet packages.

| Library | Version | Purpose | Licence |
|---|---:|---|---|
| Avalonia, Avalonia.Desktop, Fluent theme, Inter fonts | 12.1.0 | Cross-platform desktop UI | MIT |
| Mapsui.Avalonia12 / Mapsui / Mapsui.Tiling | 5.1.0 | Native map control and tile rendering | MIT |
| BruTile | 6.0.0 | Slippy-map tile schema and HTTP source | Apache-2.0 |
| CosineKitty.AstronomyEngine | 2.1.19 | Sun/Moon ephemerides and coordinate transforms | MIT |
| NodaTime | 3.3.3 | UTC, timezone, local date, and DST correctness | Apache-2.0 |
| CommunityToolkit.Mvvm | 8.4.0 | Observable MVVM state and commands | MIT |
| SkiaSharp | 3.119.4 | PNG scouting-card and saved-location thumbnail rendering (also used by Mapsui) | MIT |
| OpenCvSharp4 | 4.13.0.20260627 | .NET bindings for accelerated settlement-image kernels | Apache-2.0 |
| OpenCV native runtime | 4.13.0.20260627 | Optimised CPU image filtering for settlement thumbnails | Apache-2.0 |
| Hjg.Pngcs | 1.1.5 | Standards-compliant PNG text metadata read/write | Apache-2.0 |
| BitMiracle.LibTiff.NET | 2.4.660 | GeoTIFF validation and environmental raster sampling | BSD-style libtiff licence |
| Microsoft.Extensions.Configuration.Json | 10.0.0 | Desktop configuration primitives | MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | Composition root | MIT |
| Microsoft.Extensions.Http | 10.0.0 | `HttpClientFactory` weather, geocoding, and map-thumbnail clients | MIT |
| Microsoft.Extensions.Logging.Console | 10.0.0 | Recoverable-error logging | MIT |

Test-only dependencies are Microsoft.NET.Test.Sdk 18.0.0 (MIT), xUnit 2.9.3
(Apache-2.0), xunit.runner.visualstudio 3.1.5 (Apache-2.0), and
coverlet.collector 6.0.4 (MIT).

Mapsui brings permissively licensed rendering/geospatial dependencies including
NetTopologySuite (BSD-3-Clause), HarfBuzzSharp (MIT), Svg.Skia (MIT), and
RichTextKit (MIT). Avalonia and SkiaSharp bring their platform-native assets
under their package licences.

Default map tiles are provided by OpenStreetMap. Tile imagery/data is not
bundled with Noctaxis. The required visible attribution is:
`© OpenStreetMap contributors`. The application does not expose tile URL or
attribution editing.

Explicit saved-location map refreshes may also retrieve lightweight road and
waterway geometry from an OpenStreetMap Overpass API endpoint. This semantic
data is cached with the saved-location artwork and its own
`© OpenStreetMap contributors` provenance under the Open Database License
(ODbL). Bulk building requests are not part of normal Planner or saved-map
operation. No Overpass request is made for terrain/environment acquisition.

Weather forecasts are obtained from Open-Meteo and identified as such in the
application. No Open-Meteo response data or credential is bundled in Noctaxis.
Location search uses Open-Meteo's geocoding API with location data based on
GeoNames; the required attribution is displayed with search results.

Deep-sky catalogue data is provided by
[OpenNGC](https://github.com/mattiaverga/OpenNGC), copyright Mattia Verga and
contributors, under CC-BY-SA-4.0. Noctaxis bundles the upstream `NGC.csv` and
`addendum.csv` snapshot from commit
`da90466031b0372c896588b85be6016c617e205b`. The complete licence text is bundled
as `Noctaxis.Core/Data/OpenNGC-LICENSE.txt`, and attribution is visible beside
catalogue search results. OpenNGC documents its contributing astronomical data
sources in its upstream README and per-row `Sources` field.

Environmental source tiles are not bundled. They are downloaded on demand into
the application environmental-data cache:

- Mapzen/Tilezen Terrain Tiles in Terrarium PNG format are accessed from the
  public `elevation-tiles-prod` dataset. Tilezen's upstream attribution document
  lists the regional source acknowledgements that apply to derived terrain use.
- ESA WorldCover 2021 v200 is provided under CC BY 4.0. Required map/data
  acknowledgement: `© ESA WorldCover project / Contains modified Copernicus
  Sentinel data (2021) processed by ESA WorldCover consortium`.
- World Settlement Footprint (WSF®) 3D v02 is provided by DLR/EOC under
  CC BY 4.0 and supplies building fraction and average-height context.

Noctaxis caches these source files locally but does not claim ownership,
endorsement, or redistribution rights beyond the applicable source terms.
