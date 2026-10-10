# Noctaxis Roadmap

Legend:
- ✅ roadmap-complete
- 🟡 substantially present / needs finishing
- ⬜ not implemented
- 🚫 deliberately not planned

“Done” here means done enough for that roadmap item, not “never touch this code again because computers famously respect such declarations.”


## Alpha, current development release

### ✅ Core manual Planner
Map/pin, planning date/time, current-time startup behaviour, targets, camera bearing/pitch, framing and FoV.

### ✅ Saved locations
Location cards, favourites, current/device location, imagery/metadata and normal saved-location workflow.

### ✅ Target selection/details
Target list, Focus, focused-target details, visibility controls and Follow with camera.

### ✅ Sun/Moon astronomy context
Sunrise/sunset, moonrise/moonset, paths, darkness/twilight context and time-dependent planning.

### ✅ Camera/lens equipment model
Saved equipment, camera/sensor data, lens selection, focal length/orientation and Planner framing integration.

### ✅ Terrain elevation and horizon calculations
Terrarium DEM, observer elevation, horizon/profile generation and obstruction semantics.

### ✅ Terrain plan-view visualisation
Terrain fan/cone behaviour and the semantics we spent roughly one geological era debugging.

### ✅ Camera frame
Physical camera-direction view with real FoV projection, terrain skyline, bearing/pitch scales and obstruction context.

### ✅ Planner PiP map
Permanent secondary map view, independent zoom, Follow pin behaviour, layer-aware rendering and responsive sizing.

### ✅ Production/debug separation
PiP/Camera frame are normal product features; diagnostic terrain tooling lives behind Diagnostics/developer access.

### ✅ Weather integration
Selected location/time weather, grouped Planner presentation and configurable forecast fields.

### ✅ Planner UI reorganisation
Docking workspace, Inspector tabs, Timeline, PiP/Camera-frame overlays, duplicate controls removed and responsibilities cleaned up.

### ✅ Settings/defaults review
Stored preferences versus session/startup state separated sensibly; Settings reorganised and cleaned up.

### ✅ Appearance system
Noctaxis, High Contrast, Neon, Foxfire, Deep Space and Moonlit families with light/dark behaviour and semantic resources.

### ✅ Accessibility appearance controls
Colour-vision variants, 100–200% text scaling, non-colour target line patterns and documented accessibility behaviour.

### ✅ Theme precedence hardening
Themed values act as defaults while explicit local/style values survive live theme, appearance and colour-vision changes.

### ✅ Supporter themes
Neon, Foxfire, Deep Space and Moonlit.

### ✅ Offline Awoo supporter licensing
Frozen protocol, Ed25519 verification, activation/replacement, persistence and supporter-theme gating.

### ✅ Production supporter licence smoke test
Verified against the production Awoo signing setup and real Noctaxis activation flow.

### ✅ Ko-fi support integration
Support section and external Ko-fi button.

### ✅ Large regression/test foundation
Extensive Core/Desktop regression coverage across Planner, rendering, accessibility, themes, terrain and import/export.

### ✅ Major terrain/performance hardening
Bounded caches, cancellation/lifecycle fixes, GPU-path investigation and removal of the major allocation/performance disasters found so far.

### ✅ Theme/UI state hardening
Fluent/theme-resource ownership and explicit-value precedence are deliberately controlled by Noctaxis rather than left to framework luck.

### 🟡 Responsive populated-data UI pass
Automated high-scale/responsive coverage is extensive, and the Planner has been tuned around a 1080p reference layout, but native dogfooding at awkward sizes still deserves abuse.

### 🟡 Native accessibility validation
Contrast, colour vision, scaling, accessible names and keyboard behaviours are heavily tested, but Windows accessibility-tree/screen-reader validation still needs a proper manual pass.

### 🟡 Long-session/lifecycle validation
Cancellation/lifecycle architecture is strong, but hours-long native use, repeated navigation and shutdown still deserve deliberate testing.

### ✅ Light-pollution map layer
David Lorenz data integrated into the Planner.

### ✅ Overall “is this shot viable?” presentation
Combined photographer-facing viability presentation drawing on terrain, Moon, darkness, weather, target visibility and timing.

### ✅ Maps/location external links
Open in Maps is implemented. Street View integration was intentionally removed from scope.

### ✅ Investigate whether the planning pin itself should become a map-rendered layer rather than a separate interaction/rendering element

### ✅ OpenFreeMap themed vector basemap
Noctaxis-owned styling integrated with application themes.

### ✅ EOX Sentinel-2 Cloudless satellite imagery support

### ✅ Terrain/topographic map presentation
Terrarium-backed terrain/topographic presentation is implemented and integrated with the Planner.


### ✅ Add macOS support
macOS Intel and Apple Silicon courtesy builds added, with automated verification where available. Windows remains the officially developed and manually tested platform.

### ✅ Investigate Windows/macOS code signing and notarisation
Investigated signing, notarisation and practical distribution requirements. macOS uses ad-hoc signing; paid certificates and developer subscriptions are not part of the current release plan.

### 🟡 Create a Wiki
Wiki structure is in place; user-facing documentation and screenshots are being reviewed and polished for publication.


## v1.0, first proper public release

### 🟡 End-to-end dogfooding
Use Noctaxis for several real photography plans and fix workflow annoyances rather than inventing more infrastructure.

### ⬜ Automatic update check on startup
- non-forced
- small unobtrusive notification
- Skip this version
- Go to GitHub Releases
- remember skipped versions so it does not nag forever like a printer driver from 2007

### 🟡 Windows release packaging
Release builds are mature; installer/distribution/update packaging still needs final release treatment.

### ⬜ Theme dropdown presentation polish
Separate the visible theme availability label from tooltip details: standard themes show no redundant status beside their names, while tooltips identify them as Standard. Supporter and locked-theme labels remain informative. Keep this independent of the tooltip crash/full-row selection fixes.

### ⬜ Settings page layout and space utilisation
Review every Settings page for sensible use of available horizontal and vertical space across typical and constrained window sizes and text scales. Eliminate excessive empty space, awkward stretching and unnecessary scrolling without sacrificing readability, accessibility or responsive behaviour.

### ⬜ Show celestial objects in the Planner camera frame
Render relevant celestial objects in the camera-direction frame at their projected positions for the selected location, time and camera framing, so their placement can be checked against the field of view and terrain horizon.

### ⬜ Camera ISO and lens aperture data
Add camera ISO and lens aperture (f-number) data to the appropriate equipment and capture settings, and expose them wherever useful for planning and exported capture information.

### 🟡 Version-aware portable plan import/export
Audit `Noctaxis.Plan` schema compatibility before public release. Import supported older exports with explicit migrations, validate versions before applying any state, and reject unknown newer schemas unless their compatibility is explicitly guaranteed. Ignore safe unknown additive metadata where appropriate; never silently reinterpret changed fields or import stale calculated data. Cover older/current/newer versions with regression fixtures.

### ⬜ Final first-run/release polish
Version metadata, release-facing wording, documentation, clean-machine testing and release presentation.

### 🟡 Native GPU/live-provider smoke pass
Automated/headless validation is strong; verify the assembled app with real maps, weather, terrain and hardware acceleration on release machines.

### ⬜ Focused performance pass
Profile and fix measured PiP overhead, panel-drag latency, Layers opening latency and unnecessary map invalidation/recomposition.

### ⬜ Final general performance pass
Startup, long-session allocations, binding churn, fetch churn and trace-based remaining hotspots only.

### 🚫 Freeze v1 feature scope once the above is satisfactory


## v1.0 export / portable plans

### ✅ Canonical planning-sheet export
One detached renderer shared by Save PNG and Copy image rather than capturing the desktop UI.

### ✅ Single-target field export
Export is centred on the focused target rather than dumping every visible target into one chart.

### ⬜ Multiple-target field export
Extend the field export to include several deliberately selected targets in a single plan, while keeping the completed focused-target export available.

### ✅ Direction-over-time export Timeline
Focused target bearing/azimuth and pitch/altitude across the planning window, with darkness, terrain/horizon and useful event context.

### ✅ Clean export map
Themed geographical context, planning point, exact coordinates and attribution, without Planner overlays.

### ✅ Weather summary panel
Compact sampled weather across the exported planning window.

### ✅ Equipment/capture header
Camera, lens, focal length and FoV shown as human-readable planning context.

### ✅ Importable Noctaxis plan images
Exported PNG embeds a minimal versioned `Noctaxis.Plan` payload.

Stored state:
- UTC date/time
- latitude/longitude
- camera bearing
- camera pitch
- focused target, if any
- Follow state, when applicable

Derived/stale information is deliberately not embedded.

### ✅ Image-plan import
- Open image…
- Load from clipboard
- drag-and-drop exported PNG
- transactional validation before changing Planner state
- terrain, weather and astronomy recalculated fresh after import

### ✅ Final export visual polish
Proportions, Timeline height, weather spacing, friendly timezone labels and final native visual review.

### ⬜ Detailed print/PDF planning-sheet export
Create a printer-friendly, substantially more detailed field plan with a clean layout suitable for physical printing or saving as PDF. Include the relevant location, time, target, terrain, weather, equipment and capture context without relying on the compact image-export layout.


## v2, automatic scouting / intelligent planning

### ⬜ Automatic location finder/scouting
Rather than manually choosing every observing site.

### ⬜ Candidate location discovery
Including parking areas, parks, lay-bys and other sensible accessible sites.

### ⬜ Hard user constraints
Including:
- minimum target coverage/visibility during a requested window
- required angular/terrain clearance
- framing margin
- strict maximum Bortle/light-pollution level
- Moon constraints
- maximum search radius
- acceptable location/site types
- relevant weather constraints

### ⬜ Adaptive search radius
Search nearby first, then expand toward the configured maximum if nothing satisfies the constraints.

### ⬜ Soft ranking after hard filtering
Once unacceptable candidates are removed, rank the survivors by quality rather than allowing a mystery score to override explicit requirements.

### ⬜ Combined site visibility model
Terrain, settlement/building context, weather/cloud and light pollution combined for scouting decisions.

### ⬜ Automatic date/time opportunity search
Not merely “where?”, but “where and when is this target actually worth shooting?”

### ⬜ Location comparison/scouting results UI
Show why one candidate is better than another.

### ⬜ Broader astronomy opportunity discovery
Build on the planning engine without turning Noctaxis into a generic planetarium.

### ⬜ Suggested capture settings
Use the selected target, current equipment and planning conditions to provide practical capture guidance.

Initial scope:
- suggested exposure/shutter duration

Potential later extension:
- aperture
- ISO
- warnings about trailing/tracking limitations
- target/equipment-aware capture recommendations

This should integrate naturally with the export header so a field plan can eventually include the suggested capture settings.

### ✅ Expand map layers into PiP
PiP shares the Planner map composition and supports its relevant map layers.

### ⬜ Improve topographic map rendering
Refine the existing terrain/topographic presentation further, potentially including:
- richer hillshade
- contour treatment
- improved relief readability
- stronger elevation hierarchy
- other map-specific refinements that prove useful for field planning


## v3 and beyond

### ⬜ Aurora watch/chance indicator

### ⬜ Optional desktop alerts for genuinely useful astronomy opportunities
Aurora is the obvious initial case.

### ⬜ Simple 3D visualisation
Solid 2D first, lightweight 3D later only if it genuinely improves planning.

### ⬜ Further opportunity/discovery features
Once v2 scouting proves what photographers actually find useful, rather than designing a prophetic AI horoscope generator in advance.


## Explicit non-goals / things we have intentionally killed

### 🚫 Full offline terrain/weather/data mode
Export-first won because stale weather and enormous geographic caches solve the wrong problem.

### 🚫 Importing stale calculated data from exported plan images
Plan images restore the minimum state needed to reproduce the plan; current terrain, weather and astronomy are recalculated when reopened.

### 🚫 Treating exported plan images as full project/save files
They are compact, durable reconstruction seeds plus a human-readable snapshot, not MS Word documents carrying seventeen generations of archaeological state.
