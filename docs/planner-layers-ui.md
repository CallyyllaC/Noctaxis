# Planner Layers UI refinement

The Planner has a compact **Layers ▾** button at the top right of the map. Its standard Avalonia
flyout opens straight onto the layers, with no headings: the button already says what it is. Each
layer (Terrain relief, Light Pollution) is one row: its checkbox and name, its opacity percentage
opposite, and the opacity slider directly beneath, indented under the name. A layer that is off keeps
its slider and percentage in place, dimmed and disabled, still holding its opacity, so the flyout never
changes height and nothing moves. Two layers take 158 px where the earlier headed layout took 285.

Avalonia's slider ignores the mouse wheel, so the control adds it to the two opacity sliders only:
while the pointer is over a slider whose layer is on, each notch moves it 5 percentage points
(`PlannerLayersControl.OpacityPerWheelNotch`), trackpad deltas gathered into whole notches, clamped to
the slider's range; at either end the wheel is left to scroll the flyout. A wheel change to Light
Pollution commits when the pointer leaves the slider or the flyout closes. Dragging, tapping and the
keyboard are unchanged. Clicking outside dismisses the flyout. There are no tooltips; the checkboxes and sliders keep their descriptions as accessibility help text. No layer toggles were added.

`PlannerLayersViewModel` reads visibility and opacity directly from the map's
existing `MapLayerStateController`; setters call `SetVisibility` and `SetOpacity`.
A state-change notification keeps the controls in sync with restored/runtime
state. The Planner owns this wrapper for its lifetime and subscribes once;
attaching and detaching the control does not register additional handlers.
The existing adapters still update the same native layer and its attribution.
Same-value edits remain no-ops. MapComposition is not rebuilt, and neither the
native Light Pollution layer nor its raster source is replaced.

User edits update the existing in-memory LightPollutionPreferences immediately.
They retain the current palette and do not disturb an unsaved palette selection
in Settings. Persistence uses the existing MainViewModel/store path and schema.
Visibility clicks save immediately; opacity saves on pointer release, keyboard
release, focus loss or flyout close. A drag alone does not write on every change.
Concurrent commit requests share one task, and a later explicit commit during
disk IO is saved afterwards. A new, uncommitted drag does not trigger repeated
writes behind a pending save. Window close waits for the owned save before the
existing numerical-reader/map shutdown. Failed saves retain dirty state and
provide a retry message in the flyout; no timer or background debounce was added.

Settings → Data no longer contains visibility or opacity controls. It retains
source name, Lorenz attribution and resolution/coverage information, source link,
installation status/progress, Install / Repair / Reinstall, Cancel, Remove, and
Colour map. The copy now directs map visibility/opacity changes to Planner →
Layers and says Save settings applies the colour map. Palette Save, Reset and
unknown-ID fallback retain their existing behavior.

The UI-only palette order and descriptions are:

| Palette | Selected description |
| --- | --- |
| Turbo | Vivid colours with strong visual separation. Best for quickly spotting darker and brighter regions. |
| Viridis | Balanced colours designed for clear quantitative comparison. |
| Inferno | High-contrast purple, red and yellow palette with a heat-like appearance. |
| Magma | Softer purple, magenta and warm-yellow dark-to-light palette. |
| Cividis | Blue-to-yellow palette designed to remain useful for many users with colour-vision deficiencies. |
| Grayscale | Neutral black-to-white representation. |

The selected description appears directly beneath the selector and updates with
selection. No gradient preview was added. The palette registry, stable IDs, LUTs,
licenses, grayscale fallback and rendering semantics are unchanged.

Six new tests cover the Settings/Planner split, descriptions/order, palette and
layer preference independence, runtime/native-layer identity, same-value no-ops,
restoration, commit coalescing, save failures/retry, keyboard/focus/click-away,
detach/reattach, window bindings, theme scaling and pending-save shutdown. Two
existing Light Pollution settings tests were updated for the relocated controls;
their persistence and fallback assertions remain. Existing map lifecycle,
composition, overlay synchronization, viewport and pin tests were not edited.

Verification uses the existing `artifacts/phase4b/serial.runsettings` (one xUnit
worker, test-collection parallelism disabled), with solution projects run using
`--maxcpucount:1`. This preserves the established Avalonia verification setup.
No assertions or timeouts were weakened, and no unrelated tests were changed.

The existing `NativeTileFetchPaintsNewPaletteWithoutReplacingLayer` test was
changed **only to synchronize with native feature publication**. Its old sequence
awaited `RefreshDataAsync`, ran dispatcher jobs, and immediately required a cached
feature. That treated fetch completion/idle as a publication barrier. The test
did not read `Busy` directly: the assumption was that completion of
`RefreshDataAsync` was a sufficient readiness signal. The underlying Mapsui
fetch tracker marks a tile done before `tileCache.Add`; another planner progress
check can observe done and report `Busy == false` while publication is still in
progress. A dependency-only probe reproduced this ordering deterministically
three times, with cache publication gated explicitly and every fetch released
and awaited. It uses the existing Mapsui binaries, no Noctaxis/Avalonia types,
and no sleeps: `artifacts/lp-ui/native-fetch-race/Probe.csproj`.

The old test passed some runs but failed at the initial empty-feature assertion
in both focused and full runs (including 803 passed / 1 failed in
`solution-complete_net10.0_20260919221104.trx`). Those failures remain recorded.
The direct composition/binding fixture never creates the new Planner UI.

The corrected test subscribes to the native layer's `DataChanged` event before
requesting refresh, and checks `GetFeatures` for the requested world extent and
resolution. Events only wake the check; success requires actual published
features. The production layer's existing revision filter excludes stale
palette features. A semaphore retains notifications between checking and
waiting, avoiding lost wakeups. One five-second cancellation deadline bounds
each publication wait; there is no polling delay or arbitrary sleep. The
temporary subscription is removed in `finally`, and a lock prevents an
already-dispatched callback from releasing a disposed semaphore. Grayscale
pixels, changed Turbo pixels, non-null rasters and the original native layer
identity assertions are unchanged. No production rendering, cache, UI or Mapsui
dependency code was changed for this race.

A separate SHA-256 snapshot taken immediately before the test synchronization
fix covers **406 production files** under Core and Desktop (excluding build
outputs). The comparison found **0 changed files**; evidence is
`artifacts/lp-ui/native-fix-production-verification.json`.

The corrected native test passed **10 consecutive isolated Release runs: 10
passed, 0 failed, 0 skipped**, each in a separate test invocation. Individual
TRX files are `artifacts/lp-ui/native-publication-1.trx` through
`native-publication-10.trx`; the parsed summary is
`artifacts/lp-ui/native-publication-results.json`.

The final focused Release set passed **89 passed, 0 failed, 0 skipped**. This
includes all tests matching PlannerLayers, LightPollution, MapLayerState,
MapComposition, OverlaySynchronization, Viewport, PlannerPin and WindowPolicy.
Evidence after the publication-wait fix: [focused TRX](../artifacts/lp-ui/focused-publication.trx).

The standalone full Desktop Release suite passed **804 passed, 0 failed,
0 skipped**, including the native palette test. Evidence:
[Desktop TRX](../artifacts/lp-ui/desktop-publication.trx).

The full Core + Desktop solution then passed sequentially in Release:

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 331 | 0 | 1 |
| Desktop | 804 | 0 | 0 |
| Total | 1,135 | 0 | 1 |

The sole skip is the existing opt-in
`TerrariumLiveIntegrationTests.OfficialTerrariumSampleIsPhysicallyPlausible`.
Evidence: [Core TRX](../artifacts/lp-ui/solution-publication_net10.0_20260920095433.trx)
and [Desktop TRX](../artifacts/lp-ui/solution-publication_net10.0_20260920095930.trx).
The full solution command was:

```powershell
dotnet test Noctaxis.slnx -c Release --no-restore --maxcpucount:1 --settings artifacts/phase4b/serial.runsettings --logger 'trx;LogFilePrefix=solution-publication' --results-directory artifacts/lp-ui
```

The UI refinement and its final verification are complete. The only subsequent
code change was the native test's publication synchronization; all 406 production
files still match their pre-fix hashes. No remaining implementation work or
next-phase feature was introduced.

Headless Avalonia captures were visually inspected at 1440 × 1000, in dark and
light at 100% and 200% application text scale, plus High Contrast at 100%. The
flyout remains separate from the top-left Terrain minimap and inspector controls;
the slider and percentage are readable, attribution stays visible, and the long
Cividis description wraps within Settings. The capture fixtures disable network
basemap fetching: their white map background is not a real-data rendering check.
Representative captures: [dark 200% Planner](../artifacts/lp-ui/captures/dark-200-planner.png),
[light 100% Planner](../artifacts/lp-ui/captures/light-100-planner.png), and
[light 200% Settings](../artifacts/lp-ui/captures/light-200-settings.png).
Physical monitor DPI, native OS windows, screen-reader speech and every narrow
window size were not manually exercised. At small widths the existing Planner
and inspector offer less map space. Normal theme resources supply hover colors;
no hard-coded black hover/background was introduced. Black in High Contrast is
the selected theme's surface color.

Protected-file SHA-256 comparison against the start of this UI task found **14
unchanged files, 0 changed**, including the numerical provider/decoder/installer,
display normalization, palettes/LUTs, raster cache/source, Light Pollution map
binding, PlannerMapComposition, terrain provider, viewport scheduler and the
existing composition/state/synchronization tests. Numerical source data was not
downloaded, rewritten or reinstalled. Terrain calculations and event-driven pin
synchronization are untouched. No next-phase features were implemented.

## Map attribution

The Planner map has one attribution surface, owned by Noctaxis: a compact strip at the map's
bottom-right and an info button beside it that opens **Map data & licences**.

- **One source of truth.** Each credit is a `MapAttribution` (id, compact label, full credit, link,
  licence) defined once in `MapAttributions`. Basemap and layer metadata (`MapProviderMetadata.Credits`)
  refer to those definitions; a provider with a single plain credit may still give `AttributionText`.
- **Dynamic.** `MapLayerStateController.ActiveAttributions` holds the credits owed by what is drawn now:
  the active basemap, and each overlay while it is renderable (visible, above zero opacity, available,
  active). Credits shared by several providers are given once, by id, where first owed. The strip is
  their labels joined with " · "; the flyout gives each credit in full with its links.
- **Labels keep the credited party.** A compact label drops only a product name, never whoever is
  credited: "Terrain: Mapzen/Tilezen" and "Light pollution: David Lorenz" in the strip, with
  "Terrain: Mapzen/Tilezen Terrarium" and "David Lorenz Light Pollution Atlas 2025" in the flyout.
  OpenFreeMap's attribution (OpenFreeMap, © OpenMapTiles, OpenStreetMap data) is three credits, each
  visible and each with its own link.
- **Links** open through `IExternalUriLauncher`, by the view model's `OpenMapDataLinkCommand`; a
  failure is reported in the status line.
- **No second surface.** Mapsui draws each layer's own attribution as a hyperlink widget over the map.
  `PlannerMapComposition` switches that off on every native layer it installs; the layers keep their
  source's attribution as data.
- The strip wraps rather than truncating, and leaves the pin preview's room at the bottom-left
  (`AttributionReservedLeft`).

## Opening Layers

Opening the flyout was profiled in the headless Planner window (2026-10-02). Opening it changes no layer
state, edits no preference, recomputes no attribution and requests no map redraw, and the same content is
shown each time; the popup itself opens in about 5–40 ms, as the Panels menu does. The one large cost seen
there is not the flyout's: any frame composed over the map re-runs the environmental-shading shader once
(about 110 ms with headless software rendering). The real application draws on the GPU and redraws only
the area that changed, so that figure does not carry over, and no fix was made on it.
`OpeningLayersOnlyShowsTheFlyout` keeps the opening free of hidden work. Each real opening is timed from
the press to the flyout's first frame, and one slower than `PlannerLayersControl.SlowOpening` (150 ms) is
written to the application log with that split, so the delay seen in the app can be located.
