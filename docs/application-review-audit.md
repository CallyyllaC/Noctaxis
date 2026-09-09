# Application review — 9 September 2026

## Scope and baseline

This audit precedes this pass's UI changes. The starting working tree already has extensive terrain, camera, minimap, startup and cache changes; those are preserved. This is a source-level inventory of every XAML page/dialog/control, its commands, settings and persistence. Live desktop usability and GPU behaviour require separate verification.

## Decisions

| Surface | Keep | Change / disposition |
| --- | --- | --- |
| Window and navigation | Locations / Planner / Settings; custom window actions | Keep established shell. Narrow-window layout needs rendered checking, especially the fixed inspector and tall terrain panel. |
| Locations | Current location, search/device actions, cards, favourites, sorting, attribution, edit/delete confirmation | Remove duplicate first-run save button (current-location action and add card already provide it). Rename WSF maintenance actions in ordinary UI; keep provider attribution. |
| Location search | Shared search control in dialog and Planner | These entry points serve distinct workflows; retain both. Keep errors, results and attribution. |
| Location editor | Name, description, read-only coordinates, save/cancel | Keep. Coordinates are edited through the planning pin. |
| Planner time/export | Date/time entry, day buttons, Now, zone, PNG actions, time/date sliders, horizon graph | Entry and scrubbing are complementary, not duplicates. Explain launch-time reset in Time settings. |
| Planner location | Collapsed location editor, precise coordinates, resolved read-only ground elevation | Keep read-only calculated elevation and conditional legacy override reset. Retain deliberate saved location updates. |
| Targets | Visibility and primary-target selection | Planner selection versus Settings catalogue management is useful; visibility currently shares live state. Correct misleading “Default visibility” tooltip instead of inventing a second preference model. |
| Camera | Equipment selection, focal length, bearing/pitch, orientation, calculated FoV | Keep paired slider/precise entry. Clarify accessibility names and sensor measurement units. |
| Weather / terrain inspector | Collapsed production summaries, refresh, visibility/obstruction | Keep actual planning information. Internal sample/provider inspection belongs in diagnostics. |
| Minimap / terrain frame | Permanent upper-left geographic minimap, production terrain frame and FoV | Keep shared production pipeline, cancellation and raster reuse. Debug-labelled service names are historical API debt, not a second pipeline. |
| General | Units, existing disabled support placeholder | Preserve deliberate supporter placeholder. |
| Equipment | Camera height and editable equipment catalogue | Keep; replace implementation wording such as “canonical”. |
| Time | Zone and scrub interval preferences | Keep; explain current-time startup separately from persisted settings. |
| Data | Cache capacity/clear and image maintenance | Keep destructive confirmation. Progressively disclose bulk image repair actions and allow wrapping. |
| Appearance | Production terrain switch and framing display preferences | Add a separate terrain section; place fine rendering controls in an advanced expander. Do not change rendering defaults. |
| Weather settings | Requested/displayed fields and cache radius | Keep, including field groups; cache correctness and timeout handling need regression tests. |
| Celestial settings | Catalogue search/filter/add/reorder/remove/default restore | Keep shared catalogue, limits and primary selection; disclose current live visibility semantics. |
| Debug settings | Opt-in raw profile, ray/bearing/source/cache details and snapshot copy | Move polar renderer and diagnostic presentation into explicit Diagnostics ownership; production minimap remains independent. |
| Confirmation / export dialogs | Existing native storage selection and confirmation | Keep, review failure paths and resource disposal. |

## Persisted value classification

`state.json` is automatic working-state persistence, not an explicit saved historical plan.

| Values | Classification / policy |
| --- | --- |
| Units, selected zone, time snap | Preferences; retain. |
| Weather fields / cache distance | Preferences; retain. |
| Configured celestial list, ordering, visibility, primary target | Deliberate choices; retain existing shared Settings/Planner semantics. |
| Equipment catalogue, selected camera/lens, focal length, orientation | Deliberate equipment choices; retain. |
| Framing visibility, shading, line width, bearing/pitch, terrain colour/coverage | Deliberate camera/display configuration; retain. |
| Camera height, terrain enable, diagnostics enable, cache limit, minimap context | Preferences; retain, diagnostics defaults off. |
| Saved locations: IDs, coordinates, names, descriptions, favourites, sort metadata, zone, attribution | User data; retain. |
| Last custom coordinate, selected/recent saved location | Useful deliberate location continuity; retain. |
| Planning instant | Session state; keep serialization compatibility, replace with current rounded time on automatic launch. Test DST as well as date rollover. |
| Session zone | Preserve saved-location zone; otherwise configured zone. |
| Resolved terrain elevation / profile / weather / render state | Derived data; recompute using existing production refresh and cache validity rules. Preserve deliberate manual elevation overrides from older data. |
| Search query, loading/error/progress, pending tasks, page/inspector expansion | Transient UI state; do not add persistence. |

## Hardening priorities

Run the whole existing suite first. Add regression evidence for concrete defects before fixes. Measure real production methods and headless desktop rendering with reproducible workloads before performance changes; distinguish this from native-window/GPU, live-provider and Linux verification. Inspect cache bounds, cancellation, persisted writes, card bitmap lifetime and UI-thread work. Do not infer optimisation needs from syntax alone or change terrain numerical semantics for speed.
