# Shot viability scoring

This document describes how Noctaxis scores a planned shot. Every threshold, weight and curve here is in one
file, [`ShotViabilityPolicy.cs`](../Noctaxis.Core/Viability/ShotViabilityPolicy.cs). The tables of
representative values are checked by `ShotViabilityTests.TheDocumentedValuesAreTheCurvesOwn`, so they cannot
drift from the code without a test failing.

## 1. Purpose and scope

The score answers one question for the Planner's current state: how promising is this shot of the focused
target, from this place, at this time, with this camera, lens and pointing? The answer is a number from 0 to
100 with a rating, and a list of the individual factors behind it.

- **It is a heuristic.** It is deterministic, explainable and easy to tune, not a physical model of image
  quality.
- **It is advisory.** It never blocks planning, changing equipment, choosing targets or exporting.
- **Deep sky is the primary profile.** The Sun, Moon and stars have their own applicability rules (section 8).
- **It uses what the Planner already has.** The score adds no external services, no machine learning, no
  stored scores and no network requests. It does not change the export format or the settings schema.

The scoring engine is in `Noctaxis.Core/Viability` and has no user-interface dependencies:

| File | Role |
|---|---|
| `ScoreCurves.cs` | The curve types: anchored monotone cubic, logistic, saturating exponential, smoothstep |
| `ShotViabilityPolicy.cs` | Every weight, threshold, curve and rating band |
| `ShotViabilityScorer.cs` | The ten metrics and the weighted aggregate |
| `UsefulWindowCalculator.cs` | Follows the favourable time window along precomputed paths |
| `ShotViabilityPlanning.cs` | Fills an input from the Planner's snapshot, leaving out anything stale |
| `ShotViabilityModels.cs` | The input, metric result and assessment records |

## 2. Policy identifier

The current policy is **`shot-viability-v2`** (`ShotViabilityPolicy.Id`). It is shown at the foot of the
details popover. Any change that alters a score must also change the identifier, so a score can always be
traced to the rules that produced it.

| Policy | Change |
|---|---|
| `shot-viability-v1` | The first nine metrics |
| `shot-viability-v2` | Adds light pollution (weight 10) and rebalances the other weights to make room for it |

## 3. Metrics and weights

| Metric | Id | Weight | Measures |
|---|---|---:|---|
| Metric | Id | v1 | v2 | Measures |
|---|---|---:|---:|---|
| Cloud cover | `cloud-cover` | 20 | 18 | Forecast total cloud, % |
| Darkness | `darkness` | 15 | 13 | The Sun's altitude, ° |
| Target altitude | `target-altitude` | 15 | 13 | The target's apparent altitude, ° |
| Terrain clearance | `terrain-clearance` | 15 | 15 | Target altitude above the terrain horizon at its azimuth, °, plus landscape in the frame |
| Useful time | `useful-window` | 10 | 10 | Unbroken favourable hours from the planned moment |
| Moonlight | `moon` | 10 | 9 | Moon illumination, separation from the target and altitude |
| Light pollution | `light-pollution` | — | 10 | Artificial zenith sky brightness at the place, from the installed Lorenz atlas |
| Target in frame | `frame-position` | 5 | 4 | Where the target falls in the camera frame |
| Framing fit | `framing-fit` | 5 | 4 | How much of the frame the target fills |
| Image sampling | `image-sampling` | 5 | 4 | Sensor pixels across the target's smaller axis |

The weights total 100 for a deep-sky target. Cloud carries the most weight because it is the factor most
likely to stop a shot entirely. Terrain, darkness and altitude come next, because each decides whether the
target can be photographed at all. Framing and sampling matter, but they are partly the photographer's choice,
so they weigh least.

Light pollution's 10 points came mostly from the other factors that describe the sky (cloud, darkness, altitude
and the Moon, which together gave 7), and one point from each framing factor. Terrain and the useful window
keep their weights, because nothing about the sky's brightness changes whether the target can be seen at all or
for how long. Light pollution weighs as much as the useful window and slightly more than the Moon: like the
Moon it brightens the sky, but unlike the Moon it never sets.

## 4. Scoring functions

There are four curve types. All of them are continuous, return 0 for a non-finite input, and are clamped to
the range 0 to 100.

- **Anchored curve** (`AnchoredCurve`): monotone cubic interpolation (Fritsch–Butland tangents) through a table
  of anchor points. It passes exactly through every anchor, never overshoots between them, and holds the first
  and last anchors' scores beyond the table. To tune one, edit the table.
- **Logistic curve** (`LogisticCurve`): a sigmoid with a midpoint and a scale, rescaled to reach exactly 0 and
  100 at its ends: `N(x) = (L(x) − L(from)) / (L(to) − L(from))`, where `L(x) = 1 / (1 + e^(−(x − midpoint) / scale))`.
  A decreasing curve is `100 × (1 − N(x))`.
- **Saturating curve** (`SaturatingCurve`): `100 × (1 − e^(−x / τ))`. Each extra unit adds less than the one
  before.
- **Smoothstep**: `t²(3 − 2t)` with `t` clamped to 0–1. It is used as a factor, not a score.

### Cloud cover: decreasing logistic, midpoint 35%, scale 8, domain 0–100%

| Cloud % | 0 | 5 | 10 | 15 | 20 | 25 | 30 | 35 | 40 | 50 | 60 | 75 | 90 | 100 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Score | 100 | 98.9 | 97.0 | 93.6 | 87.8 | 78.7 | 65.9 | 50.6 | 35.3 | 13.4 | 4.2 | 0.6 | 0.1 | 0 |

There is little penalty up to 10%, and 25% is still acceptable. The score is about half at 35% and very poor
beyond 60%. A logistic suits cloud because a little cloud costs little, while the middle of the range is where
a forecast decides a night.

### Darkness: anchored on the Sun's altitude

Anchors: (−18°, 100), (−15°, 88), (−12°, 65), (−9°, 35), (−6°, 12), (−3°, 4), (0°, 0).

| Sun ° | ≤ −18 | −16 | −15 | −13.5 | −12 | −10.5 | −9 | −7.5 | −6 | −3 | ≥ 0 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Score | 100 | 92.6 | 88 | 77.8 | 65 | 50 | 35 | 21.7 | 12 | 4 | 0 |

Astronomical darkness (−18° or lower) is excellent. Nautical darkness (−12°) scores 65, which is acceptable and
exactly at the warning line, so it is not flagged. Brighter twilight falls steeply.

### Target altitude: anchored

Deep sky and stars use anchors (0°, 0), (10°, 8), (20°, 30), (30°, 65), (37.5°, 87), (45°, 100).

| Altitude ° | ≤ 0 | 5 | 10 | 15 | 20 | 25 | 30 | 35 | 37.5 | 40 | ≥ 45 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Score | 0 | 3.5 | 8 | 17.1 | 30 | 46.9 | 65 | 80.6 | 87 | 91.8 | 100 |

The Sun and Moon use anchors (−0.5°, 0), (0°, 40), (3°, 70), (10°, 90), (20°, 100).

| Altitude ° | ≤ −0.5 | −0.25 | 0 | 1 | 3 | 5 | 10 | 15 | ≥ 20 |
|---|---|---|---|---|---|---|---|---|---|
| Score | 0 | 23.6 | 40 | 56.4 | 70 | 78.2 | 90 | 95.7 | 100 |

For deep sky, 30° is acceptable and 45° or higher is excellent. For the Sun and Moon, a low disc is often the
whole point of the picture (a moonrise over a ridge), so the curve only discourages a disc that is half set,
and gently discourages the haze just above the horizon.

### Terrain clearance: increasing logistic, midpoint 1°, scale 0.35°, domain 0–90°

| Clearance ° | ≤ 0 | 0.25 | 0.5 | 0.75 | 1 | 1.5 | 2 | 3 | 5 |
|---|---|---|---|---|---|---|---|---|---|
| Score | 0 | 5.4 | 14.7 | 29.0 | 47.1 | 79.6 | 94.3 | 99.7 | 100 |

Clearance is the target's altitude minus the terrain horizon at the target's azimuth. The terrain horizon is
read with `TerrainHorizonProfile.EffectiveAltitudeAt`, the same horizon the Planner shows. The score is nearly
binary: zero when the target is hidden (exactly on the terrain horizon counts as hidden), with a narrow
transition to excellent at about 2° of clearance.

**Landscape in the frame** (deep sky and stars only) is a separate, milder factor. The Camera frame measures
how much of its height terrain covers at each bearing across its field of view (`FramingTerrainObstructionSample.EffectiveCoverage`,
beyond the frame's own minimum-coverage setting). The largest of those shares, `c`, multiplies a clear
target's score by `1 − 0.25 × smoothstep(0, 0.5, c)`.

| Landscape share of frame | 0 | 10% | 25% | ≥ 50% |
|---|---|---|---|---|
| Factor | 1.000 | 0.974 | 0.875 | 0.750 |

This keeps two things apart: the target itself being hidden (critical, scores 0), and landscape merely
entering the picture (at most a quarter off, and no warning). For the Sun and Moon, landscape is usually the
composition, so it costs nothing. The figure is still reported in the details.

### Useful time: saturating, τ = 0.95 h

| Hours | 0 | 0.25 | 0.5 | 1 | 1.5 | 2 | 3 | 4 | 6 | ≥ 8 |
|---|---|---|---|---|---|---|---|---|---|---|
| Score | 0 | 23.1 | 40.9 | 65.1 | 79.4 | 87.8 | 95.7 | 98.5 | 99.8 | 100 |

Less than an hour is poor, one hour is acceptable, two to three hours are favourable, and further hours bring
diminishing returns.

**The window.** A moment is favourable when all three of these hold at once:
- the Sun is at or below −12°;
- the target is at least 30° up;
- the target is above the terrain horizon at its azimuth (when terrain is known).

Each condition is a margin in degrees, and the smallest margin decides. The window is the single unbroken
stretch from the planned moment for which that margin stays at or above zero, with its end interpolated between
the 10-minute samples. Separate favourable stretches are never added together: if the target is high until
22:00 and the sky is dark only from 21:00, the window is one hour, not the sum of the two.

- If the moment itself is not favourable, the window scores 0, and the explanation says when the next one opens
  and how long it lasts.
- The window is followed for at most 12 hours. A longer one is reported as "at least 12 h".
- The explanation names what ends the window: twilight, the target sinking below 30°, or terrain.

The target's and the Sun's paths come from `IPlanningService.CalculateShotWindowPathsAsync`. This is local
astronomy only, with nothing fetched. The paths are calculated once for a span from 6 hours before to 30
hours after the planned moment, and reused while the moment stays inside that span.

### Moonlight

When the Moon is below the horizon, the score is 100. Otherwise:

```
score = 100 − (100 − I(illumination)) × S(separation) / 100 × smoothstep(0°, 15°, Moon altitude)
```

`I` is anchored on the illuminated fraction: (0, 100), (0.2, 85), (0.5, 50), (0.75, 25), (1, 10).

| Illuminated | 0 | 10% | 20% | 35% | 50% | 75% | 90% | 100% |
|---|---|---|---|---|---|---|---|---|
| I | 100 | 92.9 | 85 | 68.2 | 50 | 25 | 15.6 | 10 |

`S` is the share of that penalty that applies at a given separation from the target, anchored at (0°, 100),
(30°, 100), (60°, 80), (90°, 65), (180°, 60).

| Separation ° | ≤ 30 | 45 | 60 | 90 | 120 | 180 |
|---|---|---|---|---|---|---|
| S | 100 | 92.1 | 80 | 65 | 62.5 | 60 |

The altitude factor:

| Moon altitude ° | 0 | 2 | 5 | 7.5 | 10 | ≥ 15 |
|---|---|---|---|---|---|---|
| Factor | 0 | 0.05 | 0.26 | 0.50 | 0.74 | 1 |

So a Moon under 20% lit is generally favourable. Brighter Moons are progressively worse, especially near the
target. A Moon just risen does little, because its light is heavily dimmed by the atmosphere. Separation is the
great-circle angle between the Moon's and the target's horizontal positions, both from the same snapshot.

### Light pollution: anchored on the magnitudes of darkness lost

**Data.** The [David Lorenz light pollution atlas](https://djlorenz.github.io/astronomy/lp/) that Noctaxis
already installs for its map layer (Settings › Data). Its value at a place is the LPI: the artificial zenith
sky brightness as a multiple of the natural sky brightness. The score reads it at the plan's place through the
Planner map's own reader of the installed files (bilinear between the atlas's 1/120° cells), sharing its
decoded-tile cache. It downloads nothing, and reads again only when the place or the installed atlas changes.

**Function.** The ratio is converted into the magnitudes of sky darkness lost to artificial light,
`Δm = 2.5 log10(1 + LPI)`. That's the astronomical scale of brightness: equal steps of Δm are equal factors of
brightness, which is how sky glow drowns faint detail. The score is anchored on Δm: (0, 100), (0.1, 97),
(0.3, 90), (0.75, 75), (1.2, 60), (1.5, 52), (2.5, 30), (3.6, 14), (5, 5).

| Artificial ÷ natural (LPI) | 0 | 0.05 | 0.11 | 0.33 | 0.5 | 1 | 2 | 3 | 6 | 9 | 27 | 50 | ≥ 99 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Δm (mag) | 0 | 0.05 | 0.11 | 0.31 | 0.44 | 0.75 | 1.19 | 1.51 | 2.11 | 2.50 | 3.62 | 4.27 | ≥ 5.0 |
| ≈ zenith mag/arcsec² | 22.00 | 21.95 | 21.89 | 21.69 | 21.56 | 21.25 | 20.81 | 20.49 | 19.89 | 19.50 | 18.38 | 17.73 | ≤ 17.0 |
| Score | 100 | 98.4 | 96.6 | 89.7 | 85.3 | 74.9 | 60.2 | 51.9 | 37.6 | 30.0 | 13.8 | 9.2 | 5 |

A near-natural sky loses almost nothing, rural skies remain good, and a sky twice its natural brightness or
more becomes a warning (below 65). The floor of 5 is deliberate: bright-sky photography (narrowband, the
brighter clusters and nebulae) is still possible in a city, so light pollution is never critical.

**Explanation bands** (wording only; the score is continuous across them):

| LPI | Explanation begins | Example |
|---|---|---|
| ≤ 0.11 | Dark site | "Dark site: artificial light adds only 5% to the natural sky glow." |
| ≤ 0.33 | Rural sky | "…adds about 20% to the natural sky glow." |
| ≤ 1 | Some light pollution | "…adds about 80% to the natural sky glow, dimming faint detail." |
| ≤ 3 | Suburban sky glow | "…about 4.0× the natural sky brightness…" |
| ≤ 9 | Bright suburban sky | "…narrowband filters or brighter targets will help." |
| > 9 | Urban sky glow | "…broadband deep-sky imaging is very limited; narrowband can still work." |

**Stored, not shown.** The ratio, Δm and an approximate zenith brightness are kept as measurements for Scout and
the tests. The zenith brightness, `22.0 − Δm` mag/arcsec², assumes a natural sky of 22.0 mag/arcsec². That's
reasonable for a moonless night at solar minimum, but the real natural sky varies by a few tenths. No Bortle
class is derived: the atlas has no Bortle scale, and a class inferred from a model would be falsely precise.

**Applicability.** Deep-sky targets and stars. Not the Sun or Moon, whose brightness swamps sky glow.

**Unknown, not zero.** With no atlas installed, while it is being read, outside its coverage (north of 75° N or
south of 65° S), or when its files can't be read, the metric is unavailable. Its explanation says which; it
earns nothing and lowers coverage. A missing value is never treated as a dark sky.

### Target in frame: anchored on the offset

The target is projected with `CameraFrameGeometry.Project`, the same gnomonic rule the Camera frame draws by.
The offset is the larger of its two coordinates (0 at the centre, 1 at an edge). Anchors: (0, 100), (0.8, 100),
(1, 50). Beyond the edge, or behind the camera, the score is 0.

| Offset | ≤ 0.8 | 0.85 | 0.9 | 0.95 | 1 | > 1 |
|---|---|---|---|---|---|---|
| Score | 100 | 94.5 | 81.3 | 64.8 | 50 | 0 |

### Framing fit: anchored on the frame fill

`fill = max(major axis / long side of frame, minor axis / short side of frame)` is the best alignment, because
rotating the camera is the photographer's choice. Anchors: (0, 100), (0.8, 100), (1, 65), (1.5, 40), (3, 25).

| Fill | ≤ 0.8 | 0.9 | 1 | 1.25 | 1.5 | 2 | ≥ 3 |
|---|---|---|---|---|---|---|---|
| Score | 100 | 84.6 | 65 | 48.4 | 40 | 33.1 | 25 |

Up to 80% of the frame (a 10% margin on each side) fits comfortably, and up to 100% is a tight fit. Larger
targets decline gently to a floor of 25, because an intentional close-up or a mosaic is a choice, not an
impossibility.

### Image sampling: anchored on pixels across the smaller axis

On a rectilinear lens an angle θ spans `2 f tan(θ / 2)` on the sensor. Dividing by the camera profile's pixel
pitch (times the framing multiplier, which scales the sensor just as it scales the field of view) gives pixels.
Anchors: (0, 0), (12.5, 10), (25, 30), (50, 55), (100, 75), (200, 95), (400, 100).

| Pixels | 5 | 12.5 | 20 | 25 | 38 | 50 | 75 | 100 | 150 | 200 | 300 | ≥ 400 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Score | 3.7 | 10 | 22 | 30 | 45.1 | 55 | 67 | 75 | 87.9 | 95 | 98.1 | 100 |

The bands in the explanation are: under 25 px, very limited detail; 25–50, small; 50–100, modest detail
potential; 100–200, good; over 200, excellent projected size. These are guidance values, not resolution
guarantees: seeing, focus, tracking and optics are not modelled.

## 5. Thresholds and rationale, at a glance

| Threshold | Value | Why |
|---|---|---|
| Warning below | 65 | The score every "acceptable" anchor sits at: 25% cloud is above it, −12° Sun, 30° altitude and one hour of window sit exactly on it |
| Astronomical darkness | −18° | Standard definition; the sky adds no measurable twilight |
| Nautical darkness | −12° | Acceptable for most deep-sky imaging; the window's darkness condition |
| Acceptable altitude | 30° | About two airmasses; the window's altitude condition |
| Favourable altitude | 45° | Under about 1.4 airmasses |
| Marginal clearance | 2° | Where the terrain curve reaches about 94; below it, refraction and DEM error can matter |
| Critical cloud | 90% | The forecast says the sky is effectively closed |
| Forecast offset | 90 min | A forecast for a different hour is not used |
| Frame near edge | 80% of the half-frame | Leaves a 10% composition margin |
| Comfortable fill | 80% | The same margin, for the target's size |
| Sun and Moon diameters | 32.0′, 31.1′ | Nominal; the real values vary by a few per cent through the year |
| Natural sky | 22.0 mag/arcsec² | Only for the stored zenith-brightness estimate; the score uses Δm, which doesn't depend on it |

## 6. Representative values

The tables in section 4 are the representative input and output values for each curve. Here is a worked
example for a deep-sky target:
- 25% cloud: 78.7;
- the Sun at −15°: 88;
- 37.5° altitude: 87;
- 3° of terrain clearance: 99.7;
- a 2-hour window: 87.8;
- the Moon down: 100;
- centred in the frame: 100;
- filling half the frame: 100;
- a rural sky (LPI 0.33): 89.7;
- 100 px across: 75.

The weighted mean is
`(78.7×18 + 88×13 + 87×13 + 99.7×15 + 87.8×10 + 100×9 + 89.7×10 + 100×4 + 100×4 + 75×4) / 100 = 89.6`,
shown as **90 · Excellent**. Without a forecast, cloud is left out and the same night scores 92 with 82%
coverage and moderate confidence. Without the light-pollution atlas it scores 89.6 too, with 90% coverage, because
its 89.7 is close to the night's average.

## 7. Missing data

Missing or invalid data never earns points.

- **An applicable metric without reliable data is *unavailable*.** It contributes nothing to the score and
  counts only against coverage. Its explanation says why.
- **Cloud is unavailable** when the forecast is still loading, has failed, is stale, has no cloud value, has a
  value outside 0–100%, or is for a time more than 90 minutes from the planned moment.
- **Darkness and Moonlight are unavailable** when the Sun's or Moon's position describes a different moment, for
  example while the time is being scrubbed and before they are recalculated. A stale position is left out, not
  reused.
- **Terrain is unavailable** when terrain calculations are off, the horizon is still loading or incomplete, or
  there is no DEM coverage. In those cases the useful window is followed without terrain and says so.
- **Framing fit and Image sampling are unavailable** without a reliable catalogue size. Image sampling is also
  unavailable without a focal length or without a sensor resolution in the camera profile, and it says to add
  the resolution in Equipment. Nothing is assumed in its place.
- **Light pollution is unavailable** without an installed atlas, while it is being read, outside its coverage,
  or when it can't be read (section 4).
- **A target with only a major axis** in the catalogue is treated as round. This is the conservative reading
  for its smaller axis.
- **With no focused target**, there is no assessment: the indicator shows "—" and the details say to focus a
  target.

**Coverage** is the evaluated weight divided by the applicable weight. **Confidence** is High from 90%,
Moderate from 60%, and Low below that. Coverage is shown separately from the score.

## 8. Target-specific applicability

| Profile | Targets | Not applicable |
|---|---|---|
| Deep sky | Galaxies, nebulae, clusters, asterisms, "other" | — |
| Point source | Stars and double stars | Framing fit, Image sampling (a point has no size) |
| Sun | The Sun | Darkness, Moonlight, Light pollution, Useful time |
| Moon | The Moon | Darkness, Moonlight, Light pollution, Useful time |

A not-applicable metric counts towards neither the score nor coverage. So daylight Sun photography is never
penalised for daylight, and the Moon is never penalised for moonlight. The Sun and Moon use their own altitude
curve and are not penalised for landscape in the frame. Their size is the nominal diameter in section 5. There
are no planets in the catalogue yet; when they are added, they should get their own profile.

## 9. Warning severity

Each metric has a status, independent of the overall score. A high aggregate can still carry a critical
issue, and the counts of critical issues and warnings never depend on the number.

| Status | Icon | When |
|---|---|---|
| Critical | Octagon | A major limitation: the target is hidden by terrain (clearance ≤ 0) · the target is below the astronomical horizon with no lower terrain horizon to see it over · the target is outside the frame or behind the camera · the Sun is up, for a deep-sky target or star · cloud is 90% or more |
| Warning | Triangle | Conditions may reduce image quality or exposure time: any other scored metric under 65 · clearance under 2° · the target near the frame's edge · a target larger than the frame · a target below the astronomical horizon but above a lower terrain horizon |
| Good | Tick | Favourable: a scored metric at 65 or more, with none of the above |
| Unknown | Circled "i" | Not enough data to assess (the metric is unavailable) |

(In code, `ShotViabilitySeverity.Caution` is the Warning status and `None` is Unknown.) Metrics that don't apply
to the target are not listed. All statuses are advisory, even critical ones.

**Presentation.** The header shows the score with the icon of the most severe status. The details list each
applicable metric as its name and one explanation, which states its measurement once, with its status icon;
points, weights and raw values are not shown. Each status is told apart in three ways:
- **Its icon shape.**
- **Its word,** which starts the metric's accessible name ("Cloud cover: Warning. 80% cloud cover: …").
- **Its colour,** as a supplement: the theme's own `Success`, `Warning`, `Error` and `Information` resources.

The header's accessible name gives the score, the rating, and the critical issues and warnings separately
("1 critical issue and 2 warnings"). Its help text and tooltip, and an ⓘ button beside the details' title, say
that viability scores are advisory estimates that do not restrict planning or exporting.

**Accessibility checks** (`ShotViabilityAccessibilityTests`):
- In every theme, Light and Dark, and every colour-vision variant (56 combinations), each status colour reaches
  4.5:1 as text on the Camera frame card and on the details' surface (`ElevatedSurface`).
- The keyboard focus ring around a metric reaches 3:1.
- The four status colours stay apart for each variant's own viewer under simulation
  (`StatusColoursStayDistinctForTheirViewer`).
- In the running app, the details draw on `ElevatedSurface`, and every icon and explanation takes its status's
  theme resource, in all 56 combinations.

## 10. Combined score

```
score    = Σ (scoreᵢ × weightᵢ) / Σ weightᵢ        over evaluated metrics only
contributionᵢ = scoreᵢ × weightᵢ / Σ weight(evaluated)   (the contributions add up to the score)
coverage = Σ weight(evaluated) / Σ weight(applicable)
```

The score is shown rounded half away from zero. The rating follows the shown number, so 79.6 is shown as 80
and rated Excellent:

| Rating | Excellent | Good | Fair | Poor | Very poor |
|---|---|---|---|---|---|
| From | 80 | 65 | 45 | 25 | 0 |

With no metric evaluated there is no score, and the rating is "Unknown".

## 11. Known limitations and future refinements

- **The forecast is for one moment.** Cloud is scored at the planned moment only, not across the useful window.
  Scoring cloud across the window would need the forecast for each hour of it.
- **Light pollution is the atlas's model at the zenith.** It does not account for glow towards the horizon
  where the target may be, local lights or shielding at the site, or the atlas's age. Its approximate zenith
  brightness assumes a 22.0 mag/arcsec² natural sky.
- **Moonlight and light pollution are scored separately.** A brighter sky from both at once is not combined
  physically.
- **Moonlight is a heuristic.** It does not model sky brightness (for example Krisciunas–Schaefer), and the
  separation term ignores the Moon's altitude relative to the target.
- **The target's orientation is ignored.** Framing fit assumes the best alignment. Catalogue position angles
  and the camera's roll could refine it.
- **Sampling ignores seeing and optics.** A seeing-limited or Nyquist-based measure would be more defensible
  for long focal lengths.
- **Severity does not cap the score.** A hidden target can still score fairly well overall when everything else is
  ideal. The critical warning carries the message. Whether critical geometry should also cap the score is
  listed for review.
- **Window paths are sampled every 10 minutes**, and the window's ends are interpolated linearly between
  samples.
- **The weights and anchors are judgement calls** and need tuning against real nights.

## 12. Towards Scout

The engine was built for v2 automated scouting:
- `ShotViabilityScorer.Assess` takes a plain `ShotViabilityInput`, so Scout can build inputs for many candidate
  places and times with no user interface.
- It is pure and deterministic, so results can be cached by input and compared across candidates.
- Each metric reports its raw measurements as well as its score. Scout's hard constraints (minimum clearance,
  maximum cloud, Moon limits) can filter on the measurements directly; the roadmap's "hard filtering first,
  soft ranking after" maps onto this.
- The overall score is the soft ranking. Coverage tells Scout how far to trust a candidate whose data is
  incomplete.
- `UsefulWindowCalculator` already answers "for how long, from when?". Run over a span of start times, it is the
  core of the date/time opportunity search.
- Light pollution's stored ratio and Δm let Scout apply a strict sky-brightness limit before ranking.
- A future policy can add metrics such as cloud across the window without changing the shape of the results;
  v2 added light pollution this way.
