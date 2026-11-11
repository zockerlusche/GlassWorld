"""B2-7 measurement (acceptance 5, A1369): the FPS benchmark at the reference resolutions.

Runs `tools/benchmark.sh` once per resolution and pass (passes from content/look/benchmark.json),
samples the graphics memory with one nvidia-smi child per pass (a few samples before the game
starts are the baseline), then judges every pass from what was really measured:
- frame times of the CSV: mean fps, nearest-rank p95, maximum, against the marks of the data;
- the log of the game: the size it rendered at must be the requested one, the measured duration
  the one of the data, and the game's own numbers must agree with the CSV;
- graphics memory of the game = peak minus baseline of the whole card, against `game_vram_mib` of
  content/hardware/models.json. No samples counts as not measured.
It writes `report.md` for an AMW document. A window on the screen: only as an AMW screen run
(`amw lauf start --schirm --gpu --warten`, then `amw lauf warte <nr>`).

    uv run python tools/bench/measure.py --screen-queue --binary <GlassWorld.exe> --out ../bench \
        --note "export of main <sha>"
    uv run python tools/bench/measure.py --evaluate-only --out ../bench   # judge again

Arguments for the scene go after `--` (e.g. `-- --bench-seconds=5` for a dry run). Any such
argument is shown in the report and makes every pass invalid: the acceptance run has none.
The output folder must be outside the repository; an earlier run's folder or report is kept under a new name (rule PR4).
Exit code 0: every pass measured and within marks and budget. 3: measured, but a mark or the budget
is missed. 1: a run failed or something was not measured as required. (2 is a usage error.)
"""

from __future__ import annotations

import argparse
import contextlib
import csv
import datetime
import itertools
import json
import math
import os
import re
import shutil
import statistics
import subprocess
import sys
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
MARKER = ".glassworld-bench"
GIT_BASH = Path("C:/Program Files/Git/bin/bash.exe")
SMI_SAMPLES = [
    "nvidia-smi",
    "--query-gpu=memory.used,memory.total",
    "--format=csv,noheader,nounits",
    "-l",
    "1",
]
SMI_CARD = ["nvidia-smi", "--query-gpu=name,driver_version", "--format=csv,noheader"]
ENTRY = re.compile(r"^(\d+x\d+)(?::(fullscreen))?$")
BASELINE_S = 4.0
PASS_TIMEOUT_S = 1000
EXIT_OK, EXIT_FAILED, EXIT_MISSED = 0, 1, 3
NONE = "-"


@dataclass(frozen=True)
class Marks:
    mean_fps: float
    p95_ms: float
    duration_s: float
    figures: int
    vram_mib: int
    passes: tuple[str, ...]
    warmup_s: float = 0.0
    seed: str = ""
    quality: str = ""


@dataclass
class PassResult:
    resolution: str
    mode: str
    name: str
    frames: int = 0
    mean_fps: float = 0.0
    p95_ms: float = 0.0
    max_ms: float = 0.0
    size: str = "?"
    quality: str = "?"
    figures: str = "?"
    vsync: str = "?"
    window: str = "?"
    build: str = "?"
    driver: str = "?"
    warmup_frames: str = "?"
    baseline_mib: int | None = None
    peak_mib: int | None = None
    samples: int = 0
    sample_errors: int = 0
    missed: list[str] = field(default_factory=list[str])  # measured, a mark or the budget missed
    invalid: list[str] = field(default_factory=list[str])  # not measured as required

    @property
    def game_mib(self) -> int | None:
        if self.baseline_mib is None or self.peak_mib is None:
            return None
        return max(0, self.peak_mib - self.baseline_mib)

    @property
    def verdict(self) -> str:
        return "ungültig" if self.invalid else "verfehlt" if self.missed else "erfüllt"


def load_marks(root: Path = ROOT) -> Marks:
    bench = json.loads((root / "content/look/benchmark.json").read_text("utf-8"))
    hardware = json.loads((root / "content/hardware/models.json").read_text("utf-8"))
    quality = json.loads((root / "content/look/quality.json").read_text("utf-8"))
    return Marks(
        float(bench["target_mean_fps"]),
        float(bench["target_p95_frame_ms"]),
        float(bench["duration_s"]),
        int(bench["crowd"]["count"]),
        int(hardware["game_vram_mib"]),
        tuple(p["id"] for p in bench["passes"]),
        float(bench["warmup_s"]),
        str(bench["seed"]),
        str(quality["default_level"]),
    )


def judge_setup(
    result: PassResult,
    start: dict[str, str],
    done: dict[str, str],
    env: dict[str, str],
    marks: Marks,
) -> None:
    """What the run really ran on (BENCH_ENV of the game, review GW1 #224): anything else than the
    data's scene at the requested size, in the requested window mode, without VSync or a frame cap,
    from a named build is not a measurement for the acceptance."""
    result.window = env.get("mode", "?")
    result.vsync = env.get("vsync", "?")
    result.build = env.get("build", "?")
    result.driver = " ".join(v for v in (env.get("adapter"), env.get("driver")) if v) or "?"
    result.warmup_frames = done.get("warmup_frames", "?")
    if not env:
        result.invalid.append("Log ohne BENCH_ENV (Export ohne diese Zeile)")
        return
    wanted = "ExclusiveFullscreen" if result.mode == "fullscreen" else "Windowed"
    checks = [
        (env.get("size") == result.resolution, f"Fenster in {env.get('size')}"),
        (result.window == wanted, f"Modus {result.window} statt {wanted}"),
        (result.vsync == "Disabled", f"VSync {result.vsync}"),
        (env.get("max_fps") == "0", f"Bildraten-Grenze {env.get('max_fps')}"),
        (
            result.build not in ("?", "dev", "unknown"),
            f"Build '{result.build}' ist kein benannter Stand",
        ),
        (result.warmup_frames not in ("?", "0"), f"Aufwärm-Bilder {result.warmup_frames}"),
        (
            start.get("quality") == marks.quality,
            f"Qualität {start.get('quality')} statt {marks.quality}",
        ),
        (start.get("seed") == marks.seed, f"Seed {start.get('seed')} statt {marks.seed}"),
    ]
    result.invalid += [reason for good, reason in checks if not good]
    if not start.get("figures", "").isdigit() or int(start["figures"]) < marks.figures:
        result.invalid.append(
            f"nur {start.get('figures', '?')} Figuren statt mindestens {marks.figures}"
        )
    try:
        if float(start["warmup_s"]) != marks.warmup_s:
            result.invalid.append(f"Aufwärmzeit {start['warmup_s']} s statt {marks.warmup_s:g} s")
    except (KeyError, ValueError):
        result.invalid.append("Aufwärmzeit nicht im Log")


def parse_entries(entries: list[str]) -> list[tuple[str, str]] | None:
    """[(WxH, 'fullscreen'|'window')], or None if one entry is not WxH or WxH:fullscreen."""
    parsed: list[tuple[str, str]] = []
    for entry in entries:
        match = ENTRY.match(entry)
        if match is None:
            return None
        parsed.append((match.group(1), match.group(2) or "window"))
    return parsed


def percentile(values: list[float], share: float) -> float:
    """Nearest-rank percentile: the smallest value with `share` of all values at or below it."""
    ordered = sorted(values)
    return ordered[max(0, math.ceil(share * len(ordered)) - 1)]


def read_frames(path: Path) -> list[float]:
    """The frame times of a pass CSV (columns pass,time_s,frame_ms); ValueError if unusable."""
    if not path.is_file():
        raise ValueError("keine CSV-Datei")
    with path.open(encoding="utf-8", newline="") as file:
        try:
            frames = [float(row["frame_ms"]) for row in csv.DictReader(file)]
        except (KeyError, TypeError, ValueError) as error:
            raise ValueError(f"CSV nicht lesbar ({type(error).__name__})") from error
    if not frames:
        raise ValueError("CSV ohne Bilder")
    return frames


def log_line(path: Path, start: str) -> dict[str, str]:
    """The key=value pairs of the last log line beginning with `start`; empty if there is none."""
    if not path.is_file():
        return {}
    found: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.startswith(start + " "):
            found = dict(part.split("=", 1) for part in line.split()[1:] if "=" in part)
    return found


def read_vram(path: Path) -> tuple[int | None, int | None, int, int, float]:
    """Baseline (median before the game), peak while it ran, samples while it ran, failed reads,
    and the seconds between the first and the last sample while it ran."""
    if not path.is_file():
        return None, None, 0, 0, 0.0
    times: list[float] = []
    base: list[int] = []
    run: list[int] = []
    errors = 0
    with path.open(encoding="utf-8", newline="") as file:
        for row in csv.DictReader(file):
            try:
                (base if row["phase"] == "baseline" else run).append(int(row["used_mib"]))
                if row["phase"] != "baseline":
                    times.append(float(row["time_s"]))
            except (KeyError, TypeError, ValueError):
                errors += 1
    baseline = int(statistics.median(base)) if base else None
    covered = max(times) - min(times) if times else 0.0
    return baseline, (max(run) if run else None), len(run), errors, covered


def judge(
    out: Path,
    resolution: str,
    mode: str,
    name: str,
    marks: Marks,
    code: int | None,
    extra: list[str] | None = None,
) -> PassResult:
    """One pass judged from its CSV, its log and its memory samples."""
    result = PassResult(resolution, mode, name)
    stem = f"{resolution}_{name}"
    if code not in (None, 0):
        result.invalid.append(f"Lauf endete mit Fehler ({code})")
    if extra:
        # Any extra scene argument changes what is measured (preset, speed, warm ticks ...): shown
        # in the report, and never the acceptance run (review GW1 #230 F2).
        result.invalid.append("zusätzliche Szenen-Argumente: " + " ".join(extra))
    try:
        frames = read_frames(out / f"bench_{stem}.csv")
    except ValueError as error:
        result.invalid.append(str(error))
        frames = []
    if frames:
        result.frames = len(frames)
        result.mean_fps = 1000.0 * len(frames) / sum(frames)
        result.p95_ms = percentile(frames, 0.95)
        result.max_ms = max(frames)
        if result.mean_fps < marks.mean_fps:
            result.missed.append(f"Mittel {result.mean_fps:.2f} unter {marks.mean_fps:g}")
        if result.p95_ms > marks.p95_ms:
            result.missed.append(f"p95 {result.p95_ms:.2f} ms über {marks.p95_ms:g} ms")

    start = log_line(out / f"bench_{stem}.log", "BENCH_START")
    done = log_line(out / f"bench_{stem}.log", "BENCH_PASS")
    result.quality = start.get("quality", "?")
    result.figures = start.get("figures", "?")
    judge_setup(result, start, done, log_line(out / f"bench_{stem}.log", "BENCH_ENV"), marks)
    # The size of the BENCH_PASS line: BENCH_START is printed right after the mode switch is asked.
    result.size = done.get("size", "?")
    if not start or not done:
        result.invalid.append("Log ohne BENCH_START/BENCH_PASS")
    else:
        if result.size != resolution:
            result.invalid.append(f"gerendert in {result.size} statt {resolution}")
        try:
            if float(start["duration_s"]) != marks.duration_s:
                result.invalid.append(
                    f"verkürzt: {start['duration_s']} s statt {marks.duration_s:g} s"
                )
            if frames and (
                int(done["frames"]) != result.frames
                or abs(float(done["mean_fps"]) - result.mean_fps) > 0.1
                or abs(float(done["p95_ms"]) - result.p95_ms) > 0.01
            ):
                result.invalid.append("Zahlen des Spiels weichen von der CSV ab")
        except (KeyError, ValueError):
            result.invalid.append("Log-Zeile unvollständig")

    (
        result.baseline_mib,
        result.peak_mib,
        result.samples,
        result.sample_errors,
        covered,
    ) = read_vram(out / f"vram_{stem}.csv")
    game = result.game_mib
    if game is None:
        result.invalid.append("Grafikspeicher nicht gemessen")
    elif result.samples < marks.duration_s / 2:
        # One sample a second is asked for: fewer than half of them do not cover the pass.
        result.invalid.append(f"Grafikspeicher nur {result.samples} Messpunkte")
    elif covered < marks.duration_s:
        # A sampler that died early has enough samples but misses the late, close camera passes.
        result.invalid.append(f"Grafikspeicher nur {covered:.0f} s lang gemessen")
    elif game > marks.vram_mib:
        result.missed.append(f"Grafikspeicher {game} MiB über {marks.vram_mib} MiB")
    return result


def report(results: list[PassResult], marks: Marks, meta: dict[str, Any]) -> str:
    """The German report for the AMW document (developer text, not shown in the game)."""
    figures = next((r.figures for r in results if r.figures != "?"), str(marks.figures))
    lines = [
        f"# B2-7 Leistungsmessung: Benchmark-Szene mit {figures} Figuren (Abnahme 5)",
        "",
        f"- **Stand:** {meta.get('note') or 'keine Angabe'}",
        f"- **Programm:** `{meta.get('binary', 'nicht bekannt')}`",
        f"- **Gemessen am:** {meta.get('started', 'nicht bekannt')}",
        f"- **Grafikkarte, Treiber:** {meta.get('card', 'nicht bekannt')}",
        "- **Laut Spiel (BENCH_ENV):** Build "
        + (", ".join(dict.fromkeys(r.build for r in results if r.build != "?")) or "?")
        + "; "
        + (", ".join(dict.fromkeys(r.driver for r in results if r.driver != "?")) or "?"),
        "- **Zusätzliche Szenen-Argumente:** "
        + (" ".join(meta.get("extra") or []) or ("keine" if "extra" in meta else "nicht bekannt")),
        f"- **Szene laut Daten:** Seed {marks.seed}, Qualität {marks.quality}, Aufwärmzeit "
        f"{marks.warmup_s:g} s. Die erste Auflösung der Liste ist die Abnahme-Auflösung.",
        f"- **Marken:** im Mittel mindestens {marks.mean_fps:g} Bilder je Sekunde, 95 % der "
        f"Bilder in höchstens {marks.p95_ms:g} ms, Grafikspeicher des Spiels höchstens "
        f"{marks.vram_mib} MiB, Messdauer {marks.duration_s:g} s je Durchgang.",
        "",
        "| Verlangt | Gerendert | Durchgang | Qualität | Bilder | Mittel (Bilder/s) | p95 (ms) "
        "| Maximum (ms) | Grafikspeicher Spiel (MiB) | Urteil | Grund |",
        "|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    for r in results:
        asked = f"{r.resolution} {'Vollbild' if r.mode == 'fullscreen' else 'Fenster'}"
        game = "nicht gemessen" if r.game_mib is None else str(r.game_mib)
        lines.append(
            f"| {asked} | {r.size} | {r.name} | {r.quality} | {r.frames} | {r.mean_fps:.2f} | "
            f"{r.p95_ms:.2f} | {r.max_ms:.2f} | {game} | {r.verdict} | "
            f"{'; '.join(r.invalid + r.missed) or NONE} |"
        )
    lines += [
        "",
        "| Verlangt | Durchgang | Modus | Aufwärm-Bilder | Grundlast Karte (MiB) "
        "| Spitze Karte (MiB) | Messpunkte | Lesefehler |",
        "|---|---|---|---|---|---|---|---|",
    ]
    lines += [
        f"| {r.resolution} | {r.name} | {r.window} | {r.warmup_frames} | "
        f"{NONE if r.baseline_mib is None else r.baseline_mib} | "
        f"{NONE if r.peak_mib is None else r.peak_mib} | {r.samples} | {r.sample_errors} |"
        for r in results
    ]
    vsync = ", ".join(dict.fromkeys(r.vsync for r in results if r.vsync != "?"))
    vsync_text = f"laut Spiel `{vsync}`." if vsync else "vom Spiel nicht gemeldet."
    lines += [
        "",
        "**Verfahren Grafikspeicher:** einmal je Sekunde die Belegung der ganzen Karte "
        "(nvidia-smi); Grundlast = mittlerer Wert der Messpunkte vor dem Start des Spiels, "
        "Spiel = Spitze minus Grundlast. Anderes, was währenddessen Speicher belegt oder "
        "freigibt, verfälscht den Wert.",
        "",
        f"**VSync:** {vsync_text}",
    ]
    first = results[0].resolution if results else ""
    for resolution in dict.fromkeys(r.resolution for r in results):
        mine = [r for r in results if r.resolution == resolution]
        good = sum(r.verdict == "erfüllt" for r in mine)
        role = "Abnahme-Auflösung" if resolution == first else "zusätzlich"
        outcome = "bestanden" if good == len(mine) else "nicht bestanden"
        lines += [
            "",
            f"**Ergebnis {resolution} ({role}):** {good} von {len(mine)} Durchgängen erfüllt, "
            f"{outcome}.",
        ]
    return "\n".join(lines) + "\n"


def exit_code(results: list[PassResult]) -> int:
    if not results or any(r.invalid for r in results):
        return EXIT_FAILED
    return EXIT_MISSED if any(r.missed for r in results) else EXIT_OK


def archive_name(path: Path) -> Path:
    """A free name next to `path` for keeping an earlier one: <name>.alt-1, .alt-2, ..."""
    for n in itertools.count(1):
        candidate = path.with_name(f"{path.name}.alt-{n}")
        if not candidate.exists():
            return candidate
    raise AssertionError("unreachable")


def guard(out: Path, options: argparse.Namespace) -> str | None:
    """Why the run must not start, or None."""
    if parse_entries(options.resolutions) is None:
        return "resolutions must be WxH or WxH:fullscreen"
    if out == ROOT or ROOT in out.parents:
        return f"output folder {out} is inside the repository"
    if options.evaluate_only:
        return None if out.is_dir() else f"{out} does not exist"
    if not options.screen_queue:
        return "a benchmark window runs only as an AMW screen-queue run (pass --screen-queue)"
    if options.binary is None or not options.binary.is_file():
        return f"binary {options.binary} not found"
    if not options.note:
        return "a run needs --note (which build is measured)"
    if out.exists() and any(out.iterdir()) and not (out / MARKER).is_file():
        return f"{out} exists, is not empty and is not a folder of this tool"
    return None


class Sampler:
    """One nvidia-smi child writing time_s,phase,used_mib,total_mib until stopped."""

    def __init__(self, path: Path, command: list[str] | None = None) -> None:
        self._path = path
        self._command = command or SMI_SAMPLES
        self._phase = "baseline"
        self._child: subprocess.Popen[str] | None = None
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        try:
            self._child = subprocess.Popen(
                self._command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True
            )
        except OSError:
            return  # no card tool: the pass is judged "not measured"
        self._thread = threading.Thread(target=self._read, daemon=True)
        self._thread.start()

    def game_starts(self) -> None:
        self._phase = "run"

    def _read(self) -> None:
        assert self._child is not None and self._child.stdout is not None
        started = time.monotonic()
        with self._path.open("w", encoding="utf-8", newline="") as file:
            writer = csv.writer(file)
            writer.writerow(["time_s", "phase", "used_mib", "total_mib"])
            for line in self._child.stdout:
                parts = [p.strip() for p in line.split(",")]
                stamp = f"{time.monotonic() - started:.1f}"
                # A line that is no reading is kept as a failed read; sampling goes on.
                good = len(parts) == 2 and all(p.isdigit() for p in parts)
                writer.writerow([stamp, self._phase, *(parts if good else ["", ""])])
                file.flush()

    def stop(self) -> None:
        if self._child is not None:
            self._child.terminate()  # only the child this tool started
            try:
                self._child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self._child.kill()
        if self._thread is not None:
            self._thread.join(timeout=10)


class ProcessGroup:
    """Everything a started benchmark starts, so that all of it can be ended (review GW1 #230 F1).

    `taskkill /T` follows parent ids, and bash running a script that calls `timeout <program>`
    breaks that chain: bash ended, the game kept the screen. On Windows a job object holds every
    process started below the child, whoever its parent is, and ends them together (also when
    this tool itself is ended: kill on close). Elsewhere the child leads its own process group.
    """

    def __init__(self) -> None:
        self._job: Any = None
        self._child: subprocess.Popen[bytes] | None = None
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes

            class Basic(ctypes.Structure):
                _fields_ = [  # noqa: RUF012
                    ("PerProcessUserTimeLimit", ctypes.c_int64),
                    ("PerJobUserTimeLimit", ctypes.c_int64),
                    ("LimitFlags", wintypes.DWORD),
                    ("MinimumWorkingSetSize", ctypes.c_size_t),
                    ("MaximumWorkingSetSize", ctypes.c_size_t),
                    ("ActiveProcessLimit", wintypes.DWORD),
                    ("Affinity", ctypes.c_size_t),
                    ("PriorityClass", wintypes.DWORD),
                    ("SchedulingClass", wintypes.DWORD),
                ]

            class Extended(ctypes.Structure):
                _fields_ = [  # noqa: RUF012
                    ("BasicLimitInformation", Basic),
                    ("IoInfo", ctypes.c_uint64 * 6),
                    ("ProcessMemoryLimit", ctypes.c_size_t),
                    ("JobMemoryLimit", ctypes.c_size_t),
                    ("PeakProcessMemoryUsed", ctypes.c_size_t),
                    ("PeakJobMemoryUsed", ctypes.c_size_t),
                ]

            self._k32 = ctypes.WinDLL("kernel32", use_last_error=True)
            self._k32.CreateJobObjectW.restype = wintypes.HANDLE
            self._k32.SetInformationJobObject.argtypes = [
                wintypes.HANDLE,
                ctypes.c_int,
                ctypes.c_void_p,
                wintypes.DWORD,
            ]
            self._k32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
            self._k32.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
            self._k32.CloseHandle.argtypes = [wintypes.HANDLE]
            self._ntdll = ctypes.WinDLL("ntdll")
            self._ntdll.NtResumeProcess.argtypes = [wintypes.HANDLE]
            self._ntdll.NtResumeProcess.restype = ctypes.c_long
            job = self._k32.CreateJobObjectW(None, None)
            info = Extended()
            info.BasicLimitInformation.LimitFlags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if job and self._k32.SetInformationJobObject(
                job, 9, ctypes.byref(info), ctypes.sizeof(info)
            ):
                self._job = job

    def start(self, command: list[str], env: dict[str, str]) -> subprocess.Popen[bytes]:
        """Starts the command inside the group; what it starts later belongs to the group too."""
        if os.name == "nt":
            # Started suspended, put into the job, then resumed: nothing it starts can be outside
            # the job (with an assignment after a running start a quick child escaped, review
            # GW1 #232 B).
            child = subprocess.Popen(command, env=env, creationflags=0x00000004)  # CREATE_SUSPENDED
            handle = int(child._handle)  # type: ignore[attr-defined]
            if self._job is not None and not self._k32.AssignProcessToJobObject(self._job, handle):
                # Without the job only the old way is left (taskkill by parent ids): say so.
                print(
                    "BENCH_MEASURE_WARN: no job object for the benchmark, ending it may leave "
                    "the game running",
                    file=sys.stderr,
                )
                self._k32.CloseHandle(self._job)
                self._job = None
            if self._ntdll.NtResumeProcess(handle) != 0:
                child.kill()
                raise OSError("the benchmark could not be resumed after its suspended start")
        else:
            child = subprocess.Popen(command, env=env, start_new_session=True)
        self._child = child
        return child

    def end(self) -> None:
        """Ends every process of the group that still runs (only what this tool started)."""
        child = self._child
        if os.name == "nt":
            if self._job is not None:
                self._k32.TerminateJobObject(self._job, 1)
                self._k32.CloseHandle(self._job)
                self._job = None
            elif child is not None and child.poll() is None:
                command = ["taskkill", "/T", "/F", "/PID", str(child.pid)]
                subprocess.run(command, capture_output=True, check=False)
                if child.poll() is None:
                    child.kill()
        elif child is not None:
            import signal

            with contextlib.suppress(ProcessLookupError, PermissionError):
                os.killpg(child.pid, signal.SIGKILL)  # type: ignore[attr-defined]
        if child is not None:
            with contextlib.suppress(subprocess.TimeoutExpired):
                child.wait(timeout=10)


def run_pass(
    bash: str, binary: Path, resolution: str, mode: str, name: str, out: Path, extra: list[str]
) -> int:
    """One benchmark.sh call for one pass, with memory samples before and during; its exit code."""
    command = [bash, str(ROOT / "tools/benchmark.sh"), str(binary), resolution, str(out)]
    command += (["--fullscreen"] if mode == "fullscreen" else []) + extra
    sampler = Sampler(out / f"vram_{resolution}_{name}.csv")
    group = ProcessGroup()
    try:
        # Everything after the sampler's start is inside the try: a stop during the baseline wait
        # or a bash that cannot start must not leave the nvidia-smi child running.
        sampler.start()
        time.sleep(BASELINE_S)
        sampler.game_starts()
        child = group.start(command, {**os.environ, "BENCH_PASSES": name})
        return child.wait(timeout=PASS_TIMEOUT_S)
    except subprocess.TimeoutExpired:
        return 124
    except OSError:
        return 127
    finally:
        group.end()
        sampler.stop()


def card() -> str:
    try:
        answer = subprocess.run(SMI_CARD, capture_output=True, text=True, timeout=15, check=True)
        return answer.stdout.strip().splitlines()[0]
    except (OSError, subprocess.SubprocessError, IndexError):
        return "nicht lesbar (nvidia-smi)"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--out", type=Path, required=True, help="folder outside the repository")
    parser.add_argument("--binary", type=Path, help="exported GlassWorld.exe (release)")
    parser.add_argument(
        "--resolutions",
        nargs="+",
        default=["3840x1600:fullscreen", "1920x1080"],
        help="WxH or WxH:fullscreen; the first is the acceptance resolution (rule KR5)",
    )
    parser.add_argument("--screen-queue", action="store_true", help="required for a run")
    parser.add_argument("--evaluate-only", action="store_true", help="judge again what is there")
    parser.add_argument("--bash", default=None, help="bash to run benchmark.sh with")
    parser.add_argument("--note", default="", help="which build is measured (required for a run)")
    parser.add_argument("extra", nargs="*", help="after --: arguments for the scene")
    options = parser.parse_args(argv)
    out = options.out.resolve()
    problem = guard(out, options)
    if problem is not None:
        print(f"BENCH_MEASURE_FAIL: {problem}", file=sys.stderr)
        return EXIT_FAILED
    marks = load_marks()
    meta_file = out / "run.json"
    meta: dict[str, Any]
    entries = parse_entries(options.resolutions) or []
    if options.evaluate_only:
        meta = json.loads(meta_file.read_text("utf-8")) if meta_file.is_file() else {}
        entries = [(str(e[0]), str(e[1])) for e in meta.get("entries", [])] or entries
    else:
        bash = options.bash or (str(GIT_BASH) if GIT_BASH.is_file() else shutil.which("bash"))
        if bash is None:
            print("BENCH_MEASURE_FAIL: no bash found (pass --bash)", file=sys.stderr)
            return EXIT_FAILED
        if out.exists():
            out.rename(archive_name(out))  # rule PR4: an earlier run is kept, never overwritten
        out.mkdir(parents=True)
        (out / MARKER).write_text("made by tools/bench/measure.py", encoding="utf-8")
        binary = options.binary.resolve()
        codes: dict[str, int] = {}
        meta = {
            "note": options.note,
            "binary": str(binary),
            "started": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
            "card": card(),
            "entries": entries,
            "extra": options.extra,
            "exit": codes,
        }
        for resolution, mode, name in [(r, m, n) for r, m in entries for n in marks.passes]:
            code = run_pass(bash, binary, resolution, mode, name, out, options.extra)
            codes[f"{resolution}_{name}"] = code
            meta_file.write_text(json.dumps(meta, indent=2), encoding="utf-8")
            if code == 124:
                # A pass over its time: whatever state screen and card are in, no further pass
                # is started on top of it. The remaining passes are reported as not measured.
                print("BENCH_MEASURE_FAIL: a pass ran over its time, stopping", file=sys.stderr)
                break
    known: dict[str, int] = meta.get("exit", {})
    results = [
        judge(
            out, resolution, mode, name, marks, known.get(f"{resolution}_{name}"), meta.get("extra")
        )
        for resolution, mode in entries
        for name in marks.passes
    ]
    target = out / "report.md"
    if target.exists():
        target.rename(archive_name(target))  # an earlier report is kept (rule PR4)
    target.write_text(report(results, marks, meta), encoding="utf-8", newline="\n")
    for r in results:
        print(
            f"BENCH_RESULT {r.resolution} {r.name} size={r.size} frames={r.frames} "
            f"mean_fps={r.mean_fps:.2f} p95_ms={r.p95_ms:.2f} max_ms={r.max_ms:.2f} "
            f"game_vram_mib={r.game_mib} verdict={r.verdict}"
        )
    return exit_code(results)


if __name__ == "__main__":
    raise SystemExit(main())
