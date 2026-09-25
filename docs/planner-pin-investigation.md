# Planner location pin investigation — 14 September 2026

**Recommendation: KEEP CURRENT IMPLEMENTATION.** The pin's measured drawing/coordinate work is small, with no navigation-induced layout churn. Neither the Mapsui prototype nor hiding demonstrated a material responsiveness improvement. The layer prototype also fails the current cross-overlay stacking requirement. Production pin selection and rendering remain unchanged; only explicitly attached diagnostics activate alternatives.

This is a bounded conclusion, not a claim that the full Planner maintains a particular frame rate. Native measurements use the real `NoctaxisMapView` and Mapsui control on an offline blank map, without terrain, tile downloads, or user state. Compositor presentation and GPU completion were not measured. Build/test activity overlapped parts of the runs; process-wide CPU/GC and scheduling outliers are exploratory, not causal evidence or suitable migration criteria.

## Current architecture and update path

`NoctaxisMapView` contains a Grid with a Mapsui `MapControl`, a full-size custom Avalonia `MapOverlay : Control`, then attribution. The pin is **drawing commands inside the shared overlay**, not a separately positioned control, Canvas child, or bound layout element. `Render` projects the observer, draws camera/environmental/celestial content, then the pin on top.

The marker uses cached `StreamGeometry`, dark `#0B0F17` fill and a white 3-DIP outline. Its two semicircles form a 12-DIP-radius circle centred at `(0, 2)` relative to the geographic anchor; its bottom is at `y=14`. Changing this to a conventional bottom-anchored marker would change existing semantics. Colours are fixed across themes; text-size resources do not scale the pin. Avalonia device scaling scales its DIPs.

| Trigger | Existing behavior |
|---|---|
| Pan / zoom / rotation | A UI DispatcherTimer polls viewport centre, resolution, rotation, width and height every 33 ms. Changed signature calls the deliberately empty `PlanningPinInteractionState.ViewportChanged()` and invalidates the **shared** overlay. |
| Render / hit test | Forward Web Mercator, clamp polar latitude, wrap X nearest viewport centre, then Mapsui world-to-screen. Projection is computed again on left-button hit testing; the Render calculation is not stored in a position property. |
| Resize | Avalonia lays out the map/overlay normally; viewport dimensions also change the polling signature. Pin movement itself does not request measure/arrange. |
| Location / drag | `Observer` updates committed/preview interaction state, updates the overlay observer, invalidates geometry and normally recentres. Drag previews update the overlay and emit preview events; release emits one commit. Geographic elevation reset and cancellation semantics remain untouched. |
| Loading | Any non-None activity invalidates the shared overlay every timer tick. The activity ring builds fresh geometry, brush and pen. |
| Idle | Timer continues polling, but unchanged viewport and no activity produce no timer redraw. Timer is started in the constructor and is not stopped on detach in the existing implementation. This is a lifecycle follow-up candidate, not demonstrated pin-navigation overhead. |
| Theme / text / DPI | Marker resources are fixed. Text scaling affects attribution/containing layout, not marker geometry. DPI scales drawing through Avalonia. No pin-specific theme notification is needed. |
| Provider / style | Main map currently constructs the OSM tile layer; external-map preferences are unrelated. Projection depends on Mapsui's Web Mercator viewport, not tile content. Arbitrarily replacing `MapControl.Map` is not an established workflow: its ground-scale subscription captures the initial map. No provider-switch refactor was attempted. |

Viewport notifications also update `GroundMetresPerPixel` and `ViewportHeightPixels` for the production minimap. Off-UI notifications are coalesced onto the UI dispatcher. Those styled-property updates belong to the existing map/minimap path and persist with every pin variant. Pin navigation does not notify Observer bindings or invalidate the map. Overlay `Render` records Avalonia draw commands on the UI thread; actual composition occurs separately. The Mapsui layer callback executes inside Mapsui's rendering path and reads an immutable published pin state rather than Avalonia properties.

## Supported Mapsui mechanisms and prototype

The installed `Mapsui.Avalonia12` / Mapsui version is **5.1.0**, Avalonia **12.1.0**. Local package metadata identifies Mapsui source commit `f8b4da7d46c52a828d0c6bb133aa377a677ddbe3`. Supported options include `MemoryLayer`, `PointFeature`, `SymbolStyle`, image/custom point styles, and custom layer rendering. See [Mapsui custom point styles](https://mapsui.com/v5/custom-point-style-renderer/), [custom layer rendering](https://mapsui.com/v5/custom-layer-renderer/) and [SymbolStyle](https://mapsui.com/v5/api/pages/Mapsui.Styles.SymbolStyle.html). The code was compiled and rendered against the installed package, not assumed to match unpinned online documentation.

The isolated B prototype uses a `MemoryLayer` and supported `RegisterLayerRenderer` callback. This small hook draws the same circle/outline/loading arc with Skia and explicitly wraps the coordinate near the current viewport. It avoids ordinary point-feature extent culling dropping a pin in an adjacent wrapped world. A stock point symbol would be simpler for an unwrapped, unanimated circle; it does not remove the stacking or activity-refresh issues. No new rendering framework or map abstraction was introduced.

All modes are internal diagnostics, attached only by `tools/Noctaxis.PinProbe` or tests:

| Variant | Result / tradeoff |
|---|---|
| A: existing floating drawing | Existing topmost pin and hit testing; cached marker resources; approximately 30 Hz viewport polling can lag independently rendered map frames. No pin layout churn. |
| B: Mapsui layer | Draws at Mapsui's current viewport and often more frequently than A. Original parent input handlers retain drag, preview and commit behavior. Observer/activity changes explicitly refresh the map; loading adds map refreshes. **Layer sits below the separate Avalonia camera/terrain overlay, so this is not a production-equivalent replacement.** Preserving topmost stacking would require extra composition changes beyond moving one pin. |
| C: hidden during movement | Hides only the marker/ring, with a 200 ms quiet period before restoring. Active pin dragging keeps it visible. Retains geographic hit testing, including while hidden; this invisible hit target is a UX drawback. Shared overlay render/projection work remains. First movement is detected on the existing timer, so hiding is not instantaneous. |
| D: no marker drawing | Additional attribution baseline: keeps the shared overlay, its projection, timer, layout, and activity invalidations; removes only marker/ring drawing. It does not simulate removing the entire overlay. |

The prototype is deliberately not promoted: B has a known stacking mismatch, and C trades away a visible geographic reference for a tiny drawing saving. Terrain/minimap composition was not changed to accommodate either experiment.

## Native benchmark and results

The probe opens one 800×600 native window, clears online layers, disables planning content by supplying no snapshot, and drives 90 deterministic navigator inputs per scenario: idle, pan, zoom, combined, rapid repeated pan/zoom, resize, and stationary loading. Normal inputs request 16 ms spacing; rapid requests 4 ms. It performs two passes with reversed variant order, settling before each measurement and checking C restores after idle. Windows timer scheduling made actual UI wakeups closer to 31.5 / 16 ms in this run: these are **not 60 Hz pointer-input or presented-frame tests**.

Two native invocations completed. The retained second run is [raw JSON](benchmarks/planner-pin-native.json), with all 56 scenario records, and [derived CSV](benchmarks/planner-pin-native.csv). Source was the existing dirty worktree based on `5e0dc5e`, plus this investigation. Native render scaling was 1.0. First-pass/JIT samples are retained; the following table uses reverse-order pass 1 and is descriptive rather than statistically significant.

| Scenario | A: pin calls / mean µs | B: pin calls / mean µs | C: pin calls | A / B / C measure + arrange |
|---|---:|---:|---:|---:|
| Idle | 0 / — | 0 / — | 0 | 0 / 0 / 0 |
| Pan | 52 / 13.50 | 89 / 20.40 | 0 | 0 / 0 / 0 |
| Zoom | 65 / 12.13 | 88 / 18.35 | 0 | 0 / 0 / 0 |
| Combined | 66 / 12.68 | 89 / 18.35 | 0 | 0 / 0 / 0 |
| Rapid | 37 / 11.62 | 62 / 18.23 | 0 | 0 / 0 / 0 |
| Resize | 143 / 7.85 | 117 / 18.82 | 2 | 89+89 / 89+89 / 89+89 |
| Loading | 58 / 73.51 | 58 / 27.66 | 62 | 0 / 0 / 0 |

These pin timings have different boundaries: A records Avalonia draw commands; B issues Skia commands. Neither includes GPU completion/presentation, and B's lower loading callback time is not proof of lower total frame cost. Probe timing/counter overhead is included.

Additional measured characteristics:

- A's projection averaged 1.56–3.02 µs/call across active scenarios, with **zero measured projection allocations**. Projection remains in the shared overlay for B/C/D; B additionally projects in its layer callback.
- A allocated **776 bytes per ordinary pin draw**, despite cached marker resources (draw-command/transform recording is not allocation-free). At roughly 30 draws/s this is about 23 KB/s. Loading allocated **3,632 bytes/call**, about 109 KB/s at 30 Hz. B allocated 88 managed bytes/callback; this does not measure native Skia allocations.
- Idle produced zero overlay/pin renders after settling in every variant. Resizing produced 89 measure and 89 arrange calls in **all four** variants, identifying container resize work rather than pin-position layout churn.
- C eliminated ordinary pin draws during pan/zoom, but still recorded 68/66/66 shared overlay renders for pan/zoom/combined. A recorded 52/65/66, B 68/63/62, D 60/68/64. Differences reflect asynchronous polling/coalescing, not equal frame counts.
- Pan/zoom generated 89 map refresh requests in every variant. Stationary loading generated **57 extra map refresh requests for B**, versus zero for A/C/D. A's loading animation redraws its existing overlay instead.
- UI wakeup p95 stayed approximately 31.1–31.8 ms in normal scenarios and 16.1 ms in rapid scenarios. A's pan maximum was 50.9 ms; D's zoom maximum was 49.4 ms. There is no demonstrated consistent jank improvement, and these wakeups cannot count dropped compositor frames.
- Process allocation/CPU/GC totals are retained in CSV/JSON. Example zoom totals: A 1.85 MB / 453 ms CPU, B 1.58 MB / 359 ms, C 1.40 MB / 172 ms, D 1.37 MB / 281 ms. Non-monotonic baseline/order results and concurrent build/test load make these unsuitable for attributing whole-process gains to one pin. No absolute performance thresholds were added.

## Behavioral coverage and limits

| Concern | Evidence |
|---|---|
| Coordinate / pan / zoom / resize / rotation / dateline | New automated tests compare prototype projection to production, including an adjacent wrapped world, three resolutions and locations, and resized controls. Native navigation/resize runs exercised all variants. |
| Drag / preview / commit / location switching | New headless pointer-input test runs all three variants through drag preview and commit, then switches Observer. Existing MainViewModel location/overlay regression group passed. |
| Current / saved location | Source audit: workflows feed the same Observer/session path. Existing workflow tests were run; live device positioning and manual saved-card operation were not exercised. No services or persistence changed. |
| Visual / scaling | Actual Mapsui bitmap rendering verifies dark centre, white outline, dimensions and callback execution at 1× and 2×. The 1× output was visually inspected. Native monitor migration, fractional DPI, and full-window theme/text-scale interaction remain manual QA. |
| Theme | Both drawings preserve the existing fixed pin colours by construction. Theme screenshots for every family were not taken. |
| Providers | Offline rendering verifies no tile/provider dependency; main-map runtime provider switching is not currently exposed. Non-Mercator providers are outside the current architecture. |
| Terrain / minimap | Existing overlay regressions passed and implementations were untouched. Full native terrain plus B is not certified: known stacking mismatch blocks promotion. |

## Reproduce / profile

From the repository in PowerShell:

```powershell
dotnet restore tools/Noctaxis.PinProbe/Noctaxis.PinProbe.csproj
dotnet build tools/Noctaxis.PinProbe/Noctaxis.PinProbe.csproj -c Release --no-restore -p:OutDir=H:/Noctaxis/artifacts/pin-probe/
dotnet artifacts/pin-probe/Noctaxis.PinProbe.dll H:/Noctaxis/artifacts/pin-probe/native-results.json
./scripts/Summarize-PlannerPinProbe.ps1 artifacts/pin-probe/native-results.json | Export-Csv artifacts/pin-probe/summary.csv -NoTypeInformation
```

Allow approximately three minutes; the window exits automatically. Run on a quiet desktop without concurrent builds/tests, keep the window unobscured, repeat at least five times before drawing process-wide timing conclusions. It does not read/write user settings or request map tiles. Run it on the intended monitor for native DPI testing. Keep the existing application running if desired; isolated output avoids replacing its binaries, but close unrelated workloads for clean measurements.

```powershell
dotnet test Noctaxis.Desktop.Tests/Noctaxis.Desktop.Tests.csproj -c Release --no-restore -p:OutDir=H:/Noctaxis/artifacts/pin-tests/ --filter 'FullyQualifiedName~PlannerPinProbeTests|FullyQualifiedName~MainViewModelTests|FullyQualifiedName~EnvironmentalOverlayTests'
$env:NOCTAXIS_PIN_CAPTURES='H:/Noctaxis/artifacts/pin-captures'
dotnet test Noctaxis.Desktop.Tests/Noctaxis.Desktop.Tests.csproj -c Release --no-restore -p:OutDir=H:/Noctaxis/artifacts/pin-tests/ --filter FullyQualifiedName~PlannerPinProbeTests
```

For presentation timing, use Windows Performance Recorder / Analyzer on the same executable. The installed `wpr -profiles` lists CPU, GPU, and DesktopComposition. From an elevated PowerShell, with no other WPR session running:

```powershell
wpr -start CPU -start GPU -start DesktopComposition -filemode
dotnet artifacts/pin-probe/Noctaxis.PinProbe.dll H:/Noctaxis/artifacts/pin-probe/traced-results.json
wpr -stop H:/Noctaxis/artifacts/pin-probe/pin-presentation.etl
```

Open the ETL in Windows Performance Analyzer and inspect the probe process's UI/render threads and DWM presentation intervals. Match the run's variant order and cumulative `elapsedMs` plus 750 ms settling per scenario; for precise event alignment, add trace markers before collecting a causal trace. Do not report Task.Delay gaps as displayed frames. These recording commands are a reproduction recipe, not a collected trace: no ETL/GPU-completion trace was collected here. Full-Planner follow-up would need a settled, validated terrain snapshot and identical cached map data across variants; the existing terrain ViewportProbe is separate evidence, not a substitute for that comparison.

## Changes and validation

- `Noctaxis.Desktop/Controls/NoctaxisMapView.cs`: nullable diagnostic attachment, drawing/projection/layout/timer counters, diagnostic-only suppression and layer refresh hooks; existing production behavior retained.
- `Noctaxis.Desktop/Diagnostics/PlannerPinProbe.cs`: isolated A/B/C/D policy, counters/timings, immutable layer state and supported Mapsui prototype.
- `Noctaxis.Desktop/Properties/AssemblyInfo.cs`: friend access for the new executable, preserving prior friend declarations.
- `tools/Noctaxis.PinProbe/`: standalone native benchmark outside the production solution.
- `Noctaxis.Desktop.Tests/PlannerPinProbeTests.cs`: five tests for debounce, actual pointer interactions, coordinates, layout, and rendered layer pixels/scaling.
- `scripts/Summarize-PlannerPinProbe.ps1`, this report and `docs/benchmarks/planner-pin-native.{json,csv}`: reproducible evidence.

Release desktop/probe builds passed with zero warnings/errors. Targeted existing location/environmental tests plus the initial four new tests passed **197/197**. After adding the pointer-interaction test and stronger pixel assertions, the final pin group passed **5/5**. The entire solution suite was not rerun. Initial restore was blocked by sandbox NuGet access and succeeded with network approval. Unrelated pre-existing changes were preserved.

No migration plan is proposed because migration is not recommended. If pin-related work is revisited, first obtain a clean compositor trace showing a meaningful pin contribution; only then consider a narrow loading-ring allocation or timer-lifecycle improvement. Neither justifies changing terrain or map-layer architecture now.
