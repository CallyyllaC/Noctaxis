#!/usr/bin/env bash
# Verifies, by MSBuild evaluation only (no restore, build or tests), that the working tree resolves
# to the expected local version X.Y.0.
# Used by version-bump-after-merge.sh before pushing a version commit.
# Usage: verify-msbuild-version.sh <expected X.Y.0>
set -euo pipefail

expected=${1:?expected version required}
project=Noctaxis.Core/Noctaxis.Core.csproj
# Evaluate as a local build would: no CI build number.
unset NoctaxisBuildNumber

actual=$(dotnet msbuild "$project" -nologo -getProperty:Version | tr -d '\r')
if [ "$actual" != "$expected" ]; then
  echo "::error::MSBuild resolves version '$actual', expected '$expected'." >&2
  exit 1
fi
echo "MSBuild resolves version $actual."
