#!/usr/bin/env python3
"""Decide what a run of the Release workflow (.github/workflows/release.yml) does with the version in
Directory.Build.props: build, test and package only, or also publish a GitHub Release.

The version of a package is X.Y.Z: X.Y is NoctaxisVersion in Directory.Build.props (noctaxis_version.py), Z
the run's build number. Its release is tagged vX.Y.Z, and each X.Y is released at most once, so a run that
finds a vX.Y.* tag already published does not publish X.Y again.

A run publishes only when:
  - it was started by hand with "publish" chosen, on the release branch; or
  - a push to the release branch changed X.Y, or the version-bump workflow advanced it (dev into main),
    and X.Y has not been released yet.
Inconsistent requests fail with a message saying why: publishing from another branch, publishing an X.Y
already released by hand, a version lower than one already released, or a tag vX.Y.Z already on another
commit.

Usage (in CI; reads git and writes key=value lines to --output, default stdout):
  release_plan.py --event push|workflow_dispatch|version-bump --ref REF --sha SHA --build Z
                  [--before SHA] [--previous X.Y] [--publish true|false] [--output FILE]
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import noctaxis_version  # noqa: E402

RELEASE_BRANCH = "main"
PROPS = "Directory.Build.props"


class PlanError(ValueError):
    pass


@dataclass(frozen=True)
class Plan:
    version: str
    xy: str
    tag: str
    publish: bool
    prerelease: bool
    reason: str

    def outputs(self) -> dict[str, str]:
        return {
            "version": self.version,
            "xy": self.xy,
            "tag": self.tag,
            "publish": "true" if self.publish else "false",
            "prerelease": "true" if self.prerelease else "false",
            "title": f"Noctaxis {self.version}" + (" (alpha)" if self.prerelease else ""),
            "reason": self.reason,
        }


_TAG = re.compile(r"^v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*))\.(0|[1-9][0-9]*)$")


def plan(*, event: str, ref: str, sha: str, current: str, previous: str | None, build: str,
         publish_requested: bool, tags: dict[str, str], release_branch: str = RELEASE_BRANCH) -> Plan:
    """The pure decision. tags maps every existing tag name to the commit it points at."""
    version = noctaxis_version.compose(current, build)
    tag = f"v{version}"
    major, minor = noctaxis_version.parse(current)
    released = sorted((name for name in tags if (m := _TAG.match(name)) and m.group(1) == current),
                      key=lambda name: int(name.rsplit(".", 1)[1]))
    highest = max((noctaxis_version.parse(m.group(1)) for name in tags if (m := _TAG.match(name))), default=None)
    on_branch = ref == f"refs/heads/{release_branch}"

    def result(publish: bool, reason: str) -> Plan:
        if publish and tag in tags and tags[tag] != sha:
            raise PlanError(f"Tag {tag} already exists on commit {tags[tag][:12]}, not on this commit {sha[:12]}. "
                            "A tag is never moved; advance NoctaxisVersion in Directory.Build.props.")
        return Plan(version, current, tag, publish, major == 0, reason)

    if event == "workflow_dispatch":
        if not publish_requested:
            return result(False, f"Manual run: builds, tests and packages {version}; publishes nothing.")
        if not on_branch:
            raise PlanError(f"Publishing is allowed only from {release_branch}; this run is on {ref}.")
        if released and released != [tag]:
            raise PlanError(f"{current} has already been released as {released[-1]}. "
                            "Advance NoctaxisVersion in Directory.Build.props to publish a new release.")
        if highest is not None and (major, minor) < highest:
            raise PlanError(f"{current} is lower than the latest release, {'.'.join(map(str, highest))}.")
        return result(True, f"Manual run with publish chosen: publishes {tag}.")

    if event not in ("push", "version-bump"):
        raise PlanError(f"Unknown event '{event}'.")
    if not on_branch:
        return result(False, f"Not {release_branch}: builds, tests and packages {version}; publishes nothing.")
    if previous is None or previous == current:
        return result(False, f"NoctaxisVersion {current} unchanged: builds, tests and packages {version}; "
                             "publishes nothing.")
    if (major, minor) < noctaxis_version.parse(previous):
        raise PlanError(f"NoctaxisVersion went down from {previous} to {current}.")
    if released and released != [tag]:
        return result(False, f"{current} has already been released as {released[-1]}; not publishing it again.")
    if highest is not None and (major, minor) < highest:
        raise PlanError(f"{current} is lower than the latest release, {'.'.join(map(str, highest))}.")
    return result(True, f"NoctaxisVersion changed from {previous} to {current}: publishes {tag}.")


def _git(*args: str) -> str:
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def _version_at(commit: str) -> str | None:
    """X.Y at a commit, or None when there is no such commit here (a new branch's all-zero 'before')."""
    if not commit or set(commit) == {"0"}:
        return None
    try:
        return noctaxis_version.read_from(_git("show", f"{commit}:{PROPS}"))
    except subprocess.CalledProcessError:
        return None


def _remote_tags() -> dict[str, str]:
    """Every tag on origin and the commit it points at (an annotated tag's peeled commit)."""
    tags: dict[str, str] = {}
    for line in _git("ls-remote", "--tags", "origin").splitlines():
        sha, ref = line.split("\t")
        name = ref.removeprefix("refs/tags/")
        if name.endswith("^{}"):
            tags[name[:-3]] = sha
        else:
            tags.setdefault(name, sha)
    return tags


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--event", required=True)
    parser.add_argument("--ref", required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--build", required=True)
    parser.add_argument("--before", default="", help="commit before a push")
    parser.add_argument("--previous", default="", help="X.Y before a version bump")
    parser.add_argument("--publish", default="false")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        current = noctaxis_version.read_from(_git("show", f"{args.sha}:{PROPS}"))
        previous = args.previous or _version_at(args.before)
        result = plan(event=args.event, ref=args.ref, sha=args.sha, current=current, previous=previous,
                      build=args.build, publish_requested=args.publish == "true", tags=_remote_tags())
    except (PlanError, noctaxis_version.VersionError, subprocess.CalledProcessError) as error:
        print(f"::error::{error}", file=sys.stderr)
        return 1
    lines = [f"{key}={value}" for key, value in result.outputs().items()]
    if args.output:
        with args.output.open("a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
