# Noctaxis

Noctaxis is a desktop planning tool for astronomy and astrophotography.

The aim is to bring the useful parts of observing and shoot planning into one place: celestial positioning, terrain, camera framing, weather, and saved observing locations without needing to juggle several separate tools.

> [!WARNING]
> **Noctaxis is in active development.**
> The UI, data models and features are still changing, and there is no stable release yet.

## Current direction

- Interactive map-based observing locations
- Sun and Moon position, rise/set and path information
- Milky Way and night-sky planning
- Camera and lens field-of-view planning
- Terrain-aware horizon visibility
- Weather and observing-condition data
- Saved locations and scouting information

## Built with

- .NET 10
- Avalonia
- Mapsui
- Astronomy Engine
- NodaTime
- SkiaSharp / OpenCV

## Running from source

```bash
dotnet run --project Noctaxis.Desktop
```

Development is currently focused on getting the core planning workflow and data sources into shape. Expect rough edges, unfinished features, and occasional structural changes while that settles.

## Versioning

Builds are versioned `X.Y.Z`:

- `X.Y` lives in `Directory.Build.props` (`NoctaxisVersion`). X is only ever changed by hand; Y is incremented automatically once each time `dev` is merged into `main` (see `.github/workflows/version-bump.yml`), and the new `X.Y` is applied to both branches.
- `Z` is the CI build number (`NoctaxisBuildNumber`). Local builds use `0`, so a local build of `0.4` is `0.4.0`, while CI run 173 builds `0.4.173`.

CI only validates and versions builds; it does not tag or publish releases.
