#!/usr/bin/env bash
# Increments Y of X.Y once on main after dev was merged into main, then applies the same X.Y to dev.
# Run by .github/workflows/version-bump.yml from a copy outside the working tree, because it
# switches branches. Never force-pushes; never tags or releases.
#
# Required environment:
#   PR_NUMBER      number of the merged dev -> main pull request (idempotency key)
#   MERGE_SHA      merge commit reported by GitHub for that pull request
#   VERSION_TOOL   path to a copy of scripts/noctaxis_version.py
#   VERIFY_VERSION command run in the working tree as `$VERIFY_VERSION <X.Y.0>` after each version
#                  commit and before it is pushed (scripts/verify-msbuild-version.sh in CI); a
#                  non-zero exit aborts before anything is pushed
# Optional: REMOTE (default origin), MAIN_BRANCH (main), DEV_BRANCH (dev), MAX_ATTEMPTS (5)
set -euo pipefail

: "${PR_NUMBER:?}" "${MERGE_SHA:?}" "${VERSION_TOOL:?}" "${VERIFY_VERSION:?}"
REMOTE=${REMOTE:-origin}
MAIN_BRANCH=${MAIN_BRANCH:-main}
DEV_BRANCH=${DEV_BRANCH:-dev}
MAX_ATTEMPTS=${MAX_ATTEMPTS:-5}
PROPS=Directory.Build.props
TRAILER="Noctaxis-Version-Bump-PR: ${PR_NUMBER}"
PYTHON=$(command -v python3 || command -v python)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

fail() { echo "::error::$*" >&2; exit 1; }
fetch() { git fetch --quiet "$REMOTE" "+refs/heads/$1:refs/remotes/$REMOTE/$1"; }
version_at() { # version_at <commit> -> X.Y stored in Directory.Build.props at that commit
  git show "$1:$PROPS" > "$WORK/props" || fail "$PROPS is missing at $1."
  "$PYTHON" "$VERSION_TOOL" get --file "$WORK/props"
}
bump_commit() { # the main commit carrying this pull request's bump trailer, if any
  local sha
  for sha in $(git log "$REMOTE/$MAIN_BRANCH" --format=%H -E --grep="^${TRAILER}\$"); do
    if git show -s --format=%B "$sha" | grep -qxF "$TRAILER"; then echo "$sha"; return; fi
  done
}
# Bump commits pushed with GITHUB_TOKEN get no CI run, so the written version is verified here.
verify() { # verify <X.Y> -> the working tree must resolve to X.Y.0 before it is pushed
  "$VERIFY_VERSION" "$1.0" \
    || fail "The committed tree does not resolve to version $1.0; nothing was pushed to $(git rev-parse --abbrev-ref HEAD)."
}
# A rejected push is retried only if the branch moved (a concurrent update). Anything else, such as
# branch protection refusing the push, is reported rather than retried or worked around.
push_or_classify() { # push_or_classify <branch> <base-sha>
  if git push "$REMOTE" "HEAD:refs/heads/$1"; then return 0; fi
  fetch "$1"
  [ "$(git rev-parse "$REMOTE/$1")" != "$2" ] && return 1
  fail "Push to '$1' was rejected without a concurrent update. Branch protection or token permissions probably forbid this workflow from pushing; the version was not changed."
}

fetch "$MAIN_BRANCH"
git merge-base --is-ancestor "$MERGE_SHA" "$REMOTE/$MAIN_BRANCH" \
  || fail "Merge commit $MERGE_SHA is not on $MAIN_BRANCH; refusing to bump."

# 1. main: increment Y exactly once for this pull request.
before="" after=""
for attempt in $(seq 1 "$MAX_ATTEMPTS"); do
  fetch "$MAIN_BRANCH"
  existing=$(bump_commit)
  if [ -n "$existing" ]; then
    before=$(version_at "$existing^"); after=$(version_at "$existing")
    echo "$MAIN_BRANCH already bumped for #$PR_NUMBER in $existing ($before -> $after)."
    break
  fi
  base=$(git rev-parse "$REMOTE/$MAIN_BRANCH")
  git checkout --quiet -B "$MAIN_BRANCH" "$base"
  before=$("$PYTHON" "$VERSION_TOOL" get --file "$PROPS")
  after=$("$PYTHON" "$VERSION_TOOL" bump --file "$PROPS")
  git add "$PROPS"
  git commit --quiet -m "Bump version to $after after merging $DEV_BRANCH into $MAIN_BRANCH (#$PR_NUMBER)" -m "$TRAILER"
  verify "$after"
  if push_or_classify "$MAIN_BRANCH" "$base"; then
    echo "$MAIN_BRANCH: $before -> $after"
    break
  fi
  echo "$MAIN_BRANCH moved during attempt $attempt; retrying."
  before="" after=""
done
[ -n "$after" ] || fail "Could not update $MAIN_BRANCH after $MAX_ATTEMPTS attempts."

# 2. dev: apply the same X.Y, but only over the value that was merged (never over a manual change).
git ls-remote --exit-code --heads "$REMOTE" "$DEV_BRANCH" > /dev/null \
  || fail "Branch '$DEV_BRANCH' no longer exists; $MAIN_BRANCH is at $after but $DEV_BRANCH could not be synchronised."
for attempt in $(seq 1 "$MAX_ATTEMPTS"); do
  fetch "$DEV_BRANCH"
  base=$(git rev-parse "$REMOTE/$DEV_BRANCH")
  current=$(version_at "$base")
  if [ "$current" = "$after" ]; then echo "$DEV_BRANCH already at $after."; exit 0; fi
  [ "$current" = "$before" ] || fail "$DEV_BRANCH has version $current, expected $before (pre-bump) or $after. Refusing to overwrite it; set $DEV_BRANCH to $after manually if appropriate."
  git checkout --quiet -B "$DEV_BRANCH" "$base"
  "$PYTHON" "$VERSION_TOOL" set "$after" --file "$PROPS" > /dev/null
  git add "$PROPS"
  git commit --quiet -m "Sync version $after from $MAIN_BRANCH after merging $DEV_BRANCH (#$PR_NUMBER)"
  verify "$after"
  if push_or_classify "$DEV_BRANCH" "$base"; then echo "$DEV_BRANCH: $current -> $after"; exit 0; fi
  echo "$DEV_BRANCH moved during attempt $attempt; retrying."
done
fail "Could not update $DEV_BRANCH after $MAX_ATTEMPTS attempts; $MAIN_BRANCH is at $after."
