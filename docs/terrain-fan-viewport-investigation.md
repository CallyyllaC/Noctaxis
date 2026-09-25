# Viewport zoom/pan and high-zoom fan investigation

This historical baseline is followed by [the completed presentation correction and native GPU validation](terrain-fan-presentation-correction.md).

This is an investigation-only continuation. No terrain, shader, geometry or compositing semantics were changed.

## Native production path

`tools/Noctaxis.ViewportProbe` launches the real Avalonia `MainWindow`, reads the existing user state, selects the shortest configured lens (kit lens), waits for the Planner refresh, then drives static, pan, zoom, bearing and pitch sequences. It suppresses settings saves. `ViewportRenderProbe` records timings inside the production Skia custom draw callback and reports whether the Skia lease exposes a `GRContext`. When enabled, it flushes/submits the GPU before and after the measured stages so GPU completion wait is separate from CPU submission.

The completed kit-lens run reported 360 terrain bearings and 72.59° horizontal FoV. The context was GPU-backed (`Gpu=true` for every sample). The run used the existing cached terrain; cache timestamp warnings and a separate cache-release warning were logged by the existing provider when another process held a cache file, but the profile completed and the render samples were collected.

| Native stage | Samples | Mean total | Median / worst | Mean tone preparation | Mean projection | Mean path build | Mean fill | Mean GPU completion |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Static | 12 | 43.13 ms | 26.12 / 223.94 ms | 9.60 ms | 4.23 ms | 5.15 ms | 5.89 ms | 15.49 ms |
| Pan | 22 | 24.89 ms | 24.91 / 34.11 ms | 0 | 3.61 ms | 4.52 ms | 5.93 ms | 9.02 ms |
| Zoom | 35 | 18.06 ms | 20.67 / 24.58 ms | 0 | 2.05 ms | 2.99 ms | 4.54 ms | 7.27 ms |
| Bearing | 11 | 112.32 ms | 130.31 / 156.26 ms | 91.18 ms | 2.06 ms | 3.06 ms | 5.57 ms | 9.21 ms |
| Pitch | 11 | 20.19 ms | 19.93 / 24.56 ms | 0 | 2.02 ms | 2.82 ms | 5.33 ms | 9.03 ms |

The dominant live bearing cost is therefore terrain local-contrast preparation for each changed fan. The base environmental shader is not the zoom bottleneck: its measured CPU submission is below 1 ms in the native callback. The GPU completion wait is measurable but small. Pan and zoom reuse the existing fan/tone state and do not rerun fan semantics or tone preparation; their remaining callback cost is fill/path work and GPU completion. No native 60 Hz claim is made: the probe measures callback and GPU waits, not compositor presentation or monitor frame pacing.

The probe's input-to-observed-callback interval averaged 57.50 ms for zoom (129.14 ms worst), 50.26 ms for pan (85.43 ms worst), 73.88 ms for bearing (157.38 ms worst), and 33.72 ms for pitch (43.96 ms worst). This interval includes the programmatic event delay and native scheduling, but does not isolate Mapsui tile download/render work from Avalonia presentation. The native probe therefore confirms the overlay callback's cost and GPU mode, while leaving any additional map-tile or compositor stall as a separate unmeasured component.

The earlier CPU bitmap harness remains useful for controlled before/after comparisons, but it is not a native GPU measurement. Its approximately 190 ms base-shader result does not describe the live GPU-backed window.

## Artifact reproduction and cause

`TerrainFanArtifactDiagnosticsTests` creates a deterministic 73-ray fan with several radial transitions and renders at 1×, 2×, 4×, 8× and 16× viewport scale. Every path was finite and valid. Polar-domain containment showed no gaps and no overlapping cells (`polarOverlap=0`, `polarMax=1`) at every scale.

The projected SKPaths do overlap at shared boundaries. At 1×, 42,888 of 50,627 covered pixels were covered by more than one path, with up to five path hits; at 16×, the overlap remained 11,338 pixels. The projected probe also found 174, 86 and 55 clear pixels inside expected terrain at 1×, 2× and 4× respectively, falling to zero at 8× and 16×. The first measured overlap was between adjacent patches `[8°,9°]` and `[9°,10°]`; both legitimately share the 9° radial boundary, but independent path rasterization paints that shared edge from both sides. The image capture shows the resulting repeated internal edge stripes. This is a projected raster/compositing artifact, not a terrain-profile discontinuity: the polar partition remains exact (`polarOverlap=0`, `polarMax=1`) and its coarse polar probes have no gaps.

The production capture at 16× shows the same faint repeated internal bands under the normal single SaveLayer composition. The current layer prevents ordinary alpha accumulation of opaque patch fills, but it does not prevent adjacent paths from competing for shared raster edge pixels. A future correction should therefore use shared-edge-safe tessellation or a terrain-only mask/mesh with one ownership decision per pixel. Reducing opacity would conceal the symptom without correcting the measured geometry/compositing cause, so no opacity change was made here.

## Files added for measurement

- `Noctaxis.Desktop/Diagnostics/ViewportRenderProbe.cs` — opt-in callback timing sink.
- `tools/Noctaxis.ViewportProbe/` — native production window probe, outside the solution.
- `Noctaxis.Desktop.Tests/TerrainFanArtifactDiagnosticsTests.cs` — opt-in multi-scale projected-path diagnostic and captures.

The probe is opt-in through `NOCTAXIS_VIEWPORT_PROBE`; the artifact diagnostic is opt-in through `NOCTAXIS_FAN_ARTIFACTS`. Neither runs during ordinary CI tests. No production correction has been selected until the shared-edge strategy is benchmarked against the captured artifact and semantic geometry tests.
