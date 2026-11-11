"""B2-7 measurement (acceptance 5, A1369): the FPS benchmark at the reference resolutions.

Runs `tools/benchmark.sh` once per resolution (every pass of content/look/benchmark.json in its own
process), samples the graphics memory with nvidia-smi meanwhile, evaluates the frame-time CSVs (mean
fps, p95 and maximum frame time per pass, against the marks of the benchmark data) and writes
`report.md` for an AMW document. A window on the screen: only as an AMW screen-queue run.

    uv run python tools/bench/measure.py --screen-queue --binary <GlassWorld.exe> --out ../bench-out
    uv run python tools/bench/measure.py --screen-queue --binary <GlassWorld.exe> --out ../bench

The output folder must be outside the repository; an earlier run's folder is kept under a new name
(rule PR4). Exit code 0 if every pass meets the marks, 2 if one misses them, 1 if a run failed.
"""

from __future__ import annotations

import argparse
import csv
import itertools
import json
import math
import shutil
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MARKER = ".glassworld-bench"
GIT_BASH = Path("C:/Program Files/Git/bin/bash.exe")
SMI = ["nvidia-smi", "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"]


@dataclass(frozen=True)
class Marks:
    mean_fps: float
    p95_ms: float
    passes: tuple[str, ...]


@dataclass(frozen=True)
class PassResult:
    resolution: str
    name: str
    frames: int
    mean_fps: float
    p95_ms: float
    max_ms: float
    ok: bool


def load_marks(root: Path = ROOT) -> Marks:
    data = json.loads((root / "content/look/benchmark.json").read_text("utf-8"))
    return Marks(
        float(data["target_mean_fps"]),
        float(data["target_p95_frame_ms"]),
        tuple(p["id"] for p in data["passes"]),
    )


def percentile(values: list[float], share: float) -> float:
    """Nearest-rank percentile: the smallest value with `share` of all values at or below it."""
    ordered = sorted(values)
    return ordered[max(0, math.ceil(share * len(ordered)) - 1)]


def evaluate_csv(path: Path, resolution: str, name: str, marks: Marks) -> PassResult:
    """Mean fps, p95 and maximum frame time of one pass (columns pass,time_s,frame_ms)."""
    with path.open(encoding="utf-8", newline="") as file:
        frames = [float(row["frame_ms"]) for row in csv.DictReader(file)]
    if not frames:
        raise ValueError(f"{path} has no frames")
    mean_fps = 1000.0 * len(frames) / sum(frames)
    p95 = percentile(frames, 0.95)
    ok = mean_fps >= marks.mean_fps and p95 <= marks.p95_ms
    return PassResult(resolution, name, len(frames), mean_fps, p95, max(frames), ok)


def evaluate(out: Path, resolutions: list[str], marks: Marks) -> tuple[list[PassResult], list[str]]:
    """The results of every pass that has a CSV, and what is missing."""
    results: list[PassResult] = []
    missing: list[str] = []
    for resolution in resolutions:
        for name in marks.passes:
            path = out / f"bench_{resolution}_{name}.csv"
            if path.is_file() and path.stat().st_size > 0:
                results.append(evaluate_csv(path, resolution, name, marks))
            else:
                missing.append(f"{resolution} {name}")
    return results, missing


def vram_peaks(out: Path, resolutions: list[str]) -> dict[str, tuple[int, int]]:
    """Per resolution the highest used graphics memory and the card's total (MiB), where sampled."""
    peaks: dict[str, tuple[int, int]] = {}
    for resolution in resolutions:
        path = out / f"vram_{resolution}.csv"
        if not path.is_file():
            continue
        with path.open(encoding="utf-8", newline="") as file:
            rows = [(int(r["used_mib"]), int(r["total_mib"])) for r in csv.DictReader(file)]
        if rows:
            peaks[resolution] = (max(used for used, _ in rows), rows[0][1])
    return peaks


def report(
    results: list[PassResult],
    missing: list[str],
    peaks: dict[str, tuple[int, int]],
    marks: Marks,
    note: str,
) -> str:
    """The German report for the AMW document (developer text, not shown in the game)."""
    lines = [
        "# B2-7 Leistungsmessung: Benchmark-Szene mit 100 Figuren (Abnahme 5)",
        "",
        note,
        "",
        f"**Marken:** im Mittel mindestens {marks.mean_fps:g} Bilder je Sekunde, "
        f"95 % der Bilder in höchstens {marks.p95_ms:g} ms.",
        "",
        "| Auflösung | Durchgang | Bilder | Mittel (Bilder/s) | p95 (ms) | Maximum (ms) | Marken |",
        "|---|---|---|---|---|---|---|",
    ]
    lines += [
        f"| {r.resolution} | {r.name} | {r.frames} | {r.mean_fps:.1f} | {r.p95_ms:.1f} | "
        f"{r.max_ms:.1f} | {'erfüllt' if r.ok else 'verfehlt'} |"
        for r in results
    ]
    if missing:
        lines += ["", "**Ohne Messwerte:** " + ", ".join(missing) + "."]
    if peaks:
        lines += [
            "",
            "| Auflösung | Grafikspeicher höchstens (MiB) | Karte gesamt (MiB) |",
            "|---|---|---|",
        ]
        lines += [f"| {res} | {used} | {total} |" for res, (used, total) in peaks.items()]
        lines += [
            "",
            "Der Grafikspeicher ist für die ganze Karte gemessen, nicht nur für das Spiel.",
        ]
    passed = sum(r.ok for r in results)
    lines += ["", f"**Ergebnis:** {passed} von {len(results)} Durchgängen erfüllen die Marken."]
    return "\n".join(lines) + "\n"


def archive_name(folder: Path) -> Path:
    """A free name next to `folder` for keeping an earlier run: <name>.alt-1, .alt-2, ..."""
    for n in itertools.count(1):
        candidate = folder.with_name(f"{folder.name}.alt-{n}")
        if not candidate.exists():
            return candidate
    raise AssertionError("unreachable")


def guard(out: Path, screen_queue: bool, evaluate_only: bool, binary: Path | None) -> str | None:
    """Why the run must not start, or None."""
    if out == ROOT or ROOT in out.parents:
        return f"output folder {out} is inside the repository"
    if evaluate_only:
        return None if out.is_dir() else f"{out} does not exist"
    if not screen_queue:
        return "a benchmark window runs only as an AMW screen-queue run (pass --screen-queue)"
    if binary is None or not binary.is_file():
        return f"binary {binary} not found"
    if out.exists() and any(out.iterdir()) and not (out / MARKER).is_file():
        return f"{out} exists, is not empty and is not a folder of this tool"
    return None


def sample_vram(path: Path, stop: threading.Event, interval_s: float) -> None:
    """Writes time_s,used_mib,total_mib per interval until `stop` is set; silent without a card."""
    started = time.monotonic()
    with path.open("w", encoding="utf-8", newline="") as file:
        writer = csv.writer(file)
        writer.writerow(["time_s", "used_mib", "total_mib"])
        while not stop.is_set():
            try:
                answer = subprocess.run(SMI, capture_output=True, text=True, timeout=10, check=True)
                used, total = (int(v) for v in answer.stdout.splitlines()[0].split(","))
                writer.writerow([f"{time.monotonic() - started:.1f}", used, total])
                file.flush()
            except (OSError, subprocess.SubprocessError, ValueError, IndexError):
                return
            stop.wait(interval_s)


def run_resolution(bash: str, binary: Path, entry: str, out: Path, extra: list[str]) -> int:
    """One benchmark.sh call for 'WxH' or 'WxH:fullscreen', with VRAM sampling; its exit code."""
    resolution, _, mode = entry.partition(":")
    command = [bash, str(ROOT / "tools/benchmark.sh"), str(binary), resolution, str(out)]
    command += (["--fullscreen"] if mode == "fullscreen" else []) + extra
    stop = threading.Event()
    sampler = threading.Thread(
        target=sample_vram, args=(out / f"vram_{resolution}.csv", stop, 1.0), daemon=True
    )
    sampler.start()
    try:
        return subprocess.run(command, check=False).returncode
    finally:
        stop.set()
        sampler.join(timeout=15)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--out", type=Path, required=True, help="folder outside the repository")
    parser.add_argument("--binary", type=Path, help="exported GlassWorld.exe (release)")
    parser.add_argument(
        "--resolutions",
        nargs="+",
        default=["3840x1600:fullscreen", "1920x1080"],
        help="WxH or WxH:fullscreen (rule KR5: reference full screen plus 1920x1080)",
    )
    parser.add_argument("--screen-queue", action="store_true", help="required for a run")
    parser.add_argument("--evaluate-only", action="store_true", help="only evaluate CSVs in --out")
    parser.add_argument("--bash", default=None, help="bash to run benchmark.sh with")
    parser.add_argument("--note", default="", help="a line for the report (build, machine, run)")
    parser.add_argument("extra", nargs="*", help="more arguments for the scene, e.g. --quality=ID")
    options = parser.parse_args(argv)
    out = options.out.resolve()
    problem = guard(out, options.screen_queue, options.evaluate_only, options.binary)
    if problem is not None:
        print(f"BENCH_MEASURE_FAIL: {problem}", file=sys.stderr)
        return 1
    failed = False
    if not options.evaluate_only:
        bash = options.bash or (str(GIT_BASH) if GIT_BASH.is_file() else shutil.which("bash"))
        if bash is None:
            print("BENCH_MEASURE_FAIL: no bash found (pass --bash)", file=sys.stderr)
            return 1
        if out.exists():
            out.rename(archive_name(out))  # rule PR4: an earlier run is kept, never overwritten
        out.mkdir(parents=True)
        (out / MARKER).write_text("made by tools/bench/measure.py", encoding="utf-8")
        for entry in options.resolutions:
            failed |= run_resolution(bash, options.binary.resolve(), entry, out, options.extra) != 0
    marks = load_marks()
    resolutions = [entry.partition(":")[0] for entry in options.resolutions]
    results, missing = evaluate(out, resolutions, marks)
    text = report(results, missing, vram_peaks(out, resolutions), marks, options.note)
    (out / "report.md").write_text(text, encoding="utf-8", newline="\n")
    for r in results:
        print(
            f"BENCH_RESULT {r.resolution} {r.name} frames={r.frames} mean_fps={r.mean_fps:.1f} "
            f"p95_ms={r.p95_ms:.1f} max_ms={r.max_ms:.1f} ok={'yes' if r.ok else 'no'}"
        )
    if failed or missing or not results:
        print(
            f"BENCH_MEASURE_FAIL: run failed or no values for: {', '.join(missing)}",
            file=sys.stderr,
        )
        return 1
    return 0 if all(r.ok for r in results) else 2


if __name__ == "__main__":
    raise SystemExit(main())
