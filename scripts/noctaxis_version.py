#!/usr/bin/env python3
"""Read and update the repository-controlled Noctaxis X.Y version.

Directory.Build.props holds exactly one <NoctaxisVersion>X.Y</NoctaxisVersion> element; the build
number Z is supplied separately by CI. This tool only ever rewrites that element's text, so the
rest of the file (formatting, comments, line endings) is preserved byte for byte.

Usage:
  noctaxis_version.py get      [--file PATH]          print X.Y
  noctaxis_version.py bump     [--file PATH]          Y += 1 (X unchanged), print new X.Y
  noctaxis_version.py set X.Y  [--file PATH]          write X.Y, print it
  noctaxis_version.py compose  [--file PATH] [--build Z]   print X.Y.Z (Z defaults to 0)
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

DEFAULT_FILE = Path(__file__).resolve().parent.parent / "Directory.Build.props"
_ELEMENT = re.compile(r"(<NoctaxisVersion>)([^<]*)(</NoctaxisVersion>)")
_VERSION = re.compile(r"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$")
_BUILD = re.compile(r"^(0|[1-9][0-9]{0,4})$")
# Every assembly/file version component must be at most 65534.
MAXIMUM_COMPONENT = 65534


class VersionError(ValueError):
    pass


def parse(text: str) -> tuple[int, int]:
    """Parse an X.Y value, rejecting anything MSBuild validation would also reject."""
    match = _VERSION.fullmatch(text.strip())
    if not match:
        raise VersionError(f"Expected X.Y with non-negative integers and no leading zeros, found {text!r}.")
    major, minor = int(match.group(1)), int(match.group(2))
    if major > MAXIMUM_COMPONENT or minor > MAXIMUM_COMPONENT:
        raise VersionError(f"Version components must not exceed {MAXIMUM_COMPONENT}, found {text!r}.")
    return major, minor


def format_version(major: int, minor: int) -> str:
    return f"{major}.{minor}"


def increment(version: str) -> str:
    """Increment Y exactly once. X is never changed by automation."""
    major, minor = parse(version)
    if minor + 1 > MAXIMUM_COMPONENT:
        raise VersionError(f"Y cannot be incremented beyond {MAXIMUM_COMPONENT}; change X manually.")
    return format_version(major, minor + 1)


def compose(version: str, build: str | int | None) -> str:
    """X.Y.Z exactly as Directory.Build.props composes VersionPrefix (Z falls back to 0)."""
    major, minor = parse(version)
    build_text = "0" if build is None or str(build) == "" else str(build)
    if not _BUILD.fullmatch(build_text) or int(build_text) > MAXIMUM_COMPONENT:
        raise VersionError(f"Build number must be an integer from 0 to {MAXIMUM_COMPONENT}, found {build_text!r}.")
    return f"{format_version(major, minor)}.{build_text}"


def read_from(content: str) -> str:
    matches = _ELEMENT.findall(content)
    if len(matches) != 1:
        raise VersionError(f"Expected exactly one <NoctaxisVersion> element, found {len(matches)}.")
    value = matches[0][1]
    parse(value)
    return value


def replace_in(content: str, new_version: str) -> str:
    read_from(content)  # Validates the existing element before rewriting it.
    parse(new_version)
    return _ELEMENT.sub(lambda m: m.group(1) + new_version + m.group(3), content, count=1)


def _read_file(path: Path) -> str:
    # newline="" keeps CRLF/LF exactly as stored.
    with path.open("r", encoding="utf-8", newline="") as handle:
        return handle.read()


def _write_file(path: Path, content: str) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(content)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["get", "bump", "set", "compose"])
    parser.add_argument("value", nargs="?", help="X.Y for 'set'")
    parser.add_argument("--file", type=Path, default=DEFAULT_FILE)
    parser.add_argument("--build", help="Z for 'compose'")
    args = parser.parse_args(argv)

    try:
        content = _read_file(args.file)
        current = read_from(content)
        if args.command == "get":
            print(current)
        elif args.command == "compose":
            print(compose(current, args.build))
        elif args.command == "bump":
            updated = increment(current)
            _write_file(args.file, replace_in(content, updated))
            print(updated)
        else:
            if args.value is None:
                parser.error("'set' requires an X.Y value")
            _write_file(args.file, replace_in(content, args.value))
            print(args.value)
    except (VersionError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
