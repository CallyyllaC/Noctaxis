#!/usr/bin/env bash
# Judges a startup check of a Noctaxis package (.github/workflows/platform-package.yml) from its newest log.
#
#   scripts/check-startup-log.sh <log-directory> <alive> [version]
#
#   alive    1 if the app was still running when the check ended, else 0
#   version  X.Y.Z the app must report starting as, when given
#
# Passes when the app was still running, logged its start (as that version) and logged nothing fatal ([CRT]).
# Prints the log either way, so a failure shows why.
set -euo pipefail

logs=${1:?usage: scripts/check-startup-log.sh <log-directory> <alive>}
alive=${2:?usage: scripts/check-startup-log.sh <log-directory> <alive>}
version=${3:-}

log=""
if [ -d "$logs" ]; then
    # The newest log; nothing when there is none (the glob then names no file, and ls reports it).
    log=$(ls -t "$logs"/*.log 2>/dev/null | head -n 1 || true)
fi
if [ -n "$log" ]; then cat "$log"; fi

fail() { echo "::error::$1"; exit 1; }
[ "$alive" = 1 ] || fail "Noctaxis exited during startup"
[ -n "$log" ] || fail "Noctaxis wrote no log in $logs"
grep -q ' starting on ' "$log" || fail "Noctaxis did not log its start"
# The informational version is X.Y.Z, followed by +<commit> when the build knows its commit.
if [ -n "$version" ] && ! grep -Fq "Noctaxis $version+" "$log" && ! grep -Fq "Noctaxis $version starting on " "$log"; then
    fail "Noctaxis did not start as version $version"
fi
if grep -q '\[CRT\]' "$log"; then fail "Noctaxis logged a fatal error"; fi
echo "Noctaxis started and ran with no fatal error."
