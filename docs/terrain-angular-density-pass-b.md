# Pass B terrain angular-density comparison

The minimap setting default is now **2.5×**. Its persisted range and crop-only behaviour are unchanged.

## Current production path

`TerrainProfileRequest.AzimuthSampleCount` is 360 for the normal horizon profile (1° bearing spacing). The camera obstruction path does not acquire another terrain profile: it calls `LocalHorizonCalculator.GetConeProfiles` over the camera FoV and reuses the profile's interpolated sightlines. With the current `TerrainCastAngularDetailDegrees = 10°` and a 70° FoV, the candidates are 8, 15, 29 and 57 rays (including both FoV edges), with actual spacings 8.75°, 4.667°, 2.414° and 1.228° respectively. Each ray uses the unchanged 60-point deterministic radial fixture in this benchmark. First obstruction is the first `TerrainOccluded` segment; terrain horizon is the maximum running slope. The two results remain separate.

The benchmark is opt-in (`NOCTAXIS_ANGULAR_BENCHMARK_OUTPUT=<path>`), offline and deterministic. It uses synthetic fixtures and does not touch environmental caches or network providers. Six runs are made after warm-up and the median is reported.

## Results

| Scenario | Density | Bearings | Spacing | Median ms | Allocated KiB | Radial samples | Frontier segments | Hit-sector differences vs 8× |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Flat | 1× | 8 | 8.750° | 0.118 | 12.3 | 60 | 8 | 0 |
| Flat | 2× | 15 | 4.667° | 0.224 | 21.0 | 60 | 15 | 0 |
| Flat | 4× | 29 | 2.414° | 0.807 | 130.9 | 60 | 29 | 0 |
| Flat | 8× | 57 | 1.228° | 1.842 | 350.1 | 60 | 57 | 0 |
| Coastal | 1× | 8 | 8.750° | 0.112 | 12.6 | 60 | 8 | 0 |
| Coastal | 2× | 15 | 4.667° | 0.208 | 21.3 | 60 | 15 | 0 |
| Coastal | 4× | 29 | 2.414° | 0.749 | 130.9 | 60 | 29 | 0 |
| Coastal | 8× | 57 | 1.228° | 1.679 | 350.1 | 60 | 57 | 0 |
| Mountain | 1× | 8 | 8.750° | 0.122 | 12.6 | 60 | 8 | 0 |
| Mountain | 2× | 15 | 4.667° | 0.168 | 21.3 | 60 | 15 | 0 |
| Mountain | 4× | 29 | 2.414° | 0.536 | 130.9 | 60 | 29 | 0 |
| Mountain | 8× | 57 | 1.228° | 1.380 | 350.1 | 60 | 57 | 0 |
| Narrow ridge | 1× | 8 | 8.750° | 0.078 | 12.6 | 60 | 8 | 0 |
| Narrow ridge | 2× | 15 | 4.667° | 0.123 | 21.3 | 60 | 15 | 0 |
| Narrow ridge | 4× | 29 | 2.414° | 0.450 | 130.9 | 60 | 29 | 0 |
| Narrow ridge | 8× | 57 | 1.228° | 1.123 | 350.1 | 60 | 57 | 0 |
| Quarry | 1× | 8 | 8.750° | 0.075 | 12.6 | 60 | 8 | 1 |
| Quarry | 2× | 15 | 4.667° | 0.140 | 21.3 | 60 | 16 | 1 |
| Quarry | 4× | 29 | 2.414° | 0.584 | 131.6 | 60 | 31 | 3 |
| Quarry | 8× | 57 | 1.228° | 1.436 | 373.1 | 60 | 61 | 0 |

These are geometry-only costs. No Terrarium tile loads, WorldCover classifications, or provider calls occur because the comparison consumes an already prepared immutable terrain profile. In production, increasing camera-cone density would therefore add profile mathematics and allocations, while the 360-bearing acquisition remains the dominant terrain-data operation.

The synthetic quarry fixture is the only case in this matrix with hit/no-hit differences. It shows that coarse angular sampling can miss a narrow transition; the broad flat, coastal, mountain and ridge fixtures do not distinguish the densities under the current interpolation. The 8× result is a highest-density benchmark reference, not ground truth. No visual image was generated because this headless geometry comparison already produces deterministic frontier counts and the current production renderer was intentionally left unchanged.

Memory scales with the returned ray list: approximately 12.6 KiB at 1×, 21.3 KiB at 2×, 130.9 KiB at 4× and 350–373 KiB at 8× for this 60-sample fixture. This is incremental transient/cone geometry memory, not a process working-set ceiling; the bounded completed-profile cache remains unchanged.

There is clear diminishing return after 2× on these fixtures: 2× is about 1.8–2.0× the 1× CPU cost, 4× is about 4.5–7×, and 8× is about 10–16×. The only measured geometric benefit is improved narrow-transition handling at 4×/8× in the quarry fixture. Adaptive refinement around hit/no-hit or sharp-distance transitions could reduce that cost, but the current `GetConeProfiles` API would need a new two-stage sampling contract and explicit result accounting; it was not implemented here.

The benchmark comparison remains historical: production camera detail was 10° when these measurements were captured. Production now uses the existing 1° default (71 rays for a 70° FoV), while the 360 profile bearings and all terrain acquisition remain unchanged. No user-facing angular-density setting, minimap LOD, Pass C obstruction work, or terrain semantic change was introduced.
