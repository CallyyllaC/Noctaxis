# Planner map runtime state (Phase 3)

## Definitions and state

`MapComposition` remains the immutable registration/order/metadata snapshot. It is now
stable even across basemap replacement. Its Basemap is the initial definition; use
`PlannerMapComposition.Runtime.ActiveBasemap` for the active selection. The legacy
definition-level AttributionText remains a catalogue summary; the UI uses runtime attribution.

`MapLayerStateController` owns one cached `MapLayerState` snapshot per registered ID and
one adapter per ID. Snapshots change only when a value changes. State contains visibility,
opacity, availability and an internal active/content gate. `IsRenderable` requires active,
visible, positive opacity and Available. Loading and Unavailable retain user intent while
suppressing content. Registration is independent of availability; an unavailable layer
still exists in the definitions and state lookup.

Visibility and opacity are the only capability flags. Unsupported operations throw
InvalidOperationException; invalid opacity (nonfinite or outside 0..1) is rejected.
Camera framing and celestial rays support visibility. Basemap, observer pin and the
joint environmental fill have no new user controls. Availability remains runtime state.
Internal active gates cannot deactivate the exclusive basemap slot.

## Propagation and existing rendering

All mutations run on the owning UI thread; a future asynchronous data provider must
marshal availability notifications there. This does not add another dispatcher or timer.
Setting an existing value returns false without a new state snapshot, adapter call,
attribution enumeration or invalidation. An actual change applies only that ID's adapter.

`MapsuiLayerStateAdapter` writes native Enabled/Opacity only when different, then requests
one Map.RefreshGraphics redraw for that adapter application. Native property mutation alone
did not raise the map redraw event in the deterministic test, so this is explicit.
It does not replace native layers or request redraws for unchanged values.
`DelegateMapLayerStateAdapter` supports custom/shared rendering without a universal
renderer interface. Adapters are bound once during view construction, not on attachment.

The existing shared MapOverlay uses one cohesive adapter method. Existing
`CameraOverlayReady`/`CelestialOverlaysReady` bindings feed internal active gates.
Runtime visibility cannot bypass those readiness gates or write back into their bindings.
The environmental fill additionally follows effective camera visibility and the existing
positive ShadingOpacityPercent gate. Its fallback fill and joint base/weather/terrain
shader stay together; current shading opacity still comes from FramingSettings, not a
new duplicate preference. Availability can suppress the fill without suppressing framing
boundaries. The observer pin has an internal availability gate for drawing/hit testing,
but no user visibility/opacity control or persisted state.

Physical order remains basemap, combined fill, framing boundaries, celestial rays,
existing diagnostics, observer pin/activity, attribution chrome. There is no per-layer
surface split. Terrain providers, EnvironmentalTileCache, HorizonService, analytical
obstruction, sampling and FoV calculations were untouched. Minimap behaviour is unchanged.

Event-driven viewport invalidation, UI immediacy, Render-priority worker posts,
render-time viewport reads, generation rejection/coalescing, pin interactions and the
activity/diagnostics-only 33 ms timer are unchanged. No pin benchmark was rerun.

## Active attribution and lifecycle

Attribution is cached on actual state/selection changes, in composition order, deduplicated
by ordinal credit text. The active basemap always contributes. Other layers contribute
only while renderable; hidden/inactive/unavailable/loading/zero-opacity content does not.
None of the current providers requires credit while hidden, so no speculative licence
exception flag was added. A future licence with such a requirement needs an explicit policy.

The original TextBlock and styling are retained. One AttributionChanged subscription is
installed on attachment and removed on detachment; attachment also refreshes the cached
text to catch detached changes. Internal adapter references are view-owned, not global
subscriptions. They remain bound through reattachment without creating extra handlers.

## Basemap and native ownership

Exactly one logical basemap slot remains active. Selecting the same adapter instance is
a no-op; a different instance can intentionally replace the implementation even if its
semantic kind is still Street. There is no selection UI or new basemap kind.
Native preparation/installation occurs before removing the working basemap; creation,
validation or installation failure leaves the previous selection and attribution intact.
Successful replacement changes only the basemap's native layers/state/active metadata;
all composition definitions and unrelated states remain identical. Removed layers are
disposed, and MapControl retains Map lifetime ownership. Providers supply fresh,
exclusively owned native layers; adapters are synchronous property appliers, not loaders.

`InstallNativeOverlay` installs a registered layer once, ordered by the immutable semantic
stack. Visibility/opacity changes use the adapter thereafter. Native layers always remain
inside MapControl below the shared floating overlay; semantic group labels do not make a
native Interaction layer appear above Avalonia content. Such a future renderer must select
an appropriate surface. This preserves Phase 2's intentional rendering freedom and order.

## Preferences

`MapLayerPreferences.Capture/Restore` is a storage-neutral, JSON-roundtrip-tested API.
A definition must explicitly opt its supported visibility/opacity fields into persistence.
Restore ignores unknown IDs, unsupported fields and invalid stored values. It never
changes availability or active gates. Snapshots, loading, drag preview, pin animation,
diagnostics and render resources are not serializable preference fields.

All current production registrations opt out: existing settings remain authoritative and
the new capture returns an empty collection. No AppSettings fields, schema migration or
disk writes were added. Basemap selection persistence will need a stable choice mapping
when a second implementation is introduced; only Street/OSM exists today.

## Future optional layer example (illustrative only)

The following sketches registration and wiring using an already-created future renderer.
It supplies no Lorenz implementation, provider metadata, download code or data source.

```csharp
var controls = MapLayerCapabilities.Visibility | MapLayerCapabilities.Opacity;
var definition = new MapLayerDefinition("light-pollution", MapLayerGroup.Environmental,
    -10, futureProviderMetadata, controls, PersistedPreferences: controls);
var map = new PlannerMapComposition(additionalLayers: [definition]);
map.Runtime.SetAvailability("light-pollution", MapLayerAvailability.Unavailable);
map.Runtime.SetVisibility("light-pollution", false);
map.Runtime.SetOpacity("light-pollution", 0.5);
map.InstallNativeOverlay("light-pollution", futureNativeLayers);
var view = new NoctaxisMapView(map);

// Later, on the UI thread, once data is usable:
map.Runtime.SetAvailability("light-pollution", MapLayerAvailability.Available);
map.Runtime.SetVisibility("light-pollution", true);
// Native properties and the existing attribution TextBlock update automatically.
// Optional host persistence uses MapLayerPreferences.Capture/Restore.
```

A custom renderer can instead bind an IMapLayerStateAdapter through Runtime.BindAdapter.
No new Planner-specific state handler is necessary. Choosing/adding its actual drawing
surface remains the renderer's responsibility, rather than the state model's.

## Files and verification

Production changes: `Mapping/MapComposition.cs`, `Mapping/PlannerMapComposition.cs`,
`Controls/NoctaxisMapView.cs`. New production files: `Mapping/MapLayerStateController.cs`,
`Mapping/MapLayerStateAdapters.cs`, `Mapping/MapLayerPreferences.cs`.
New tests: `Noctaxis.Desktop.Tests/MapLayerStateTests.cs`. Existing tests are unchanged.

Coverage includes stable definitions/state identities, no-op propagation, targeted native
properties, capability/opacity validation, availability recovery, cached ordered/deduplicated
attribution, replacement/failure preservation, semantic native installation, selective
preference serialization/restoration, existing readiness gates, live TextBlock updates,
and exact subscription counts across repeated detach/reattach plus navigation.

Build outputs and TRX files are isolated under `artifacts/runtime-state-tests` and
`artifacts/runtime-state-results`.

- Added 13 deterministic runtime-state tests.
- Focused Release verification: 173 passed, 0 failed, 0 skipped, 1 minute 9 seconds.
  Includes all new tests, existing composition/synchronization/viewport/pin tests,
  activation, view-model, relevant settings, persistence and external-map tests.
  Evidence: `artifacts/runtime-state-results/focused-verified.trx`.
- Full desktop Release suite: 755 passed, 0 failed, 0 skipped, 1 minute 37 seconds.
  Evidence: `artifacts/runtime-state-results/desktop-release.trx`.
- A new native-redraw assertion initially failed because property mutation did not emit
  Map.RefreshGraphicsRequest. The adapter now explicitly requests one redraw when values
  change. The assertion was retained and passed; existing tests/timeouts were not altered.
- Build completed without warnings/errors. Changed/new file whitespace checks passed.
- SHA-256 checks against the pre-phase snapshot verified that MapCompositionTests,
  OverlaySynchronizationTests, ViewportRenderTests, PlannerPinProbeTests and
  ViewportRedrawScheduler were unchanged.

No unresolved regression was observed in this verification. Limits are the intentionally
unwired new preference storage, UI-thread mutation contract, native-versus-floating
surface boundary, and future licence-specific hidden-credit policy described above.

No timing/allocation benchmark or native visual comparison was run. Performance neutrality
is based on architecture/code inspection and regression coverage, not measured timing:
pan/zoom does not reconstruct definitions, states, adapters, native layers or attribution.
Rendering reads only cached booleans for the new availability gates; metadata processing
and graph work remain outside the hot path.
