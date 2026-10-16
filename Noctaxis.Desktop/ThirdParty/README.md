# Third-party package inventory and licence files

Audit date: 3 October 2026. This inventory comes from the local project files, central package versions, resolved NuGet assets and installed package metadata. It is not a list copied from upstream Noctaxis. The direct references agree with the resolved assets. The targeted follow-up inspected a concrete Release/win-x64 framework-dependent publish; see [Windows redistribution audit](../../docs/windows-redistribution-audit.md). Other RIDs and self-contained releases still need their own reconciliation.

The Desktop dependency graph contains 71 packages, including one build-only package. The application projects have 20 distinct direct package references (23 references counting the three shared dependencies). Package versions below are NuGet versions, not necessarily the version of every embedded native library.

## Included original texts

These files are byte-for-byte copies of licence/notice files already present in the locally resolved packages. Identical files across packages are stored once. Their presence does not establish that every component named in a broad upstream notice collection is included in every platform binary. PngCs supplies only an Apache notice header; the complete Apache-2.0 terms are also present in BruTile-LICENSE.txt. This does not replace project-specific copyright or NOTICE obligations.

| Included file | Package source and original path | SHA-256 |
| --- | --- | --- |
| [ANGLE-LICENSE.txt](ANGLE-LICENSE.txt) | Avalonia.Angle.Windows.Natives/2.1.27548.20260419: LICENSE | `54aff7276217df9f6b5181613999d208c9e40d2b1d51bf55217837e6871a4a63` |
| [BouncyCastle-LICENSE.md](BouncyCastle-LICENSE.md) | BouncyCastle.Cryptography/2.7.0: LICENSE.md | `be0592a6c76f398356fbf252a4fb43476fb3ca3c45331428c2f4f22c499e8311` |
| [BruTile-LICENSE.txt](BruTile-LICENSE.txt) | BruTile/6.0.0: license.txt | `675d2ff71d14c6eb464652fa0294471c142216cd3ce2cdf9ea465ff58cb5c5d4` |
| [CommunityToolkit-LICENSE.md](CommunityToolkit-LICENSE.md) | CommunityToolkit.Mvvm/8.4.0: License.md | `651997ef19dbb9ecb8dd660dbc86444a65af21f18cfe08daa69bf201cd5cfdfc` |
| [CommunityToolkit-NOTICES.txt](CommunityToolkit-NOTICES.txt) | CommunityToolkit.Mvvm/8.4.0: ThirdPartyNotices.txt | `7774f8b0ab66bfb47ec154ce24a2d6d574febc44a74d940b8a037d1c56756163` |
| [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt) | HarfBuzzSharp/8.3.1.3: LICENSE.txt | `89101e35a8c66fd4d6dffc1763259161d35cb564c169714ec227a768c89f2938` |
| [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt) | SkiaSharp.NativeAssets.Win32/3.119.4: THIRD-PARTY-NOTICES.txt | `21504c46c4c58aa64c1055bd2dcbc5f9a136b4b8c412ed3cc6740e22c5b127f5` |
| [PngCs-NOTICE.txt](PngCs-NOTICE.txt) | Hjg.Pngcs/1.1.5: license.txt | `7b002b855ea30dd2fa6be7e7ba8353fae491558f52cc232a2e8080a406aa3200` |
| [Mapsui-LICENSE.txt](Mapsui-LICENSE.txt) | Mapsui/5.1.0: LICENSE | `e073e9546ab156def674a42331051862504d8dc7fd439a5c993a0488b932909f` |
| [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) | Microsoft.Extensions.Configuration/10.0.0: THIRD-PARTY-NOTICES.TXT | `6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21` |

Matplotlib-LICENSE.txt predates this audit and records the colour-table adaptation. The Core OpenNGC-LICENSE.txt is also copied into the release ThirdParty directory by the Desktop project; its repository source remains Noctaxis.Core/Data/OpenNGC-LICENSE.txt.

## Runtime and build package inventory

The follow-up obtained missing upstream texts using package-recorded revisions and inspected the Windows artifact. Upstream source URLs and SHA-256 values are in [upstream-sources.json](upstream-sources.json). Unchanged Windows package binaries are verified against their package assets; supplied notices and the specific additional terms identified in the Windows check are retained. No internal link manifest is required without concrete evidence of an uncovered obligation.

| Package | Version | Classification | Declared licence | Local distributed text / remaining work |
| --- | --- | --- | --- | --- |
| [Avalonia](https://www.nuget.org/packages/Avalonia/12.1.0) | 12.1.0 | Direct linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.Angle.Windows.Natives](https://www.nuget.org/packages/Avalonia.Angle.Windows.Natives/2.1.27548.20260419) | 2.1.27548.20260419 | Transitive native/runtime | BSD-3-Clause | [ANGLE-LICENSE.txt](ANGLE-LICENSE.txt), xxHash/Abseil/ASTC/RapidJSON/zlib notices; unchanged Windows asset and provenance verified |
| [Avalonia.BuildServices](https://www.nuget.org/packages/Avalonia.BuildServices/11.3.2) | 11.3.2 | Build only | MIT | Not shipped in Windows artifact; build tooling only |
| [Avalonia.Controls.ColorPicker](https://www.nuget.org/packages/Avalonia.Controls.ColorPicker/12.1.0) | 12.1.0 | Direct linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.Desktop](https://www.nuget.org/packages/Avalonia.Desktop/12.1.0) | 12.1.0 | Direct linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.Fonts.Inter](https://www.nuget.org/packages/Avalonia.Fonts.Inter/12.1.0) | 12.1.0 | Direct linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt); unchanged Inter fonts: [Inter-OFL.txt](Inter-OFL.txt) |
| [Avalonia.FreeDesktop](https://www.nuget.org/packages/Avalonia.FreeDesktop/12.1.0) | 12.1.0 | Transitive linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.FreeDesktop.AtSpi](https://www.nuget.org/packages/Avalonia.FreeDesktop.AtSpi/12.1.0) | 12.1.0 | Transitive linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.HarfBuzz](https://www.nuget.org/packages/Avalonia.HarfBuzz/12.1.0) | 12.1.0 | Transitive linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.Native](https://www.nuget.org/packages/Avalonia.Native/12.1.0) | 12.1.0 | Transitive linked + macOS native | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.Remote.Protocol](https://www.nuget.org/packages/Avalonia.Remote.Protocol/12.1.0) | 12.1.0 | Transitive linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.Skia](https://www.nuget.org/packages/Avalonia.Skia/12.1.0) | 12.1.0 | Direct linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.Themes.Fluent](https://www.nuget.org/packages/Avalonia.Themes.Fluent/12.1.0) | 12.1.0 | Direct linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.Win32](https://www.nuget.org/packages/Avalonia.Win32/12.1.0) | 12.1.0 | Transitive linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [Avalonia.X11](https://www.nuget.org/packages/Avalonia.X11/12.1.0) | 12.1.0 | Transitive linked | MIT | [Avalonia-LICENSE.txt](Avalonia-LICENSE.txt); [Avalonia-NOTICE.txt](Avalonia-NOTICE.txt) |
| [BitMiracle.LibTiff.NET](https://www.nuget.org/packages/BitMiracle.LibTiff.NET/2.4.660) | 2.4.660 | Direct linked | BSD-3-Clause + IJG/libtiff/ComponentAce notices | [LibTiff.NET-LICENSE.txt](LibTiff.NET-LICENSE.txt) |
| [BouncyCastle.Cryptography](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0) | 2.7.0 | Direct linked | MIT | [BouncyCastle-LICENSE.md](BouncyCastle-LICENSE.md) |
| [BruTile](https://www.nuget.org/packages/BruTile/6.0.0) | 6.0.0 | Transitive linked | Apache-2.0 | [BruTile-LICENSE.txt](BruTile-LICENSE.txt) |
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.0) | 8.4.0 | Direct linked | MIT | [CommunityToolkit-LICENSE.md](CommunityToolkit-LICENSE.md); [CommunityToolkit-NOTICES.txt](CommunityToolkit-NOTICES.txt) |
| [CosineKitty.AstronomyEngine](https://www.nuget.org/packages/CosineKitty.AstronomyEngine/2.1.19) | 2.1.19 | Direct linked | MIT | [AstronomyEngine-LICENSE.txt](AstronomyEngine-LICENSE.txt) |
| [ExCSS](https://www.nuget.org/packages/ExCSS/4.3.1) | 4.3.1 | Transitive linked | MIT | [ExCSS-LICENSE.txt](ExCSS-LICENSE.txt) |
| [HarfBuzzSharp](https://www.nuget.org/packages/HarfBuzzSharp/8.3.1.3) | 8.3.1.3 | Transitive linked | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt) |
| [HarfBuzzSharp.NativeAssets.Linux](https://www.nuget.org/packages/HarfBuzzSharp.NativeAssets.Linux/8.3.1.3) | 8.3.1.3 | Transitive native/runtime | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt); [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt); native asset not selected by win-x64 publish |
| [HarfBuzzSharp.NativeAssets.macOS](https://www.nuget.org/packages/HarfBuzzSharp.NativeAssets.macOS/8.3.1.3) | 8.3.1.3 | Transitive native/runtime | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt); [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt); native asset not selected by win-x64 publish |
| [HarfBuzzSharp.NativeAssets.WebAssembly](https://www.nuget.org/packages/HarfBuzzSharp.NativeAssets.WebAssembly/8.3.1.3) | 8.3.1.3 | Transitive native/runtime | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt); [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt); native asset not selected by win-x64 publish |
| [HarfBuzzSharp.NativeAssets.Win32](https://www.nuget.org/packages/HarfBuzzSharp.NativeAssets.Win32/8.3.1.3) | 8.3.1.3 | Transitive native/runtime | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt); [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt); Windows native path verified |
| [Hjg.Pngcs](https://www.nuget.org/packages/Hjg.Pngcs/1.1.5) | 1.1.5 | Direct linked | Apache-2.0 | [PngCs-NOTICE.txt](PngCs-NOTICE.txt); full Apache terms in BruTile-LICENSE.txt |
| [Mapsui](https://www.nuget.org/packages/Mapsui/5.1.0) | 5.1.0 | Transitive linked | MIT | [Mapsui-LICENSE.txt](Mapsui-LICENSE.txt) |
| [Mapsui.Avalonia12](https://www.nuget.org/packages/Mapsui.Avalonia12/5.1.0) | 5.1.0 | Direct linked | MIT | [Mapsui-LICENSE.txt](Mapsui-LICENSE.txt) |
| [Mapsui.Nts](https://www.nuget.org/packages/Mapsui.Nts/5.1.0) | 5.1.0 | Transitive linked | MIT | [Mapsui-LICENSE.txt](Mapsui-LICENSE.txt) |
| [Mapsui.Rendering.Skia](https://www.nuget.org/packages/Mapsui.Rendering.Skia/5.1.0) | 5.1.0 | Transitive linked | MIT | [Mapsui-LICENSE.txt](Mapsui-LICENSE.txt) |
| [Mapsui.Tiling](https://www.nuget.org/packages/Mapsui.Tiling/5.1.0) | 5.1.0 | Transitive linked | MIT | [Mapsui-LICENSE.txt](Mapsui-LICENSE.txt) |
| [MicroCom.Runtime](https://www.nuget.org/packages/MicroCom.Runtime/0.11.6) | 0.11.6 | Transitive linked | MIT | [MicroCom-LICENSE.txt](MicroCom-LICENSE.txt) |
| [Microsoft.Extensions.Configuration](https://www.nuget.org/packages/Microsoft.Extensions.Configuration/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Configuration.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Configuration.Abstractions/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Configuration.Binder](https://www.nuget.org/packages/Microsoft.Extensions.Configuration.Binder/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Configuration.FileExtensions](https://www.nuget.org/packages/Microsoft.Extensions.Configuration.FileExtensions/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Configuration.Json](https://www.nuget.org/packages/Microsoft.Extensions.Configuration.Json/10.0.0) | 10.0.0 | Direct linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/10.0.0) | 10.0.0 | Direct linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Diagnostics](https://www.nuget.org/packages/Microsoft.Extensions.Diagnostics/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Diagnostics.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Diagnostics.Abstractions/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.FileProviders.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.FileProviders.Abstractions/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.FileProviders.Physical](https://www.nuget.org/packages/Microsoft.Extensions.FileProviders.Physical/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.FileSystemGlobbing](https://www.nuget.org/packages/Microsoft.Extensions.FileSystemGlobbing/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Http](https://www.nuget.org/packages/Microsoft.Extensions.Http/10.0.0) | 10.0.0 | Direct linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Logging](https://www.nuget.org/packages/Microsoft.Extensions.Logging/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Logging.Configuration](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Configuration/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Logging.Console](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Console/10.0.0) | 10.0.0 | Direct linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Options](https://www.nuget.org/packages/Microsoft.Extensions.Options/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Options.ConfigurationExtensions](https://www.nuget.org/packages/Microsoft.Extensions.Options.ConfigurationExtensions/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [Microsoft.Extensions.Primitives](https://www.nuget.org/packages/Microsoft.Extensions.Primitives/10.0.0) | 10.0.0 | Transitive linked | MIT | [DotNet-LICENSE.txt](DotNet-LICENSE.txt); [Microsoft-NOTICES.txt](Microsoft-NOTICES.txt) |
| [NetTopologySuite](https://www.nuget.org/packages/NetTopologySuite/2.6.0) | 2.6.0 | Transitive linked | BSD-3-Clause | [NetTopologySuite-LICENSE.txt](NetTopologySuite-LICENSE.txt) |
| [NetTopologySuite.Features](https://www.nuget.org/packages/NetTopologySuite.Features/2.1.0) | 2.1.0 | Transitive linked | BSD-3-Clause | [NetTopologySuite.Features-LICENSE.txt](NetTopologySuite.Features-LICENSE.txt) |
| [NetTopologySuite.IO.GeoJSON4STJ](https://www.nuget.org/packages/NetTopologySuite.IO.GeoJSON4STJ/4.0.0) | 4.0.0 | Transitive linked | BSD-3-Clause | [NetTopologySuite.GeoJSON-LICENSE.txt](NetTopologySuite.GeoJSON-LICENSE.txt) |
| [NodaTime](https://www.nuget.org/packages/NodaTime/3.3.3) | 3.3.3 | Direct linked | Apache-2.0 | [NodaTime-LICENSE.txt](NodaTime-LICENSE.txt); [NodaTime-NOTICE.txt](NodaTime-NOTICE.txt) |
| [OpenCvSharp4](https://www.nuget.org/packages/OpenCvSharp4/4.13.0.20260627) | 4.13.0.20260627 | Direct linked | Apache-2.0 | [OpenCvSharp-LICENSE.txt](OpenCvSharp-LICENSE.txt) |
| [OpenCvSharp4.runtime.win.slim](https://www.nuget.org/packages/OpenCvSharp4.runtime.win.slim/4.13.0.20260627) | 4.13.0.20260627 | Direct native/runtime | Apache-2.0 | [OpenCV-LICENSE.txt](OpenCV-LICENSE.txt), OpenCV/ component notices and IPP terms; unchanged Windows asset and provenance verified |
| [ShimSkiaSharp](https://www.nuget.org/packages/ShimSkiaSharp/3.2.1) | 3.2.1 | Transitive linked | MIT | [SvgSkia-LICENSE.txt](SvgSkia-LICENSE.txt) |
| [SkiaSharp](https://www.nuget.org/packages/SkiaSharp/3.119.4) | 3.119.4 | Direct linked | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt) |
| [SkiaSharp.HarfBuzz](https://www.nuget.org/packages/SkiaSharp.HarfBuzz/3.119.2) | 3.119.2 | Transitive linked | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt) |
| [SkiaSharp.NativeAssets.Linux](https://www.nuget.org/packages/SkiaSharp.NativeAssets.Linux/3.119.4) | 3.119.4 | Transitive native/runtime | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt); [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt); native asset not selected by win-x64 publish |
| [SkiaSharp.NativeAssets.macOS](https://www.nuget.org/packages/SkiaSharp.NativeAssets.macOS/3.119.4) | 3.119.4 | Transitive native/runtime | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt); [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt); native asset not selected by win-x64 publish |
| [SkiaSharp.NativeAssets.WebAssembly](https://www.nuget.org/packages/SkiaSharp.NativeAssets.WebAssembly/3.119.4) | 3.119.4 | Transitive native/runtime | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt); [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt); native asset not selected by win-x64 publish |
| [SkiaSharp.NativeAssets.Win32](https://www.nuget.org/packages/SkiaSharp.NativeAssets.Win32/3.119.4) | 3.119.4 | Transitive native/runtime | MIT | [SkiaSharp-HarfBuzzSharp-LICENSE.txt](SkiaSharp-HarfBuzzSharp-LICENSE.txt); [SkiaSharp-HarfBuzzSharp-NOTICES.txt](SkiaSharp-HarfBuzzSharp-NOTICES.txt); Windows native path verified |
| [Svg.Custom](https://www.nuget.org/packages/Svg.Custom/3.2.1) | 3.2.1 | Transitive linked | MS-PL | [Svg.Custom-MS-PL.txt](Svg.Custom-MS-PL.txt); [DTD notice](Svg.Custom-DTD-NOTICE.txt); separate assembly |
| [Svg.Model](https://www.nuget.org/packages/Svg.Model/3.2.1) | 3.2.1 | Transitive linked | MIT | [SvgSkia-LICENSE.txt](SvgSkia-LICENSE.txt) |
| [Svg.Skia](https://www.nuget.org/packages/Svg.Skia/3.2.1) | 3.2.1 | Transitive linked | MIT | [SvgSkia-LICENSE.txt](SvgSkia-LICENSE.txt) |
| [Tmds.DBus.Protocol](https://www.nuget.org/packages/Tmds.DBus.Protocol/0.94.1) | 0.94.1 | Transitive linked | MIT | [Tmds.DBus-LICENSE.txt](Tmds.DBus-LICENSE.txt) |
| [Topten.RichTextKit](https://www.nuget.org/packages/Topten.RichTextKit/0.4.167) | 0.4.167 | Transitive linked | Apache-2.0 | [RichTextKit-LICENSE.txt](RichTextKit-LICENSE.txt) |

## Copyright and revision evidence

The following is package metadata, not a replacement for complete upstream notices. Empty copyright metadata does not mean public domain. Repository commits are included where the package records one.

| Package | Copyright metadata | Repository revision |
| --- | --- | --- |
| Avalonia/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.Angle.Windows.Natives/2.1.27548.20260419 | Copyright 2013-2026 © The AvaloniaUI Project | 1c89805903c1482166356d3b950d474973180e61 |
| Avalonia.BuildServices/11.3.2 | Copyright 2023-2025 © The AvaloniaUI Project | 777f975b0a0cecf0311273711d56697212c558c0 |
| Avalonia.Controls.ColorPicker/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.Desktop/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.Fonts.Inter/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.FreeDesktop/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.FreeDesktop.AtSpi/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.HarfBuzz/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.Native/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.Remote.Protocol/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.Skia/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.Themes.Fluent/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.Win32/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| Avalonia.X11/12.1.0 | Copyright 2013-2026 © The AvaloniaUI Project | a21b9f573172f705a944dcc8aad7f036b9986f39 |
| BitMiracle.LibTiff.NET/2.4.660 | Copyright (C) 2008-2024, Bit Miracle | Not recorded |
| BouncyCastle.Cryptography/2.7.0 | Copyright © Legion of the Bouncy Castle Inc. 2000-2026 | 4007498b13582d90ee1eda5d9920c324428b98b3 |
| BruTile/6.0.0 | Copyright © BruTile Developers Team 2008-2025 | 88d8fa03436984c49d664e2ca307f498f05e7062 |
| CommunityToolkit.Mvvm/8.4.0 | (c) .NET Foundation and Contributors. All rights reserved. | 638b41dad30dffabb123a39aa38eabc7e3721371 |
| CosineKitty.AstronomyEngine/2.1.19 | Copyright (c) 2019-2022 Don Cross <cosinekitty@gmail.com> | Not recorded |
| ExCSS/4.3.1 | Not recorded | c97e84d6126bb2e42658cf5af627d52e697c8779 |
| HarfBuzzSharp/8.3.1.3 | © Microsoft Corporation. All rights reserved. | 2888c737ad016d584c74525e2d35db5097ea8576 |
| HarfBuzzSharp.NativeAssets.Linux/8.3.1.3 | © Microsoft Corporation. All rights reserved. | 2888c737ad016d584c74525e2d35db5097ea8576 |
| HarfBuzzSharp.NativeAssets.macOS/8.3.1.3 | © Microsoft Corporation. All rights reserved. | 2888c737ad016d584c74525e2d35db5097ea8576 |
| HarfBuzzSharp.NativeAssets.WebAssembly/8.3.1.3 | © Microsoft Corporation. All rights reserved. | 2888c737ad016d584c74525e2d35db5097ea8576 |
| HarfBuzzSharp.NativeAssets.Win32/8.3.1.3 | © Microsoft Corporation. All rights reserved. | 2888c737ad016d584c74525e2d35db5097ea8576 |
| Hjg.Pngcs/1.1.5 | Copyright © 2022 | b4a13ccff0a3b85623d3ff8738a551a531539501 |
| Mapsui/5.1.0 | Copyright © Mapsui Developers 2018-2026 | f8b4da7d46c52a828d0c6bb133aa377a677ddbe3 |
| Mapsui.Avalonia12/5.1.0 | Copyright © Mapsui Developers 2018-2026 | f8b4da7d46c52a828d0c6bb133aa377a677ddbe3 |
| Mapsui.Nts/5.1.0 | Copyright © Mapsui Developers 2018-2026 | f8b4da7d46c52a828d0c6bb133aa377a677ddbe3 |
| Mapsui.Rendering.Skia/5.1.0 | Copyright © Mapsui Developers 2018-2026 | f8b4da7d46c52a828d0c6bb133aa377a677ddbe3 |
| Mapsui.Tiling/5.1.0 | Copyright © Mapsui Developers 2018-2026 | f8b4da7d46c52a828d0c6bb133aa377a677ddbe3 |
| MicroCom.Runtime/0.11.6 | Copyright 2021 © Nikita Tsukanov | 76785efcafd91b5902fd19dd11145f6dd655b7b4 |
| Microsoft.Extensions.Configuration/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Configuration.Abstractions/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Configuration.Binder/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Configuration.FileExtensions/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Configuration.Json/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.DependencyInjection/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.DependencyInjection.Abstractions/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Diagnostics/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Diagnostics.Abstractions/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.FileProviders.Abstractions/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.FileProviders.Physical/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.FileSystemGlobbing/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Http/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Logging/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Logging.Abstractions/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Logging.Configuration/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Logging.Console/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Options/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Options.ConfigurationExtensions/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| Microsoft.Extensions.Primitives/10.0.0 | © Microsoft Corporation. All rights reserved. | b0f34d51fccc69fd334253924abd8d6853fad7aa |
| NetTopologySuite/2.6.0 | Copyright © 2006 - 2025 NetTopologySuite - Team, Diego Guidi, John Diss (www.newgrove.com), Felix Obermaier (www.ivv-aachen.de), Todd Jackson, Joe Amenta | 97d26b92586448b52d5a4f0c060217f94f882c75 |
| NetTopologySuite.Features/2.1.0 | Copyright © 2020 NetTopologySuite - Team | 0cb0a03dba8028fde185601168eeb652c3699a50 |
| NetTopologySuite.IO.GeoJSON4STJ/4.0.0 | Copyright © 2023 NetTopologySuite - Team | 0721747abf0cca9106684e039bbc846951eecc8e |
| NodaTime/3.3.3 | Not recorded | 381de9834a40f6596ee6218b2c6447ba2860f670 |
| OpenCvSharp4/4.13.0.20260627 | Copyright 2008-2026 | b161e7e012f5101f6d5dc68a835c59db6cc88b18 |
| OpenCvSharp4.runtime.win.slim/4.13.0.20260627 | Copyright 2008-2026 | b161e7e012f5101f6d5dc68a835c59db6cc88b18 |
| ShimSkiaSharp/3.2.1 | Copyright © Wiesław Šoltés 2025 | 0164d01769a8b577f6dcc678f25d4802a06ff8c0 |
| SkiaSharp/3.119.4 | © Microsoft Corporation. All rights reserved. | f568ac94dd768ef9a2f593537cfde2dd0d348ef5 |
| SkiaSharp.HarfBuzz/3.119.2 | © Microsoft Corporation. All rights reserved. | 2888c737ad016d584c74525e2d35db5097ea8576 |
| SkiaSharp.NativeAssets.Linux/3.119.4 | © Microsoft Corporation. All rights reserved. | f568ac94dd768ef9a2f593537cfde2dd0d348ef5 |
| SkiaSharp.NativeAssets.macOS/3.119.4 | © Microsoft Corporation. All rights reserved. | f568ac94dd768ef9a2f593537cfde2dd0d348ef5 |
| SkiaSharp.NativeAssets.WebAssembly/3.119.4 | © Microsoft Corporation. All rights reserved. | f568ac94dd768ef9a2f593537cfde2dd0d348ef5 |
| SkiaSharp.NativeAssets.Win32/3.119.4 | © Microsoft Corporation. All rights reserved. | f568ac94dd768ef9a2f593537cfde2dd0d348ef5 |
| Svg.Custom/3.2.1 | Copyright © Wiesław Šoltés 2025 | 0164d01769a8b577f6dcc678f25d4802a06ff8c0 |
| Svg.Model/3.2.1 | Copyright © Wiesław Šoltés 2025 | 0164d01769a8b577f6dcc678f25d4802a06ff8c0 |
| Svg.Skia/3.2.1 | Copyright © Wiesław Šoltés 2025 | 0164d01769a8b577f6dcc678f25d4802a06ff8c0 |
| Tmds.DBus.Protocol/0.94.1 | Tom Deseyn | b4a7fed0b878f74cb54f7cca84d2889af4e596ba |
| Topten.RichTextKit/0.4.167 | Not recorded | ae434c82d8a197e3d259761879fb3f862f12efd8 |

## Development and test dependencies

These are not Desktop runtime dependencies and should not be shipped as part of the application. If distributing test binaries or tools, include their own applicable notices. Probe/calibration projects reference Core or Desktop; they add no direct packages. CI actions and the SDK are build infrastructure, not application libraries. A self-contained release additionally needs the selected .NET runtime pack licences and notices.

| Package | Version | Licence in local package metadata |
| --- | --- | --- |
| coverlet.collector | 6.0.4 | MIT |
| Microsoft.CodeCoverage | 18.0.0 | MIT |
| Microsoft.NET.Test.Sdk | 18.0.0 | MIT |
| Microsoft.TestPlatform.ObjectModel | 18.0.0 | MIT |
| Microsoft.TestPlatform.TestHost | 18.0.0 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.abstractions | 2.0.3 | Legacy licence URL only; verify before distributing test binaries |
| xunit.analyzers | 1.18.0 | Apache-2.0 |
| xunit.assert | 2.9.3 | Apache-2.0 |
| xunit.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |
| Avalonia.Headless | 12.1.0 | MIT |
| Avalonia.Headless.XUnit | 12.1.0 | MIT |
| Microsoft.ApplicationInsights | 2.23.0 | MIT |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | MIT |
| Microsoft.Testing.Extensions.Telemetry | 1.9.1 | MIT |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 1.9.1 | MIT |
| Microsoft.Testing.Platform | 1.9.1 | MIT |
| Microsoft.Testing.Platform.MSBuild | 1.9.1 | MIT |
| Microsoft.Win32.Registry | 5.0.0 | MIT |
| xunit.analyzers | 1.27.0 | Apache-2.0 |
| xunit.v3 | 3.2.2 | Apache-2.0 |
| xunit.v3.assert | 3.2.2 | Apache-2.0 |
| xunit.v3.common | 3.2.2 | Apache-2.0 |
| xunit.v3.core.mtp-v1 | 3.2.2 | Apache-2.0 |
| xunit.v3.extensibility.core | 3.2.2 | Apache-2.0 |
| xunit.v3.mtp-v1 | 3.2.2 | Apache-2.0 |
| xunit.v3.runner.common | 3.2.2 | Apache-2.0 |
| xunit.v3.runner.inproc.console | 3.2.2 | Apache-2.0 |

Core tests use xUnit 2.9.3; Desktop tests use xunit.v3 3.2.2 and Avalonia.Headless.XUnit 12.1.0. Their analyser versions differ as shown above.
