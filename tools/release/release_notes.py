"""Release notes from CHANGELOG.md (B10 R8): the section of one version, plus the licence note.

Usage: python tools/release/release_notes.py 0.1.0 [--changelog CHANGELOG.md] [--out notes.md]
       python tools/release/release_notes.py --unreleased   (probe builds before a version exists)

The section is the text between "## [<version>]" (optionally followed by " - <date>") and the next
"## [" heading. A missing or empty section is an error: a release never goes out without notes.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$")
FOOTER = (
    "\n---\n\n"
    "Everything runs locally; the first start downloads Ollama and the language model only after "
    "your consent, from their official sources. "
    "Third-party licences: `LICENSES.md` in the archive. "
    "Check the download against `SHA256SUMS.txt`."
)


class NotesError(Exception):
    pass


def section(changelog: str, heading: str) -> str:
    """Body of the section `## [heading]` (without the heading line)."""
    pattern = re.compile(rf"^## \[{re.escape(heading)}\][^\n]*\n", re.MULTILINE)
    found = pattern.search(changelog)
    if found is None:
        raise NotesError(f"CHANGELOG.md has no section [{heading}]")
    rest = changelog[found.end() :]
    following = re.search(r"^## \[", rest, re.MULTILINE)
    body = (rest[: following.start()] if following else rest).strip()
    if not body:
        raise NotesError(f"section [{heading}] of CHANGELOG.md is empty")
    return body


def notes(changelog: str, version: str | None) -> str:
    if version is not None and not SEMVER.fullmatch(version):
        raise NotesError(f"'{version}' is not a semantic version (e.g. 0.1.0)")
    return section(changelog, version or "Unreleased") + "\n" + FOOTER + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("version", nargs="?", help="semantic version without 'v', e.g. 0.1.0")
    group.add_argument("--unreleased", action="store_true")
    parser.add_argument("--changelog", type=Path, default=ROOT / "CHANGELOG.md")
    parser.add_argument("--out", type=Path, help="write the notes there (UTF-8) instead of stdout")
    args = parser.parse_args(argv)
    try:
        text = notes(
            args.changelog.read_text(encoding="utf-8"), None if args.unreleased else args.version
        )
    except NotesError as error:
        print(f"RELEASE_NOTES_FAIL: {error}", file=sys.stderr)
        return 1
    if args.out is not None:
        args.out.write_text(text, encoding="utf-8", newline="\n")
    else:
        sys.stdout.buffer.write(text.encode("utf-8"))  # Windows consoles default to cp1252
    return 0


if __name__ == "__main__":
    sys.exit(main())
