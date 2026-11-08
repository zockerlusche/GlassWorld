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

CHUNK = 1 << 20


class PackError(Exception):
    pass


def _needles(forbidden: list[str]) -> set[bytes]:
    """Each path in both slash styles, as UTF-8 and UTF-16-LE bytes (Windows resources), lower."""
    variants = {
        variant.lower()
        for path in forbidden
        if len(path.strip()) > 3  # never a bare drive or "/"
        for variant in (path.rstrip("/\\"), path.replace("\\", "/"), path.replace("/", "\\"))
    }
    return {v.encode(enc) for v in variants for enc in ("utf-8", "utf-16-le")}


def leaked_paths(stage: Path, forbidden: list[str]) -> list[str]:
    """Files of any kind (text, byte code, executables) that contain a forbidden path, any case
    (review GW1 #130: .pyc and launchers carried the build machine's user folder)."""
    needles = _needles(forbidden)
    found: list[str] = []
    for file in sorted(stage.rglob("*")):
        if not file.is_file():
            continue
        data = file.read_bytes().lower()
        if any(needle in data for needle in needles):
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
