#!/usr/bin/env bash
# Fails unless this GitHub-hosted runner really is the operating system and processor a job expects
# (.github/workflows/platform-package.yml, platform-tests.yml). A job then never reports a native result
# from a relabelled runner or from an emulated .NET SDK.
#
#   scripts/check-runner-platform.sh <rid>
#
#   rid  win-x64 | win-arm64 | linux-x64 | linux-arm64 | osx-x64 | osx-arm64
#
# Checks the runner's own report (RUNNER_OS, RUNNER_ARCH) and the runtime identifier of the .NET SDK
# that will build: an x64 SDK running under emulation on an Arm64 machine reports an x64 identifier.
set -euo pipefail

rid=${1:?usage: scripts/check-runner-platform.sh <rid>}
case "$rid" in
    win-*) os=Windows ;;
    linux-*) os=Linux ;;
    osx-*) os=macOS ;;
    *) echo "::error::Unknown runtime identifier '$rid'"; exit 2 ;;
esac
case "$rid" in
    *-x64) arch=X64 ;;
    *-arm64) arch=ARM64 ;;
    *) echo "::error::Unknown processor in '$rid'"; exit 2 ;;
esac

sdk=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -n 1 | tr -d '\r')
echo "Expected: $os $arch ($rid)"
echo "Runner:   ${RUNNER_OS:-unknown} ${RUNNER_ARCH:-unknown}, .NET SDK runtime identifier ${sdk:-unknown}"

fail() { echo "::error::$1"; exit 1; }
[ "${RUNNER_OS:-}" = "$os" ] || fail "This runner is ${RUNNER_OS:-unknown}, not $os."
[ "${RUNNER_ARCH:-}" = "$arch" ] || fail "This runner's processor is ${RUNNER_ARCH:-unknown}, not $arch."
[ "$sdk" = "$rid" ] || fail "The .NET SDK runs as '${sdk:-unknown}', not natively as '$rid'."
echo "Running natively on $rid."
