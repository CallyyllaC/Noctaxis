#!/usr/bin/env bash
# Checks a finished Noctaxis package as a user receives it (.github/workflows/platform-package.yml): its name,
# its layout once extracted, and that it is the built application with its runtime, notices and README, not a
# source archive. scripts/package.sh and package.ps1 check the folder before archiving; this checks the archive.
#
#   scripts/check-package.sh <rid> <archive> <version> <extracted-directory>
#
# The archive must already be extracted into <extracted-directory> with tools that keep file modes
# (tar, ditto, or Expand-Archive on Windows).
set -euo pipefail

rid=${1:?usage: scripts/check-package.sh <rid> <archive> <version> <extracted-directory>}
archive=${2:?}
version=${3:?}
root=${4:?}
name="Noctaxis-$version-$rid"
failed=0
error() { echo "::error::$name: $1"; failed=1; }

case "$rid" in
    win-*) expected="$name.zip"; app="$root/$name"; exe=Noctaxis.Desktop.exe; native=libSkiaSharp.dll ;;
    linux-*) expected="$name.tar.gz"; app="$root/$name"; exe=Noctaxis.Desktop; native=libSkiaSharp.so ;;
    osx-*) expected="$name.zip"; app="$root/Noctaxis.app/Contents/MacOS"; exe=Noctaxis.Desktop; native=libSkiaSharp.dylib ;;
    *) echo "::error::Unknown runtime identifier '$rid'"; exit 2 ;;
esac

# The name says what is inside: product, version, operating system and processor.
[ "$(basename "$archive")" = "$expected" ] || error "archive is named $(basename "$archive"), expected $expected"

# One top-level entry, as the platform's README describes: the folder, or Noctaxis.app beside README.txt.
top=$(cd "$root" && ls -A | sort | tr '\n' ' ')
case "$rid" in
    osx-*) [ "$top" = "Noctaxis.app README.txt " ] || error "top level is '$top', expected Noctaxis.app and README.txt" ;;
    *) [ "$top" = "$name " ] || error "top level is '$top', expected the folder $name" ;;
esac

for file in "$exe" Noctaxis.Desktop.dll Noctaxis.Core.dll "$native" LICENSE THIRD_PARTY_NOTICES.md AWOO-SUPPORTER-SYSTEM.md \
            ThirdParty/DotNet-LICENSE.txt ThirdParty/Microsoft-NOTICES.txt ThirdParty/OpenNGC-LICENSE.txt; do
    [ -f "$app/$file" ] || error "missing $file"
done
# Self-contained: the .NET runtime is inside, so users install nothing else.
ls "$app" | grep -Eqi '^(lib)?coreclr\.(dll|so|dylib)$' || error "missing the .NET runtime (coreclr)"
case "$rid" in
    osx-*) [ -f "$root/README.txt" ] || error "missing README.txt" ;;
    *) [ -f "$app/README.txt" ] || error "missing README.txt" ;;
esac

# The application, not its source.
if [ -n "$(find "$root" \( -name '*.cs' -o -name '*.csproj' -o -name '*.axaml' -o -name '*.slnx' \) -print -quit)" ]; then
    error "contains source files"
fi

case "$rid" in
    linux-*)
        [ -x "$app/Noctaxis.Desktop" ] || error "Noctaxis.Desktop is not executable"
        [ -x "$app/install-desktop-entry.sh" ] || error "install-desktop-entry.sh is not executable" ;;
    osx-*)
        [ -x "$app/Noctaxis.Desktop" ] || error "Noctaxis.Desktop is not executable"
        plist="$root/Noctaxis.app/Contents/Info.plist"
        grep -A1 CFBundleShortVersionString "$plist" | grep -q "<string>$version</string>" \
            || error "Info.plist does not carry version $version" ;;
esac

[ "$failed" = 0 ] || exit 1
echo "$expected: layout, contents and version $version checked."
