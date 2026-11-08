"""Package the AI service with an embedded Python (B10-S2, A1421).

Builds <out>/ai-service/ next to the game:
- python/: python-build-standalone 3.12 (MPL-2.0 project; CPython PSF), trimmed: Tcl/Tk, test
  modules, bz2, lzma and sqlite are removed (not needed; their licences are outside the approved
  list). zlib stays (inside python312.dll, approved F5265 = A).
- the service and its dependencies installed into that Python, exactly from uv.lock
  (uv export --frozen, no dev dependencies, no hashes needed for local wheels).
- prompts/, schemas/, workflows/, config.toml (paths relative to it) and run.cmd.
The game starts python/python.exe -m glassworld_ai --config config.toml and sets GW_AI_* for the
folders that must be writable (user data). Usage:
  uv run python tools/package_ai_service.py <python-build-standalone dir> <out dir>
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MARKER = ".glassworld-ai-package"  # only a folder with this file is ever replaced
REMOVE = [  # relative to the Python folder; not used by the service, licences outside the list
    "tcl",
    "Lib/tkinter",
    "Lib/idlelib",
    "Lib/turtledemo",
    "Lib/test",
    "Lib/sqlite3",
    "Lib/turtle.py",
    "DLLs/_tkinter.pyd",
    "DLLs/tcl86t.dll",
    "DLLs/tk86t.dll",
    "DLLs/_bz2.pyd",
    "DLLs/_lzma.pyd",
    "DLLs/_sqlite3.pyd",
    "DLLs/sqlite3.dll",
    "DLLs/_ctypes_test.pyd",
    "DLLs/_testbuffer.pyd",
    "DLLs/_testcapi.pyd",
    "DLLs/_testclinic.pyd",
    "DLLs/_testconsole.pyd",
    "DLLs/_testimportmultiple.pyd",
    "DLLs/_testinternalcapi.pyd",
    "DLLs/_testmultiphase.pyd",
    "DLLs/_testsinglephase.pyd",
    "include",
    "libs",
]
CONFIG = """# Packaged AI service (B10-S2). Paths are relative to this file. The game overrides the
# writable folders with GW_AI_* (lock, logs, outputs) so nothing is written next to the program.
prompts_dir = "prompts"
content_dir = "../content"
schemas_dir = "schemas"
image_workflows_dir = "workflows"
log_dir = "data/logs"
lock_dir = "data"
benchmark_file = "data/benchmark.json"
image_output_dir = "data/images"
sound_output_dir = "data/sounds"
model3d_image_dir = "data/images"
model3d_output_dir = "data/models"
hardware_tiers_file = "../content/hardware/tiers.json"
model_footprints_file = "../content/hardware/models.json"
night_plan_file = "../content/hardware/night.json"
ollama_runtime_file = "../content/ai/ollama-runtime.json"
ollama_install_dir = "data/ollama"
model_downloads_file = "../content/ai/model-downloads.json"
model_store_dir = "data/model-store"
moss_worker_script = "data/moss-worker-not-shipped.py"
"""


def run(*args: str, cwd: Path = ROOT) -> str:
    done = subprocess.run(args, cwd=cwd, capture_output=True, text=True)
    if done.returncode != 0:
        raise SystemExit(f"{' '.join(args)} failed:\n{done.stdout}\n{done.stderr}")
    return done.stdout


def packaged_config() -> str:
    """The repository config with the packaged paths (keys that are paths are replaced)."""
    import tomllib

    base = tomllib.loads((ROOT / "ai-service/config.toml").read_text(encoding="utf-8"))
    paths = tomllib.loads(CONFIG)
    lines = [
        CONFIG.rstrip(),
        "",
        "# Everything else as in the repository (ai-service/config.toml).",
    ]
    for key, value in base.items():
        if key in paths or key == "token":
            continue
        lines.append(f"{key} = {_toml(value)}")
    return "\n".join(lines) + "\n"


def _toml(value: object) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, int | float):
        return repr(value)
    if isinstance(value, str):
        return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'
    if isinstance(value, list):
        return "[" + ", ".join(_toml(v) for v in value) + "]"
    raise SystemExit(f"config value of type {type(value).__name__} not supported")


def prepare_target(out_dir: Path) -> Path:
    """The folder to (re)build. Never inside the repository (it would replace the source
    ai-service/, review GW1 #115), and an existing folder only if this script made it."""
    out = out_dir.resolve()
    if out == ROOT or ROOT in out.parents:
        raise SystemExit(f"refusing to package into the repository: {out}")
    target = out / "ai-service"
    if target.exists():
        if not (target / MARKER).is_file():
            raise SystemExit(f"{target} exists and was not made by this script; not replacing it")
        shutil.rmtree(target)
    target.mkdir(parents=True)
    (target / MARKER).write_text("made by tools/package_ai_service.py", encoding="utf-8")
    return target


def strip_build_paths(python_dir: Path) -> None:
    """pip records where a local package came from (PEP 610 direct_url.json, a file:// URL of the
    build machine). That path must not ship (privacy, B10 R8): remove the file and its RECORD
    line. Packages from the index have no such file."""
    for direct in python_dir.rglob("*.dist-info/direct_url.json"):
        if "file:" not in direct.read_text(encoding="utf-8", errors="ignore"):
            continue
        record = direct.parent / "RECORD"
        if record.is_file():
            lines = record.read_text(encoding="utf-8").splitlines(keepends=True)
            kept = [line for line in lines if not line.split(",", 1)[0].endswith("direct_url.json")]
            record.write_text("".join(kept), encoding="utf-8", newline="")
        direct.unlink()


def finish_python(python_dir: Path) -> None:
    """Release hygiene (review GW1 #130): nothing in the package may name the build machine.

    - Scripts/*.exe launchers embed the absolute path of the python.exe they were made for; they
      would not work on the player's PC anyway (the game runs `python.exe -m glassworld_ai`).
    - Test folders of the installed packages are not needed.
    - Byte code stores the source path it was compiled from: compile everything again with paths
      relative to the Python folder (`-d Lib`).
    """
    scripts = python_dir / "Scripts"
    if scripts.is_dir():
        shutil.rmtree(scripts)
    for tests in sorted((python_dir / "Lib" / "site-packages").glob("*/tests")):
        if tests.is_dir():
            shutil.rmtree(tests)
    for cache in sorted(python_dir.rglob("__pycache__"), reverse=True):
        shutil.rmtree(cache)
    python = python_dir / "python.exe"
    if python.is_file():  # Windows build machine (the release runs on Windows)
        # unchecked-hash: a ZIP keeps file times only to 2 s, so timestamp byte code would look
        # stale on the player's PC and be rewritten into the program folder (with its path).
        run(str(python), "-m", "compileall", "-q", "-f", "-d", "Lib",
            "--invalidation-mode", "unchecked-hash",
            "-x", r"[\\/]tests?[\\/]", str(python_dir / "Lib"))  # fmt: skip


def check_inventory(python_dir: Path) -> None:
    """Every DLL and extension module must map to a LICENSES.md row (review GW1 #51): a new
    library in a newer Python build fails the packaging instead of slipping through."""
    import fnmatch

    inventory = json.loads(
        (ROOT / "tools/licenses/embedded-python.json").read_text(encoding="utf-8")
    )["files"]
    rows = (ROOT / "LICENSES.md").read_text(encoding="utf-8").lower()
    found = sorted(
        p.name for p in [*python_dir.glob("*.dll"), *(python_dir / "DLLs").glob("*")]
        if p.suffix.lower() in {".dll", ".pyd"}
    )  # fmt: skip
    problems: list[str] = []
    for name in found:
        row = next((r for pat, r in inventory.items() if fnmatch.fnmatchcase(name, pat)), None)
        if row is None:
            problems.append(f"{name}: not in tools/licenses/embedded-python.json")
        elif f"| {row.lower()}" not in rows:
            problems.append(f"{name}: row '{row}' missing in LICENSES.md")
    if problems:
        raise SystemExit("embedded Python inventory:\n  " + "\n  ".join(problems))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("python_dir", type=Path, help="unpacked python-build-standalone 3.12")
    parser.add_argument("out_dir", type=Path)
    args = parser.parse_args()
    target = prepare_target(args.out_dir)
    python_dir = target / "python"
    shutil.copytree(args.python_dir, python_dir)
    for rel in REMOVE:
        path = python_dir / rel
        if path.is_dir():
            shutil.rmtree(path)
        elif path.exists():
            path.unlink()
    check_inventory(python_dir)
    for marker in python_dir.rglob("EXTERNALLY-MANAGED"):
        marker.unlink()
    python = python_dir / "python.exe"
    requirements = target / "requirements.txt"
    run("uv", "export", "--frozen", "--no-dev", "--no-hashes", "--no-emit-workspace",
        "--no-header", "-o", str(requirements))  # fmt: skip
    common = [
        "uv",
        "pip",
        "install",
        "--python",
        str(python),
        "--break-system-packages",
        "--no-cache",
    ]
    run(*common, "-r", str(requirements))
    run(*common, "--no-deps", str(ROOT / "contracts/python"), str(ROOT / "ai-service"))
    strip_build_paths(python_dir)
    finish_python(python_dir)
    shutil.copytree(ROOT / "prompts", target / "prompts")
    shutil.copytree(ROOT / "contracts/schemas", target / "schemas")
    shutil.copytree(ROOT / "tools/comfyui/workflows", target / "workflows")
    (target / "data").mkdir()
    (target / "config.toml").write_text(packaged_config(), encoding="utf-8", newline="\n")
    (target / "run.cmd").write_text(
        "@echo off\r\nrem Start the AI service by hand (the game starts it itself).\r\n"
        '"%~dp0python\\python.exe" -m glassworld_ai --config "%~dp0config.toml" %*\r\n',
        encoding="utf-8",
    )
    shutil.copy2(python_dir / "LICENSE.txt", target / "LICENSE-python.txt")
    size = sum(p.stat().st_size for p in target.rglob("*") if p.is_file()) / 2**20
    print(f"packaged {target} ({size:.0f} MiB)")


if __name__ == "__main__":
    sys.exit(main())
