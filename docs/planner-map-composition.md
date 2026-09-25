# Planner map composition foundation

This records Phase 2. Phase 3 adds runtime state and active attribution; see
[Planner map runtime state](planner-map-runtime-state.md) for current selection/state APIs.
In particular, basemap replacement now preserves the original composition and stores the
selected definition in Runtime.ActiveBasemap, and the attribution UI now refreshes live.

## Audit and preserved physical order

Previously `NoctaxisMapView` created a Mapsui Map and added the OSM tile layer directly.
Its Grid contained MapControl, MapOverlay, then the bottom-right attribution TextBlock.
MapOverlay reads the current viewport at render time and draws:

1. Camera sector fallback fill, or the environmental shader's combined base/weather/terrain fill.
2. Optional debug terrain topology, then camera left/right/centre boundaries.
3. Celestial rays (including Sun/Moon when present in the existing plans).
4. Optional terrain sample diagnostics.
5. Pin activity and observer marker. Probe-only composition instrumentation follows.

This physical order, all visibility/settings gates, and the Grid order are unchanged.
Environmental shading is currently camera-sector content, not an independent full-map raster.

## Foundation

`Mapping/MapComposition.cs` contains an immutable semantic stack: one required
BasemapDefinition, plus multiple Environmental, Planning and Interaction definitions.
Overlay basemap registrations and duplicate IDs are rejected. Order is group, explicit
within-group order, then ordinal ID. Metadata and aggregated, deduplicated attribution
are computed only when constructing a composition. BasemapKind currently exposes Street
only. Appearance remains independent; no unused appearance setting or persistence was added.

`Mapping/PlannerMapComposition.cs` creates the production registrations and owns native
basemap installation/replacement. `OpenStreetMapBasemap` preserves the exact existing
Mapsui factory, with semantic Street identity and provider attribution/licence metadata.
The Mapsui-specific adapter accepts multiple ILayer instances, not just XYZ TileLayer.
The semantic definitions themselves have no renderer dependency.

Replacement prepares the provider first, removes/disposes the previous native basemap,
inserts its replacement below existing native overlays and retains the logical overlays.
Selecting the same adapter instance is a no-op. Adapters must return fresh, exclusively
owned layers; call replacement on the UI thread. MapControl retains Map lifetime ownership.
There is no basemap-selection UI in this phase. The attribution TextBlock reads the initial
composition; a future selection UI must refresh it alongside the changed composition.

`Controls/NoctaxisMapView.cs` now obtains its Map and attribution from this controller.
It retains all original input, rendering, renderer-release and attachment behaviour.
There are no new events, timers, subscriptions or work on the viewport path. One controller
is created per view, not per attachment. Logical registrations describe available content;
existing settings continue to control visibility without rebuilding the graph.

## Existing rendering ownership

| Registration | Group | Existing implementation |
| --- | --- | --- |
| basemap | Basemap | Mapsui OSM factory |
| environmental-shading | Environmental | EnvironmentalOverlayRenderer via MapOverlay |
| camera-framing | Planning | MapOverlay geographic fill and framing boundaries |
| celestial-rays | Planning | MapOverlay cached celestial geometry |
| observer-pin | Interaction | MapOverlay marker/activity plus NoctaxisMapView input and pin state |

The shared MapOverlay intentionally remains intact. Its shader jointly composites base
fill, weather and terrain, while projection caches, visibility gates and pin scheduling
already share a tested lifecycle. Splitting these into separate controls would change
render scheduling and potentially Z-order. Semantic groups therefore describe content;
they do not require one renderer or one surface per registration. Diagnostics remain
diagnostics, not new production layers. The minimap is unchanged.

Terrain calculations, providers, caches, HorizonService and analytical visibility/FoV
logic were untouched by this work. The existing event-driven pin synchronization,
Render-priority worker posts, generation rejection, render-time viewport read and
activity/diagnostics-only timer are untouched. Existing native probes were not modified
or rerun; no further pin performance experiment was performed.

## Next environmental layer

A future Light Pollution implementation would add an Environmental definition with its
own stable ID, order and provider credits. Its renderer adapter would install native
layers above the basemap and below the current floating overlay, or use a suitable custom
surface. The semantic model does not dictate raster/vector/custom rendering. That phase
must connect actual layer-state changes and attribution updates to the view; navigation
must continue to avoid graph reconstruction. No new provider is implemented here.

## Verification

`MapCompositionTests` covers the required exclusive basemap, deterministic coexistence,
duplicate rejection, aggregated attribution, native replacement and failure preservation,
OSM defaults, and repeated view detachment/attachment plus navigation without rebuilding
registrations. Existing synchronization, viewport, pin, activation, view-model and
persistence tests are run unchanged.

- Final focused Release run: 16 passed, 0 failed, 0 skipped (five composition tests,
  existing synchronization/viewport/pin tests, and the asynchronous test rerun below).
- Full desktop Release suite: 742 passed, 0 failed, 0 skipped, 1 minute 52 seconds.
- Initial broader focused run: 159 passed and one timeout in the unchanged
  `MainViewModelTests.StaleEnvironmentCannotEnrichNewerObserver`. It passed unchanged
  in the final focused run and full suite. No timeout threshold or assertion was changed.
- Final build completed without warnings/errors. `git diff --check` passed (only Git
  line-ending notices). Build output was isolated under `artifacts/composition-tests`.
- TRX evidence: `artifacts/composition-results/focused.trx`, `focused-final.trx`, and
  `desktop-release.trx`. No native visual comparison or hardware test was performed.
- Initial NuGet restore was blocked by sandbox network access; the approved restore
  succeeded, and subsequent builds used `--no-restore`.

No timing/allocation benchmark was run. Code inspection confirms no composition work
in the per-frame or navigation paths; lifecycle tests assert stable composition and native
layer identity. Runtime performance neutrality is not claimed as a measured result.
