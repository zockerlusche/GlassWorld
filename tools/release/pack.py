"""Pack a release folder (B10 R8): ZIP with the folder as its root, SHA256SUMS.txt, and a check that
no path of the build machine ends up in the package (privacy: nothing personal leaves with it).

Usage: python tools/release/pack.py <stage_dir> <out_dir> --forbid <path> [--forbid <path> ...]
"""

from __future__ import annotations

import argparse
import hashlib
import sys
import zipfile
from pathlib import Path

TEXT_SUFFIXES = {
    ".cmd", ".bat", ".ps1", ".sh", ".txt", ".md", ".json", ".toml", ".cfg", ".ini", ".xml",
    ".yaml", ".yml", ".py", ".pth", "._pth",
}  # fmt: skip
CHUNK = 1 << 20


class PackError(Exception):
    pass


def leaked_paths(stage: Path, forbidden: list[str]) -> list[str]:
    """Text files that mention a forbidden path (both slash styles, any case)."""
    needles = {
        variant.lower()
        for path in forbidden
        if path.strip()
        for variant in (path, path.replace("\\", "/"), path.replace("/", "\\"))
    }
    found: list[str] = []
    for file in sorted(stage.rglob("*")):
        if not file.is_file() or file.suffix.lower() not in TEXT_SUFFIXES:
            continue
        text = file.read_text(encoding="utf-8", errors="ignore").lower()
        if any(needle in text for needle in needles):
            found.append(file.relative_to(stage).as_posix())
    return found


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        while block := f.read(CHUNK):
            digest.update(block)
    return digest.hexdigest()


def pack(stage: Path, out_dir: Path, forbidden: list[str]) -> Path:
    if not stage.is_dir():
        raise PackError(f"{stage} is not a folder")
    leaks = leaked_paths(stage, forbidden)
    if leaks:
        raise PackError("build machine paths in the package: " + ", ".join(leaks))
    out_dir.mkdir(parents=True, exist_ok=True)
    archive = out_dir / f"{stage.name}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        for file in sorted(stage.rglob("*")):
            if file.is_file():
                zf.write(file, (Path(stage.name) / file.relative_to(stage)).as_posix())
    sums = out_dir / "SHA256SUMS.txt"
    sums.write_text(f"{sha256(archive)}  {archive.name}\n", encoding="utf-8", newline="\n")
    return archive


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("stage", type=Path)
    parser.add_argument("out_dir", type=Path)
    parser.add_argument("--forbid", action="append", default=[], help="path that must not appear")
    args = parser.parse_args(argv)
    try:
        archive = pack(args.stage, args.out_dir, args.forbid)
    except PackError as error:
        print(f"PACK_FAIL: {error}", file=sys.stderr)
        return 1
    print(f"PACK_OK {archive.name} {archive.stat().st_size} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
