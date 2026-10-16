# Third-party notices

Noctaxis uses libraries, astronomical catalogues, maps and environmental data made by other people. I'm grateful to their authors. **These components and datasets keep their own licences; the [AWOO Licence](LICENSE) applies only to material I'm entitled to license.**

The full licence texts and applicable notices for redistributed packages are included in the application's `ThirdParty/` directory. For the complete resolved dependency inventory, see [ThirdParty package inventory](Noctaxis.Desktop/ThirdParty/README.md). This page is an attribution overview, not a substitute for those licence files.

## Software libraries

| Software | What I use it for | Licence |
| --- | --- | --- |
| [Avalonia](https://avaloniaui.net/) and Fluent theme | Desktop interface | MIT |
| [Inter](https://github.com/rsms/inter) (via Avalonia.Fonts.Inter) | Interface fonts | SIL OFL 1.1 |
| [Mapsui](https://github.com/Mapsui/Mapsui), [BruTile](https://github.com/BruTile/BruTile) and NetTopologySuite | Maps and geometry | MIT / Apache-2.0 / BSD-3-Clause |
| [Astronomy Engine](https://github.com/cosinekitty/astronomy) | Astronomical calculations | MIT |
| [NodaTime](https://nodatime.org/) | Time zones and dates | Apache-2.0 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Application state and commands | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp), HarfBuzzSharp and associated native components | Graphics and text | MIT and bundled native-component notices |
| [OpenCvSharp](https://github.com/shimat/opencvsharp) and OpenCV | Image processing | Apache-2.0 and bundled component notices |
| [Hjg.Pngcs](https://www.nuget.org/packages/Hjg.Pngcs), [LibTiff.NET](https://bitmiracle.github.io/libtiff.net/) | PNG metadata and TIFF/GeoTIFF | Apache-2.0 / BSD-style and bundled notices |
| [BouncyCastle](https://www.bouncycastle.org/) | Supporter-code signature verification | MIT |
| Microsoft.Extensions libraries and .NET | Configuration, HTTP, logging, and runtime | MIT and bundled notices |
| Svg.Custom, Svg.Skia, RichTextKit and related packages | SVG and text rendering | MS-PL / MIT / Apache-2.0 (see bundled notices) |

The packaged native libraries may include further third-party components. Their licence and copyright texts are supplied with the build rather than repeated here. Test-only libraries aren't distributed as part of the desktop application.

## Bundled data and assets

- **[OpenNGC](https://github.com/mattiaverga/OpenNGC):** Deep-sky catalogue by Mattia Verga and contributors, licensed **CC BY-SA 4.0**. Noctaxis includes upstream `NGC.csv` and `addendum.csv` from commit `da90466031b0372c896588b85be6016c617e205b`. The licence is included at `Noctaxis.Core/Data/OpenNGC-LICENSE.txt` and in the packaged notices.
- **[Matplotlib](https://matplotlib.org/):** Turbo, Viridis, Inferno, Magma and Cividis colour tables, taken from Matplotlib 3.10.8 and converted to C# arrays with interpolation. Copyright © Matplotlib Development Team; see `Noctaxis.Desktop/ThirdParty/Matplotlib-LICENSE.txt` for terms and attribution.
- **Inter font:** Copyright © The Inter Project Authors. Its separate SIL OFL 1.1 terms are included in the packaged notices.

## Online maps, weather and environmental data

These sources are accessed or downloaded while the application runs. Their data isn't mine, and local caching doesn't change their ownership.

| Source | Used for | Attribution / terms |
| --- | --- | --- |
| [OpenFreeMap](https://openfreemap.org/), OpenMapTiles and [OpenStreetMap](https://www.openstreetmap.org/copyright) | Basemap | © OpenMapTiles; © OpenStreetMap contributors; provider terms |
| [OpenStreetMap raster tiles](https://operations.osmfoundation.org/policies/tiles/), [Overpass](https://dev.overpass-api.de/overpass-doc/en/preface/commons.html), [Nominatim](https://operations.osmfoundation.org/policies/nominatim/) | Location previews, roads, waterways and geocoding | © OpenStreetMap contributors; ODbL and endpoint policies |
| [Open-Meteo](https://open-meteo.com/en/licence) and [GeoNames](https://www.geonames.org/export/) | Forecasts and location search | Provider attribution and data terms; Open-Meteo's free API has separate usage restrictions |
| [NOAA Space Weather Prediction Center](https://www.swpc.noaa.gov/) | Aurora and planetary K-index | NOAA/SWPC attribution and applicable terms |
| [Mapzen/Tilezen Terrain Tiles](https://github.com/tilezen/joerd/blob/master/docs/attribution.md) | Elevation and terrain horizons | Regional source acknowledgements |
| [ESA WorldCover](https://esa-worldcover.org/en/data-access) | Land cover | CC BY 4.0; © ESA WorldCover project / modified Copernicus Sentinel data (2021) |
| [DLR/EOC World Settlement Footprint 3D](https://geoservice.dlr.de/web/datasets/wsf_3d) | Settlement and building-height context | CC BY 4.0; DLR/EOC attribution |
| [David Lorenz's Light Pollution Atlas](https://djlorenz.github.io/astronomy/lp/) | Light pollution | Used with the author's specific permission; see below |

### Light Pollution Atlas permission

David Lorenz gave me permission on **8 October 2026** to use his Light Pollution Atlas as a downloaded, locally cached light-pollution layer in Noctaxis. The data isn't bundled with the app. This permission doesn't turn the Atlas into an open-licensed dataset or grant permission to redistribute it independently. I credit David Lorenz and link to the [Atlas](https://djlorenz.github.io/astronomy/lp/); a materially different use would need fresh permission.

Some data providers impose attribution, request-rate, caching or other service conditions. Those apply independently of my application's software licence. See [3rd Party References](docs/user-guide/third-party-references.md) for the user-facing credits and links.

For release packaging, **keep the complete `ThirdParty/` licence and notice collection** with the app. Don't assume this short overview replaces required notices.
