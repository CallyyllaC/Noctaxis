# Windows redistribution follow-up

Targeted check, 3 October 2026. This follows the local-tree inventory in
[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md); it does not replace that
inventory or change the Awoo Licence.

## Evidence scope

This records an internal 3 October 2026 binary/notice inspection, not a public
release or a fresh audit of the current source. It remains because its native
component selections and hashes explain the supplied third-party notices.
[Awoo Licence v2.0](../LICENSE) is the sole operative project licence; the
[current licence audit](licensing-final-audit.md) covers the current declaration
and package licence copying without restarting third-party discovery.

## Concrete artifact and evidence

An initial fresh publish of the working tree during this audit succeeded with:

```powershell
dotnet publish Noctaxis.Desktop/Noctaxis.Desktop.csproj -c Release -r win-x64 --self-contained false --artifacts-path artifacts/licensing-win-x64-20261003 -o artifacts/licensing-win-x64-20261003/publish -m:1 -p:PublishSingleFile=false -p:PublishTrimmed=false
```

No separate alpha publish profile exists in this checkout. This representative
artifact uses the current net10.0 default, Windows x64, framework-dependent,
untrimmed, multi-file output. It is not clearance for other RIDs or a bundled
.NET runtime. The initial sandboxed restore failed with NU1301; the authorised
network-enabled restore and publish then succeeded. Application tests were not
run and application source was not changed by this audit.

A final rebuild encountered concurrent, unrelated compilation errors in
InfoButton.cs (`Path` ambiguity) and SettingsViewModels.cs (`field` accessor
keyword). Those files were left untouched. Publishing with `--no-build
--no-restore` then successfully copied the completed notice collection using
the earlier build outputs. The binary/licence findings below apply to that
concrete artifact, not a claim that the latest concurrently edited application
source currently builds.

The [artifact manifest](windows-redistribution-artifact.json) maps **76
third-party DLLs to 64 NuGet packages**, with hashes, original asset paths and
packaged licence files. Every DLL matches its installed package asset byte for
byte; there are no orphan DLLs. Noctaxis.Core.dll and Noctaxis.Desktop.dll are
project assemblies; their embedded third-party material is addressed separately.
The Noctaxis executable is a .NET apphost, covered by the included .NET licence
and notices. No .NET runtime is bundled in this configuration.

Seven entries from the 71-package restore inventory contribute no DLL here:
Avalonia.BuildServices and the Linux, macOS and WebAssembly native-asset
packages for SkiaSharp and HarfBuzzSharp. Cross-platform **managed** Avalonia
assemblies still appear in this untrimmed Windows publish, so their notices
remain relevant. No Linux/macOS native library, FFmpeg DLL, native TBB DLL,
libmicrohttpd binary or test assembly is present.

The published native DLLs are exactly:

| File | Source package | SHA-256 |
| --- | --- | --- |
| av_libglesv2.dll | Avalonia.Angle.Windows.Natives/2.1.27548.20260419 | `53191a77fe783cd757ca7767077c2a64a662e7043777a5b4ab74980d4a0b73e3` |
| libHarfBuzzSharp.dll | HarfBuzzSharp.NativeAssets.Win32/8.3.1.3 | `145ade963eba427027d5e1381db5d51a0ef9e98e9fef9efbfa6074ca20da246f` |
| libSkiaSharp.dll | SkiaSharp.NativeAssets.Win32/3.119.4 | `7dec3ba900ab353491e6446f0083739924c6f8dd668832e2f09d38ebffdbbe1c` |
| OpenCvSharpExtern.dll | OpenCvSharp4.runtime.win.slim/4.13.0.20260627 | `1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd` |

[Package and licence inventory](../Noctaxis.Desktop/ThirdParty/README.md) and
[upstream text provenance](../Noctaxis.Desktop/ThirdParty/upstream-sources.json)
record the source revisions and hashes of added licence files. Whole upstream
texts are preserved unchanged. The Wuffs and SVG DTD notices are verbatim
extractions of their relevant copyright/permission headers.

## Redistribution evidence standard

For unchanged upstream package binaries, retain the applicable upstream
licences/notices and document the package, version, asset path and hash. Dig
further where concrete evidence identifies a component with an uncovered
obligation. A broad historical notice list is not proof that every named
component is shipped, and absence of a complete static link manifest alone is
not a release blocker. This is the standard used for the conclusions below.

## Native alternatives and libmicrohttpd

The historical SkiaSharp-HarfBuzzSharp-NOTICES.txt comes from both Windows
native packages. Its references to GPL/LGPL/MPL are alternatives or notices for
upstream components; they are not cumulative licences on Noctaxis.

The SkiaSharp 3.119.4 package identifies source revision
`f568ac94dd768ef9a2f593537cfde2dd0d348ef5`, which selects mono/skia
`7dbfc07dd33181f84e0958afb7ee805c6c769f0b`. The pinned
[Windows build](https://github.com/mono/SkiaSharp/blob/f568ac94dd768ef9a2f593537cfde2dd0d348ef5/native/windows/build.cake)
requests the SkiaSharp target. The pinned
[Skia target graph](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/BUILD.gn)
links that target to skia and skottie. Its libmicrohttpd dependency belongs to
**skiaserve**, a separate test application. Neither skiaserve nor libmicrohttpd
is selected or shipped. No Noctaxis call/reference or PE import connects to
libmicrohttpd. Therefore its LGPL-2.1/eCos notice does not create a compliance
action for this artifact. The notice does not identify an exact historical
libmicrohttpd release; no version is invented.

The current GIF decoder is **Wuffs**, with Apache-2.0 in its pinned generated
C source, not the old Mozilla tri-licensed GIF code named in the broad notice.
Wuffs' licence and copyright header are now included. The current Chromium
zlib build selects zconf.h, not the old mozzconf.h named in that notice.
FreeType is disabled by Skia's Windows defaults; Windows text uses its platform
font implementation. HarfBuzzSharp's Windows project compiles harfbuzz-subset.cc
and does not link FreeType. The old FreeType GPL alternative therefore does not
create a Windows application copyleft requirement either.

No selected Windows component has been established to require GPL, LGPL or MPL
source/relinking obligations from these historical entries. If a future build
actually adds covered code, MPL source availability can apply even to
**unmodified** covered files distributed in executable form; LGPL can require
source and replacement/relinking rights. These are not obligations to impose
speculatively on this artifact. An unavoidable GPL combined work would conflict
with Awoo's restrictions, but none was identified here.

## OpenCV Windows slim

The managed OpenCvSharp4 and native wrapper are Apache-2.0. The actual native
DLL reports OpenCV **4.13.0**, revision `fe38fc6`, built 27 June 2026. The package
records OpenCvSharp revision `b161e7e012f5101f6d5dc68a835c59db6cc88b18`, with OpenCV
submodule `fe38fc608f6acb8b68953438a62305d8318f4fcd`.

The [pinned Windows workflow](https://github.com/shimat/opencvsharp/blob/b161e7e012f5101f6d5dc68a835c59db6cc88b18/.github/workflows/windows.yml)
builds the slim wrapper with NO_CONTRIB, NO_VIDEOIO, NO_HIGHGUI and NO_DNN, but
links it to the existing broader static OpenCV build. The binary still exports
ML, stitching and video wrappers and contains protobuf/DNN implementation
strings. The current package README's smaller module list is not a sufficient
inventory of statically incorporated code.

The following evidence is specific to the actual DLL, not a generic OpenCV
feature list:

| Component | Evidence and consequence |
| --- | --- |
| FFmpeg | Build-information text says support was configured, but the Windows implementation loads a separate opencv_videoio_ffmpeg plugin. The slim package/publish has no such plugin, no FFmpeg DLL and no FFmpeg PE import. This is not evidence that FFmpeg's codec binaries are distributed; no FFmpeg LGPL action is asserted for this artifact. |
| Intel IPP / IW | Build information identifies IPP 2022.2.0 and its integration wrappers. The exact OpenCV-pinned Windows archive was checksum-verified and its Intel Simplified Software License (October 2022) is included as OpenCV/Intel-IPP-EULA.rtf. It permits redistribution of unmodified binaries with its notice, and restricts modification/reverse engineering of Intel's software. Those component-specific terms do not relicense Noctaxis code. |
| Intel ITT | Build information identifies ITT 3.25.4. Pinned OpenCV source declares GPL-2.0-only **OR BSD-3-Clause**. Redistribution here uses the BSD alternative; its original text is included. GPL is not cumulative. |
| TBB | The actual build uses Microsoft's Concurrency framework. No TBB binary or selected TBB backend was found. |
| zlib / JPEG / PNG / WebP / TIFF | Build information and implementation strings identify zlib 1.3.1, libjpeg-turbo 3.1.3 (JPEG ABI 70), libpng 1.6.55, WebP 1.6.0 and libtiff 4.7.1. These agree with the workflow's pinned vcpkg baseline. Applicable upstream licence/patent/acknowledgement material is included. |
| OpenEXR / OpenJPEG / protobuf | Build information identifies 2.3.0 / 2.5.3 / 3.19.1, and codec/protobuf implementation strings occur in the binary. Their pinned source notices are included. |
| Flatbuffers / SoftFloat | OpenCV's source/build information identifies the bundled Flatbuffers and core SoftFloat material; corresponding upstream terms are included. |
| Tesseract / other optional inputs | Build scripts can offer libraries to the linker without proving that their object code is retained. No Tesseract binary/implementation string was found. The full static link closure, including retained contrib/optional material, is not proven by the package's build-information string alone. |

There is no separate native libtiff DLL, but libtiff code **is statically
incorporated in OpenCvSharpExtern.dll**. This is independent of the managed
BitMiracle library. No GPL-only native payload was identified. The published binary is unchanged
from its upstream package. Its provenance, upstream licence and the additional
component terms concretely identified above are recorded and shipped. There is
no concrete evidence of a further uncovered licence obligation; a complete
producer link manifest is not required merely because static code is present.

## ANGLE, Skia and graphics notices

Avalonia supplies av_libglesv2.dll from its ANGLE package; SkiaSharp supplies
libSkiaSharp.dll; HarfBuzzSharp supplies libHarfBuzzSharp.dll. All three published
files match the Windows x64 package assets. PE imports name Windows system
libraries, not additional shipped graphics DLLs.

Avalonia's pinned [ANGLE workflow](https://github.com/AvaloniaUI/angle/blob/1c89805903c1482166356d3b950d474973180e61/.github/workflows/build.yml)
selects the Windows D3D build and disables Vulkan, GL, Metal, WebGPU,
SwiftShader and D3D9. ANGLE's BSD licence and relevant xxHash, zlib,
Abseil, RapidJSON and ASTC dependency notices are included; Linux/macOS backend
licences were not added as Windows obligations. Avalonia's own MIT licence and
upstream NOTICE (including incorporated WPF, Silverlight and other material)
are also supplied.

The original Skia/HarfBuzz notice collection is retained, supplemented with
pinned Skia, Wuffs, D3D12 allocator and Chromium zlib texts. This resolves the
specific obsolete copyleft concerns above. The published binaries are unchanged upstream package assets. Their supplied
notices and provenance are retained, with the specific additional terms found
in this check. No concrete evidence identifies a further uncovered obligation.
Absence of a complete build-generated manifest is not itself a release blocker
or a reason to ship GPL/LGPL source for libmicrohttpd.

## Apache NOTICE, MS-PL, fonts and LibTiff.NET

| Item | Result |
| --- | --- |
| NodaTime 3.3.3 | Pinned source contains NOTICE.txt crediting the Joda.org/Joda Time port. Both full Apache terms and this section 4(d) notice are now packaged. |
| RichTextKit 0.4.167 | Pinned source has license.txt; no project-specific NOTICE file appears in its complete source tree. Full licence retained; no invented NOTICE. |
| BruTile 6.0.0 | Pinned source and package contain its full licence/copyright text, already packaged; no project-specific NOTICE file found. |
| Hjg.Pngcs 1.1.5 | Pinned source supplies the same copyright/Apache header as the package. No separate NOTICE file found. The full Apache terms are available in the collection alongside that header. |
| OpenCvSharp 4.13.0.20260627 | Pinned wrapper source has LICENSE but no project-specific NOTICE. Native OpenCV COPYRIGHT and separately licensed component notices are additional material, not a fabricated Apache NOTICE. |
| Native Apache components | Retain their actual supplied notices and copyright headers; the specific additional component terms found in this pass are included. No NOTICE is invented for an upstream project that supplies none. |
| Svg.Custom 3.2.1 | Source project explicitly declares MS-PL and compiles the upstream SVG sources into Svg.Custom.dll. The artifact ships that separate, unchanged assembly; it is not merged into a Noctaxis assembly. MS-PL text, existing package copyright and the embedded SVG DTD permission notice are retained. Upstream SVG AssemblyInfo also credits Microsoft 2006; package metadata credits Wiesław Šoltés 2025. MS-PL binary compliance does not force independent Noctaxis code under MS-PL. Covered source redistributed later must retain MS-PL and its full text. |
| Inter 3.019, git-0a5106e0b | Six embedded fonts match the pinned Avalonia source font files byte for byte. Noctaxis embeds the unchanged Avalonia assembly and does not rename, subset or modify these fonts. Font metadata says Copyright 2020 The Inter Project Authors; the matching OFL file says 2016–2020 and preserves the Inter trademark statement. No Reserved Font Name is expressly declared in that header. The full OFL now accompanies distribution; it applies to the font, not application code or rendered documents. |
| BitMiracle.LibTiff.NET 2.4.660 | Managed port/derivative of libtiff; the package has no native DLL. The complete official combined notice now ships: Bit Miracle BSD terms, IJG, Sam Leffler/SGI libtiff terms and ComponentAce. Preserve the package's 2008–2024 copyright metadata as well as the upstream file's 2008–2023 notice. The package gives no source commit; the fetched official licence blob is recorded without claiming it identifies the exact build revision. |

## Data and service boundary

OpenNGC remains separately licensed CC BY-SA 4.0 catalogue material. Its
share-alike provisions concern applicable catalogue/adapted database material,
not automatic relicensing of unrelated application code. OSM-derived data
retains ODbL attribution/database rights. CallyyllaC/Awoo does not claim those
rights. The earlier CSV comparison was not repeated.

Open-Meteo's [free-service terms](https://open-meteo.com/en/terms) permit only
non-commercial service use, impose request limits (600/minute, 5,000/hour,
10,000/day; the terms also list a monthly limit), and require data attribution.
Noctaxis contacts the public endpoints directly and caches forecasts; there is
no app-specific commercial-provider arrangement in the repository. A public
alpha is not automatically prohibited merely because its source licence is
custom. However, supporter-linked distribution and paid professional use must
be assessed under the provider's definition, which the Awoo exception cannot
override. Confirm service eligibility/limits with the provider for those uses.
This is a provider/service question, not software-licence incompatibility;
architecture and service behaviour were not changed.

The separately raised photography-tool/provider question is non-blocking for
this pass. No distinct named component was identified for it in the completed
inventory; no new dependency or permission has been invented. Formal permission
is not explicitly documented, and the maintainer may contact its author. This
does not change the independently recorded **Lorenz Atlas** status, which no
binary check could settle. That status was later resolved by permission: David
Lorenz granted explicit permission on 8 October 2026 for the Atlas's use in
Noctaxis as a downloaded, locally cached layer
([permission record](../THIRD_PARTY_NOTICES.md#david-lorenz-light-pollution-atlas-permission)).
No atlas dataset is included in this Windows artifact.

## Final classification

| Flagged item | Status | Exact reason |
| --- | --- | --- |
| Historical native MPL/GPL/LGPL alternatives | Resolved — documentation updated | Current Windows GIF/font/zlib paths differ from the historical notices; no applicable source obligation established from those entries. ITT's BSD option is explicit. |
| libmicrohttpd | Resolved — no additional action | Dependency of unshipped skiaserve test target, not the published SkiaSharp library. |
| OpenCV native runtime | Resolved — packaging updated | Unchanged upstream DLL verified; wrapper/OpenCV licences, identified codec notices, ITT BSD alternative and Intel IPP terms now ship. No concrete uncovered obligation remains. |
| ANGLE / Skia native material | Resolved — packaging updated | Unchanged upstream binaries and provenance verified; supplied notices and specifically identified additions ship. Historical unselected copyleft entries do not impose application source obligations. |
| Apache-2.0 NOTICE | Resolved — packaging updated | NodaTime's actual NOTICE added; named managed Apache dependencies without a NOTICE are documented accurately. Specific native additions are recorded in the native rows. |
| Svg.Custom / MS-PL | Resolved — packaging updated | Separate unchanged DLL, complete MS-PL and applicable attribution supplied; no Noctaxis relicensing. |
| Inter / OFL | Resolved — packaging updated | Six unchanged font resources verified; matching copyright/OFL terms supplied. |
| LibTiff.NET / native libtiff | Resolved — packaging updated | Complete managed combined notice and separate OpenCV libtiff notice supplied. Native TIFF is static, not a missing standalone DLL. |
| Previously missing managed licence texts | Resolved — packaging updated | All 76 DLLs have package licence/notice mappings and the missing project texts are now included. Unchanged native assets are covered by their supplied and specifically supplemented notices. |
| Windows publish selection and file copies | Resolved — packaging updated | Concrete artifact exists, no orphan DLLs, licence collection is selected and copied; final hashes verified. |
| OpenNGC / ODbL boundary | Resolved — no additional action | Existing separate-data/share-alike boundary remains clear; no repeat catalogue audit needed. |
| Open-Meteo | Non-blocking external/provider issue | Service eligibility, rate limits and commercial-use definition remain independent; not an Awoo licence conflict. Resolve provider arrangements for uses outside the free service. |
| Photography-tool/provider question | Non-blocking external/provider issue | Formal permission remains undocumented; no inferred grant and no block on licence finalisation. |
| Lorenz Atlas | Resolved — explicit permission granted | David Lorenz granted explicit permission on 8 October 2026 for use in Noctaxis as a downloaded, locally cached, credited layer. It remains his third-party data, not Awoo-licensed. No atlas is bundled in the artifact. |

### Awoo Licence compatibility

The internal inspection identified no component requiring relicensing of
Noctaxis-owned code. No unavoidable GPL-covered combination was identified. Separate MS-PL,
OFL, dataset and Intel binary terms remain separate, including rights or
restrictions different from the Awoo Licence.

### Source repository readiness

These third-party findings apply to the inspected source and internal artifact.
Current project-licence readiness is recorded in the final licence audit.
Provider/data permissions remain separate from freezing the software licence.

### Windows binary redistribution readiness

**Ready as regards the third-party binary licence and notice obligations
identified for this concrete artifact.** No remaining binary-licensing blocker
was found after the additions. All third-party DLLs are unchanged upstream
assets, their provenance is documented and the supplied/applicable notices
ship alongside them. A complete internal native link manifest is not required
without concrete evidence of an uncovered obligation.

This conclusion does not grant runtime provider/data rights. Lorenz Atlas use
rests on David Lorenz's explicit permission of 8 October 2026, not on this
audit, and no atlas data is bundled. Open-Meteo service eligibility remains
independent. The final full rebuild is blocked by the unrelated compilation errors noted
above; a fresh release built from the latest source must resolve those errors.
Recheck licence-file copying and package provenance if the release
configuration, binaries or dependencies change.

## Final validation

- All 20 distinct direct runtime package references (23 project references) are
  accounted for in the existing inventory.
- All 76 third-party DLLs match their source assets from 64 packages byte for
  byte; no orphan DLLs were found.
- MSBuild's successful no-build publish copied all 62 licence, notice,
  inventory and provenance files identically to their repository sources.
- All 45 added upstream text/header files match their recorded SHA-256 values.
- All 164 local Markdown links checked in the changed documentation and added
  upstream Markdown licence resolve, including document anchors.
- `git diff --check` passed. The root LICENSE, commercial document, supporter
  definition and Desktop project file match the start-of-pass hashes.
- No application tests were run; no application source was edited by this
  audit, and nothing was committed or pushed.
