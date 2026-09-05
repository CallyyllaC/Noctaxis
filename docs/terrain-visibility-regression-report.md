# Terrain and visibility regression audit — 2026-09-05

This audit treats the supplied working tree, including its uncommitted terrain/rendering changes, as the accepted baseline. It does not attribute those pre-existing changes to this pass. No optimisation, terrain-source substitution, or visual redesign was performed.

## 1. Coverage found before editing

The existing tests already directly covered:

- **Terrarium:** documented RGB conversion for zero, fractional positive, shallow negative and deep negative elevations; Britain/equator/north/date-line addressing; pixel orientation; horizontal seam and four-tile corner interpolation; missing contributing tiles; retry with zero retry delay; shared decoded loads; invalid PNG dimensions.
- **Surface resolution:** positive and negative land, permanent-water bathymetry correction, elevated water preservation, negative explicit inland water, genuine zero water, land/water/land rays, negative coastal horizon, bulk classification, and explicit absent WorldCover tiles within the mapped extent.
- **Horizon/observer:** directional synthetic ridge, unavailable elevation, manual and automatic observer altitude, camera height added once, coordinate cache invalidation, adaptive/uniform distances, curvature, sequential/parallel equivalence, decoded-cache coalescing and eviction.
- **Framing:** negative horizon with positive target, positive horizon occultation, equality defined as occulted, angular-only profiles producing no invented first hit, multiple bearings, refined hit/clear transitions, pitch and vertical-FoV independence.
- **Geometry:** fixed 500 km cone, independent weather and terrain, clear foreground, nearby wall, distant obstruction, variable first-hit frontier, open corridors, all-clear/all-blocked cones, side edges, winding/topology and north/date-line wrap. Existing weather tests cover unavailable, poor, beyond-cone and equal boundaries, plus both orderings of terrain/weather.
- **Renderer:** real Skia runtime-effect compilation and headless drawing, observer-local alignment, profile/resource reuse, render-state invalidation independent of terrain data, and reactive polar/local-map controls.
- **Planner:** stale core/environment completion, A→B→C→D debug profiles, stale local-map completion, immediate invalidation, preview versus committed observer, terrain/manual/ocean elevation, and static-environment reuse for time changes.
- **Integration:** real Avalonia service composition with deterministic PNG fixtures; opt-in official-source plausibility tests for Brigg, Blaenau Ffestiniog, Ben Nevis, Irish Sea, Atlantic and Schiphol.

Good tests were retained. Existing parameterised tests were expanded where that avoided duplicate fixtures.

## 2. Gaps identified and tests added

| Area | Added or strengthened guarantee |
|---|---|
| A: RGB decoding | Minimum/maximum encoding, smallest positive/negative fractional step, and large positive elevation using literal RGB expectations |
| B–C: addressing/interpolation | Southern/high latitudes, either side of the longitude tile boundary, north/south seam at three positions, repeated date-line wrapping, fractional interior interpolation |
| D: caching | Thirty interior samples reuse one tile load/cache lookup; a neighbour is acquired only at the seam; persistent cache reopened with an acquisition delegate that throws; shared-cache concurrency uses completion sources instead of a sleep |
| E: surfaces | Zero land, positive explicit inland water, unavailable-classification diagnostics at negative/zero/positive elevation; real resolver feeding the local-map service produces physical ocean surface |
| F–G: observer | Zero/negative/positive ground with zero/positive camera height, manual override with missing terrain, explicit unresolved/zero/manual state distinctions and camera-height changes |
| H–I: stale results | Four sub-metre commits, D completing first, then B/A/C; assertions after every completion on elevation, horizon, debug profile, map and FoV obstruction |
| J: horizon | Flat, rising, falling, nearby hill, distant mountain, multiple peaks and negative terrain with independent expected winning distances/angles |
| J/AG: curvature | 5/50/250/500 km profile endpoints and angles against the documented 7/6 effective-radius formula |
| K: occultation | Values immediately either side of angular equality |
| L–N: first hit | First hit distinguished from maximum-angle/highest/final samples; quarry geometry expanded to 50/500/2000 m; distant geometry includes 490 km as well as 499 km |
| AC: time/weather | Normal 15:00→16:00 and reversed weather completion order, 25→10 km boundary, nonempty obstruction identity and terrain-object reuse, one environment request |
| AE–AF: renderer | Real shader interior pixels for weather/terrain orderings, equal boundaries, clear water, unavailable/beyond-cone weather; near-field alignment expanded to 50/100/500 m |
| AH: map ownership | Completed map cannot change when a provider mutates its original arrays; default 20 km/128×128 dimensions; raw bathymetry remains diagnostic while rendered surface is zero |
| AK: profile cache | Azimuth count, range, step, curvature, camera height, manual override, adaptive policy; precise sub-metre longitude identities and repeat-request reuse |
| AL–AM: architecture | Unmodified production DI contains exactly one elevation registration, resolves Terrarium→resolver→horizon→view model, and the Core assembly has only one concrete elevation-provider implementation |

## 3. Production defect and minimum fix

`CompletedMapSnapshot_OwnsImmutableResolvedSurfaceData` failed before the fix: after sampling a 25 m map, modifying the fake provider's array changed the completed snapshot to −3000 m.

`TerrainDebugMapService.GetMapAsync` previously passed arrays through behind `IReadOnlyList` interfaces. It now copies coordinates, raw and resolved elevations, classifications, adjustment/status values and tile identifiers into immutable arrays at the completed-snapshot boundary. The regression passes. This is the only production change made in this pass; the file itself was already untracked on arrival.

No terrain calculation, interpolation, water policy, shader semantics or production DI was changed.

## 4. Asynchronous coverage

The new combined test uses request-start semaphores and controllable completions. Providers deliberately ignore cancellation. Timeout guards detect hangs; they are not performance assertions. Four distinct locations less than a metre apart start before any completes. Completion order is D, B, A, C. Each completion is awaited and checked; only D remains current for all five requested terrain consumers.

Existing two-location stale core/environment and map tests remain. The new weather theory covers both ordinary time progression and a 15:00 weather result arriving after the 16:00 refresh has committed. It checks final session time, current visibility, unchanged terrain object, unchanged nonempty first-hit samples and no extra environment request.

These exercise observable stale-result rejection even when the provider ignores cancellation. They do not prove that generation checks alone, independently of the caller's cancellation checks, are responsible for rejection.

## 5. First-hit and weather contract

Plan hatching remains first-horizontal-hit to 500 km. Existing geometry tests preserve interpolated frontiers, winding, clear sectors, midpoint/refined transitions, all-clear/no geometry, continuous all-blocked coverage and side closure. New cases explicitly cover 50/500/2000 m foreground and a 490 km first hit. Maximum-angle samples and target occultation remain separate.

Weather remains colour treatment, with the boundary included in the beyond-visibility region. CPU semantic tests cover equality and both boundary orderings without edge-AA assertions. Headless shader tests sample interior pixels, asserting normal colour, grayscale and hatch presence. Solid-hatch fixtures test hatch presence over either weather state; they do not assert the exact RGB mixture of the base underneath the hatch.

Weather changes preserve profile identity and first-hit data. Terrain geometry changes do not move the weather fill boundary. The base cone retains its fixed extent. Existing optical/pitch tests remain unchanged.

## 6. Diagnostics and canonical observer

Existing local-map tests check north/east/south/west orientation, reactive map replacement and FoV direction. Existing polar tests check profile replacement, null resolving state, observer, generation, bearing, FoV and weather properties. Existing XAML binding tests keep the diagnostic controls attached to Planner state. The new combined stale-result test checks the same current observer across elevation, horizon, debug profile, map and framing; it also retains the raster across a focal-length change.

The local map receives completed data; rendering has no provider/service calls. The service uses the production resolver in one preload/classification/elevation pass. The new ocean test checks raw −3000 m and resolved 0 m across that path. Snapshot collection ownership is now protected against mutation.

## 7. Architecture audit

Production C# search found no references matching the obsolete Hgt/Srtm/Skadi/Copernicus/MapzenTerrainProvider/DemDirectory names. The stronger executable guard resolves the original DI graph without overriding the elevation provider and asserts a single Terrarium registration and implementation. WorldCover remains classification only. The existing fixture-backed production integration test is retained.

## 8. Verification

`dotnet restore Noctaxis.slnx` passed.

The exact requested standard-output Release build was attempted and failed with MSB3027/MSB3021: running `Noctaxis.Desktop` PID 28804 locked the destination Core DLL. The app was not terminated. Fresh verification used the same Release configuration with a separate output folder at the normal directory depth:

```powershell
dotnet build Noctaxis.slnx -c Release --no-restore -p:OutputPath=bin/RegressionRelease/net10.0/
dotnet test Noctaxis.Core.Tests/Noctaxis.Core.Tests.csproj -c Release --no-build --no-restore -p:OutputPath=bin/RegressionRelease/net10.0/
dotnet test Noctaxis.Desktop.Tests/Noctaxis.Desktop.Tests.csproj -c Release --no-build --no-restore -p:OutputPath=bin/RegressionRelease/net10.0/
```

- Fresh isolated Release build: **passed, 0 warnings, 0 errors**.
- Final Core: **237 passed, 1 skipped, 238 total**. The skipped item is the opt-in live theory; the benchmark body also remains opt-in and was not executed.
- Final Desktop: **242 passed, 0 failed**.
- Focused Core: **138 passed, 1 skipped**, filtering Terrain, Terrarium, FramingVisibility, EnvironmentalIntelligence, EquipmentAndObserver and LocalHorizon class/name matches.
- Focused Desktop: **150 passed, 0 failed**; filter includes EnvironmentalOverlay, GeographicOverlayGeometry, MainViewModel, TerrariumPlannerIntegration and WindowPolicy. This intentionally includes all view-model/settings tests in those classes rather than labelling every match a terrain-only test.
- `git diff --check`: passed (Git emitted line-ending conversion warnings, not whitespace errors).

## 9. Separate live verification

With `NOCTAXIS_RUN_LIVE_TERRAIN_TESTS=1`, **5 passed, 1 failed**. Brigg, Blaenau Ffestiniog, Ben Nevis, Schiphol and Irish Sea passed. Irish Sea sampled raw −35.442 m and resolved 0 m.

Open Atlantic had available Terrarium elevation but unavailable WorldCover classification. Its resolution reason was `RawTerrainClassificationUnavailable`; the test correctly failed its expected permanent-water assertion. A diagnostic rerun reproduced this. The test now prints classification state/message before assertions so failures retain evidence. The real-provider suite can reuse persistent local cache; these results do not establish that every tile was freshly downloaded.

No synthetic ocean classification or changed fallback policy was introduced to make the live case pass. The cause of Atlantic classification unavailability remains unconfirmed.

## 10. Remaining limits

- The requested normal-output Release build needs the running app closed before it can pass; the tested assemblies are the fresh isolated Release build.
- External dataset availability and exact live classifications cannot be made deterministic. Atlantic remains an explicit live failure.
- Refraction is a fixed 7/6 production policy, not an independently configurable profile input. Tests lock its numeric effect; no new refraction setting was introduced.
- Headless Skia drawing does not establish correctness on every GPU/driver or physical display. AA edges and exact hatch RGB blending are deliberately not pixel-golden tests.
- Control property/reactivity and geographic direction tests do not constitute exhaustive rendered-pixel verification of every polar/local-map annotation. Those controls currently have no terrain I/O in rendering; this audit did not add a source-text ban on future I/O.
- Existing older Planner tests still contain polling/sleeps. Newly added async ordering tests use explicit start/completion signals; the shared tile-cache coalescing test's arbitrary delay was removed.
- No allocation/hardware optimisation or benchmark phase was started.
