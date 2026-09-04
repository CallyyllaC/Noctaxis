# Settlement-galaxy visual calibration plan

## Scope and authority

The retained original prototype frames are the visual-strength and hierarchy authority. The
reconstructed Python Passes 1-14 bundle is used only to confirm equations and pass structure.
Production continues to use WSF settlement rasters. Exact stars, local topology, road geometry,
and raw pixel similarity are intentionally not optimisation targets. Files under
`debug_not_targets` are excluded from scoring and review.

The calibration fixture is saved location `015cb354-786a-45ab-acd4-6f8502813731` at zoom 13.
The harness reads its persisted `source.png`, `features.json`, and `settlement-field.bin.gz`; it
does not perform network or acquisition work.

## Regression-first corrections

1. Lock Pass 12 to `powered * (minimumOpacity + (1 - minimumOpacity) * powered)`, then apply
   `outerWeight`; derive `mid` from that weighted falloff.
2. Apply the satellite meaningful-strength threshold before dividing subordinate components into
   satellites and faint minor components. Microscopic WSF islands receive no component treatment.

## Bounded calibration groups

| Group | Tunable style members | Candidate bounds around the current preset | Ranking emphasis |
|---|---|---|---|
| Passes 1-3 | hierarchy halo/body/core gains; luminance broad/dense/knot gains | low halo/broad gains up to 2.25x; other gains 0.75-1.65x | blurred positive-light coverage and energy, body mean/peak, core/body and outer/body ratios |
| Passes 4-6 | core bloom/aura; cloud gain; wisp gain range | radiance 0.85-1.50x, cloud up to 2.00x, wisps 0.75-1.20x | Pass 6 broad-body energy and coverage without core collapse |
| Pass 7 | star density/cap; percentile; core sigma; class/bloom gains | count 1,600-6,000, density 0.25-1.00x, percentile 99.70-99.93 | highlight fractions and percentiles, near-white connected area, star/body energy, bright count and footprint |
| Pass 8 | core/bridge/haze sigma only | 0.85-1.20x | hue/chroma distribution and zone continuity; chroma strengths remain exactly 0.58/0.49/0.40 |
| Passes 9-12 | satellite gains; ambience gains; falloff radius/gain | satellites 0.85-1.15x, ambience 0.75-1.10x, falloff radius 64-80 and gain 0.175-0.225 | subordinate components, background retreat, broad envelope coverage, no threshold edge |
| Pass 13 | selected tonemap values | threshold 0.64-0.70, compression 0.24-0.32, local contrast 0.26-0.34, curve 0.09-0.14, saturation 1.07-1.15 | final luminance histogram, highlight compression, and mean chroma |

Each group uses a small deterministic baseline plus coordinated lower/upper candidates. The top
few are retained under the calibration output directory for visual review. Only reviewed values
are written to `noctaxis_galaxy_style_v1.json`; there is no global random search.

## Harness outputs

Every run writes `01-hierarchy.png` through `13-tonemapping.png`, plus `metrics.json`. Per-pass
metrics include positive-light coverage/energy, blurred luminance/chroma statistics, body/core/
outer ratios, luminance percentiles and highlight fractions, near-white component size/count,
star delta energy and footprint, and lavender/cyan hue shares. Candidate ranking is recorded in
`ranking.json` together with the style hash and fixed-location identity.

Run the bounded sweep with:

```powershell
dotnet run --project Noctaxis.GalaxyCalibration -c Release -- `
  <saved-location-directory> <original-prototype-frames-directory> <output-directory>
```

After committing reviewed values to the embedded preset, render only that selected preset with:

```powershell
dotnet run --project Noctaxis.GalaxyCalibration -c Release -- `
  --render-only <saved-location-directory> <output-directory>
```

Render-only mode also writes `selected-style.json` and never reads the reference pack.
