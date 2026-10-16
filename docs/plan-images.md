# Plan images

A plan image is a PNG planning sheet for one target — the focused one — with the plan inside it. It answers
"where do I point the camera at this target over time?". It can be saved, copied, and opened again —
from a file, from the clipboard or by dropping it on the Planner — to put the Planner back at that plan. This
is a prototype of the final plan-image workflow: one layout, no export options yet.

## Export: one renderer, two deliveries

```
Planner state (the plan the export started from)
    ↓  MainViewModel.CreateExportPngAsync
PlannerExportSnapshot                      everything the sheet shows, and its colours
    ↓  IPlannerExportRenderer.RenderPng   (PlannerExportRenderer)
pixels (PNG)
    ↓  NoctaxisPlanImage.Embed            the plan, as one iTXt chunk
plan image (PNG)
    ├── Save PNG        writes these bytes
    └── Copy PNG        puts these bytes on the clipboard
```

- The renderer draws from the snapshot alone. It composes its own detached visual tree and renders it off
  screen; it never captures the window, a panel or the dock layout. The snapshot holds the theme's colours,
  so the same snapshot always draws the same pixels.
- Save PNG and Copy PNG only deliver. Both call `MainWindow.CreatePlanImageAsync`, which is the only caller
  of `CreateExportPngAsync`.
- Export refreshes the weather for the plan it started from, as the old card did: if the Planner moves on
  while the weather is fetched, the sheet keeps the plan it started from and never pairs it with another
  plan's weather.
- Weather is optional. If the refresh fails or times out, the sheet uses the cached forecast and says when it
  was retrieved. With no weather at all, it says "Weather unavailable" and why, with no values. Either way the
  plan is embedded and the image reopens as usual; Save PNG and Copy PNG behave identically.

### One target

The sheet's subject is the focused target, and only it: Focus in the Planner is the export's target, with no
selector of its own. Other shown targets are not drawn. With nothing focused, Export says "Focus a target to
export a plan." and makes nothing; a focused target that has been hidden is no longer drawn, so it cannot be
exported either.

### The sheet (1600 × 1100)

| Region | Content |
|---|---|
| Header | The location's name; the target · the planning window (start – end, local, with the zone's short name, e.g. BST); camera · lens · focal length · field of view (capture guidance such as shutter, aperture and ISO would join this line later). |
| Map (left) | A clean map (below). The planning location is a ring at the centre, its precise coordinates in a chip, the map credits in another. |
| Weather (right) | A row every three hours across the window: time, cloud, chance of rain, wind, temperature, in the user's units. If no row can be fetched, a one-line note. |
| Chart (full width, 420 px) | When and where to point: the target's altitude against the terrain in its direction, with its bearing on the time axis (`ExportDirectionChart`, below). |
| Footer | Noctaxis version and the weather credit. |

The window is the Planner's timeline window, which starts at the selected planning moment.

### The chart: altitude against the terrain, with direction on the time axis

`ExportDirectionChart` is the export's own chart; the Planner's Timeline is unchanged. It answers "when should I
point the camera, and which way, given the local terrain?" with one axis and no second scale to decode.

- **One axis: pitch / altitude**, labelled 0°, 30°, 60°, 90°. It runs to −12° so a rise or a set reads as a
  crossing; the geometric horizon is the 0° line.
- **The target** (`ExportTargetSeries`, from its Timeline path): its altitude over the window, solid in its own colour
  where it is in view and thin and dotted where it is below the horizon or behind the terrain.
- **The terrain horizon in the target's direction**: for every sample, the terrain horizon at the target's bearing
  *at that moment* (`TerrainHorizonProfile.EffectiveAltitudeAt(bearing)`), drawn as a filled silhouette with a firm
  edge. It follows the target round the sky: not one fixed bearing, and not the camera's. Above the silhouette the
  target is in view; below it, the terrain hides it.
- **Direction on the time axis**: every three hours on the clock, the time (or the date at midnight) and under it the
  target's bearing then, as a compass point and degrees ("SE 135°"). The bearing is interpolated between the 10-minute
  samples the short way round; the point is the nearest of eight, taken from the rounded degrees so the two always
  agree (22° is N, 23° is NE, 359.6° is N 0°). On a narrow chart every other bearing is left out before any would
  overlap; the times always stay.
- **Events**: astronomical darkness as a band; ticks for sunset, sunrise, darkness beginning and ending, moonrise and
  moonset; on the target's line, where it rises and sets (crossing 0°), where it clears and drops behind the terrain
  (crossing the terrain curve — found where the two straight-between-samples curves meet, so the label sits exactly
  on the drawn crossing), and its highest point with its direction ("Highest 79° · S 178° · 00:50").
- The target is told from the terrain without colour: a line against a filled area. No other target's path and no
  Sun or Moon altitude line is drawn. Bearing is not plotted, so there is no wrap at North to handle.

## The plan: `Noctaxis.Plan`, schema 1

One iTXt chunk, keyword `Noctaxis.Plan`, holding compact UTF-8 JSON:

```json
{
  "format": "noctaxis-plan",
  "schemaVersion": 1,
  "timeUtc": "2026-10-07T21:00:00Z",
  "location": { "latitude": 52.59302, "longitude": -3.64697 },
  "camera": { "bearingDegrees": 293.3, "pitchDegrees": 7.2 },
  "focus": { "target": "m31", "follow": true }
}
```

- `focus` is `null` when nothing is focused. `target` is the target's stable catalogue identifier.
- That is all. The plan deliberately carries no elevation, terrain, weather, astronomy, equipment, map,
  light-pollution, theme, colour-vision, layout, dock or PiP state, and nothing derived.
- `NoctaxisPlan` writes and parses it; `NoctaxisPlanImage` embeds and reads it. Nothing else does either.
- Every field is required. `focus` is `null` or an object with both `target` and `follow` (a boolean).

### Compatibility

| Format | Where | Writes | Reads |
|---|---|---|---|
| Plan | PNG iTXt `Noctaxis.Plan` | schema 1 | schema 1 |
| Scouting card (earlier versions) | PNG `Noctaxis.ExportData`, `exportSchemaVersion` 2 | no longer written | recognised and named, never read as a plan |
| Location export | JSON file, `schemaVersion` | schema 1 | schema 1 |
| Application state | `state.json`, `Version` | 4 | its own migrations; not an import/export format |

- **Older schemas** are each read by their own reader (`NoctaxisPlan.SupportedSchemaVersions`), and none is dropped
  to simplify the code. Schema 1 is the only plan schema so far. Fixtures written by each schema's writer are kept
  under `Noctaxis.Core.Tests/Samples/Compatibility`, and must keep reading.
- **The schema version changes only when the serialised format changes.** A test holds the writer to the schema-1
  fixture byte for byte, so any change to the format is a deliberate decision.
- **Unknown additional fields** are ignored at every level, so a later schema-1 writer may add fields.
- **Refused, with the reason shown, rather than guessed:**

  | Case | Reason shown |
  |---|---|
  | A newer schema | "made by a newer version… Update Noctaxis to open it." |
  | A missing required value, or one of the wrong kind (`follow` as `"true"` or `1`) | damaged |
  | A property given twice | damaged |
  | A latitude or longitude off the globe | location not valid |
  | A time that is not UTC (`Z`), or within a day of the calendar's ends | time not valid |
  | A pitch beyond ±90° | camera direction not valid |

- **Directions keep their meaning.** Any finite bearing is accepted (370° is 10°). A pitch between −90° and the
  camera's −45° is a real direction, and is brought to the camera's limit as the camera would.
- **Earlier scouting cards** recorded what the card showed, not a plan. Their bearing was the target's, and they
  have no camera pitch or focus. So they are named ("a scouting card from an earlier version… Export the plan again
  from the Planner"), never converted with guessed values.
- **Nothing changes until the whole plan is valid.** That includes the moment, which is converted to the planning
  time zone before the place changes.

## Import: three ways in, one importer

```
Open plan image… ─┐
Load from clipboard ─┼─ PlanImageSources → PlanImageSource (bytes, or why there are none)
drop on the Planner ─┘        ↓
                     MainViewModel.ImportPlanImage
                         read → validate → resolve target → apply
```

- **Open plan image…** (Import ▾): a file picker for PNG files.
- **Load from clipboard** (Import ▾): first a copied file (Copy PNG's, or a PNG copied in a file manager): exactly
  one, several are refused. Then the raw PNG bytes (`PNG` on Windows, `public.png` on macOS, `image/png` elsewhere).
  An image offered only as decoded pixels has lost its plan and is reported, not read. Text is never read as a plan.
- **Drop**: files dropped anywhere on the Planner page. While files are dragged over it, a quiet accent edge
  and one line show it will take them. Exactly one file is imported; several are refused, not one chosen.
- The adapters only produce bytes. Parsing, validation and applying are the one importer's.

### Transactional

The image is read, the plan parsed and validated, and its target resolved before anything changes. A plan
that fails leaves the Planner exactly as it was, and the reason is shown as a status message:

| Case | Message |
|---|---|
| No image, empty or text-only clipboard | There is no image to import. |
| Clipboard image as decoded pixels only | The clipboard image does not contain Noctaxis plan data. Copy the plan PNG file instead, or open it with Open plan image… |
| Several files on the clipboard | The clipboard holds several files. Copy one plan image at a time. |
| Several dropped files | Drop one plan image at a time. |
| Unreadable file | That file could not be read. |
| Not an image | That is not an image Noctaxis can read. |
| JPEG, GIF, BMP, WebP or TIFF | Only PNG plan images can be imported. |
| PNG without a plan | This image has no Noctaxis plan in it. |
| Malformed plan | The plan in this image is damaged. |
| Newer schema | This plan was made by a newer version of Noctaxis. Update Noctaxis to open it. |
| Pitch beyond ±90° | The plan's camera direction is not valid. |
| Earlier scouting card | This is a scouting card from an earlier version of Noctaxis. It records what the card showed, not a plan, so it cannot be opened. Export the plan again from the Planner. |
| Bad coordinates / time | The plan's location is not valid. / The plan's time is not valid. |

### Applying a plan

1. The location is committed as a pin move: the elevation and terrain are recalculated for it, the place
   name is looked up again, and astronomy and weather are recalculated and fetched as for any move.
2. The UTC time is shown in the Planner's planning time zone. Noctaxis has no time-zone-from-coordinates
   lookup: a pin-placed location uses the configured planning zone, so an imported plan does too.
3. The focus: a target in the user's list is focused (shown first if it was hidden). A target that is not in
   the list is not added: the plan comes in without focus and with Follow off, and the message says so. This
   is not a failure.
4. The camera's bearing and pitch, through the camera's own rules (the bearing wraps, the pitch is clamped).
5. Follow, only when the focused target resolved.

## Clipboard

**The PNG file is the portable plan.** The plan lives inside the PNG, so the file itself, sent to someone else or
opened later, opens back to the plan in Noctaxis.

- **Save PNG** writes that file.
- **Copy PNG** copies that same file through the clipboard (`ClipboardPlanFile`). It writes exactly Save PNG's
  bytes to `noctaxis-plan.png` in its own folder under `%TEMP%\Noctaxis\Clipboard` (the system's temporary folder
  elsewhere), and puts one clipboard item with three formats: the file, so Explorer and file-aware applications
  paste a real PNG file; the same bytes as raw PNG; and the picture, for applications that only take pictures.
  Each copy gets a fresh folder, so a copy never rewrites a file still being pasted. Only the newest three are
  kept, and older ones are removed on the next copy.
- **Load from clipboard** expects that file, or raw PNG bytes.

An application that screenshots the image, re-encodes it, strips its metadata or copies only its decoded pixels
loses the plan. That is expected, not an import fault: Noctaxis says the clipboard image has no plan data, and
never guesses one from pixels.

Platform notes: only Windows has been exercised. The file goes through Avalonia's own file clipboard format
(`DataFormat.File`), so no format is Noctaxis' own; how macOS and Linux back ends publish files, and the raw-PNG
format names there (`public.png`, `image/png`), are untested.

## Non-goals

- No stale weather, terrain, elevation or astronomy is imported: all of it is recalculated or fetched.
- No equipment: the header shows the camera, lens, focal length and field of view for people; the plan does
  not carry them.
- No interface or layout state is restored: theme, colour-vision mode, dock, PiP, layers.
- No altitude, bearing or terrain history: the chart is the image's; an opened plan recalculates the target's path.
- No reading of plans from pixels, text or other image formats.
