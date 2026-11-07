"""License check for the build (B10 S1, B2482): every dependency and asset must be in LICENSES.md
with an allowed license.

Checks
1. every external package in uv.lock has a row in LICENSES.md;
2. every NuGet package referenced in a .csproj has a row; with --nuget-assets also every package
   that NuGet restored transitively (obj/project.assets.json, after dotnet build/restore);
3. every asset file under game/ (fonts, models, sounds, images) matches a rule in
   tools/licenses/assets.json, and that rule names a row in LICENSES.md or "own" (made for
   GlassWorld, MIT);
4. every row's license is allowed: Apache-2.0, MIT, CC0, BSD-2/3-Clause, PSF-2.0, ISC, MPL-2.0
   (with "unmodified"), Public Domain; anything else only with Marcel's approval written in the
   row ("approved F1234"). GPL/AGPL/LGPL and non-commercial licenses always need that approval.

Standard library only. Exit code 1 and one line per problem when something is missing.
Usage: python tools/licenses/check_licenses.py [--root DIR] [--nuget-assets]
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import re
import sys
import tomllib
from dataclasses import dataclass
from pathlib import Path

ALLOWED = (
    "apache-2.0",
    "mit",
    "cc0",
    "bsd-2-clause",
    "bsd-3-clause",
    "psf-2.0",
    "isc",
    "mpl-2.0",
    "public domain",
)
NEEDS_APPROVAL = ("gpl", "agpl", "lgpl", "non-commercial", "noncommercial", "cc-by-nc", "unknown")
APPROVAL = re.compile(r"approved\s+F\d+", re.IGNORECASE)
ASSET_SUFFIXES = {
    ".glb", ".gltf", ".fbx", ".obj", ".blend",
    ".ttf", ".otf", ".woff", ".woff2",
    ".png", ".jpg", ".jpeg", ".webp", ".svg", ".exr", ".hdr",
    ".ogg", ".wav", ".mp3", ".flac",
}  # fmt: skip


@dataclass(frozen=True)
class Row:
    name: str
    license: str
    keys: frozenset[str]  # normalized names this row covers


def normalize(name: str) -> str:
    """Package names compare like PyPI/NuGet: case-insensitive, '_' and '.' like '-'."""
    return re.sub(r"[-_.]+", "-", name.strip().lower())


def parse_rows(text: str) -> list[Row]:
    """Rows of the markdown table in LICENSES.md (first column: one or more names)."""
    rows: list[Row] = []
    for line in text.splitlines():
        if not line.startswith("| ") or line.startswith("| Name ") or line.startswith("|---"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) < 4:
            continue
        name, license_text = cells[0], cells[3]
        keys: set[str] = set()
        base = re.sub(r"\(\+ ([^)]*)\)", "", name)  # "X (+ core, provider)" -> X, X.core, ...
        parts = [p.strip() for p in re.split(r",| / ", base) if p.strip()]
        first = parts[0] if parts else ""
        for part in parts:
            if part.startswith("."):
                # "A.B / .C" is used both for the sibling A.C and the child A.B.C: accept both.
                keys.add(normalize(first.rsplit(".", 1)[0] + part))
                keys.add(normalize(first + part))
            else:
                keys.add(normalize(part))
        extra = re.search(r"\(\+ ([^)]*)\)", name)
        if extra:
            stem = base.strip().rsplit("_", 1)[0].rsplit(".", 1)[0]
            for part in extra.group(1).split(","):
                keys.add(normalize(f"{stem}.{part.strip()}"))
        rows.append(Row(name, license_text, frozenset(keys)))
    return rows


def license_problem(row: Row) -> str | None:
    text = row.license.lower()
    if APPROVAL.search(row.license):
        return None
    if any(bad in text for bad in NEEDS_APPROVAL):
        return (
            f"{row.name}: license '{row.license}' needs Marcel's approval (write 'approved F<nr>')"
        )
    if not any(ok in text for ok in ALLOWED):
        return f"{row.name}: license '{row.license}' is not allowed by B2482"
    if "mpl-2.0" in text and "unmodified" not in text:
        return f"{row.name}: MPL-2.0 only for unmodified files (write 'unmodified')"
    return None


def uv_packages(root: Path) -> list[str]:
    lock = tomllib.loads((root / "uv.lock").read_text(encoding="utf-8"))
    return sorted(
        p["name"]
        for p in lock.get("package", [])
        if not ({"editable", "virtual"} & set(p.get("source", {})))
    )


def nuget_direct(root: Path) -> list[str]:
    names: set[str] = set()
    for project in root.rglob("*.csproj"):
        if {"bin", "obj", ".godot"} & set(project.parts):
            continue
        for match in re.finditer(
            r'<PackageReference\s+Include="([^"]+)"', project.read_text(encoding="utf-8")
        ):
            names.add(match.group(1))
    return sorted(names)


def nuget_restored(root: Path) -> list[str]:
    """All packages NuGet restored (direct and transitive), from obj/project.assets.json."""
    names: set[str] = set()
    for assets in root.rglob("project.assets.json"):
        if "obj" not in assets.parts:
            continue
        data = json.loads(assets.read_text(encoding="utf-8"))
        for key, library in data.get("libraries", {}).items():
            if library.get("type") == "package":
                names.add(key.split("/", 1)[0])
    return sorted(names)


def asset_files(root: Path) -> list[str]:
    game = root / "game"
    if not game.exists():
        return []
    return sorted(
        p.relative_to(root).as_posix()
        for p in game.rglob("*")
        if p.is_file()
        and p.suffix.lower() in ASSET_SUFFIXES
        and not ({".godot", ".import"} & set(p.parts))
    )


def check(root: Path, *, nuget_assets: bool = False) -> list[str]:
    rows = parse_rows((root / "LICENSES.md").read_text(encoding="utf-8"))
    covered = {key for row in rows for key in row.keys}
    names_lower = [row.name.lower() for row in rows]
    problems = [p for p in (license_problem(r) for r in rows) if p]

    for package in uv_packages(root):
        if normalize(package) not in covered:
            problems.append(f"uv.lock: package '{package}' has no row in LICENSES.md")

    nuget = set(nuget_direct(root))
    if nuget_assets:
        restored = nuget_restored(root)
        if not restored:
            problems.append("no obj/project.assets.json found: run dotnet restore/build first")
        nuget |= set(restored)
    for package in sorted(nuget):
        if normalize(package) not in covered:
            problems.append(f"NuGet: package '{package}' has no row in LICENSES.md")

    rules_file = root / "tools" / "licenses" / "assets.json"
    rules: list[dict[str, str]] = json.loads(rules_file.read_text(encoding="utf-8"))["rules"]
    for rule in rules:
        entry = rule["entry"]
        if entry != "own" and not any(n.startswith(entry.lower()) for n in names_lower):
            problems.append(f"assets.json: rule {rule['glob']} names '{entry}', not in LICENSES.md")
    for path in asset_files(root):
        if not any(fnmatch.fnmatchcase(path, rule["glob"]) for rule in rules):
            problems.append(f"asset '{path}' matches no rule in tools/licenses/assets.json")
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--nuget-assets", action="store_true")
    args = parser.parse_args(argv)
    problems = check(args.root, nuget_assets=args.nuget_assets)
    for problem in problems:
        print(f"LICENSE CHECK: {problem}")
    if not problems:
        print("License check passed.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
