# Pass C: camera-frame terrain coverage

## Camera inputs and persistence

`CameraFramingSettings.CameraPitchDegrees` is independent of astronomical target altitude:
0 degrees is horizontal, positive looks upward, range -90 to +90, default 0. Planner's
Camera and framing section exposes a numeric pitch input. It persists through the existing
automatic framing/settings store.

The new numeric bearing input uses the existing target-azimuth-plus-composition-offset
calculation. Editing it sets the existing composition offset relative to the current target
and the existing manual-bearing fallback. There is no second yaw state. The current map
has no separate yaw-edit event to reconcile; its displayed FoV consumes this same guide.
Target-relative bearing behaviour and landscape/portrait sensor orientation remain intact.

Minimum terrain frame coverage lives in normal framing Settings, uses Save / Reset unsaved
changes, defaults to 5%, and is clamped to 0–50% with a 1% numeric increment. Old files receive
pitch 0 and threshold 5; persistence tests cover endpoints, intermediate and out-of-range values.
These three camera inputs do not schedule physical terrain/environment refreshes.

## Model and rendering

Frame bounds are pitch ± vertical FoV / 2. Each bearing scans its existing interpolated
sightline. Raw coverage is `clamp((maximum apparent angle - lower) / vertical FoV, 0, 1)`.
For threshold t, raw <= t is suppressed; otherwise effective coverage is `(raw-t)/(1-t)`.
This continuous mapping reaches zero exactly at the threshold.

The meaningful frontier is the first existing radial sample whose angle reaches
`lower + vertical FoV*t`, provided effective coverage is positive. This deliberately uses
the first qualifying sample, as requested, rather than changing radial sampling or
interpolating a new radial intersection. The regression fixture crosses horizontal at 500 m
but first enters the meaningful frame at 2 km; the new frontier is 2 km.

Angular edges retain `SightlineAt` interpolation, fixed approximately 1-degree subdivision,
and the existing hit/no-hit transition refinement. Distance and coverage interpolate between
hit bearings; hit/no-hit boundaries retain their guarded transition treatment. The shader
multiplies the existing premultiplied hatch by effective coverage, keeping pattern geometry,
maximum opacity, weather composition and the 500 km cone boundary unchanged.

Astronomical occultation still uses target altitude <= existing terrain horizon, independently
of camera coverage. No camera height is added here. Profile datum, source, 360 bearings,
radial sampling, minimap and terrain caches are unchanged.

## Temporary Terrain frame

The informational widget sits directly below Terrain within the same non-hit-testable
top-left overlay container. Its available area is 208 by 160 px. It fits the camera aspect
ratio using tan(H-FoV/2)/tan(V-FoV/2), including portrait orientation.

`CameraTerrainDepth` is immutable calculation data, separate from Avalonia: bearings,
upper/lower altitude, row-angle mapping and first-hit distances. Zero means sky. A normal
70-degree FoV has 71 columns by 72 rows. This structured result can feed a later silhouette
or perspective renderer without provider work; no wireframe renderer was implemented.

Rows run from upper frame altitude to lower; columns run left to right across camera bearings.
During the same radial scan used for coverage, each newly reached vertical bin is filled once
with its first-hit sample distance. Complexity is radial samples plus vertical bins per column,
not radial samples times pixels. Threshold changes leave physical depth values unchanged.

Near terrain is brighter and far terrain darker using a logarithmic 100 m–500 km mapping;
sky is blue-black. Optical-centre and meaningful-threshold horizontal lines are overlaid.
Disabled, resolving and unavailable messages replace absent data. Existing canonical-observer
and generation checks prevent stale data from appearing current. The renderer caches one
bitmap per current depth result and disposes replacements; threshold-only redraw does not
alter the depth object supplied to it.

The main map and widget share the single UI-owned memoized framing assessment, keyed on
profile identity, bearing, FoVs, pitch, threshold, weather and target altitude. The widget
remains available when the main overlay is hidden. There is no background provider task.
Selected-bearing Debug text includes bounds, pitch, horizon, raw/effective coverage, threshold
and meaningful frontier; the old horizontal-hit diagnostic remains separate.

## Derived-work measurement

Opt-in `NOCTAXIS_PASS_C_OUTPUT` runs a deterministic existing 360-bearing profile with 655
radial samples per bearing. Every mode uses 71 camera bearings (46,505 sample visits), no
network/cache access, and zero additional provider calls. Medians use seven batches of 20
calculations after 23 warm-up batches. Timings never determine test success.

| Bearing alignment | Work | Median ms | Allocated bytes |
|---|---|---:|---:|
| Integer | Horizontal-only compatibility baseline | 0.292 | 11,074 |
| Integer | Camera coverage only | 0.359 | 9,658 |
| Integer | Coverage and 71×72 depth | 0.479 | 91,330 |
| Interpolated | Horizontal-only compatibility baseline | 2.885 | 5,219,066 |
| Interpolated | Camera coverage only | 3.680 | 5,219,922 |
| Interpolated | Coverage and 71×72 depth | 3.592 | 5,301,594 |

The coverage-only row excludes framing envelope construction. Small timing differences
between coverage-only and combined work are noise, not evidence that producing depth saves
CPU. Combined production work adds roughly 80–83 KB allocation compared with the baseline.
The approximately 5.2 MB interpolated-sightline cost remains, without an interpolation refactor.
Previous pre-Pass-C results (7,618 / 5,215,610 bytes) predate the additional per-bearing fields;
the same-tree compatibility rows above include those fields for a fair current comparison.
One completed 71×72 depth distance array retains 40,896 bytes plus 568 bytes of bearings and
small metadata; transient assembly storage is released after calculation.

The legacy path remains callable without camera-frame options for horizontal diagnostics and
benchmark/regression comparisons. Production always supplies the independent camera frame.

## Planning time audit (report only)

The canonical instant is `PlanningSession.Instant`, held in `MainViewModel._session` and
exposed through `Session`. Startup obtains the current clock instant, rounds to the nearest
local hour in the machine timezone through `PlannerStartupTime.RoundedLocalHour`, and uses
that instant when restoring the working session. Historical auto-restored time is not reused.

Planner already has a DatePicker bound to LocalDate, a local-time TextBox bound to TimeText,
Previous day / Next day / Now buttons, a nearby-date slider and a time-of-day slider.
It does not expose only current-time/horizon behaviour. Date/time edits resolve through the
effective timezone into Session.Instant and schedule astronomy refresh. Sliders preview
and then commit through CommitTemporalPreview. No date/time code or UI was changed here.

## Verification and limits

Deterministic tests cover numeric coverage, threshold equality, late meaningful frontier,
near/far depth, sky and downward views, angular interpolation/transitions, depth threshold
invariance, target independence, settings persistence/defaults, derived-only pitch/bearing/
threshold/FoV changes, blocked completion, stale observer handling, shader strength/weather,
renderer replacement/status and input layout. Existing map navigation and FoV outline tests
remain part of the full Desktop suite.

Release verification uses `H:\Noctaxis\Noctaxis.Desktop.Tests\bin\PassC\net10.0\`
via `-p:OutDir=...` and serial build `-m:1`. The running application is not terminated.

| Verification | Result |
|---|---|
| Restore | All projects up to date |
| Release solution build | Passed, 0 warnings / errors |
| Focused Core camera/frame/persistence/visibility | 42 passed |
| Focused Desktop frame/shader/settings/memoization | 9 passed |
| Full Core | 312 passed, 1 opt-in live test skipped |
| Full Desktop, final run | 273 passed |
| git diff --check | Passed |

One earlier full Desktop run failed the existing out-of-order weather/time test: the snapshot
instant was 15:00 instead of 16:00. Both variants passed in isolation and the full Desktop rerun
passed. No date/time implementation was changed to address this transient result. It remains
worth tracking if it recurs; it is not evidence of live date/time UI validation.

The main-map overlay now represents the requested angular camera-frame coverage model.
The depth preview agrees with it to the 72-row centre-sampling discretization (about 1.4
percentage points per row). It is a modest diagnostic rendering, not a photorealistic view.
Live visual tuning on the running application's new build remains to be performed; headless
tests do not substitute for that inspection. The known interpolation allocation is the main
remaining derived-cost issue and was deliberately left out of scope.
