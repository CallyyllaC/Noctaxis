# Noctaxis Roadmap

Noctaxis is in public alpha. This is a short overview of what is available, what is planned for v1.0, what needs attention, and what may come later. It is not a development task log.

Releases and downloads: [v0.1.3](https://github.com/CallyyllaC/Noctaxis/releases/tag/v0.1.3) · [v0.2.4](https://github.com/CallyyllaC/Noctaxis/releases/tag/v0.2.4). See also the [v0.2 release notes](docs/release-notes/0.2.md).

Status: ✅ released · 🟡 partly complete or being validated · ⬜ planned or awaiting investigation · 🚫 intentionally out of scope.

## Released: v0.1 and v0.2

### ✅ v0.1 — First public alpha
The first public alpha brings photography and astrophotography planning together in one desktop workspace.

- Interactive map, planning pin, saved locations and place search.
- Sun, Moon and target positions, visibility, timing and twilight information.
- Camera and lens profiles, field-of-view framing, terrain horizon and camera-direction preview.
- Weather and light-pollution information, plus a secondary planning map.
- Photographer-facing shot-viability guidance and portable planning-sheet images.
- Customisable themes, colour-vision options, touch-friendly controls and optional supporter themes.

### ✅ v0.2 — Diagnostics and reliability
A follow-up alpha focused on useful bug reports, geographical context and a more dependable planning experience.

- Export application logs and basic diagnostics to a ZIP without uploading private information.
- Keep the latest ten previously used days of logs as daily archives, plus today's session logs.
- Generate local place descriptions from map geography rather than relying on public reverse-geocoding requests.
- Improve measured performance, cancellation and caching; continue automated package checks for all six offered builds.

## Road to v1.0

### ⬜ More informative camera framing
Make the Planner's camera view and timeline better at showing what a photograph will actually contain.

- Limit the map's view cone using camera pitch, field of view, observer height, Earth's curvature and terrain instead of an arbitrary distance.
- Show relevant celestial objects at their projected positions inside the camera-direction preview.
- Shade the altitude timeline by the Sun's elevation, from daylight through twilight to astronomical night.

### ⬜ Capture settings and equipment detail
Record more of the information photographers need when preparing a shot.

- Add camera ISO and lens aperture (f-number) to the relevant equipment and capture settings.
- Include useful capture details in planning views and exported sheets.

### ⬜ Better saved-location context
Make saved places easier to recognise and revisit without opening each one.

- Show the stored timezone on saved-location cards using a readable label.

### 🟡 Portable plans and richer exports
The existing PNG planning sheet and image import work. Extend them without breaking plans people have already saved.

- Verify version-aware import of older plans and safe rejection of unsupported newer formats.
- Allow deliberately selected multiple targets in one field plan while preserving focused-target exports.
- Offer a more detailed, printer-friendly planning sheet suitable for paper or PDF.

### ⬜ Quiet update notifications
Let people know when a newer Noctaxis release is available without forcing an update or interrupting planning.

- Check for releases unobtrusively at startup.
- Open GitHub Releases directly from the notice.
- Allow skipping a version and remember the choice.

### 🟡 Settings and presentation polish
Make the interface comfortable at small window sizes and higher text scaling without sacrificing the simple layout.

- Use available Settings space sensibly and avoid clipping, awkward stretching or unnecessary scrolling.
- Simplify theme availability labels while keeping supporter/locked-state explanations available.
- Refine rough edges found through real photography sessions.

### 🟡 Ready for everyday use
Finish the practical checks needed before describing v1.0 as stable.

- Try the Windows x64 portable package on a clean machine.
- Exercise real maps, weather, terrain and hardware-accelerated rendering in the assembled application.
- Review first-run guidance, release wording and binary provenance. Windows x64 remains the officially supported platform.

## Engineering backlog

### ⬜ Equipment profiles can interfere with saving
An incomplete or invalid camera or lens profile can prevent unrelated valid edits from saving. The persistence behaviour needs a focused review.

- Isolate profile drafts or validation so one unfinished profile does not block another.
- Investigate Manufacturer/Model-only edits that may not trigger autosave.
- Cover pending edits, removal of the last profile and closing with invalid drafts.

### ⬜ Investigate slow or noisy environment requests
Check reports of slow environment processing when the planning pin moves offshore, along with recurring terrain/settlement data warnings.

- Distinguish genuine stalls from superseded requests being cancelled normally.
- Verify bounded lookup timeouts and graceful fallback when data is unavailable.
- Revisit performance only if measurements establish a significant problem.

### ⬜ Decide on consistent fonts
Noctaxis names Inter, but current font lookup can use system fonts instead. Decide whether to use the bundled font everywhere.

- Compare the real rendering and text measurements across operating systems.
- If a font change is chosen, recheck layout, localisation and accessibility before shipping it.

### 🟡 Native accessibility and responsive checks
Automated coverage is extensive; real desktop interaction still needs validation.

- Check screen-reader/accessibility-tree behaviour and keyboard navigation on Windows.
- Try populated screens at narrow widths, large text sizes and with touch input.
- Keep colour-vision and High Contrast readability in scope.

### 🟡 Long-session and lifecycle checks
Exercise normal use for longer than a quick automated test, including repeated planning, navigation and shutdown.

- Watch for resource growth, abandoned requests and stale views during extended sessions.
- Investigate measured regressions rather than reopening a speculative performance sweep.

## Future: v2 and beyond

### ⬜ Find suitable photography locations
Go beyond manually choosing a site by scouting sensible candidates for a particular shot.

- Discover potential public-access sites such as parks, parking areas and lay-bys where data supports them.
- Respect firm constraints for visibility, terrain clearance, framing, light pollution, Moon, weather, distance and site type.
- Expand the search radius only when needed, then explain the ranking of suitable candidates.

### ⬜ Find the best time to shoot
Search dates and times as well as places to reveal genuinely useful opportunities.

- Compare darkness, target position, terrain obstruction, weather and Moon conditions.
- Show why a suggested window or location is preferable to the alternatives.

### ⬜ Suggest capture settings
Use the target, selected equipment and planning conditions to offer practical starting points rather than pretending there is one perfect exposure.

- Start with suggested shutter/exposure duration.
- Later consider aperture, ISO, star trailing and tracker-related warnings.

### ⬜ Explore topographic map layers
Consider additional terrain and topographic map layers where they would make planning locations and sightlines easier to understand.

- Explore hillshade, contours and readable elevation information.
- Add layers only where their value for field planning is clear.

### ⬜ Useful astronomy alerts
Surface events worth planning for, without turning the application into a stream of notifications.

- Explore an aurora-watch or chance indicator.
- Consider optional desktop alerts for genuinely useful opportunities.

### ⬜ Optional 3D and further discovery
Consider lightweight 3D visualisation and additional discovery tools only where they add something the existing 2D Planner cannot communicate well.

### 🚫 Deliberately out of scope
A few tempting directions would make Noctaxis more complicated without clearly improving photography planning.

- A full offline mirror of terrain, weather and map services.
- Reusing stale calculated terrain, weather or astronomy results from imported plan images.
- Treating exported images as full editable project files.
- Paid code-signing and notarisation as a requirement for v1.0, or promising manual support for courtesy platforms.
