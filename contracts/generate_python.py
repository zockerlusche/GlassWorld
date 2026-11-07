"""Generate the pydantic models in contracts/python from contracts/schemas.

Usage (from the repository root):
    uv run python contracts/generate_python.py          # regenerate
    uv run python contracts/generate_python.py --check  # fail if checked-in code is stale
"""

from __future__ import annotations

import argparse
import filecmp
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

CONTRACTS = Path(__file__).resolve().parent
SCHEMAS = CONTRACTS / "schemas"
TARGET = CONTRACTS / "python" / "src" / "glassworld_contracts" / "models"

# Fixed options: the output must be byte-identical on every machine.
OPTIONS = [
    "--input-file-type", "jsonschema",
    "--output-model-type", "pydantic_v2.BaseModel",
    "--target-python-version", "3.12",
    "--use-standard-collections",
    "--use-union-operator",
    "--field-constraints",
    "--collapse-root-models",
    "--extra-fields", "forbid",
    "--disable-timestamp",
    "--formatters", "builtin",
]  # fmt: skip


def generate(output: Path) -> None:
    cmd = [
        sys.executable,
        "-m",
        "datamodel_code_generator",
        "--input",
        str(SCHEMAS),
        "--output",
        str(output),
        *OPTIONS,
    ]
    subprocess.run(cmd, check=True, capture_output=True, text=True)
    # The repository stores text with LF (.gitattributes); Windows writes CRLF.
    for path in output.glob("*.py"):
        path.write_bytes(path.read_bytes().replace(b"\r\n", b"\n"))


def _differences(a: Path, b: Path) -> list[str]:
    names_a: set[str] = {p.name for p in a.glob("*.py")}
    names_b: set[str] = {p.name for p in b.glob("*.py")} if b.exists() else set()
    diffs = sorted(names_a ^ names_b)
    for name in sorted(names_a & names_b):
        if not filecmp.cmp(a / name, b / name, shallow=False):
            diffs.append(name)
    return diffs


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="only compare, do not write")
    args = parser.parse_args()

    with tempfile.TemporaryDirectory() as tmp:
        fresh = Path(tmp) / "models"
        generate(fresh)
        diffs = _differences(fresh, TARGET)
        if args.check:
            if diffs:
                print("Generated models are stale: " + ", ".join(diffs), file=sys.stderr)
                print("Run: uv run python contracts/generate_python.py", file=sys.stderr)
                return 1
            print("Generated models are up to date.")
            return 0
        if TARGET.exists():
            shutil.rmtree(TARGET)
        shutil.copytree(fresh, TARGET)
        count = len(list(TARGET.glob("*.py")))
        print(f"Wrote {count} files to {TARGET.relative_to(CONTRACTS.parent).as_posix()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
