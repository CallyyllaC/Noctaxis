# Building and releasing Noctaxis

How release packages are built for Windows, Linux and macOS, what each one has been verified to do,
and what still needs checking by hand on real machines. User-facing install steps ship in each
package's `README.txt` ([Windows](../packaging/windows/README.txt), [Linux](../packaging/linux/README.txt),
[macOS](../packaging/macos/README.txt)).

## Support policy

- **Windows x64 is the only officially supported platform.** It is developed and tested by hand on
  Windows, and gets full developer support. Its test suites must pass before anything is released.
- **Windows Arm64, Linux (x64, Arm64) and macOS (Apple silicon, Intel) are courtesy builds,** offered on
  a best-effort basis:
  - Every release builds, checks and starts them on GitHub-hosted runners of their own operating system
    and processor. These are real hosted machines, not emulators or cross-compilation. The test suites
    also run on Linux x64 and macOS Apple silicon, reported separately; they do not hold up a release.
  - Nobody uses them as a daily desktop, so native desktop behaviour and hardware compatibility are not
    guaranteed.
  - Bugs reported on them may be investigated, but fixes are not promised.
  - They need no personal Linux or macOS hardware, and no paid signing or developer accounts.
  - The unsigned-application warnings below apply.

The [native checklist](#native-verification-checklist) is there for anyone with one of those systems
who would like to help; it is not a release requirement for courtesy builds.

## Platform status

From the first full CI run (16 October 2026, commit `ad60002`) and the fixes since. Each column claims
only what was actually done:
- **Packaged and started:** the release workflow built the package on its own system, checked it and
  launched it; it stayed running with nothing fatal logged. This says nothing about the interface.
- **Automated tests:** the headless Core and Desktop suites on that operating system.
- **Native use:** a person used the app on that platform.

| Package | Support | Runner | Packaged and started | Automated tests | Native use |
|---|---|---|---|---|---|
| Windows x64 (`win-x64`) | **Official** | `windows-latest` | Yes, in CI | Required. Two failures in that run, both fixed; passing locally | Yes: developed and tested by hand |
| Windows Arm64 (`win-arm64`) | Courtesy | `windows-11-arm` | Yes, in CI | Not run | No |
| Linux x64 (`linux-x64`) | Courtesy | `ubuntu-latest` | Yes, in CI (Xvfb) | Best effort. Fixed: Core tests could not load Skia. Known: four text-layout tests (see [platform differences](#known-platform-test-differences)) | No |
| Linux Arm64 (`linux-arm64`) | Courtesy | `ubuntu-24.04-arm` | Yes, in CI (Xvfb) | Not run | No |
| macOS Apple silicon (`osx-arm64`) | Courtesy | `macos-latest` | Failed at signing; fixed, not yet re-run | Best effort. Fixed: pixel byte order, time-zone list, timing. Known: one text-layout test | No |
| macOS Intel (`osx-x64`) | Courtesy | `macos-15-intel` | Failed at signing; fixed, not yet re-run | Not run | No |

Update this table from the next release workflow run.

## Building and testing

.NET 10 SDK on any platform:

```bash
dotnet build Noctaxis.slnx -c Release
dotnet test Noctaxis.Core.Tests -c Release --no-build --filter "Category!=LiveTerrain"
dotnet test Noctaxis.Desktop.Tests -c Release --no-build --filter "Category!=LiveTerrain"
```

The Desktop tests run headless (Avalonia Headless with Skia), so they need no display.

## Packages

Every package is self-contained: it includes the .NET runtime, so users install nothing else. Builds
are multi-file and untrimmed, and Release-only. Noctaxis' own debug symbols are kept so crash logs have
line numbers; the native libraries' symbols (about 100 MB on Windows) are left out
(`NoctaxisOmitNativeSymbolsFromPublish` in `Noctaxis.Desktop.csproj`). Each script checks before
archiving that the package contains these:
- the app and the runtime;
- Skia and HarfBuzz, plus AvaloniaNative on macOS and ANGLE on Windows;
- the licence and notice files, including the .NET runtime's `DotNet-LICENSE.txt` and
  `Microsoft-NOTICES.txt`.

The operative project terms are [Awoo Licence v2.0](../LICENSE) and the initial
public [supporter definition](../AWOO-SUPPORTER-SYSTEM.md). Both package scripts
reject an incorrect licence version, stale copies of those files or the root
third-party notice, and missing/changed files in the retained third-party
collection (including the OpenNGC licence). These checks verify copying; they
are not a substitute for review of new third-party components. Old internal
build outputs must not be used as alpha packages. See the
[final licensing audit](licensing-final-audit.md).

| Platform | Command | Output (`artifacts/packages`) |
|---|---|---|
| Windows | `powershell -File scripts/package.ps1 [-RuntimeIdentifier win-arm64]` | `Noctaxis-<version>-win-x64.zip`: a folder to extract and run |
| Linux (on Linux, or Git Bash on Windows) | `bash scripts/package.sh linux-x64` | `Noctaxis-<version>-linux-x64.tar.gz`: a folder to extract and run, with an optional per-user menu entry (`install-desktop-entry.sh`) |
| macOS (on macOS only) | `bash scripts/package.sh osx-arm64` (or `osx-x64`) | `Noctaxis-<version>-osx-arm64.zip`: `Noctaxis.app`, ad-hoc signed |

macOS packages need macOS because the bundle must be sealed with `codesign`: an unsealed bundle that
a user downloads is reported as damaged. Elsewhere,
`NOCTAXIS_PACKAGE_LAYOUT_ONLY=1 bash scripts/package.sh osx-arm64` lays out and checks the bundle
without writing a package. All five targets cross-publish from Windows with the .NET 10 SDK, and the
SDK ad-hoc signs the macOS executable even there.

The version is `X.Y.Z` from `Directory.Build.props`, with `Z` taken from the `NoctaxisBuildNumber`
environment variable (CI sets the run number; local builds use 0). It names the package and fills the
bundle's `CFBundleShortVersionString`; see [the version and what a release is](#the-version-and-what-a-release-is).

Package metadata lives in `packaging/`:
- `macos/Info.plist`, with bundle identifier `io.github.callyyllac.noctaxis`;
- `linux/io.github.callyyllac.noctaxis.desktop`, whose window class matches the app's
  (`Program.LinuxWindowClass`);
- each platform's `README.txt`;
- the `.icns` and PNG icons, generated with `NoctaxisIcon.WritePackagingAssets` (see
  [branding](branding.md)).

`PackagingTests` keeps all of these consistent with the app.

The executable is `Noctaxis.Desktop` (`.exe` on Windows), the assembly's name. Renaming it would mean
renaming the assembly and every `avares://Noctaxis.Desktop/` resource path, which is not worth it for v1.0.

### Native dependencies

| Library | Windows | Linux | macOS |
|---|---|---|---|
| Skia, HarfBuzz (rendering, text) | Bundled | Bundled | Bundled (universal, publisher-signed) |
| Avalonia platform layer | Win32, ANGLE bundled | X11: uses the system's libX11, libICE, libSM and fontconfig | AvaloniaNative bundled |
| OpenCV (map image filtering) | Bundled (x64) | **Not bundled**: managed fallback | **Not bundled**: managed fallback |
| Browser for links | Shell | `xdg-open` (xdg-utils) | `open` |

OpenCV only speeds up two map image filters. Where its native library is missing, Noctaxis uses its
managed implementation with the same results (`MapImageAcceleration`), so this costs speed only. No
official OpenCvSharp runtime exists for macOS, and the Linux one depends on a specific distribution.

### Where Noctaxis keeps its data

`PlatformUserDataPathProvider`: settings, saved locations, logs and installed datasets (the Lorenz atlas).
- **Windows:** `%APPDATA%\Noctaxis`.
- **Linux:** `$XDG_CONFIG_HOME/Noctaxis` (`~/.config/Noctaxis`).
- **macOS:** `~/Library/Application Support/Noctaxis`.

`CacheStorage`: re-downloadable caches (terrain, land cover, settlement and basemap tiles).
- **Windows:** `%LOCALAPPDATA%\Noctaxis\Cache`.
- **Linux:** `$XDG_CACHE_HOME/Noctaxis` (`~/.cache/Noctaxis`).
- **macOS:** `~/Library/Caches/Noctaxis`.

Caches from earlier versions move there at startup by a same-volume rename only. They are never copied; on another volume they stay where they are. One Noctaxis runs per OS user and holds `.noctaxis-instance.lock` in the data folder. Details: [architecture](architecture.md#persistence-storage-and-single-instance).

## Continuous integration and releases

Four workflows, plus two reusable ones that do the work:

| Workflow | Runs | Does |
|---|---|---|
| `ci.yml` (CI) | Pushes to `main` or `dev`, pull requests, by hand | Tests the version and release scripts; builds and runs both test suites on Windows x64. |
| `platform-tests-best-effort.yml` | The same as CI | Builds and runs both test suites on Linux x64 and macOS Apple silicon. Gates nothing. |
| `release.yml` (Release) | By hand; pushes to `main` that change the version or the packaging; called by the version bump | Builds, checks and starts all six packages, runs the Windows x64 tests, and publishes a GitHub Release when the version calls for it. |
| `version-bump.yml` | A `dev` → `main` pull request is merged | Increments Y on `main` and `dev`, then calls `release.yml` for the new version. |
| `platform-package.yml`, `platform-tests.yml` | Called by the above | One package built, checked and started; one platform's test suites. |

`trigger-awoo-rebuild.yml` rebuilds awoo.ltd on pushes to `main` and when a release is published; it is
unrelated to the build.

Every job first runs `scripts/check-runner-platform.sh`, which fails unless the runner and its .NET SDK
really are the expected operating system and processor, so no result is claimed for a platform the job did
not run on. No workflow uses `continue-on-error`.

### Ordinary pushes and pull requests

CI and the best-effort tests run for every change except documentation-only ones (`docs/`, `images/`,
`ROADMAP.md`, `awoo.project.json`). The licensing documents the tests read still trigger them. A newer push
to a pull request cancels its older runs. Nothing is packaged or published.

Release also runs, without publishing, when a push to `main` touches packaging: `Directory.Build.props`,
`Directory.Packages.props`, the Desktop project, `packaging/`, the package and check scripts, or the
release workflows.

### The version and what a release is

`NoctaxisVersion` in `Directory.Build.props` (X.Y) is the only version kept in the repository. A build's
version is X.Y.Z, where Z is the workflow's run number. Packages are named `Noctaxis-X.Y.Z-<rid>.zip`
(`.tar.gz` for Linux), and a release is tagged `vX.Y.Z` and titled `Noctaxis X.Y.Z`. While X is 0, every
release is marked as an alpha pre-release.

Each X.Y is released once. `scripts/release_plan.py` decides, at the start of each Release run:
- **A push to `main` that changes X.Y**, or the version bump after a `dev` → `main` merge, publishes,
  unless a `vX.Y.*` release already exists (a repeated push or re-run then only verifies).
- **Any other push** only verifies.
- **A run by hand** publishes only when *publish* is ticked, only on `main`, and only for an X.Y not yet
  released.
- It fails with the reason when X.Y would go down, when X.Y is lower than an existing release, or when the
  tag `vX.Y.Z` already exists on another commit.

### How a release is published

The Publish job runs only when the plan says so **and** every package job and the Windows x64 test job have
succeeded. It:
1. collects the six packages, as built and checked, and fails unless they are exactly the six for X.Y.Z;
2. writes `SHA256SUMS.txt`;
3. checks that no other `vX.Y.*` release appeared meanwhile;
4. creates a draft release with every file attached, then publishes it, which creates the tag. A re-run
   of a failed run (it keeps its run number) finishes the same draft; a published release is never changed.

It uses the workflow's own `GITHUB_TOKEN`, with write access in that job only. No other secrets are used.

Workflow artifacts are not releases. Each run keeps its packages (`package-<rid>`) and test results
(`test-results-<rid>`) for 14 days, for diagnosis; downloading them needs a GitHub sign-in.

To release the current version for the first time, or after a failure: run Release by hand on `main`
with *publish* ticked. To release a new version: change `NoctaxisVersion` on `main`, or merge `dev` into
`main`.

### Building by hand

Actions → **Release** → **Run workflow**, choose the branch, and leave *publish* unticked. It builds,
checks and starts all six packages and runs the Windows tests; the packages are on the run's page under
Artifacts. Actions → **CI** (or **Best-effort platform tests**) → **Run workflow** runs only the tests.

Locally: [Building and testing](#building-and-testing) and [Packages](#packages).

### When a platform job fails

1. Open the failed run, then the failed job (`Package (<rid>)` or `Tests (<rid>)`). The first step in red
   is where it failed: an error in *Confirm the runner's operating system and processor* means the hosted
   runner image changed, not the code.
2. For tests, the error is in the test step's log, and `test-results-<rid>` holds the `.trx` files. Find
   the first failure: a crashed test host (`Test Run Aborted`) hides everything after it.
3. For packages, *Check the package* names any missing file or wrong version, and *Start the package*
   prints the app's log. `[CRT]` lines are the fatal errors.
4. Re-run only the failed jobs if the cause was outside the code (a runner or network fault). Otherwise fix
   it and push: a release that did not publish can simply be run again.

A `Test Case Cleanup Failure` with "The calling thread cannot access this object because a different thread
owns it" from `EnsureIsolatedApplication` is a test-isolation defect, not a product fault, and is fixed; it
used to hit a different UI test on most full Desktop runs. Avalonia creates its UI dispatcher on whichever
thread first asks after the headless session resets it between tests. Pool work left by other tests asks
too: Mapsui's map invalidation, `MainViewModel`'s terrain-cache updates, and plain tests that build pens or
brushes. `TestAppBuilder` now claims the dispatcher for the headless thread before each test's platform
setup, under the dispatcher's own lock, so that race can no longer be lost. The claim reaches two
non-public members of Avalonia's `Dispatcher`. If an Avalonia upgrade renames them, every UI test fails at
once with `MissingFieldException` or `MissingMethodException` from `HeadlessUiThread`; update the names
there. Do not re-run a job that shows the old failure: something has reopened the race, so fix it.

### Known platform test differences

On Linux and macOS the following fail because of the system font, not the code under test. The window style
names the `Inter` family, which resolves to each system's own font for that name: Segoe UI on Windows,
which the layout tests were written against, and wider fonts on the hosted Linux and macOS runners. They
point to real text-fitting problems on those systems at large text sizes.
- Linux x64: `ANarrowWindowShrinksTheLocationFirstAndKeepsEverythingElse`,
  `LongerTranslatedTextStillFitsOnEveryScreenAtEveryTextSize`, `TheFourValuesAreOneStatusLine`,
  `HorizonAndObstructionShareOneLine`.
- macOS Apple silicon: `ANarrowWindowShrinksTheLocationFirstAndKeepsEverythingElse`.

## Signing and distribution trust

| | Conclusion |
|---|---|
| Windows Authenticode with publisher identity | **Deferred: not worth the effort for v1.0.** It needs a paid certificate from a CA, or Azure Artifact Signing (a monthly subscription with identity validation, and open to individuals only in some countries). Both need an account and an identity check. |
| Windows self-signed certificate | Not used. It establishes no publisher identity, and is trusted only if users install the certificate, which they should not be asked to do. |
| macOS Developer ID signing and notarisation | **Deferred: not worth the effort for v1.0.** Both need a paid Apple Developer Program membership. |
| macOS ad-hoc signing | **Used**: free, needs no account, and adds no maintenance. Apple silicon runs only signed code: the .NET SDK ad-hoc signs the executable, and the native libraries come signed by their publishers. `package.sh` signs every file in `Contents/MacOS` except the app itself, then the bundle, which signs the app and seals the rest, as Avalonia's macOS deployment guide does. It establishes no publisher identity. |

`codesign` keeps the signatures of files that are not Mach-O code (the .NET assemblies and the licence
texts in `Contents/MacOS`) in extended attributes. `ditto` stores those in the zip and Finder restores
them, but a tool that drops extended attributes would break the bundle's seal, and macOS would then call
the app damaged. So the package is a zip made with `ditto`, opened in Finder. The CI step re-verifies the
seal after extracting with `ditto`; extraction by Safari or Finder after a real download is on the
[native checklist](#native-verification-checklist).

### What users see without trusted signing

- **Windows:**
  - SmartScreen may show "Windows protected your PC" for a downloaded unsigned app; "More info" then
    "Run anyway" opens Noctaxis.
  - SmartScreen's reputation for unsigned files is per file, so each new release can be warned about
    again until it gains reputation of its own.
  - **Smart App Control** is available only on clean installations of Windows 11. It starts in an
    evaluation mode that may switch it on. When it is on, unsigned apps without cloud reputation are
    blocked with no override, so Noctaxis will not start. This is the main cost of not signing.
- **macOS:**
  - Gatekeeper will not open a downloaded app that is not notarised. The user opens Noctaxis once, then
    chooses System Settings → Privacy & Security → **Open Anyway**. That works on macOS 14 and later.
  - From macOS 15, Control-click → Open no longer bypasses Gatekeeper; on macOS 14 it still does.
  - The package's `README.txt` gives the Open Anyway steps.
  - Do not suggest removing the quarantine attribute or turning Gatekeeper off.
- **Linux:** nothing. Packages are verified by downloading from the project's own releases page;
  publishing SHA-256 checksums with each release is cheap and recommended.

## Native verification checklist

For anyone with Linux or a Mac who would like to help. Courtesy builds do not depend on it, but a
report of how far you got is very useful. With a fresh user account or an empty data folder:

1. Extract or install the package as a user would, from a browser download (so quarantine or
   SmartScreen applies), and start it. On macOS, open the zip in Finder, check
   `codesign --verify --deep --strict Noctaxis.app` passes, and follow the Open Anyway steps in its
   README. Do this on Apple silicon and, if possible, an Intel Mac.
2. Check the window: title bar dragging, minimise, maximise and restore, close, resizing, and display
   scaling at 100% and 150–200%, on more than one monitor if possible. On macOS: the custom title bar
   draws its own buttons instead of the native ones, so check they work and decide whether that is
   acceptable. On Linux: X11 and a Wayland session (Noctaxis runs on XWayland).
3. Planner: the map draws and pans and zooms, the Camera frame and PiP render, the theme and
   appearance switch, and text scaling works.
4. Search for a place, place the pin, and check terrain, weather and light pollution download (network).
5. Export: Save PNG through the file dialog, Copy PNG, then Import → Load from clipboard, and paste
   into the file manager. On Linux and macOS the file clipboard has not been exercised; see
   [plan images](plan-images.md#clipboard).
6. Open in Maps and the Ko-fi button: the browser opens.
7. Supporter licence: activate a licence, restart (Settings restart), and confirm it persists.
8. Quit and restart: settings, locations and the session persist in the data folder above.
9. Check the log in `<data folder>/logs` for errors.

## Known limitations

- Courtesy builds are checked automatically only; nobody uses them as a daily desktop (see the table above).
- macOS uses Noctaxis' own title bar buttons, not the native window controls.
- Linux runs on X11 or XWayland only; Avalonia 12 has no native Wayland backend.
- The in-app restart starts the executable directly. Inside `Noctaxis.app` this has not been tried.
- OpenCV acceleration is Windows x64 only.
- There is no installer, auto-update or package-manager listing. Updates are a fresh download.
