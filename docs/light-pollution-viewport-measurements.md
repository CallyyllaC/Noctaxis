# Light Pollution viewport measurements

Generated from `artifacts/lp-viewport/baseline-final` and `after-native`. All times are milliseconds. Working sets are the lists recorded inside the native fetch strategy, including cached tiles; dispatch counts are actual source requests. Eviction, repeat and numerical counters in the tables below are stage deltas. Native relevance includes all current required fallback levels. Native eviction totals include tiles published and evicted within the same four-job batch.

## Path overview

| Viewport | Initial set | Pan peak | Zoom peak | Repeated XYZ before → after | Required native evictions before → after | Path fetch seconds before → after |
|---|---|---|---|---|---|---|
| 1920x1080 | 58 | 62 | 121 | 146 → 17 | 120 → 0 | 15.73 → 10.66 |
| 2560x1440 | 94 | 114 | 196 | 318 → 24 | 420 → 0 | 31.51 → 14.38 |
| 3440x1440 | 118 | 151 | 251 | 433 → 25 | 592 → 0 | 42.34 → 18.89 |
| 3840x2160 | 216 | 221 | 406 | 738 → 53 | 1049 → 0 | 66.24 → 31.28 |

## Before: 1920x1080

| Stage | Selected | Required levels:counts | Current | Fallback | Total set | Dispatched levels:counts | Dispatched | Unscheduled |
|---|---|---|---|---|---|---|---|---|
| initial | 8 | z8:40, z7:12, z6:4, z5:2 | 40 | 18 | 58 | z8:40, z7:12, z6:4, z5:2 | 58 | 0 |
| pan-east | 8 | z8:40, z7:12, z6:6, z5:2 | 40 | 20 | 60 | z8:10, z7:3, z6:2 | 15 | 0 |
| pan-south | 8 | z8:40, z7:12, z6:6, z5:4 | 40 | 22 | 62 | z8:12, z7:10, z6:5, z5:3 | 30 | 0 |
| zoom-out | 7 | z7:40, z6:15, z5:6, z4:2 | 40 | 23 | 63 | z7:40, z6:12, z5:3, z4:2 | 57 | 0 |
| zoom-mid | 8 | z8:84, z7:24, z6:9, z5:4 | 84 | 37 | 121 | z8:84, z6:1, z5:1 | 86 | 0 |
| return | 8 | z8:40, z7:12, z6:4, z5:2 | 40 | 18 | 58 | z8:30, z7:12, z6:3, z5:2 | 47 | 0 |

| Stage | Native count | Native evictions | Current-level evictions | All required evictions | Raster count | Raster evictions | Current raster evictions | Repeated XYZ | Missing detail | Uncovered centres |
|---|---|---|---|---|---|---|---|---|---|---|
| initial | 58 | 0 | 0 | 0 | 58 | 0 | 0 | 0 | 0 | 0 |
| pan-east | 40 | 33 | 6 | 20 | 64 | 9 | 9 | 0 | 6 | 6 |
| pan-south | 37 | 33 | 9 | 25 | 64 | 20 | 9 | 13 | 9 | 0 |
| zoom-out | 61 | 33 | 0 | 2 | 64 | 34 | 0 | 26 | 0 | 0 |
| zoom-mid | 48 | 99 | 42 | 73 | 64 | 86 | 23 | 60 | 42 | 24 |
| return | 62 | 33 | 0 | 0 | 64 | 33 | 4 | 47 | 0 | 0 |

| Stage | Raster hits | Raster misses/generations | Numerical hits | Numerical misses/decompressions | Numerical evictions | Numerical occupancy | Peak raster in flight |
|---|---|---|---|---|---|---|---|
| initial | 0 | 58 | 15204334 | 18 | 0 | 18 | 4 |
| pan-east | 0 | 15 | 3932160 | 0 | 0 | 18 | 4 |
| pan-south | 10 | 20 | 5242874 | 6 | 0 | 24 | 4 |
| zoom-out | 23 | 34 | 8388594 | 14 | 14 | 24 | 4 |
| zoom-mid | 0 | 86 | 22544379 | 5 | 5 | 24 | 4 |
| return | 14 | 33 | 8650745 | 7 | 7 | 24 | 4 |

| Stage | Fetch wall ms | Read wait sum | Read wait mean | Generation mean | PNG mean | Raster-start→publication mean | Raster-start→publication p95 | Source→publication p95 |
|---|---|---|---|---|---|---|---|---|
| initial | 4550.47 | 7608.38 | 131.18 | 54.74 | 22.81 | 209.19 | 797.91 | 0.242 |
| pan-east | 908.23 | 1255.73 | 83.72 | 34.69 | 25.44 | 144.50 | 243.04 | 0.190 |
| pan-south | 1191.54 | 1856.44 | 61.88 | 37.15 | 22.20 | 101.55 | 182.42 | 0.139 |
| zoom-out | 2054.82 | 3230.92 | 56.68 | 40.29 | 19.89 | 92.68 | 237.54 | 0.231 |
| zoom-mid | 5108.28 | 7629.01 | 88.71 | 35.89 | 23.31 | 148.04 | 245.08 | 0.239 |
| return | 1918.14 | 2856.80 | 60.78 | 35.36 | 22.58 | 101.49 | 185.36 | 0.034 |

Errors: 0; empty results: 0; rejected palette revisions: 0.

## Before: 2560x1440

| Stage | Selected | Required levels:counts | Current | Fallback | Total set | Dispatched levels:counts | Dispatched | Unscheduled |
|---|---|---|---|---|---|---|---|---|
| initial | 8 | z8:66, z7:18, z6:8, z5:2 | 66 | 28 | 94 | z8:66, z7:18, z6:8, z5:2 | 94 | 0 |
| pan-east | 8 | z8:66, z7:18, z6:6, z5:2 | 66 | 26 | 92 | z8:35, z7:3 | 38 | 0 |
| pan-south | 8 | z8:77, z7:24, z6:9, z5:4 | 77 | 37 | 114 | z8:59, z7:14, z6:9, z5:4 | 86 | 0 |
| zoom-out | 7 | z7:77, z6:24, z5:8, z4:2 | 77 | 34 | 111 | z7:68, z6:15, z5:4, z4:2 | 89 | 0 |
| zoom-mid | 8 | z8:135, z7:40, z6:15, z5:6 | 135 | 61 | 196 | z8:135, z7:24, z6:14, z5:4 | 177 | 0 |
| return | 8 | z8:66, z7:18, z6:8, z5:2 | 66 | 28 | 94 | z8:66, z7:14 | 80 | 0 |

| Stage | Native count | Native evictions | Current-level evictions | All required evictions | Raster count | Raster evictions | Current raster evictions | Repeated XYZ | Missing detail | Uncovered centres |
|---|---|---|---|---|---|---|---|---|---|---|
| initial | 61 | 33 | 33 | 33 | 64 | 30 | 30 | 0 | 33 | 0 |
| pan-east | 33 | 66 | 43 | 59 | 64 | 38 | 24 | 23 | 43 | 17 |
| pan-south | 53 | 66 | 46 | 61 | 64 | 75 | 40 | 53 | 46 | 0 |
| zoom-out | 43 | 99 | 47 | 68 | 64 | 84 | 34 | 20 | 47 | 0 |
| zoom-mid | 55 | 165 | 105 | 141 | 64 | 177 | 113 | 142 | 105 | 0 |
| return | 36 | 99 | 38 | 58 | 64 | 80 | 26 | 80 | 38 | 30 |

| Stage | Raster hits | Raster misses/generations | Numerical hits | Numerical misses/decompressions | Numerical evictions | Numerical occupancy | Peak raster in flight |
|---|---|---|---|---|---|---|---|
| initial | 0 | 94 | 24641518 | 18 | 0 | 18 | 4 |
| pan-east | 0 | 38 | 9961472 | 0 | 0 | 18 | 4 |
| pan-south | 11 | 75 | 19660794 | 6 | 0 | 24 | 4 |
| zoom-out | 5 | 84 | 21495788 | 20 | 20 | 24 | 4 |
| zoom-mid | 0 | 177 | 46399465 | 23 | 23 | 24 | 4 |
| return | 0 | 80 | 20971520 | 0 | 0 | 24 | 4 |

| Stage | Fetch wall ms | Read wait sum | Read wait mean | Generation mean | PNG mean | Raster-start→publication mean | Raster-start→publication p95 | Source→publication p95 |
|---|---|---|---|---|---|---|---|---|
| initial | 5420.26 | 7916.56 | 84.22 | 36.25 | 21.28 | 141.82 | 244.18 | 0.181 |
| pan-east | 2315.89 | 3471.90 | 91.37 | 37.15 | 23.67 | 152.26 | 245.79 | 0.193 |
| pan-south | 4432.88 | 6362.54 | 73.98 | 36.05 | 22.82 | 125.44 | 239.03 | 0.201 |
| zoom-out | 4357.17 | 6453.92 | 72.52 | 34.99 | 16.63 | 121.38 | 225.81 | 0.230 |
| zoom-mid | 10098.44 | 15271.40 | 86.28 | 35.89 | 20.97 | 143.24 | 247.19 | 0.207 |
| return | 4880.79 | 7168.06 | 89.60 | 38.77 | 22.13 | 150.57 | 258.12 | 0.173 |

Errors: 0; empty results: 0; rejected palette revisions: 0.

## Before: 3440x1440

| Stage | Selected | Required levels:counts | Current | Fallback | Total set | Dispatched levels:counts | Dispatched | Unscheduled |
|---|---|---|---|---|---|---|---|---|
| initial | 8 | z8:84, z7:24, z6:8, z5:2 | 84 | 34 | 118 | z8:84, z7:24, z6:8, z5:2 | 118 | 0 |
| pan-east | 8 | z8:84, z7:24, z6:10, z5:3 | 84 | 37 | 121 | z8:54, z7:15, z6:2, z5:1 | 72 | 0 |
| pan-south | 8 | z8:98, z7:32, z6:15, z5:6 | 98 | 53 | 151 | z8:65, z7:17, z6:13, z5:5 | 100 | 0 |
| zoom-out | 7 | z7:98, z6:28, z5:8, z4:2 | 98 | 38 | 136 | z7:88, z6:15, z5:3, z4:2 | 108 | 0 |
| zoom-mid | 8 | z8:180, z7:50, z6:15, z5:6 | 180 | 71 | 251 | z8:180, z7:38, z6:15, z5:6 | 239 | 0 |
| return | 8 | z8:84, z7:24, z6:8, z5:2 | 84 | 34 | 118 | z8:84, z7:24, z6:4 | 112 | 0 |

| Stage | Native count | Native evictions | Current-level evictions | All required evictions | Raster count | Raster evictions | Current raster evictions | Repeated XYZ | Missing detail | Uncovered centres |
|---|---|---|---|---|---|---|---|---|---|---|
| initial | 52 | 66 | 54 | 66 | 64 | 54 | 54 | 0 | 54 | 0 |
| pan-east | 58 | 66 | 44 | 63 | 64 | 72 | 26 | 54 | 44 | 2 |
| pan-south | 59 | 99 | 67 | 92 | 64 | 99 | 72 | 56 | 67 | 0 |
| zoom-out | 35 | 132 | 67 | 101 | 64 | 101 | 54 | 28 | 67 | 0 |
| zoom-mid | 43 | 231 | 152 | 208 | 64 | 239 | 175 | 183 | 152 | 0 |
| return | 56 | 99 | 56 | 62 | 64 | 112 | 48 | 112 | 56 | 0 |

| Stage | Raster hits | Raster misses/generations | Numerical hits | Numerical misses/decompressions | Numerical evictions | Numerical occupancy | Peak raster in flight |
|---|---|---|---|---|---|---|---|
| initial | 0 | 118 | 30932974 | 18 | 0 | 18 | 4 |
| pan-east | 0 | 72 | 18874362 | 6 | 0 | 24 | 4 |
| pan-south | 1 | 99 | 25952247 | 9 | 9 | 24 | 4 |
| zoom-out | 7 | 101 | 25952214 | 42 | 42 | 24 | 4 |
| zoom-mid | 0 | 239 | 62652386 | 30 | 30 | 24 | 4 |
| return | 0 | 112 | 29360123 | 5 | 5 | 24 | 4 |

| Stage | Fetch wall ms | Read wait sum | Read wait mean | Generation mean | PNG mean | Raster-start→publication mean | Raster-start→publication p95 | Source→publication p95 |
|---|---|---|---|---|---|---|---|---|
| initial | 6597.88 | 9660.67 | 81.87 | 34.71 | 21.06 | 137.69 | 239.45 | 0.153 |
| pan-east | 4076.83 | 6178.32 | 85.81 | 34.60 | 21.92 | 142.37 | 236.43 | 0.124 |
| pan-south | 6146.38 | 8996.94 | 89.97 | 39.29 | 22.65 | 151.36 | 268.67 | 0.176 |
| zoom-out | 5611.67 | 8592.35 | 79.56 | 37.07 | 18.36 | 131.45 | 239.04 | 0.160 |
| zoom-mid | 13432.35 | 20379.22 | 85.27 | 35.60 | 20.43 | 141.35 | 239.52 | 0.145 |
| return | 6471.16 | 9533.27 | 85.12 | 36.49 | 21.17 | 142.83 | 244.27 | 0.144 |

Errors: 0; empty results: 0; rejected palette revisions: 0.

## Before: 3840x2160

| Stage | Selected | Required levels:counts | Current | Fallback | Total set | Dispatched levels:counts | Dispatched | Unscheduled |
|---|---|---|---|---|---|---|---|---|
| initial | 8 | z8:160, z7:40, z6:12, z5:4 | 160 | 56 | 216 | z8:160, z7:40, z6:12, z5:4 | 216 | 0 |
| pan-east | 8 | z8:160, z7:40, z6:15, z5:6 | 160 | 61 | 221 | z8:132, z7:35, z6:3, z5:2 | 172 | 0 |
| pan-south | 8 | z8:144, z7:40, z6:15, z5:6 | 144 | 61 | 205 | z8:120, z7:23, z6:12, z5:4 | 159 | 0 |
| zoom-out | 7 | z7:144, z6:45, z5:15, z4:6 | 144 | 66 | 210 | z7:137, z6:33, z5:11, z4:6 | 187 | 0 |
| zoom-mid | 8 | z8:286, z7:84, z6:28, z5:8 | 286 | 120 | 406 | z8:256 | 256 | 140 |
| return | 8 | z8:160, z7:40, z6:12, z5:4 | 160 | 56 | 216 | z8:160, z7:40, z6:12, z5:4 | 216 | 0 |

| Stage | Native count | Native evictions | Current-level evictions | All required evictions | Raster count | Raster evictions | Current raster evictions | Repeated XYZ | Missing detail | Uncovered centres |
|---|---|---|---|---|---|---|---|---|---|---|
| initial | 51 | 165 | 132 | 165 | 64 | 152 | 152 | 0 | 132 | 0 |
| pan-east | 58 | 165 | 130 | 163 | 64 | 172 | 112 | 142 | 130 | 64 |
| pan-south | 52 | 165 | 115 | 153 | 64 | 159 | 112 | 135 | 115 | 0 |
| zoom-out | 41 | 198 | 114 | 169 | 64 | 171 | 130 | 51 | 114 | 0 |
| zoom-mid | 33 | 264 | 233 | 234 | 64 | 256 | 192 | 194 | 263 | 114 |
| return | 51 | 198 | 132 | 165 | 64 | 216 | 174 | 216 | 132 | 0 |

| Stage | Raster hits | Raster misses/generations | Numerical hits | Numerical misses/decompressions | Numerical evictions | Numerical occupancy | Peak raster in flight |
|---|---|---|---|---|---|---|---|
| initial | 0 | 216 | 56623080 | 24 | 0 | 24 | 4 |
| pan-east | 0 | 172 | 45088759 | 9 | 9 | 24 | 4 |
| pan-south | 0 | 159 | 41680887 | 9 | 9 | 24 | 4 |
| zoom-out | 16 | 171 | 43253655 | 105 | 105 | 24 | 4 |
| zoom-mid | 0 | 256 | 67108838 | 26 | 26 | 24 | 4 |
| return | 0 | 216 | 56623096 | 8 | 8 | 24 | 4 |

| Stage | Fetch wall ms | Read wait sum | Read wait mean | Generation mean | PNG mean | Raster-start→publication mean | Raster-start→publication p95 | Source→publication p95 |
|---|---|---|---|---|---|---|---|---|
| initial | 11574.72 | 17346.29 | 80.31 | 34.00 | 19.41 | 133.76 | 227.90 | 0.101 |
| pan-east | 9682.96 | 14428.29 | 83.89 | 35.35 | 20.76 | 140.05 | 237.42 | 0.131 |
| pan-south | 8983.98 | 13411.64 | 84.35 | 35.39 | 20.96 | 140.74 | 241.42 | 0.125 |
| zoom-out | 8626.33 | 12758.51 | 68.23 | 34.06 | 16.23 | 114.26 | 222.60 | 0.146 |
| zoom-mid | 15439.01 | 23177.95 | 90.54 | 38.31 | 21.73 | 150.62 | 271.28 | 0.146 |
| return | 11933.13 | 17660.59 | 81.76 | 35.26 | 19.85 | 136.91 | 235.33 | 0.124 |

Errors: 0; empty results: 0; rejected palette revisions: 0.

## After: 1920x1080

| Stage | Selected | Required levels:counts | Current | Fallback | Total set | Dispatched levels:counts | Dispatched | Unscheduled |
|---|---|---|---|---|---|---|---|---|
| initial | 8 | z8:40, z7:12, z6:4, z5:2 | 40 | 18 | 58 | z8:40, z7:12, z6:4, z5:2 | 58 | 0 |
| pan-east | 8 | z8:40, z7:12, z6:6, z5:2 | 40 | 20 | 60 | z8:10, z7:3, z6:2 | 15 | 0 |
| pan-south | 8 | z8:40, z7:12, z6:6, z5:4 | 40 | 22 | 62 | z8:8, z7:4, z6:3, z5:2 | 17 | 0 |
| zoom-out | 7 | z7:40, z6:15, z5:6, z4:2 | 40 | 23 | 63 | z7:24, z6:6, z5:2, z4:2 | 34 | 0 |
| zoom-mid | 8 | z8:84, z7:24, z6:9, z5:4 | 84 | 37 | 121 | z8:40 | 40 | 0 |
| return | 8 | z8:40, z7:12, z6:4, z5:2 | 40 | 18 | 58 | none | 0 | 0 |

| Stage | Native count | Native evictions | Current-level evictions | All required evictions | Raster count | Raster evictions | Current raster evictions | Repeated XYZ | Missing detail | Uncovered centres |
|---|---|---|---|---|---|---|---|---|---|---|
| initial | 58 | 0 | 0 | 0 | 58 | 0 | 0 | 0 | 0 | 0 |
| pan-east | 73 | 0 | 0 | 0 | 64 | 9 | 9 | 0 | 0 | 0 |
| pan-south | 81 | 9 | 0 | 0 | 64 | 17 | 9 | 0 | 0 | 0 |
| zoom-out | 107 | 8 | 0 | 0 | 64 | 31 | 9 | 3 | 0 | 0 |
| zoom-mid | 147 | 0 | 0 | 0 | 64 | 40 | 16 | 14 | 0 | 0 |
| return | 147 | 0 | 0 | 0 | 64 | 0 | 0 | 0 | 0 | 0 |

| Stage | Raster hits | Raster misses/generations | Numerical hits | Numerical misses/decompressions | Numerical evictions | Numerical occupancy | Peak raster in flight |
|---|---|---|---|---|---|---|---|
| initial | 0 | 58 | 15204334 | 18 | 0 | 18 | 4 |
| pan-east | 0 | 15 | 3932160 | 0 | 0 | 18 | 4 |
| pan-south | 0 | 17 | 4456442 | 6 | 0 | 24 | 4 |
| zoom-out | 3 | 31 | 7602166 | 10 | 10 | 24 | 4 |
| zoom-mid | 0 | 40 | 10485758 | 2 | 2 | 24 | 4 |
| return | 0 | 0 | 0 | 0 | 0 | 24 | 0 |

| Stage | Fetch wall ms | Read wait sum | Read wait mean | Generation mean | PNG mean | Raster-start→publication mean | Raster-start→publication p95 | Source→publication p95 |
|---|---|---|---|---|---|---|---|---|
| initial | 4669.10 | 7662.82 | 132.12 | 56.56 | 23.17 | 212.31 | 665.93 | 0.266 |
| pan-east | 871.75 | 1219.04 | 81.27 | 32.53 | 25.49 | 139.33 | 233.09 | 0.061 |
| pan-south | 983.57 | 1383.73 | 81.40 | 34.57 | 22.81 | 139.23 | 237.09 | 0.276 |
| zoom-out | 1658.81 | 2640.37 | 77.66 | 34.72 | 18.67 | 126.39 | 208.79 | 0.050 |
| zoom-mid | 2481.22 | 3678.34 | 91.96 | 37.14 | 24.75 | 153.88 | 253.73 | 0.042 |
| return | 0.05 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |

Errors: 0; empty results: 0; rejected palette revisions: 0.

## After: 2560x1440

| Stage | Selected | Required levels:counts | Current | Fallback | Total set | Dispatched levels:counts | Dispatched | Unscheduled |
|---|---|---|---|---|---|---|---|---|
| initial | 8 | z8:66, z7:18, z6:8, z5:2 | 66 | 28 | 94 | z8:66, z7:18, z6:8, z5:2 | 94 | 0 |
| pan-east | 8 | z8:66, z7:18, z6:6, z5:2 | 66 | 26 | 92 | z8:12, z7:3 | 15 | 0 |
| pan-south | 8 | z8:77, z7:24, z6:9, z5:4 | 77 | 37 | 114 | z8:22, z7:6, z6:3, z5:2 | 33 | 0 |
| zoom-out | 7 | z7:77, z6:24, z5:8, z4:2 | 77 | 34 | 111 | z7:53, z6:15, z5:4, z4:2 | 74 | 0 |
| zoom-mid | 8 | z8:135, z7:40, z6:15, z5:6 | 135 | 61 | 196 | z8:54 | 54 | 0 |
| return | 8 | z8:66, z7:18, z6:8, z5:2 | 66 | 28 | 94 | none | 0 | 0 |

| Stage | Native count | Native evictions | Current-level evictions | All required evictions | Raster count | Raster evictions | Current raster evictions | Repeated XYZ | Missing detail | Uncovered centres |
|---|---|---|---|---|---|---|---|---|---|---|
| initial | 94 | 0 | 0 | 0 | 64 | 30 | 30 | 0 | 0 | 0 |
| pan-east | 109 | 0 | 0 | 0 | 64 | 15 | 15 | 0 | 0 | 0 |
| pan-south | 129 | 13 | 0 | 0 | 64 | 33 | 6 | 0 | 0 | 0 |
| zoom-out | 192 | 11 | 0 | 0 | 64 | 74 | 25 | 5 | 0 | 0 |
| zoom-mid | 246 | 0 | 0 | 0 | 64 | 54 | 0 | 19 | 0 | 0 |
| return | 246 | 0 | 0 | 0 | 64 | 0 | 0 | 0 | 0 | 0 |

| Stage | Raster hits | Raster misses/generations | Numerical hits | Numerical misses/decompressions | Numerical evictions | Numerical occupancy | Peak raster in flight |
|---|---|---|---|---|---|---|---|
| initial | 0 | 94 | 24641518 | 18 | 0 | 18 | 4 |
| pan-east | 0 | 15 | 3932160 | 0 | 0 | 18 | 4 |
| pan-south | 0 | 33 | 8650746 | 6 | 0 | 24 | 4 |
| zoom-out | 0 | 74 | 18874346 | 22 | 22 | 24 | 4 |
| zoom-mid | 0 | 54 | 14155768 | 8 | 8 | 24 | 4 |
| return | 0 | 0 | 0 | 0 | 0 | 24 | 0 |

| Stage | Fetch wall ms | Read wait sum | Read wait mean | Generation mean | PNG mean | Raster-start→publication mean | Raster-start→publication p95 | Source→publication p95 |
|---|---|---|---|---|---|---|---|---|
| initial | 5097.59 | 7495.02 | 79.73 | 33.43 | 20.70 | 133.90 | 228.88 | 0.044 |
| pan-east | 880.88 | 1247.17 | 83.14 | 33.30 | 25.33 | 141.82 | 235.16 | 0.041 |
| pan-south | 2051.25 | 3046.56 | 92.32 | 37.34 | 24.71 | 154.44 | 251.93 | 0.116 |
| zoom-out | 3535.80 | 5331.89 | 72.05 | 32.52 | 15.15 | 119.80 | 207.63 | 0.093 |
| zoom-mid | 2812.36 | 4080.29 | 75.56 | 32.58 | 19.37 | 127.55 | 222.44 | 0.041 |
| return | 0.07 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |

Errors: 0; empty results: 0; rejected palette revisions: 0.

## After: 3440x1440

| Stage | Selected | Required levels:counts | Current | Fallback | Total set | Dispatched levels:counts | Dispatched | Unscheduled |
|---|---|---|---|---|---|---|---|---|
| initial | 8 | z8:84, z7:24, z6:8, z5:2 | 84 | 34 | 118 | z8:84, z7:24, z6:8, z5:2 | 118 | 0 |
| pan-east | 8 | z8:84, z7:24, z6:10, z5:3 | 84 | 37 | 121 | z8:12, z7:3, z6:2, z5:1 | 18 | 0 |
| pan-south | 8 | z8:98, z7:32, z6:15, z5:6 | 98 | 53 | 151 | z8:28, z7:8, z6:5, z5:3 | 44 | 0 |
| zoom-out | 7 | z7:98, z6:28, z5:8, z4:2 | 98 | 38 | 136 | z7:66, z6:13, z5:2, z4:2 | 83 | 0 |
| zoom-mid | 8 | z8:180, z7:50, z6:15, z5:6 | 180 | 71 | 251 | z8:78 | 78 | 0 |
| return | 8 | z8:84, z7:24, z6:8, z5:2 | 84 | 34 | 118 | none | 0 | 0 |

| Stage | Native count | Native evictions | Current-level evictions | All required evictions | Raster count | Raster evictions | Current raster evictions | Repeated XYZ | Missing detail | Uncovered centres |
|---|---|---|---|---|---|---|---|---|---|---|
| initial | 118 | 0 | 0 | 0 | 64 | 54 | 54 | 0 | 0 | 0 |
| pan-east | 136 | 0 | 0 | 0 | 64 | 18 | 12 | 0 | 0 | 0 |
| pan-south | 169 | 11 | 0 | 0 | 64 | 44 | 5 | 0 | 0 | 0 |
| zoom-out | 238 | 14 | 0 | 0 | 64 | 83 | 30 | 3 | 0 | 0 |
| zoom-mid | 316 | 0 | 0 | 0 | 64 | 78 | 14 | 22 | 0 | 0 |
| return | 316 | 0 | 0 | 0 | 64 | 0 | 0 | 0 | 0 | 0 |

| Stage | Raster hits | Raster misses/generations | Numerical hits | Numerical misses/decompressions | Numerical evictions | Numerical occupancy | Peak raster in flight |
|---|---|---|---|---|---|---|---|
| initial | 0 | 118 | 30932974 | 18 | 0 | 18 | 4 |
| pan-east | 0 | 18 | 4718586 | 6 | 0 | 24 | 4 |
| pan-south | 0 | 44 | 11534328 | 8 | 8 | 24 | 4 |
| zoom-out | 0 | 83 | 21233624 | 40 | 40 | 24 | 4 |
| zoom-mid | 0 | 78 | 20447221 | 11 | 11 | 24 | 4 |
| return | 0 | 0 | 0 | 0 | 0 | 24 | 0 |

| Stage | Fetch wall ms | Read wait sum | Read wait mean | Generation mean | PNG mean | Raster-start→publication mean | Raster-start→publication p95 | Source→publication p95 |
|---|---|---|---|---|---|---|---|---|
| initial | 6420.95 | 9378.86 | 79.48 | 33.18 | 21.14 | 133.84 | 231.24 | 0.055 |
| pan-east | 1062.16 | 1499.38 | 83.30 | 33.98 | 24.94 | 142.25 | 238.65 | 0.042 |
| pan-south | 2760.52 | 4159.19 | 94.53 | 39.02 | 23.61 | 157.21 | 258.95 | 0.080 |
| zoom-out | 4369.20 | 6642.68 | 80.03 | 35.17 | 17.32 | 132.58 | 222.74 | 0.092 |
| zoom-mid | 4279.69 | 6357.87 | 81.51 | 35.03 | 19.70 | 136.28 | 233.49 | 0.054 |
| return | 0.12 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |

Errors: 0; empty results: 0; rejected palette revisions: 0.

## After: 3840x2160

| Stage | Selected | Required levels:counts | Current | Fallback | Total set | Dispatched levels:counts | Dispatched | Unscheduled |
|---|---|---|---|---|---|---|---|---|
| initial | 8 | z8:160, z7:40, z6:12, z5:4 | 160 | 56 | 216 | z8:160, z7:40, z6:12, z5:4 | 216 | 0 |
| pan-east | 8 | z8:160, z7:40, z6:15, z5:6 | 160 | 61 | 221 | z8:20, z7:5, z6:3, z5:2 | 30 | 0 |
| pan-south | 8 | z8:144, z7:40, z6:15, z5:6 | 144 | 61 | 205 | z8:16, z7:8 | 24 | 0 |
| zoom-out | 7 | z7:144, z6:45, z5:15, z4:6 | 144 | 66 | 210 | z7:96, z6:30, z5:9, z4:6 | 141 | 0 |
| zoom-mid | 8 | z8:286, z7:84, z6:28, z5:8 | 286 | 120 | 406 | z8:138 | 138 | 0 |
| return | 8 | z8:160, z7:40, z6:12, z5:4 | 160 | 56 | 216 | none | 0 | 0 |

| Stage | Native count | Native evictions | Current-level evictions | All required evictions | Raster count | Raster evictions | Current raster evictions | Repeated XYZ | Missing detail | Uncovered centres |
|---|---|---|---|---|---|---|---|---|---|---|
| initial | 216 | 0 | 0 | 0 | 64 | 152 | 152 | 0 | 0 | 0 |
| pan-east | 246 | 0 | 0 | 0 | 64 | 30 | 4 | 0 | 0 | 0 |
| pan-south | 250 | 20 | 0 | 0 | 64 | 24 | 0 | 0 | 0 | 0 |
| zoom-out | 358 | 33 | 0 | 0 | 64 | 141 | 90 | 5 | 0 | 0 |
| zoom-mid | 496 | 0 | 0 | 0 | 64 | 138 | 74 | 48 | 0 | 0 |
| return | 496 | 0 | 0 | 0 | 64 | 0 | 0 | 0 | 0 | 0 |

| Stage | Raster hits | Raster misses/generations | Numerical hits | Numerical misses/decompressions | Numerical evictions | Numerical occupancy | Peak raster in flight |
|---|---|---|---|---|---|---|---|
| initial | 0 | 216 | 56623080 | 24 | 0 | 24 | 4 |
| pan-east | 0 | 30 | 7864311 | 9 | 9 | 24 | 4 |
| pan-south | 0 | 24 | 6291456 | 0 | 0 | 24 | 4 |
| zoom-out | 0 | 141 | 35389338 | 102 | 102 | 24 | 4 |
| zoom-mid | 0 | 138 | 36175844 | 28 | 28 | 24 | 4 |
| return | 0 | 0 | 0 | 0 | 0 | 24 | 0 |

| Stage | Fetch wall ms | Read wait sum | Read wait mean | Generation mean | PNG mean | Raster-start→publication mean | Raster-start→publication p95 | Source→publication p95 |
|---|---|---|---|---|---|---|---|---|
| initial | 12319.43 | 18360.87 | 85.00 | 36.38 | 20.49 | 141.92 | 242.50 | 0.086 |
| pan-east | 1986.87 | 2850.74 | 95.02 | 39.42 | 26.69 | 161.18 | 273.78 | 0.066 |
| pan-south | 1434.02 | 2223.42 | 92.64 | 37.53 | 22.11 | 152.33 | 256.56 | 0.074 |
| zoom-out | 7425.86 | 11150.08 | 79.08 | 36.28 | 16.23 | 131.64 | 233.74 | 0.075 |
| zoom-mid | 8115.21 | 11930.78 | 86.45 | 39.00 | 19.55 | 145.07 | 263.74 | 0.121 |
| return | 0.37 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.000 |

Errors: 0; empty results: 0; rejected palette revisions: 0.

## Retained-memory estimates after the fix

Encoded bytes are measured from the two cache contents and deduplicated by byte-array reference. Numerical bytes include loaded source grids and the overview. Decoded native image storage is a conservative 256×256×4 bytes per retained feature, not a measured GPU allocation. Estimates exclude object overhead, framebuffers, temporary encoding buffers and other application/map-renderer caches.

| Viewport | Capacity high-water | Raster PNG MiB | Distinct PNG MiB | Numerical MiB | Potential native RGBA MiB | Combined estimate MiB |
|---|---|---|---|---|---|---|
| 1920x1080 | 151 | 4.48 | 8.94 | 60.64 | 36.75 | 106.33 |
| 2560x1440 | 250 | 4.26 | 13.03 | 60.64 | 61.50 | 135.17 |
| 3440x1440 | 320 | 4.22 | 16.30 | 60.64 | 79.00 | 155.95 |
| 3840x2160 | 500 | 4.30 | 24.10 | 60.64 | 124.00 | 208.74 |
