# Planner panels

The Planner's fixed layout has been replaced by a dockable panel system. The map is the permanent
central workspace; every other region of the Planner is a panel that can be docked to any edge,
floated over the map, or unpinned to an edge tab. Panels stay inside the Planner: there are no
detachable operating-system windows.

This was a layout and architecture change. The contents of the panels were moved, not redesigned.

## The pieces

| | |
| --- | --- |
| `Noctaxis.Core/Domain/PlannerDockLayout.cs` | `PlannerPanelLayout` (one panel's placement), `DockCornerOwnership` (which dock reaches into each corner) and `PlannerDockLayout` (the saved set). Pure data: limits, clamping and merging. |
| `AppSettings.PlannerDock` | Where the layout is saved, through the existing settings file and save pipeline. |
| `ViewModels/PlannerDockViewModel.cs` | `PlannerPanelViewModel` (one panel's state and its commands) and `PlannerDockViewModel` (the panel set, the default layout, reset). |
| `ViewModels/MainViewModel.PlannerPanels.cs` | The one route from a panel move to the settings file. |
| `Controls/Docking/DockWorkspace.cs` | A `Panel` that lays out the map, the docks, the floating panels, the edge tabs and the drop target. |
| `Controls/Docking/PlannerPanelHost.cs` | One panel's chrome: title, pin, menu, close, drag and resize. |
| `Controls/Docking/PanelEdgeStrip.cs` | The tabs for unpinned panels on one edge. |
| `Controls/Docking/DockingStyles.axaml` | The chrome's template and the styles for tabs, grips and the drop hint. |

```
PlannerPage (the Planner tab)
├── time bar           ‹ date › ‹ time › Now, the time zone; Export ▾ on the right
└── DockWorkspace
    ├── central        PlannerMapView + map-attached chrome (Layers, refresh strip, pin preview)
    ├── PlannerPanelHost × 4   declared in MainWindow.axaml, placed by the workspace
    └── PanelEdgeStrip × 4     the tabs for unpinned panels
```

## Panels never move in the tree

Every panel is declared once, in the Planner's own markup, and stays a child of the workspace for the
window's lifetime. Changing a panel's state changes where the workspace *arranges* it, never which
parent it belongs to. Nothing is reparented, so no panel's content is torn down and rebuilt by
docking, floating or unpinning it, and nothing the user has typed into a panel is lost by moving it.

The workspace reads each panel's state and arranges it accordingly; the content inside a panel never
learns which state it is in. `ZIndex` keeps docked panels under the map's own chrome and floating and
opened panels above it.

## The regions that became panels

| Panel | Was | Default |
| --- | --- | --- |
| `inspector` | the *Selected targets*, *Weather conditions* and *Camera and framing* headings | docked right, pinned, 420 px, on the Targets tab |
| `timeline` | the fixed strip under the map | docked bottom, pinned, 300 px |

The planning detail is one panel, the **Inspector**, with a tab per subject — **Targets**,
**Weather**, **Equipment** — rather than panels stacked down the right to scroll past. Only the open tab takes the room, so each subject gets the column's full height; Camera and
framing, which scrolled in a half-height panel, now fits. Each tab holds its old region's markup
unchanged, in its own scroller under a tab strip that stays in view: the host opts out of its own
scroller with the `selfScrolling` class so the content can decide what stays put. Switching tabs never
rebuilds anything — the controls are the same instances, bound to the same view model. Focused target details lead the Targets tab, always open.

The tab shown is saved as `PlannerPanelLayout.SelectedTab`, a stable id rather than a position, so it
rides the existing save, merge and reset paths. An id the Inspector does not have opens the first tab.
That includes `terrain`, which an earlier build saved while the Inspector still had a Terrain tab.

## Sizing the map's instruments

The PiP (top-left) and the Camera frame (bottom-left) stay where they are; only their size changes. Each
has a preferred width saved as a share of the map's own width (`PlannerOverlaySizes`, default 0.15 each:
about their old size on a full-HD map), so they keep their proportion as the window and the docked panels
change. `MapInstrumentLayout` resolves the two together on every map size change:

- each is kept between its minimum (PiP 120 px, Camera frame card 210 px, measured from what each must show
  unclipped) and a third of the map's width; the PiP stays square, and the Camera frame's frame keeps its
  shape, so the card's height follows its width;
- the PiP's bottom stays at least 8 px above the bottom-left row, 12 px in from the map's edges;
- when the map is too short for both preferences, both shrink in proportion until one reaches its minimum,
  then the other; when even both minimums do not fit, the Camera frame (which aims the camera) stays and the
  PiP is set aside (hidden, not released) until there is room. At the default 1440 × 900 window both fit.

A resolved size is for now: the preference is never overwritten by it, so a larger map brings the
instruments back towards it. Each has a resize grip — the PiP's at its bottom-right, beside Switch and
Follow pin; the Camera frame's at its top-right, beside its legend — that changes that instrument's width
only. A drag (mouse, pen or touch) resizes it live and saves its new share when the drag ends; with the grip
focused, the arrow keys step it 16 px and save each step. The grip takes its own pointer, keys and wheel, so
nothing reaches the map, the PiP's view or the camera direction, and resizing the PiP keeps its map's centre
and zoom. Nothing is rebuilt: only sizes change.

## The time bar

The Planner's bar is one line: the time group anchored left, then the planning location, then its two
actions and Export anchored right:
`‹ 05 Mar 2026 ›  ‹ 21:00 ›  Now  GMT   📍 Brigg · 53.55619°, -0.48535° · 8.0 m ASL   Save location   Open in Maps   ⇩ Export ▾`.
The location is the one part that gives way (see *Where the pin is*); when even its room is gone the bar
turns compact — closer spacing, no minimum widths on the date and time, and Export without its chevron —
rather than hiding, shortening or wrapping anything, so all of it fits down to the 720 px minimum window.

- **Date**: the arrows move the plan one day (*Previous day*, *Next day*), at the same local time. The
  date itself (in the Planner's `dd MMM yyyy` format) opens a calendar on the plan's date; a day clicked or
  tapped, or chosen with the arrow keys and Enter, becomes the plan's date, and Escape leaves it.
- **Time**: the arrows move the local time exactly 30 minutes on the clock (*Previous 30 minutes*, *Next
  30 minutes*), carrying the date across midnight either way (`MainViewModel.StepTime`). The time opens
  a time picker on the plan's time; accepting it sets the time on the same date.
- **Now** is the present moment, from the Planner's clock, in the plan's time zone.
- **The time zone** is read-only: its short name at the plan's moment (GMT, BST; an offset such as UTC+4
  where the zone has no short name), never how Settings chose it, so the system zone shows the system's
  zone. A machine zone with a Windows identifier is named by its IANA equivalent (`TimeZoneDisplay`). Its
  tooltip and accessible help add the identifier and offset; it is changed in Settings.
- **Export ▾** opens *Save PNG* and *Copy PNG*, and **Import ▾** beside it *Open plan image…* and *Load from
  clipboard* (see `docs/plan-images.md`). Export shares one
  treatment with Save location and Open in Maps (`Button.barAction`: an accent outline that holds through hover
  and press, body-size semi-bold labels, the same height and padding), so the three read as the bar's actions;
  Export alone adds its glyph and menu chevron. Compact, Save location and Open in Maps keep the outline but
  drop to small regular text, so the bar still fits the narrowest window. None of them is icon-only.

All of it reads and writes the Planner's one date and time (`LocalDate`, `TimeText`); there is no second
time model. The arrows are 32 px touch targets drawn small; the values are buttons that look like
buttons. On a narrow window the time group wraps before it is squeezed, and Export keeps its place at
the right.

## The Timeline

The Timeline is one window of 24 hours on from where the plan's moment was last set, drawn as a chart that is
also its own scrubber. It has no date or time of its own: the bar is where they are set, and the bar shows what
the chart chooses.

- **The window** (`MainViewModel.TimelineStart`, `TimelineEnd`) starts at the plan's moment whenever that is
  set directly — a date or time from the bar, its arrows or pickers, or Now — and runs exactly 24 hours,
  across midnight if it falls there; it is never clamped to the calendar day. Scrubbing never moves it, during
  a drag or after it: the next change from the bar re-anchors it. If the moment is ever set outside the window
  by another route, the window follows it there.
- **Scrubbing** is on the chart itself (`HorizonGraph.ScrubMinutes`, bound two-way to `TimelineOffsetMinutes`,
  0–1440 minutes from the start). A tap or click anywhere on it moves the moment to that time across it, and a
  drag moves it continuously: by X alone, whatever the height, clamped to the window, with the pointer held by
  the chart so nothing beneath it is selected. Mouse, pen and touch are the same gesture; there is no pinch.
  The moment previews as it moves and commits when the pointer is let go (`ScrubCompleted`), through the
  existing temporal preview path; it lands on the configured time snap of the clock, and crossing midnight
  changes the bar's date as it should.
- **The keyboard**: the chart takes focus (its cursor's tab shows a ring); Left and Right move the moment one
  time snap, Page Up and Page Down an hour, Home and End to the window's ends. Its value is said in words to
  assistive technology (*06 Mar 01:00*), followed by the sky cues it draws, each with its time.
- **The paths** come from `IPlanningService.CalculateTimelineAsync`: the same astronomy as the day's paths,
  sampled every 10 minutes from the anchor, and calculated only when the window, the place, the zone or the
  targets change — never while scrubbing. The snapshot's own paths stay on the local day, so the export card and
  terrain crossings are unchanged.

What the chart shows, strongest first:

- the selected moment: the one strong line, in the theme's accent, with a small tab (two grip strokes) on it
  above the plot that says it can be moved, and its time beside the tab — on the right, or on the left when the
  right has no room, never off the plot. It follows a drag at once (`TimelineSelectedInstant`), before the
  plan's recalculation lands;
- the paths, in each target's colour and pattern and named at their peaks, with the focused target's rise, transit and set
  marked faintly where they fall inside the window;
- astronomical darkness: a very faint band of the theme's accent (10%) across the plot while the Sun is more
  than 18° down, behind everything else on it, terrain included. It runs from the window's edge when the
  window opens or closes dark, across all of it when darkness never ends, and is absent when it never begins;
- midnight: a firmer divider in place of any hour guide, labelled on the hour scale with the new date (*04 Oct*);
- the 0° horizon: the geometric horizon, a firm line drawn over the terrain beneath it so the two are never
  confused, with 30° and 60° as faint guides and 90° as the top's label;
- a faint major guide every 3 hours, and a fainter dashed minor guide on each other whole hour while the hours
  are at least 9 px apart (`MinimumMinorGuideSpacing`); narrower, the minors go first and the majors and midnight
  stay. All of them are drawn behind the paths. Labels sit every 3, 6 or 12 hours — the smallest step whose
  labels have room — never overlapping, with the midnight date always shown;
- sky cues: short ticks rising from the time axis at sunrise and sunset, where astronomical darkness begins
  and ends (*Astro dark*, *Astro dawn*), and at moonrise and moonset. Each has its name and time just above the
  axis (*Sunset 18:37*), stacked one row higher when another label is in the way. Failing that it shows the
  name alone, and failing that no label, never one over another; the Sun's labels come first, then
  darkness's, then the Moon's. Sunrise and sunset give way while the Sun is the focused target, whose own rise
  and set marks are drawn. Moonrise and moonset appear only while the Moon's own path is hidden, since a shown
  path already crosses the horizon there (`HorizonGraph.ShowsEvent`);
- the real present moment, faint and dotted, and only when it falls inside the window.

The sky cues come from `IPlanningService.CalculateTimelineEventsAsync`. It makes the same per-day Sun and Moon
calculations the plan already uses (`IAstronomyService.Calculate`, which gives the Sun's twilight and the
Moon's rise and set), for each local day the window touches. It keeps the events inside the window and notes
whether the window opens in darkness. It runs with the window's paths, once per window, never while drawing or
scrubbing. The chart reads each cue it draws to assistive technology, with its exact time, after the chosen
moment (*06 Mar 18:00. Sunset 18:37, Astronomical darkness begins 20:34*); the band adds nothing more.

The panel ships 190 px tall (it was 300, then 210 with a separate slider), measured to keep the plot about
90 px tall; a layout someone has already sized keeps their size.

## The Picture-in-Picture map

The Planner has two map views over one map architecture: the **main map**, and a smaller
**Picture-in-Picture** (PiP) map fixed at the main map's top-left. The PiP is not a panel and not a
terrain widget: it is a second `PlannerMapView`, made by `PlannerMapView.CreatePictureInPicture()`, in a
constrained mode (`NoctaxisMapView.IsPictureInPicture`). It has the same composition and layer
registrations as the main map, and its basemap is a second `OpenFreeMapBasemap` over the main map's own
`ThemedVectorTileSource`, so decoded vector tiles, the disk cache and the HTTP client are shared, not
repeated. `NoctaxisMapView.ShareEnvironmentalSources` gives it the main map's own `TerrainReliefTileSource`
(shaded relief, computed and cached once for both maps, so after a Switch each finds the other's tiles
ready) and the main map's `LightPollutionDataProvider` (the decoded Lorenz atlas, read and held once).
What is per view is only what must be: its native layers, its layer state, and the tiles each layer has
drawn for its own zoom. Because it borrows the main map's atlas reader, the window releases it before the
main map on close. Each `OpenFreeMapBasemap` remembers the style and label language its own
layers were drawn in, so a theme change restyles both views even though the shared source changes once.

In its constrained mode the PiP draws the pin and a simplified camera frame — the framing outline,
without the environmental shading or the celestial rays — credits nothing itself (the main map's
attribution strip also gives every credit the PiP owes, via `AttributionCompanion`, each credit once) and
takes no input. Its host lays a transparent input layer over it that takes only the wheel; a press, a drag
or a menu over it does nothing, so it cannot be panned and never moves the pin.

**Enhance PiP contrast** is on by default (Settings → Planner map, `PlannerPipSettings.EnhanceContrast`). It draws the
PiP's terrain relief in a stronger tone (`TerrainReliefTileSource.ReliefTone.Enhanced`: shading gains 1.45 and
1.95 against 1.1 and 1.5, capped at 82% rather than 70%), so a small view reads its terrain clearly. The tone is
the PiP relief layer's own and applies when its tiles are drawn from the one shaded grid both maps share: nothing
is loaded or shaded again, and turning it on or off redraws only the PiP's visible relief tiles, once. It is
neutral grey, so no semantic colour changes; the theme's High Contrast factor still applies; the main map, the
layers' visibility and opacity, and light pollution are untouched. With relief off in the PiP it does nothing.

**Roads & labels** are off in the PiP by default (Settings → Planner map → *Show roads & labels*,
`PlannerPipSettings.ShowRoadsAndLabels`). Off, the PiP's `NoctaxisMapView.ShowRoadsAndLabels` gates the
basemap's reference pass (`PlannerMapComposition.BasemapReferenceId`: roads, railways, boundaries and
names) out of the PiP's own composition with the composition's content gate (`IsActive`), the same
mechanism that keeps relief off satellite imagery. Its tiles are then neither fetched nor drawn; the
geography pass, the environmental layers and the camera frame are untouched, and nothing is masked. The
main map's composition is separate, so it keeps its roads and names, and the shared source's style and
label language are never changed. Switch exchanges views, not this setting: the PiP keeps it.

- **Switch** exchanges the two views: centre, zoom and layers. `MainViewModel.SwitchPlannerMapLayers`
  swaps the main map's terrain relief and light pollution settings with the PiP's in one change and one
  save (the light pollution palette is the Planner's one palette and stays put), and the window exchanges
  the two `NoctaxisMapView.MapViewState`s with `ShowView`. Nothing is reparented. The pin, time, targets,
  camera and weather are shared state and never switch. The main map still re-centres on the pin when the
  pin next moves, as it always has.
- **Follow pin** (on by default) makes the PiP centre on the pin whenever it moves
  (`NoctaxisMapView.FollowObserver`). Only the centre: its zoom and layers stay its own. Turned off, it
  keeps its centre, and that centre is saved.
- **The wheel** over the PiP zooms it about its own centre, a step of two per notch, within the map's zoom
  range; at either end it stops. The main map is not touched.
- Its size is the user's: see *Sizing the map's instruments* below.

`PlannerPipSettings` (`AppSettings.PlannerPip`) saves whether it is enabled, Follow pin, its centre, its
zoom (ground metres per pixel), its layers and whether it shows roads & labels. Settings → Planner map → *Show the Picture-in-Picture map*
turns it off: the window then releases it entirely (`ReleasePipAsync`), so it costs nothing, and creates a
fresh one when it is turned back on. To change what it shows, switch it into the main map, change it there
with Layers, and switch it back; it has no layer controls of its own.

Its first zoom, while none is saved, shows 2.5 times as much ground as the main map
(`PlannerPipSettings.InitialContext`, the retired minimap's default). An older settings file's
`TerrainMinimapContext` is not migrated: it was the radius of a terrain grid, not a map view's zoom, so it
is ignored and the shipped defaults apply. Its default layers are the minimap's view on the normal layers: terrain relief at full strength, no light
pollution.

The retired minimap (`LocalTerrainMap`), its dedicated context setting and slider, the terrain surface grid
the Planner fetched only to draw it (`TerrainDebugMapService`), and the "Local terrain map" section of the
terrain diagnostics that described that grid are gone. Horizon and Camera frame calculations never used
that grid: they resolve terrain through `TerrainSurfaceResolver`, which is unchanged.

The PiP's terrain relief is the normal relief layer, so it does not have the old minimap's local-contrast
stretch; that was intentionally not restored. The one existing strength control, the layer's opacity, is
used instead: the PiP defaults to full strength (1) where the main map defaults to 0.6.

## The Camera frame

The Camera frame (called the Terrain frame before; its code keeps the `Terrain` names, which describe the terrain data it shows) is a static instrument laid over the main map's bottom-left, beside the pin's own place,
in one row (`PlannerMapBottomLeft`); the main map's attribution strip leaves that row's width clear. It is
not a panel, so it cannot be docked, pinned or floated, and a saved layout that still names a `terrain`
panel simply drops it. Its internals are unchanged: the frame, the camera-direction instrument and its
Bearing and Pitch line, and the Horizon and Obstruction line beneath — **Horizon** (the terrain horizon
angle along the camera's bearing) and **Obstruction** (the distance to the first terrain blocking the
view) — with the legend and the camera-direction help behind its info button. The frame keeps a 208:128
shape (`TerrainFrameView.DefaultWidth`, `DefaultHeight`) at whatever width the card has, and the camera's shape
inside that.

The wheel over the frame turns the camera's focal length, the same `FocalLength` the Equipment slider sets
(`MainViewModel.StepFocalLength`): each notch is about 5% of the current focal length, at least 1 mm, landing on
a whole millimetre like the slider's ticks, within the selected lens's range; a prime lens does not change. The
field of view and the map's framing follow through the existing bindings. The frame takes the wheel on its way
in (a tunnelling handler), so neither its own value boxes nor the map beneath see it; the wheel over the
Picture-in-Picture map stays that map's zoom. The frame's help flyout and accessible help text say so.

The frame's legend — Near, Far, Sky, Centre, Threshold — is reference rather than something to keep on
screen, so it sits behind the info button beside the frame's heading (*Camera frame legend*). A click,
a tap, or Enter/Space on the focused button opens it in a small flyout; nothing about it needs hover.
Its samples are the frame's own: the depth tones from `TerrainFrameView`'s palette, and the centre and
threshold lines in the frame's own colours, weights and dash, over the sky tone they are drawn across.

The pin's coordinates are not repeated here (the Planner's bar has them), and the source line, the
terrain data state and the observer datum are support information: they are in Settings →
Diagnostics → Terrain diagnostics, with the observer, rather than in the Planner.

Lightweight map-attached controls — **Layers** and the refresh strip — stay with the map. They belong to
the map rather than being regions of their own. Layers is a tab on the map's right edge (named as the
panels' edge tabs are), pressed while its layers are open; they open beside it, over the map, which keeps its
size. Its content is the same pre-built flyout, so it opens at once.

## Inspector → Targets

Targets leads with **Focused target details**, always open: the focused target's name, **Follow with
camera**, then its azimuth, altitude, visibility, local horizon, and rise, transit and set. Beneath it,
the **Targets** heading carries the count of shown targets ("Visible 2 / 8") and an info button for what
the lines mean; then every target with its show/hide checkbox, its line sample (its colour and pattern,
as on the map and timeline; see `docs/accessibility.md`, *Target identity*), and
**Focus**. On the focused target, *Focused* is a label, italic and accented, not a button: it is a
state, and nothing about it invites a press. Internally the focused target is still the primary target.
Adding and removing targets belongs to Settings → Targets, so the rows have no remove button.

**Focus** makes a target the focused one and aims the camera at it: bearing to its azimuth, pitch to its
altitude clamped to -45°..+90°, taken from the same position the details show. When the new target's
position is still being calculated, the aim waits for it. Focusing a hidden target shows it, as before;
hiding the focused target moves focus to another shown target, as before.

**Follow with camera** (saved with the camera's other framing settings) keeps the camera on the focused
target: whenever the Planner calculates a new position for it — a new time, date or place, or a new
focused target — bearing goes to its azimuth and pitch to its altitude, within the pitch limits. While it
is on, the camera-direction instrument, its arrows and its values give way, dimmed and read-only, still
showing the live direction. Turning it off moves nothing. Bearing has always been held relative to the
focused target (a composition offset from its azimuth), so with Follow off the bearing keeps that offset
as the target moves, while pitch stays where it was left.

Rise and set say why there is none. `TargetEvents.Horizon` records, for a day with no rise and no set,
whether the target stayed above or below the horizon all day (from the day's own samples, or for the Sun
and Moon from their altitude at the start of the day). The details then read "Up all day" / "Does not
set" or "Does not rise" / "Down all day"; a rise, transit or set that happens but not within this local
day reads "Not on this date"; "—" means no position has been calculated. "Circumpolar" is not used: the
model knows about this day, not every day.

## Camera direction, in the Camera frame

**What the frame is.** The camera's own view, in angle. Its terrain profile (`CameraTerrainDepth`) is sampled
about every 1° across the camera's real horizontal field of view, centred on the camera's bearing, and in 72
rows across its real vertical field of view, centred on its pitch. Both come from the lens calculator (sensor
size, focal length, Landscape or Portrait), so a focal-length or orientation change changes the span. The
profile is an angular grid. The picture is drawn as the lens would image it (`CameraFrameProjection`):
- each pixel's exact direction, for the camera's bearing and pitch, takes the nearest sample;
- an angle off the axis falls at its tangent, so a wide lens is a true perspective, not a stretched grid of
  angles.

The frame used to stretch the grid linearly into the frame's shape. That was close for normal lenses but
misplaced terrain toward the edges of wide ones.

**Presentation only, never the angles.**
- The farthest terrain is drawn grey 90 rather than 50, so a distant skyline stands clear of the night-blue sky.
- The sky lightens slightly toward the horizon.
- The skyline has a thin light edge.
- The centre (aim) and threshold lines are drawn where their altitudes are.
- The bearing and pitch scales are drawn to the same projection. A tick stands where its angle is in the
  picture, and the pitch scale's longest (0°) tick is where the true horizon is drawn. Tick steps adapt to
  the lens: minor ticks at least 9 px apart, labelled bearings at least 30 px apart.
- The pitch scale carries figures (`CameraDirectionPad.PitchScale`):
  - **Placement:** every tick and figure is placed by the same projection as the skyline (`AltitudeY`).
  - **Ticks:** minor ticks every 5° (1° or 2° on a long lens, 10° on a wide one), at least 7 px apart. Majors
    every 10°, or the next step that is twice a minor and 14 px apart.
  - **Figures:** signed (+20°, 0°, -10°) and on the majors, thinned to every other major when they would crowd.
    They stay inside the picture between the tilt arrows, and give way to the accent pitch mark (the status
    line has the exact pitch).
  - **0°:** the longest, slightly heavier tick with a firm, semibold figure, so the true horizontal is easy to
    find without a line across the picture.
  - **Drawing order:** the pitch scale is drawn before the bearing scale and without a clip of its own. In the
    headless renderer, a figure drawn after another figure's clip had closed came out at twice the bitmap's
    scale, along with everything after it.

Where the camera points is planning state, not equipment, and the Camera frame shows what it sees, so the
camera-direction control is drawn over the frame's picture itself (`CameraFrameStage`). The frame view takes
the picture's own size — the camera's shape, as large as fits in the frame's 208:128 shape — so nothing laid
over it can sit on a band beside the picture, and the control covers exactly the picture. It is an aircraft-
style pair of scales: a compass tape along the picture's bottom edge and a pitch ladder down its left, each
sliding under a fixed accent mark where the camera aims (the ladder's mark is level with the frame's dashed
centre line). The scales are light ticks in fixed colours like the frame's own lines, each over a narrow fade
only where its ticks are (26 px up from the bottom, 22 px in from the left) so they read over sky and terrain
alike; the rest of the picture is exactly as the frame draws it. The fades run from clear *black* to 55%
black. An earlier version faded from `Colors.Transparent`, which is clear *white*, so its mid-tones laid a
light grey veil over the picture's edges that read as two opaque slider slabs on an empty box. The frame
also draws a 1 px outline in the theme's border colour around the picture: its night sky is deliberately
dark, close to the card, and without an edge a frame looking mostly at sky looked blank.

Beneath the picture is one status line, `CameraFrameReadings` — "Bearing 348.2° · Pitch +6.0° · Horizon
+0.7° · Obstruction 80 m" — each label beside its value, on one baseline. `ReadingsPanel` lays it out: a dot
between each two pairs on the same line, and where the card is too narrow for all four, a whole pair moves
to the next line, so no line starts or ends with a dot. The whole line needs about 295 px; the card's content
at its smallest (and default) width is 192 px, where it takes two lines.

The control is `CameraDirectionPad` and four nudge arrows, each a 28 px target that repeats while held: turn
left and right at the tape's ends, tilt up and down at the ladder's, on the left so the top-right corner,
which the header's resize grip reaches over, stays the grip's (the header row is drawn above the picture).
Dragging anywhere on the picture is relative to where the camera points now: across turns the camera
(`DegreesPerPixel`, 0.25°), up tilts it up. Bearing wraps through 0°/360° (2° turned 5° left is 357°; 358°
turned 5° right is 3°) and is shown in 0–360; pitch stops at `CameraFramingSettings`' limits, -45° to +90°.
The arrows and the arrow keys nudge by `NudgeDegrees` (1°), a tenth of that with Shift; the two values read
as text and still take a typed number. Everything writes the existing `CameraBearingDegrees` and
`CameraPitchDegrees`, so the map, the field of view and saving are unchanged, and all of it gives way, still
showing the direction, while Follow with camera is on. The wheel anywhere over the frame, the control
included, stays the focal length; the resize grip resizes only. How to aim it, and the pitch convention, are
in the frame's info flyout beneath the legend.

The card's height falls from its content: the picture, then the status line. With a 3:2 camera at the
smallest width in the default window it is 184 px tall; at widths with room for the status line on one
line it is a line shorter. Before terrain arrives the frame keeps its 208:128 shape, tall enough for its
status text beside the ladder and above the tape.

## Inspector → Equipment

Equipment is the camera and what is on it: Camera and Lens (full-width selectors, the lens's focal range
beneath), Focal length (slider with its value at the end of the row), Orientation, and the Field of view
they make, beside an info button explaining it. Each label sits tight above its control, with a clear gap
between groups; the field of view is prominent but smaller than before, so the selectors lead. The tab's
content keeps 14 px clear of its scroll bar, which lies over the content and would otherwise take the
info button's clicks.

## Where the pin is

There is no planning-location panel and no card on the map. The pin's context is one line in the
Planner's bar, `PlannerLocationText`, always visible: its three parts joined by " · ". The line's tooltip
adds what the altitude is made of and, when a lookup named the place, its attribution.

| Part | Source |
| --- | --- |
| Name, e.g. *Brigg* | `LocationName` — the one name the Planner has for the pin, the same value the old panel's *Location name* field showed, with its fallbacks (*Current location*, a saved location's name, *Near …* from reverse geocoding) |
| `53.55619°, -0.48535°` | `PreviewObserver`, so it follows the pin while it is dragged |
| `8.0 m ASL` | `EffectiveCameraAltitudeMetres`: resolved ground elevation plus camera height, in the measurement profile's height unit |

Clicking or tapping the line, or Enter or Space on it (*Copy planning location*), copies exactly the text
it shows — no actions, links or hidden precision — and a tick stands in for its pin for a moment. When the
line is longer than its room it moves slowly to its end and back inside its own clipped region
(`MarqueeText`: 28 px/s, a 1.6 s rest at each end), and nothing else in the bar moves; when it fits it
stays still. With the operating system's reduced motion (Windows' *Show animations* off) it never moves
and ends in an ellipsis. The whole line is always its text for assistive technology, and its accessible
help text.

Only while a manual ground elevation is in force does the bar also offer *Reset to terrain
elevation*, after Open in Maps. Locations from older state files carry one, and nothing else in the Planner can clear it.

**Save location** and then **Open in Maps** follow the location in the bar, Save beside the place it
saves, and nowhere else; neither moves with the location's text. Save always creates a new saved location
from the pin: it never updates, moves or renames an existing one, near or not, and saving twice at one
spot makes two locations. A name already in use is numbered (*Brigg (2)*), counting from the base
name, because saved locations are told apart by name. Editing, renaming and deleting saved locations
belong to the Locations page; the Planner has no update or delete. Moving the pin by place or
coordinates is the shell search's job, and the Planner has no fields to type a location into.

## States

A panel is **docked** (pinned), **floating**, or **unpinned** (auto-hidden to an edge tab), and
nothing else. There is no close and no hidden state: the Planner's panels are its permanent tools, not
documents, and unpinning is how one is put out of the way — still named on its edge, one tap from open.
Layouts saved when panels could be hidden carried an `IsVisible` field; it is no longer a member, so it
is skipped on load and a once-hidden panel comes back in the dock, float or edge tab it was last in,
with the rest of the layout intact. Pinning is not stored separately from the dock mode: a pinned panel *is* a
docked one, so the two can never disagree in a saved file. Unpinning collapses a panel to a tab on
its dock's edge and pinning it again docks it to the side it remembers.

Opening an unpinned panel from its tab lays it over the map at the size it would have if it were
docked. The map does not move, so reading a panel never reflows the workspace. Only one panel is open
at a time on a given edge. A press anywhere that is not the open panel or its strip closes it.

## Touch and mouse are equal

Every state is a command with a visible control:

- **Pin / unpin** is a control in the title bar, with two states so it says which state the panel is
  in and not only what it will do.
- **The ••• menu** names every placement in words: pin, unpin, float, dock left/right/top/bottom, and run the dock
the full width or height.
- **Edge tabs** carry the panel's own name at the application's text size: across on the top and bottom
  edges, turned to read down the edge on the right (a layout transform the strip's `rightEdge` class selects),
  so that strip stays a narrow column. The left edge reads across for now; it can be turned the same way.

Dragging the title bar and the resize grips are accelerators for what those commands already do; a
drag of a docked or unpinned panel brings it loose under the pointer first, so it is one gesture
whatever state the panel started in, and dropping it near an edge docks it there. Nothing is reachable
only by hovering, and nothing requires a drag. Hover does not open an unpinned panel: a tap does, on
both desktop and touch. The grips are 14 px and 18 px — narrow to look at, and sized to be hit.

## Corners

Where a side dock and an end dock meet, one of them runs the whole way and the other stops short.
Which one is part of the saved layout rather than a rule built into the code, and the four corners are
independent — the timeline can run under both side docks, between them, or under just one.

`DockCornerOwnership` names each corner's owner. A corner belongs to one of its own two neighbours —
the top-left corner to the left dock or the top dock, never to the right or bottom one — so the four
dock rectangles can never overlap, and a value naming a dock that does not touch a corner is returned
to its default rather than honoured. Ownership decides how far the docks run *around* the map; the
map's own rectangle is decided by how much room the docks take, so no corner choice can shrink it.

Noctaxis ships with the side docks owning all four corners: they run the full height and the end docks
sit between them, which is what the Planner has always looked like.

**The user never meets the phrase.** Ownership is chosen by putting a panel somewhere: drop a panel
into a corner of the workspace and the dock it lands in reaches into that corner, with the drop hint
showing that shape before the drop commits. The panel menu carries the same choice in words — *Run the
full width* / *Stop at the side panels* for an end dock, *Run the full height* / *Stop at the top and
bottom panels* for a side dock — so it is reachable by tapping and by keyboard, not only by dragging.
That menu item moves both of a dock's corners together; an asymmetric arrangement is made by dropping.

## What is saved

`AppSettings.PlannerDock` holds one entry per panel — its id, whether it is shown, its mode, its side,
its dock extent, its floating rectangle and its order — and the four corner owners. It is written
through `Settings` and the existing debounced save, so a drag becomes one write and there is no second
storage mechanism.

A docking value this version cannot read — a hand edit, or a layout written by a newer build — costs
only that placement. `TolerantEnumJsonConverter` reads an unknown side or mode as its default instead
of refusing the file, because quarantining the state file over a panel's position would take the
user's locations and session with it.

## Resetting

**Settings → Planner map → Panels → Reset Planner layout** is the authoritative reset. It restores
every panel's dock, pinning, size, floating rectangle and order, the Inspector's open tab, and the
corner owners, and it takes effect immediately — the live Planner re-arranges itself and the new layout is saved.
There is no confirmation: the layout is cheap to rearrange and nothing else is touched.

There is no Panels menu: every panel is always on screen (docked, as a tab on its edge, or floating
within the workspace) and places itself from its own controls, so a global list had nothing left to do.
The reset is `PlannerDockViewModel.ResetLayoutCommand`, which restores everything from `ShippedLayout` — the one
definition the first run also builds from. No default is written down twice.

The application decides which panels exist; the file only says where the user put them. An id this
version no longer declares is dropped on load, and a panel the file does not mention arrives at its
default — so adding or removing a panel in a later version is safe.

## Safeguards

- **The map keeps its minimum, and no panel is squeezed past its own.** Docks that together ask for
  more than the workspace can give each give up the room they have above their own minimum, in
  proportion, until the central surface has 240 × 160 px. A dock's minimum is the framework's 160 px,
  or more where a panel in it declares more. Only in a window too small for both docks' minimums and
  the map's at once does the map get less than its minimum — a panel squeezed past its minimum would
  clip its content and help nobody.
- **Floating panels are clamped into the workspace** on every layout pass, so a layout saved on a
  large display opens safely on a small one, and a panel can never be dragged or restored out of
  reach. Clamping does not rewrite the saved layout: a smaller window is not an edit.
- **Persisted numbers are corrected, not obeyed.** A hand-edited or damaged layout is clamped to the
  limits in `PlannerPanelLayout`.
- **Auto-hide earns its keep on small displays**, where unpinning the panels not in use is the fastest
  way to give the map room.

## Left for later

- Panels docked on one side share that side's length equally, except that a panel needing more gets
  its minimum first. Splitters *between* panels along a dock
  are a refinement; the cross-dock resize grip is implemented.
- Tabbed groups (several panels sharing one dock slot) are not implemented.
- Panels cannot be detached into real windows, which was out of scope for this pass.
- The panel menu's span item moves both of a dock's corners at once. Giving one corner away and keeping
  the other is reachable by dropping a panel into it, and by nothing else — deliberately, rather than
  putting a four-corner matrix in Settings.
- Panel interiors were not redesigned. The Camera and framing panel in particular is tall for the
  space it is given on a 1080p display and scrolls; that is a candidate for the panel cleanup pass.
