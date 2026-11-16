"""AI throughput of game runs at several speeds (A1588, B7 M1, D1161 S5), from each run's trace.

A run is a player-like game run with the live AI
(`GlassWorld.exe --new-world=SEED --speed=S --run-s=N`, A1588); its world's run trace (A1719)
holds every AI line. Per run this counts, per game hour, the decisions asked and answered, how
long an answer took in game minutes (median, p95), what was dropped and why, and the talks asked
and answered with words; the frame rate comes from the game's RUN_FPS line. The report (German,
for Marcel) states numbers, it judges nothing.

Usage: python tools/bench/tempo_report.py RUN_DIR [RUN_DIR ...] [--out report.md]
A RUN_DIR holds the world's trace folder below it and the game's log (`*.log`).
"""

from __future__ import annotations

import argparse
import gzip
import json
import re
import statistics
import sys
from collections import Counter
from pathlib import Path
from typing import Any, cast

Json = dict[str, Any]
TICKS_PER_HOUR = 60  # content/world/time.json
DAY_FILE = re.compile(r"^day-\d+\.jsonl(\.gz)?$")
FPS = re.compile(r"RUN_FPS speed=(\S+) frames=(\d+) mean_fps=(\S+) p95_ms=(\S+) max_ms=(\S+)")


def ai_lines(run: Path) -> list[Json]:
    files = sorted(p for p in run.rglob("day-*.jsonl*") if DAY_FILE.match(p.name))
    lines: list[Json] = []
    for path in files:
        opener = gzip.open if path.suffix == ".gz" else open
        with opener(path, "rt", encoding="utf-8") as handle:
            for raw in handle:
                try:
                    line = cast(Json, json.loads(raw))
                except json.JSONDecodeError:
                    continue
                if line.get("t") == "ai":
                    lines.append(line)
    return lines


def fps_of(run: Path) -> dict[str, str]:
    for log in sorted(run.rglob("*.log")):
        if match := FPS.search(log.read_text(encoding="utf-8", errors="replace")):
            speed, frames, mean, p95, peak = match.groups()
            return {
                "speed": speed,
                "frames": frames,
                "mean_fps": mean,
                "p95_ms": p95,
                "max_ms": peak,
            }
    return {}


def summary(run: Path) -> Json:
    lines = ai_lines(run)
    ticks = [int(line["tick"]) for line in lines]
    hours = (max(ticks) - min(ticks)) / TICKS_PER_HOUR if len(ticks) > 1 else 0.0
    asked: dict[str, int] = {}
    waits: list[float] = []
    kinds: Counter[str] = Counter()
    notes: Counter[str] = Counter()
    talks_asked = talks_done = 0
    for line in lines:
        kind, log, tick = str(line.get("kind")), str(line.get("log")), int(line["tick"])
        rid = str(line.get("request_id", ""))
        if log == "decision":
            kinds[kind] += 1
            if kind == "Request":
                asked[rid] = tick
            elif kind == "Response" and rid in asked:
                waits.append((tick - asked[rid]) * 60 / TICKS_PER_HOUR)
            elif kind in ("Discarded", "Error"):
                notes[str(line.get("note") or kind)] += 1
        elif log == "job":
            if kind == "dialogue":
                talks_asked += 1
            elif kind == "JobResult" and '"dialogue"' in json.dumps(line.get("answer", {}))[:400]:
                talks_done += 1
            elif kind in ("JobDiscarded", "JobError"):
                notes[str(line.get("note") or kind)] += 1

    def per_hour(n: int) -> float:
        return n / hours if hours else 0.0

    return {
        "run": run.name,
        "game_hours": round(hours, 1),
        "decisions_asked_per_h": round(per_hour(kinds["Request"]), 2),
        "decisions_answered_per_h": round(per_hour(kinds["Response"]), 2),
        "answer_min_median": round(statistics.median(waits), 1) if waits else None,
        "answer_min_p95": round(sorted(waits)[max(0, int(0.95 * len(waits)) - 1)], 1)
        if waits
        else None,
        "dropped": dict(notes),
        "talks_asked": talks_asked,
        "talks_with_words": talks_done,
        "fps": fps_of(run),
    }


def report(rows: list[Json]) -> str:
    out = [
        "# KI-Durchsatz und Bildrate je Tempo (A1588)",
        "",
        "| Lauf | Tempo | Spielstunden | Fragen/h | Antworten/h | Antwort (Spielmin, Median/p95) "
        "| Gespräche (gefragt/mit Worten) | verworfen/zu spät | Bilder/s (p95 ms) |",
        "|---|---|---|---|---|---|---|---|---|",
    ]
    for r in rows:
        fps = r["fps"]
        dropped = ", ".join(f"{k} {v}" for k, v in sorted(r["dropped"].items())) or "-"
        out.append(
            f"| {r['run']} | {fps.get('speed', '?')} | {r['game_hours']} | "
            f"{r['decisions_asked_per_h']} | "
            f"{r['decisions_answered_per_h']} | {r['answer_min_median']} / {r['answer_min_p95']} | "
            f"{r['talks_asked']} / {r['talks_with_words']} | {dropped} | "
            f"{fps.get('mean_fps', '?')} ({fps.get('p95_ms', '?')}) |"
        )
    return "\n".join(out) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("runs", type=Path, nargs="+")
    parser.add_argument("--out", type=Path)
    options = parser.parse_args(argv)
    rows = [summary(run) for run in cast(list[Path], options.runs)]
    text = report(rows)
    if options.out:
        cast(Path, options.out).write_text(text, encoding="utf-8", newline="\n")
    print(text)
    print(json.dumps(rows, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
