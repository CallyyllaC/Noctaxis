# Event-driven overlay synchronization

## Interrupted work recovered

The worktree inspection on 15 September 2026 found the implementation already present. It was retained, not recreated:

- `NoctaxisMapView.cs`: navigation subscribes to `Navigator.ViewportChanged` on attachment and unsubscribes on detachment. UI-thread invalidations are immediate; worker notifications are posted at Render priority. The overlay reads the current viewport when rendering. The 33 ms timer runs only for pin activity or explicitly enabled diagnostic comparisons. Detachment stops it and invalidates pending scheduler callbacks.
- `ViewportRedrawScheduler.cs`: one outstanding invalidation per render, with generations rejecting callbacks from an earlier render or attachment.
- `OverlaySynchronizationTests.cs`: four tests for coalescing, immediate UI updates, stale callbacks, worker navigation/minimap updates, idle behaviour and detach/reattach animation lifecycle.
- `OverlaySynchronizationProbe.cs`, `tools/Noctaxis.PinProbe/SynchronizationRun.cs` and the `--sync` entry point: native polling/event comparison, Mapsui reference halo, composition-coordinate error measurements and plots.
- Existing pin-probe access and assembly friendship were already present. The worktree also contained unrelated appearance, terrain, support and external-map changes, including other hunks in `NoctaxisMapView.cs`; these were preserved.

This continuation adds three native detach/reattach lifecycle cycles to the existing harness, the summary script, and measured artifacts. No production changes were needed after inspection and verification.

## Verification

- Focused Release tests: 10 passed, zero failed (overlay synchronization, planner pin probe and viewport render tests).
- Full desktop Release suite: 737 passed, zero failed, zero skipped; approximately 1 minute 43 seconds. TRX: `artifacts/sync-test-results/desktop.trx`.
- Native probe Release build: zero warnings and errors. Build outputs were isolated from the user's running application. A transient shared compiler-output collision during overlapping builds was resolved by serializing the builds.
- Existing interaction tests cover drag preview/commit and location switching. Synchronization tests verify that navigation does not move the observer and that minimap scale reads the latest viewport.

The native probe compares the historical 33 ms polling path with the event-driven path in both orders. Each scenario drives 120 animation-frame callbacks. Scenarios are idle, pan, zoom, combined movement, eight-update bursts, Mapsui animated navigation, resize and loading. The final measurement runs separately from tests.

Raw samples and summaries: [JSON](benchmarks/overlay-sync-native.json), [CSV](benchmarks/overlay-sync-native.csv). PNGs beside these files are diagnostic error traces, not screenshots.

## Final native results

Both orders completed (32 scenario runs, render scaling 1). Values below pool both repeats. Errors are device-independent pixels.

| Scenario | Mean error: polling → events | P95 error: polling → events | P95 oldest pending change to overlay render: polling → events |
| --- | --- | --- | --- |
| Pan | 11.90 → 5.36 | 31.95 → 8.22 | 39.74 → 0.07 ms |
| Zoom | 7.12 → 2.65 | 22.63 → 4.65 | 41.09 → 0.05 ms |
| Combined | 15.62 → 6.90 | 52.62 → 14.93 | 42.81 → 0.06 ms |
| Burst | 15.40 → 6.50 | 50.99 → 14.16 | 43.47 → 0.11 ms |
| Animated navigation | 2.96 → 0.34 | 12.28 → 3.33 | 32.16 → 1.65 ms |

Pan P95 alignment error improved 74%; zoom improved 79%. Immediate UI invalidation P95 was below 0.01 ms, versus approximately 20 ms for polling; worker-driven animated navigation was 0.20 ms versus 21.76 ms. These render timings do not include monitor presentation.

All three native lifecycle cycles passed: no detached invalidations, loading ticks resumed after attachment, and no timer work after returning to idle. Both modes produced zero idle invalidations. Resize and loading had zero measured alignment error. No functional regressions were detected by the tests or this native probe.

The measurable cost is more navigation redraws: 240 event invalidations across two pan runs versus 61 polling invalidations; zoom was 240 versus 58. Process-wide allocations increased from 2.66 to 3.82 MB for pan and 2.74 to 3.78 MB for zoom (including instrumentation). These are total run allocations, not retained memory or isolated overlay allocation measurements. Residual pan/zoom maximum errors were 8.25/4.67 DIP, respectively; the change improves synchronization materially but does not eliminate the separate-render-schedule offset.

## Scope of the evidence

This is native Avalonia/Mapsui rendering of the shared production overlay against an offline reference map. Error is the distance in device-independent pixels between the Mapsui projection and overlay projection at composition. It is not a monitor-presentation or frame-pacing measurement. It deliberately excludes terrain preparation, tile downloads and user settings. Opt-in CPU/GPU terrain benchmarks are not exercised by the ordinary desktop suite.

Event-driven redraw removes the polling delay, but Mapsui and Avalonia still render on distinct schedules. A residual composition offset is expected and measured; perfect map/overlay lockstep is not claimed. More redraws and associated allocations during navigation are the cost of keeping the overlay current. Idle must remain quiet and loading must retain its animation cadence.

## Reproduce

```powershell
dotnet build tools/Noctaxis.PinProbe/Noctaxis.PinProbe.csproj -c Release -p:OutDir=H:\Noctaxis\artifacts\sync-native\
dotnet artifacts/sync-native/Noctaxis.PinProbe.dll --sync H:\Noctaxis\docs\benchmarks\overlay-sync-native.json
./scripts/Summarize-OverlaySynchronization.ps1
```

Run without a concurrent benchmark/test workload. Require all 32 scenario results and three successful lifecycle records, and check for an `.error` file; a process exit alone does not prove probe success. Loading-stage latest-change ages are not navigation latency, because loading invalidates without viewport movement.
