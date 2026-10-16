#!/usr/bin/env bash
# Builds a Noctaxis release package for Linux or macOS. See docs/releasing.md.
#
#   scripts/package.sh <rid> [output-dir]
#
#   rid         linux-x64 | linux-arm64 | osx-arm64 | osx-x64
#   output-dir  where the package is written (default: artifacts/packages)
#
# The app is published self-contained (no .NET install needed), multi-file and untrimmed, then
# checked and archived:
#   linux-*  Noctaxis-<version>-<rid>.tar.gz   a folder to extract and run, with an optional menu entry
#   osx-*    Noctaxis-<version>-<rid>.zip      Noctaxis.app, ad-hoc signed when built on macOS
# Nothing is uploaded or published. NoctaxisBuildNumber (environment) sets Z of the X.Y.Z version.
set -euo pipefail

rid=${1:?usage: scripts/package.sh <linux-x64|linux-arm64|osx-arm64|osx-x64> [output-dir]}
repo=$(cd "$(dirname "$0")/.." && pwd)
out=${2:-"$repo/artifacts/packages"}
project="$repo/Noctaxis.Desktop/Noctaxis.Desktop.csproj"
case "$rid" in
    linux-x64|linux-arm64) kind=linux; libext=so ;;
    osx-arm64|osx-x64) kind=macos; libext=dylib ;;
    *) echo "error: unsupported runtime identifier '$rid' (Windows packages: scripts/package.ps1)" >&2; exit 2 ;;
esac
# A macOS package must be sealed with codesign, which only macOS has: an unsealed bundle downloaded by a
# user is reported as damaged. Elsewhere NOCTAXIS_PACKAGE_LAYOUT_ONLY=1 lays the bundle out and checks it,
# without writing a package.
layout_only=${NOCTAXIS_PACKAGE_LAYOUT_ONLY:-0}
if [ "$kind" = macos ] && [ "$(uname -s)" != Darwin ] && [ "$layout_only" != 1 ]; then
    echo "error: build macOS packages on macOS (the Package workflow does). NOCTAXIS_PACKAGE_LAYOUT_ONLY=1 checks the layout only." >&2
    exit 2
fi
# Windows (Git Bash) cannot record Unix file modes; the archive sets them instead.
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) unix_modes=0 ;; *) unix_modes=1 ;; esac

version=$(dotnet msbuild "$project" -getProperty:Version | tr -d '\r')
name="Noctaxis-$version-$rid"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
publish="$work/publish"

echo "Publishing Noctaxis $version for $rid"
dotnet publish "$project" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    --artifacts-path "$work/build" -o "$publish"

# The package must run on a machine with nothing else installed: the app, its runtime and its
# native libraries, and the licence texts that go with them.
check() {
    local dir=$1 missing=0
    for file in Noctaxis.Desktop Noctaxis.Desktop.dll Noctaxis.Core.dll "libSkiaSharp.$libext" "libHarfBuzzSharp.$libext" \
                "libcoreclr.$libext" "libhostfxr.$libext" LICENSE THIRD_PARTY_NOTICES.md AWOO-SUPPORTER-SYSTEM.md \
                ThirdParty/Microsoft-NOTICES.txt ThirdParty/DotNet-LICENSE.txt; do
        [ -e "$dir/$file" ] || { echo "error: $name is missing $file" >&2; missing=1; }
    done
    if [ "$kind" = macos ] && [ ! -e "$dir/libAvaloniaNative.dylib" ]; then
        echo "error: $name is missing libAvaloniaNative.dylib" >&2; missing=1
    fi
    if [ "$unix_modes" = 1 ] && [ ! -x "$dir/Noctaxis.Desktop" ]; then
        echo "error: Noctaxis.Desktop is not executable" >&2; missing=1
    fi
    if ls "$dir"/lib*.pdb >/dev/null 2>&1; then echo "error: native debug symbols were published" >&2; missing=1; fi
    # Reject stale project terms and preserve every retained third-party notice byte for byte.
    if [ "$(head -n 1 "$dir/LICENSE" 2>/dev/null | tr -d '\r')" != '# Awoo Licence v2.0' ]; then
        echo 'error: package does not contain Awoo Licence v2.0' >&2; missing=1
    fi
    local relative notice
    for relative in LICENSE AWOO-SUPPORTER-SYSTEM.md THIRD_PARTY_NOTICES.md; do
        cmp -s "$repo/$relative" "$dir/$relative" || { echo "error: stale package licensing file: $relative" >&2; missing=1; }
    done
    while IFS= read -r -d '' notice; do
        relative=${notice#"$repo/Noctaxis.Desktop/ThirdParty/"}
        cmp -s "$notice" "$dir/ThirdParty/$relative" || { echo "error: missing or changed third-party notice: $relative" >&2; missing=1; }
    done < <(find "$repo/Noctaxis.Desktop/ThirdParty" -type f -print0)
    cmp -s "$repo/Noctaxis.Core/Data/OpenNGC-LICENSE.txt" "$dir/ThirdParty/OpenNGC-LICENSE.txt" || {
        echo 'error: missing or changed OpenNGC licence' >&2; missing=1
    }
    return $missing
}

mkdir -p "$out"
if [ "$kind" = linux ]; then
    root="$work/$name"
    mv "$publish" "$root"
    chmod +x "$root/Noctaxis.Desktop"
    cp "$repo/packaging/linux/io.github.callyyllac.noctaxis.desktop" "$repo/packaging/linux/noctaxis.png" "$root/"
    install -m 755 "$repo/packaging/linux/install-desktop-entry.sh" "$root/install-desktop-entry.sh"
    cp "$repo/packaging/linux/README.txt" "$root/README.txt"
    check "$root"
    # The two programs are archived as executable whatever the build machine recorded (GNU tar).
    tar -C "$work" -cf "$work/$name.tar" --exclude="$name/Noctaxis.Desktop" --exclude="$name/install-desktop-entry.sh" "$name"
    tar -C "$work" -rf "$work/$name.tar" --mode=0755 "$name/Noctaxis.Desktop" "$name/install-desktop-entry.sh"
    gzip -9 -n -c "$work/$name.tar" > "$out/$name.tar.gz"
    echo "Wrote $out/$name.tar.gz"
else
    app="$work/$name/Noctaxis.app"
    mkdir -p "$app/Contents/Resources"
    mv "$publish" "$app/Contents/MacOS"
    chmod +x "$app/Contents/MacOS/Noctaxis.Desktop"
    cp "$repo/packaging/macos/Noctaxis.icns" "$app/Contents/Resources/"
    sed "s/@VERSION@/$version/g" "$repo/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
    cp "$repo/packaging/macos/README.txt" "$work/$name/README.txt"
    check "$app/Contents/MacOS"
    if [ "$(uname -s)" != Darwin ]; then
        echo "Laid out and checked $name/Noctaxis.app (layout only: not sealed, no package written)"
        exit 0
    fi
    # Ad-hoc: seals the bundle so macOS can verify it is intact. It identifies no publisher, so
    # Gatekeeper still asks the user to confirm the first launch (docs/releasing.md).
    # As Avalonia's macOS deployment guide does: every file in Contents/MacOS (the app, the runtime, the
    # managed assemblies and their data) first, then the bundle, which seals them and Info.plist.
    find "$app/Contents/MacOS" -type f -print0 | xargs -0 -n 1 codesign --force --sign - --timestamp=none
    codesign --force --sign - --timestamp=none "$app"
    codesign --verify --deep --strict "$app"
    echo "Ad-hoc signed and verified $app"
    (cd "$work/$name" && ditto -c -k --sequesterRsrc --keepParent Noctaxis.app "$work/app.zip")
    # The README beside the app, in the same archive.
    (cd "$work/$name" && zip -q "$work/app.zip" README.txt)
    mv "$work/app.zip" "$out/$name.zip"
    echo "Wrote $out/$name.zip"
fi
