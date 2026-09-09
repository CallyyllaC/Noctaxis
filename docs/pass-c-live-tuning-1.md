# Pass C live tuning / fix 1

## Continuation and scope

The interrupted work contained a read-only map-state test seam and an unfinished end-to-end
coverage trace test. The slider/legend/strength changes had not yet been implemented. Those
edits were preserved, compiled and exercised before changing presentation maths. All earlier
terrain, minimap, Pass C depth and target-occultation work remains intact.

## Camera controls

Planner's Camera and framing section now presents horizontal bearing and pitch sliders beside
their existing numeric inputs. Bearing is 0–360 degrees; pitch is -90–90 with zero centred.
Both support precise numeric values (including 12.5 / 123.5 in the binding test).

Both controls bind directly to the existing CameraBearingDegrees / CameraPitchDegrees
properties. Bearing still resolves through target azimuth plus the existing composition offset
and manual fallback; no second yaw state exists. The 359 → 0 → 1 and reverse-adjacent progression
uses the same normalized angular state as the map. No arbitrary roll or dual-axis pad was added.

Derived calculations remain synchronous and memoized. There is no new calculation task or
debounce. Camera-setting persistence now allows one active save and retains only the latest
pending state, preventing rapid slider changes from spawning competing file writes.

The bound-control test drives 56 pitch values (-10 through +45) and six bearing values. It
records exactly 62 framing calculations and 62 depth results, with repeated readers adding
zero calculations. Physical profile identity, observer generation, astronomy calculation count
and environment request count remain unchanged. Providers are not involved in this prepared-
profile fixture; zero additional environment/terrain work can reach Terrarium, WorldCover or
the surface resolver through this path. A blocked save receives one write during the sweep
and one latest-state write after release, with maximum concurrency one.

## Legend

The old wording was `Near · Far · Sky (blue)`. The replacement uses labelled visual swatches
bound to the renderer's actual depth palette, not approximate independent colours:

- Near (100 m endpoint): #E6E6E6, light grey.
- Far (500 km endpoint): #323232, dark grey.
- Sky: #101D30, dark navy/blue-black, outlined for visibility.

Depth values, logarithmic colour mapping, 72-row source, aspect ratio and overlays are unchanged.

## Hatch diagnosis and fix

Before changing maths, the trace exercised raw coverage, thresholded coverage, assessment,
NoctaxisMapView state invalidation, texture revision, resampling and actual shader rendering.
The existing code preserved coverage correctly. Pitch changed texture revisions; samples and
texels retained coverage; the premultiplied RGBA hatch was multiplied once, including alpha.
The baseline was already monotonic and reached exactly zero hatch at high pitch.

No binary/stale shader defect was reproduced. The demonstrated presentation weakness was
linear attenuation of an already translucent, sparse pattern: at 25% effective coverage the
stroke alpha was only 0.48 × 0.25 = 0.12, with most pixels between the one-pixel strokes.
This is the tested explanation for subtle partial coverage; it is not a claim to have reproduced
the user's live scene or driver state.

The new presentation-only curve is sqrt(clamp(effectiveCoverage, 0, 1)). Raw and effective
coverage stay physically unchanged in the assessment, interpolation and profile texture.
The shader evaluates the curve after coverage interpolation, then multiplies the entire
premultiplied hatch by display strength exactly once. Maximum opacity, spacing, thickness,
weather composition and the 500 km FoV/outline remain unchanged. Debug selected-bearing text
now distinguishes physical effective coverage from display strength.

| Effective coverage | Display strength | Before rendered alpha | After rendered alpha |
|---:|---:|---:|---:|
| 0% | 0% | 46 | 46 |
| 25% | 50% | 71 | 96 |
| 50% | 70.71% | 96 | 117 |
| 75% | 86.60% | 121 | 133 |
| 100% | 100% | 146 | 146 |

These are 8-bit composed alpha values from a deterministic interior full-stroke probe.
46 is the base cone alpha, not a minimum hatch contribution. The zero-coverage output is
asserted equal to an entirely unhatched cone. The full-stroke probe avoids antialiasing/stripe
phase ambiguity; production retains its accepted sparse pattern.

For a fixed +10-degree terrain horizon, 60-degree V-FoV and 5% threshold:

| Pitch | Raw coverage | Effective coverage | Display strength | Composed alpha |
|---:|---:|---:|---:|---:|
| -10° | 83.33% | 82.46% | 90.81% | 137 |
| 0° | 66.67% | 64.91% | 80.57% | 127 |
| +10° | 50% | 47.37% | 68.82% | 115 |
| +20° | 33.33% | 29.82% | 54.61% | 101 |
| +30° | 16.67% | 12.28% | 35.04% | 81 |
| +45° | 0% | 0% | 0% | 46 |

Depth occupancy agrees with raw coverage within one of the 72 rows. Downward pitch strengthens
the hatch; upward pitch weakens it and can remove it completely. Coverage is never derived
from rendered depth pixels.

## Evidence and performance

- `pass-c-hatch-before.json` and `pass-c-hatch-after.json` retain the rendered trace values.
- `pass-c-pitch-comparison.png` shows the same fixture at 0/15/30/45 degrees: depth above,
  main-map texture on a neutral background below. Its labels report effective coverage.
- `NOCTAXIS_HATCH_TRACE` enables trace-file output; `NOCTAXIS_HATCH_COMPARISON` enables PNG
  export. Ordinary tests do not write these artifacts or access the user's cache.
- Shader mapping adds no terrain buffers or CPU geometry. The existing depth/interpolation
  allocation model is unchanged; this pass did not rerun the earlier allocation benchmark.
  The known approximately 5.3 MB interpolated framing/depth allocation remains out of scope.
- Memoization and the save-coalescing call counts above are deterministic, not timing gates.

Headless rendering confirms the requested direction of visual change. A fresh live run remains
the final perceptual check on the user's map/background; no driver or running-app validation
is claimed. The temporary Terrain frame remains in place and no wireframe work was started.

## Verification

Build/tests use the isolated Release output
`H:\Noctaxis\Noctaxis.Desktop.Tests\bin\PassCTuning\net10.0\` with `-p:OutDir=...` and
`-m:1` for the solution build. The running application is not terminated.

- Focused Desktop rendering/control/memoization suite: 71 passed.
- Full Core: 312 passed, 1 opt-in live skip.
- Full Desktop: 282 passed.
- Release solution build: passed, zero warnings/errors.
- `git diff --check`: passed.
