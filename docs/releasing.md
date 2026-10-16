# Building and releasing Noctaxis

How release packages are built for Windows, Linux and macOS, what each one has been verified to do,
and what still needs checking by hand on real machines. User-facing install steps ship in each
package's `README.txt` ([Windows](../packaging/windows/README.txt), [Linux](../packaging/linux/README.txt),
[macOS](../packaging/macos/README.txt)).

## Support policy

- **Windows x64 is the only officially supported platform.** It is developed and tested by hand on
  Windows, and gets full developer support.
- **Windows Arm64, Linux (x64, Arm64) and macOS (Apple silicon, Intel) are courtesy builds,** offered on
  a best-effort basis:
  - They are built, packaged and started automatically on GitHub-hosted runners of their own operating
    system and processor. These are real hosted machines, not emulators. The full test suite also runs on
    Linux and, informationally, on macOS.
  - Nobody uses them as a daily desktop, so native desktop behaviour and hardware compatibility are not
    guaranteed.
  - Bugs reported on them may be investigated, but fixes are not promised.
  - They need no personal Linux or macOS hardware, and no paid signing or developer accounts.
  - The unsigned-application warnings below apply.
- Every push to `main` or `dev`, and every pull request, builds, checks and starts every package; packages
  are kept only when they are wanted (see [continuous integration](#continuous-integration)).

The [native checklist](#native-verification-checklist) is there for anyone with one of those systems
who would like to help; it is not a release requirement for courtesy builds.

## Platform status

As of the last review. Each column claims only what was actually done:
- **Compiles:** a self-contained `dotnet publish` for that target succeeded.
- **Packaged:** the packaging script wrote and checked the package.
- **Automated tests:** headless test suites run on that operating system.
- **Started:** the packaged app was launched and stayed running with nothing fatal logged; this says
  nothing about the interface.
- **Native use:** a person used the app on that platform.

"CI, not yet run" means the workflow step exists but no run result has been seen. The runner is
where `build-<rid>.yml` and `package.yml` build and start that package.

| Package | Support | Runner | Compiles | Packaged | Automated tests | Started | Native use |
|---|---|---|---|---|---|---|---|
| Windows x64 (`win-x64`) | **Official** | `windows-latest` | Yes | Yes, locally | Yes: full Core and Desktop suites, locally | No: CI, not yet run | Yes: developed and tested by hand on Windows (built from source; the zip itself not yet run) |
| Windows Arm64 (`win-arm64`) | Courtesy | `windows-11-arm` | Yes (cross) | Yes, locally | No | No: CI, not yet run | No |
| Linux x64 (`linux-x64`) | Courtesy | `ubuntu-latest` | Yes (cross) | Yes, locally (Git Bash) | Full suites on Ubuntu in `tests-linux-x64.yml`; no result seen in this review | No: CI under Xvfb, not yet run | No |
| Linux Arm64 (`linux-arm64`) | Courtesy | `ubuntu-24.04-arm` | Yes (cross) | Yes, locally (Git Bash) | No | No: CI under Xvfb, not yet run | No |
| macOS Apple silicon (`osx-arm64`) | Courtesy | `macos-latest` | Yes (cross; SDK ad-hoc signs the executable) | Layout checked locally. Sealing and zipping need macOS: CI, not yet run | macOS in `tests-osx-arm64.yml`, informational; not yet run | No: CI, not yet run | No |
| macOS Intel (`osx-x64`) | Courtesy | `macos-15-intel` | Yes (cross) | As above | No | No: CI, not yet run | No |

Once the Package workflow has run, update the "Started" and "Packaged" columns from its results.

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
bundle's `CFBundleShortVersionString`.

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

## Continuous integration

Each platform has workflows of its own, so each has its own status badge in the README. A badge reports
its workflow's latest run on `main`, and a workflow fails when any of its steps fails.

| Workflow | Runs | Does |
|---|---|---|
| `build-<rid>.yml` (six: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`) | Every push to `main` or `dev` and every pull request, except changes only to `docs/`, `images/` or non-packaged root Markdown; by hand | Builds, checks and starts that package through `platform-package.yml`. Keeps nothing. |
| `tests-<rid>.yml` (three: `win-x64`, `linux-x64`, `osx-arm64`) | Every push to `main` or `dev` and every pull request; by hand | Builds the solution and runs the full Core and Desktop suites through `platform-tests.yml`. |
| `package.yml` | By hand (Actions → Package → Run workflow), or for a version tag (`v*`) | The same as all six build workflows through `platform-package.yml`, then keeps every package as a workflow artifact for 7 days. |
| `ci.yml` | Every push and pull request | Tests the version scheme's own logic. |

- Every job first runs `scripts/check-runner-platform.sh`. It fails unless the runner reports the expected
  operating system and processor, and the .NET SDK runs natively as that runtime identifier, not under
  emulation. No result is ever claimed for a platform the job did not actually run on.
- Every package is built on a runner of its own operating system and processor (see the table above),
  so macOS packages are sealed on macOS.
- Every package is started there: Linux under Xvfb. `scripts/check-startup-log.sh` (Linux, macOS) and the
  Windows step then check that the app was still running after 25 seconds, logged its start and
  logged nothing fatal. This is a startup check only; it does not exercise the interface.
- On macOS the extracted bundle's seal is also verified.
- Tests run only on Windows x64, Linux x64 and macOS Apple silicon. Windows Arm64, Linux Arm64 and macOS
  Intel are built, packaged and started, but their test suites are not run, so they have no test badge.
- The macOS test workflow is informational. Pixel-level tests were written against Windows and Linux Skia
  output, so it may fail until its rendering results have been reviewed once. It fails in its own
  workflow and badge without affecting the others. No branch protection requires any of these checks.
- A newer push to a pull request cancels its older runs. Runs for `main` and `dev` always finish, so
  their badges show a result.
- Nothing is released or published, and no secrets or credentials are used.

## Signing and distribution trust

| | Conclusion |
|---|---|
| Windows Authenticode with publisher identity | **Deferred: not worth the effort for v1.0.** It needs a paid certificate from a CA, or Azure Artifact Signing (a monthly subscription with identity validation, and open to individuals only in some countries). Both need an account and an identity check. |
| Windows self-signed certificate | Not used. It establishes no publisher identity, and is trusted only if users install the certificate, which they should not be asked to do. |
| macOS Developer ID signing and notarisation | **Deferred: not worth the effort for v1.0.** Both need a paid Apple Developer Program membership. |
| macOS ad-hoc signing | **Used**: free, needs no account, and adds no maintenance. Apple silicon runs only signed code: the .NET SDK ad-hoc signs the executable, and the native libraries come signed by their publishers. `package.sh` signs every file in `Contents/MacOS` and then the bundle, as Avalonia's macOS deployment guide does. It establishes no publisher identity. |

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
