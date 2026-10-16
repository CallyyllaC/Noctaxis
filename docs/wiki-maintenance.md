# Maintaining the public wiki

The public source is `docs/user-guide`. Its index becomes Wiki Home, with Documentation grouping Locations, Planner and Settings. Installing remains repository documentation and is excluded from the wiki. Release review notes are in [wiki-release-review.md](wiki-release-review.md).

## Preview locally

From Windows PowerShell 5.1 or PowerShell 7 at the repository root, run:

```powershell
./scripts/preview-wiki.ps1
./scripts/preview-wiki.ps1 -Port 8766
```

The script opens `http://127.0.0.1:8765/Home` in your default browser and serves only on IPv4 loopback. Keep its terminal open. Use **Stop preview** in the page header or press Ctrl+C in the terminal to stop the server and remove its temporary preparation directory. Closing the browser alone does not stop it. After a forced process termination, remove only the `noctaxis-wiki-preview-<run ID>` temporary directory printed at startup if it remains.

Refresh to see Markdown edits without restarting. Every page request calls `prepare-wiki.ps1`, so page names, publication links and the `_Sidebar.md` hierarchy come from the same tooling used to publish. Screenshots load locally from the existing guide assets. Broken source links produce a preparation error; correct the Markdown and refresh. No source pages or images are changed. `-NoBrowser` suppresses automatic opening, and `-Port 0` selects a free port and prints its URL. `-RepositoryUrl` and `-RepositoryRef` have the same purpose as in preparation.

Preview requires Windows PowerShell 5.1 or PowerShell 7 and the repository's .NET SDK. The script builds the small `tools/Noctaxis.WikiMarkdown` renderer with a pinned Markdig package into `artifacts/wiki/markdown-build`; the first restore requires access to NuGet unless the package is cached. Both shells use the same renderer, and page rendering uses local files. Styling approximates GitHub's light/dark Markdown, with heading permalinks and an On this page list. This is not GitHub's exact renderer or full site chrome: syntax highlighting, GitHub-specific extensions and heading slug rules can differ for new Markdown. Scripts, embedded frames and remote image resources are blocked; raw HTML is not processed by GitHub's sanitizer. Repository documents and third-party links keep their publication URLs and may open external sites; rendering the preview itself needs no remote scripts, styles or images. Installing remains excluded, as in the publication mapping.

On 9 October 2026, rendered Edge checks passed for all seven pages, seven byte-identical screenshots and 54 internal page/heading links, including sidebar clicks and a heading anchor. Desktop/narrow layouts and light/dark views were checked, with no remote resource requests. An isolated fixture verified edits on refresh, broken-link reporting and recovery without restarting. Both the browser stop control and Ctrl+C removed their temporary directories; the finished guide files and images remained unchanged.

## Regenerate screenshots

On Windows, open an unlocked desktop with enough room for a 1440 × 1000 logical-pixel window. Install the repository's .NET SDK, then run Windows PowerShell 5.1 or PowerShell 7 from the repository:

```powershell
./scripts/generate-wiki-screenshots.ps1
./scripts/generate-wiki-screenshots.ps1 -GettingStarted
./scripts/generate-wiki-screenshots.ps1 -Only celestial-objects,viability-favourable,viability-compromised
./scripts/generate-wiki-screenshots.ps1 -Only planner-overview,shot-viability
./scripts/generate-wiki-screenshots.ps1 -Only equipment-settings -NoBuild
./scripts/generate-wiki-screenshots.ps1 -ValidateOnly
```

`-NoBuild` reuses the last capture-host build. Rebuild after UI changes. `-TimeoutSeconds` defaults to 120 seconds for each readiness wait; increase it for slow data providers.

`-GettingStarted` captures equipment, celestial objects and both viability comparisons without regenerating the existing Planner overview. To reuse the equipment image too, select only the three new captures as shown above. Choose either `-GettingStarted` or `-Only`.

The screenshot definitions, captions and page placements are in [wiki-screenshots.json](wiki-screenshots.json). Stable PNG names live in `images/wiki`, the repository's media folder, which the awoo.ltd project gallery also reads. Guide pages link them as `../../images/wiki/<id>.png`. The script inserts or updates their captioned Markdown blocks only after the selected capture set succeeds.

Selected runs validate only the selected image placements. Existing blocks that already reference the correct image keep their reviewed alt text, caption, layout and line endings; manifest text supplies new blocks only. Reviewed grouped layouts, such as the Getting Started comparison table, may reference multiple PNGs without separate per-image markers; regeneration keeps those layouts untouched.

Regenerate the map right-click menu with `./scripts/generate-wiki-screenshots.ps1 -Only planner-map-context-menu`. It uses the same Eryri/M31 demonstration and opens the production context menu in a clear area of the main map. Avalonia embeds the real popup inside the capture window, so the PNG is a single native client capture, not a manually composited menu. The host checks that Open in Maps is visible and enabled before capture. Its manifest entry has no automatic page placement: the image is generated without modifying Planner Markdown, which is reviewed separately. It is also included in the normal full screenshot selection.

## Demonstration environment

The small host in `tools/Noctaxis.WikiCapture` opens the production MainWindow with the normal native Avalonia platform, themes, controls, calculation services, map sources and exporter. It uses the existing application service-registration hook through one reflection boundary; it does not change production startup.

Each run has a new directory under `artifacts/wiki/runs`. Settings, saved places, terrain/settlement downloads, atlas state and the basemap cache are isolated there. The production profile is not read or written. The host replaces the default basemap sources before window attachment, when their caches have not yet been used.

The example uses two labelled Welsh demonstration positions, M31 with Sun and Moon, a 36 × 24 mm / 6000 × 4000 camera, a 24–70 mm lens at 35 mm, Noctaxis Dark at 100% text, and 9 October 2026 at 20:00 Europe/London. The camera initially uses a manual composition rather than Follow.

The selected planning instant is repeatable. Downloaded maps and terrain remain real provider data. Non-comparison captures use live weather; weather for the fixed date becomes unavailable outside the provider's forecast range, and the application must show that state. Update the demonstrated date deliberately when refreshing those release screenshots; do not disguise unavailable forecasts.

The Getting Started viability comparisons are explicitly labelled demonstrations, not forecasts. They use M31 at the same Eryri position on 9 October 2026: 23:00 Europe/London with simulated 5% cloud cover, and 19:00 with simulated 95% cloud cover. Both use the 36 × 24 mm / 6000 × 4000 camera at 70 mm with Follow with camera enabled. A capture-only weather wrapper supplies these measurements; the actual application calculates astronomy, terrain, framing and every score. No score or assessment is hardcoded. The favourable scene must rate Excellent without warnings; the compromised scene must rate below Good with a critical cloud warning. Missing terrain, insufficient data coverage or an unsuitable assessment fails the capture before promotion.

The run report records the comparison inputs, calculated scores, warnings and factor explanations. As no atlas is installed, both comparisons visibly retain unavailable light-pollution data and 90% assessment coverage. Regenerate and inspect them when the scoring policy or UI changes, rather than assuming the old score will remain valid.

The overview retains the Eryri/M31 scene, manual camera composition and 1440 × 1000 workspace. Its main map shows only light pollution, using Inferno at 60% opacity; PiP shows only terrain relief at 100%, with a fixed 73.2421875 m/px contextual zoom. The existing top bar, pin, Inspector, Camera frame and Timeline remain visible. Run `./scripts/generate-wiki-screenshots.ps1 -Only planner-overview` to reproduce it.

Only the overview needs the complete real Lorenz 2025 atlas. The host validates and copies an isolated cached installation from `artifacts/wiki/atlas-cache`, or reuses the repository's existing `artifacts/lorenz-real-install` cache. When neither exists, it downloads the atlas into the wiki cache with a ten-minute limit; the first run may take several minutes. It never reads the production profile or copies a user atlas. All 2,016 publisher tiles and the numerical overview must pass the production installation checks. Rendering must produce atlas raster tiles, and the capture checks each map's independent layer state before promotion.

The overview's copy belongs to the fresh run. If more scenes follow, the host removes only that copied generation and restores the original layers, so the Layers image and viability comparisons retain their atlas-free setup. The reusable cache remains intact. Neither overlay controls nor production application behaviour are changed.

## Capture and failure handling

The host waits for planning and Timeline work, active map requests, native layer busy states and a quiet period in vector rendering. It requires loaded map tiles and a terrain preview for the geographical examples. Native captures use Windows PrintWindow; the Layers flyout is composited at its native position, and viability captures its native popover directly so its heading remains visible even when it extends outside the main window. Timeline is cropped from the real window. The plan-sheet image comes from the production PNG exporter and retains its embedded plan.

No headless fixture rendering is promoted as a screenshot. Blank or unreadable captures are rejected. All selected images and their provenance must exist before the script replaces images or changes Markdown. Capture/readiness failures leave existing public images and Markdown intact; diagnostic images are retained only under the ignored run directory. Promotion is atomic per image, but the complete image and Markdown update is not transactional. A failure during promotion can leave a partially updated set; there is no automatic rollback.

Inspect each successful image at full size: labels, target patterns, Camera frame, popovers, attribution and Timeline must be legible, without missing map areas or clipped controls. Automated readiness and blank-image checks do not replace that inspection. PrintWindow can fail or omit GPU content on some desktop/driver configurations; keep existing images and investigate instead of publishing a black capture.

## Prepare a separate wiki checkout

```powershell
./scripts/prepare-wiki.ps1 -ValidateOnly
./scripts/prepare-wiki.ps1
```

Preparation writes into a new local directory under `artifacts/wiki/prepared`. You can supply a new or empty `-Destination`. It produces Home, About, Getting-Started, Locations, Planner, Settings and the navigation sidebar.

The script converts source Markdown links to wiki page names, repository-document links to GitHub URLs, and image links to the public wiki's raw image URLs. It copies PNGs into the separate wiki's `images` directory. `-RepositoryUrl` and `-RepositoryRef` select the repository links when preparing a different origin or revision.

Neither script commits, pushes or publishes. Publication remains a separate task. Commit the approved source and reviewed image assets to the application repository, then copy the prepared files into the separate wiki checkout for a reviewed publication. See [GitHub's wiki editing guidance](https://docs.github.com/en/communities/documenting-your-project-with-wikis/adding-or-editing-wiki-pages).

## After UI changes

Review the affected page against the current controls and behaviour, regenerate the relevant images, inspect them, and run both validation modes. Keep the page names and image IDs stable where their purpose has not changed.

## Validation recorded for this draft

On 9 October 2026 the host built without warnings and generated all seven assets: six native captures and one production-exporter image. Each was visually inspected. Selected regeneration of viability and equipment succeeded; the equipment PNG was byte-identical to the full run, other assets stayed unchanged, and image blocks were not duplicated.

A deliberately short native readiness timeout preserved the hashes of all existing images and public Markdown pages. Source links, heading anchors, insertion markers and prepared wiki page/image targets passed validation. This covers the documentation tooling and demonstrated screens; the separate release-review checks still apply.

Stage 4 retained all seven assets and removed the equipment capture workaround after fixing the startup validation notification. Native equipment capture and the isolated generation pipeline succeeded with a byte-identical PNG; the readiness-failure fixture preserved its images and guide pages. The Core report records 651 passed and one optional skip, and 227 targeted Desktop tests passed. A bounded full Desktop rerun completed without a hang, with 2,788 passed and one pre-existing machine-dependent saved-state test failure confirmed against the pre-fix source. Both documentation validators, prepared wiki mappings and screenshot hashes passed. See [the release review](wiki-release-review.md#stage-4-validation--9-october-2026) for the test limitation and remaining manual release checks.

## PowerShell compatibility check

Run `powershell.exe -NoProfile -File scripts/tests/Test-PowerShell51.ps1` on Windows.
It parses every repository PowerShell source with 5.1, runs both validators and
wiki preparation, checks the live local HTTP preview and its shutdown cleanup,
and exercises screenshot process arguments, Unicode text, image replacement and
failure preservation using an isolated fake host. It does not generate native
screenshots or build release packages.
