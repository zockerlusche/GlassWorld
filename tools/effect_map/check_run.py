"""Plausibility check of a run against expectations as data (A1720 part 2, B2769, B2772).

Reads ``tools/effect_map/expectations.json``: expectations on the run trace (A1719, one JSON-lines file per
game day with hourly snapshots of every inhabitant's needs and action) and on the data itself (the mood
driver rule). Prints one line per expectation - ok or a finding with the numbers - for the night report.

Usage: python tools/effect_map/check_run.py WORLD_OR_TRACE_DIR [--expectations FILE]
Exit code 1 when an expectation fails.
"""

from __future__ import annotations

import argparse
import gzip
import json
import re
import sys
from collections import defaultdict
from collections.abc import Iterator
from pathlib import Path
from typing import Any, cast

ROOT = Path(__file__).resolve().parents[2]
CONTENT = ROOT / "content"
EXPECTATIONS = Path(__file__).with_name("expectations.json")
DAY_FILE = re.compile(r"^day-(\d+)\.jsonl(\.gz)?$")

Json = dict[str, Any]


def load(relative: str) -> Json:
    return cast(Json, json.loads((CONTENT / relative).read_text(encoding="utf-8")))


def snapshots(folder: Path) -> Iterator[Json]:
    """Every snapshot line of the trace in day order (a broken line is skipped)."""
    folder = folder / "trace" if (folder / "trace").is_dir() else folder
    files = sorted((int(m.group(1)), p) for p in folder.iterdir() if (m := DAY_FILE.match(p.name)))
    for _, path in files:
        opener = gzip.open if path.suffix == ".gz" else open
        with opener(path, "rt", encoding="utf-8") as handle:
            for raw in handle:
                try:
                    line = cast(Json, json.loads(raw))
                except json.JSONDecodeError:
                    continue
                if line.get("t") == "snapshot":
                    yield line


class Run:
    """The snapshots of a run: per inhabitant the needs and action at each snapshot."""

    def __init__(self, folder: Path) -> None:
        self.rows: list[tuple[int, str, str | None, dict[str, float]]] = []
        # Per row the game hours since the same inhabitant's last snapshot (review GW3 #461: from the ticks, so a
        # changing population, a partial first or last day or a missing day file do not distort it); 0 = first.
        self.hours: list[float] = []
        # Per row whether it lies between sunrise and sunset of its season (time.json), for day-only expectations.
        self.daytime: list[bool] = []
        ticks_per_hour = float(load("world/time.json")["ticks_per_hour"])
        last: dict[str, int] = {}
        for line in snapshots(folder):
            day = int(str(line["clock"]).partition("/")[0])
            tick = int(line.get("tick", 0))
            for who in cast(list[Json], line.get("inhabitants", [])):
                needs = {k: float(v) for k, v in cast(Json, who.get("needs", {})).items()}
                ident = str(who["id"])
                self.rows.append((day, ident, cast(str | None, who.get("action")), needs))
                self.daytime.append(is_daytime(day, str(line["clock"])))
                self.hours.append((tick - last[ident]) / ticks_per_hour if ident in last else 0.0)
                last[ident] = tick
        steps = sorted(h for h in self.hours if h > 0)
        # The usual time between two snapshots; a longer step is a gap in the trace.
        self.interval = steps[len(steps) // 2] if steps else 1.0


def is_daytime(day: int, clock: str) -> bool:
    """Whether day/hh:mm lies between sunrise and sunset of the day's season (time.json)."""
    time = load("world/time.json")
    season = next(s for s in cast(list[Json], time["seasons"]) if s["id"] == season_of(day))
    hours, _, minutes = clock.partition("/")[2].partition(":")
    minute = int(hours) * 60 + int(minutes or 0)
    return int(season["sunrise_minute"]) <= minute < int(season["sunset_minute"])


def season_of(day: int) -> str:
    time = load("world/time.json")
    seasons = [str(s["id"]) for s in cast(list[Json], time["seasons"])]
    per = int(time["days_per_season"])
    return seasons[((day - 1) // per) % len(seasons)] if day > 0 else seasons[0]


def check_trace(run: Run, expectation: Json, model: Json) -> tuple[bool, str]:
    kind = expectation["kind"]
    needs = {str(n["id"]): n for n in cast(list[Json], model["needs"])}
    if not run.rows:
        return False, "no snapshots in the trace"
    if kind == "max_hours_below_neglect":
        limit = float(expectation["hours"])
        longest: dict[tuple[str, str], float] = defaultdict(float)
        current: dict[tuple[str, str], float] = defaultdict(float)
        for (_, who, _, values), step in zip(run.rows, run.hours, strict=True):
            # A gap (a missing day file, a cut) ends the streak: what happened in it is unknown (review GW3 #461).
            gap = step > 1.5 * run.interval
            for need, value in values.items():
                below = float(needs.get(need, {}).get("neglect_below", 0))
                key = (who, need)
                if value >= below or gap:
                    current[key] = 0.0
                else:
                    current[key] += step
                longest[key] = max(longest[key], current[key])
        worst = max(longest.items(), key=lambda kv: kv[1], default=(("-", "-"), 0.0))
        (who, need), hours = worst
        return hours <= limit, f"longest neglect {hours:.0f} h ({need}, {who}); limit {limit:.0f} h"
    if kind == "need_range_in_season":
        need, season = str(expectation["need"]), str(expectation["season"])
        values = [v[need] for day, _, _, v in run.rows if need in v and season_of(day) == season]
        if not values:
            return True, f"no {season} days in the run (not checked)"
        spread = max(values) - min(values)
        return spread >= float(expectation["min_range"]), (
            f"{need} in {season} from {min(values):.2f} to {max(values):.2f} "
            f"(range {spread:.2f}, at least {expectation['min_range']})"
        )
    if kind == "max_share_doing_while_need_above":
        action, need = str(expectation["action"]), str(expectation["need"])
        daytime_only = bool(expectation.get("daytime_only"))
        doing = [
            v
            for (_, _, a, v), day in zip(run.rows, run.daytime, strict=True)
            if a == action and need in v and (day or not daytime_only)
        ]
        if not doing:
            return True, f"nobody did {action}{' by day' if daytime_only else ''} (not checked)"
        share = sum(1 for v in doing if v[need] >= float(expectation["above"])) / len(doing)
        return share <= float(expectation["max_share"]), (
            f"{action}{' by day' if daytime_only else ''} while {need} >= {expectation['above']}: {share:.0%} "
            f"(at most {float(expectation['max_share']):.0%})"
        )
    if kind == "decaying_needs_change":
        stuck: list[str] = []
        by_need: dict[str, list[float]] = defaultdict(list)
        for _, _, _, values in run.rows:
            for need, value in values.items():
                by_need[need].append(value)
        for need, data in needs.items():
            decays = float(data.get("decay_per_hour", 0)) > 0 or data.get("decay_when") is not None
            if (
                decays
                and by_need.get(need)
                and max(by_need[need]) - min(by_need[need]) < float(expectation["min_range"])
            ):
                stuck.append(need)
        return not stuck, (
            "every decaying need moves" if not stuck else f"stuck: {', '.join(stuck)}"
        )
    return False, f"unknown kind {kind}"


def check_data(expectation: Json, model: Json) -> tuple[bool, str]:
    if expectation["kind"] == "mood_driver_lift_per_day":
        moods = {str(m["id"]): m for m in cast(list[Json], model["moods"])}
        weak: list[str] = []
        for driver in cast(list[Json], model["mood_drivers"]):
            mood = moods.get(str(driver["mood"]))
            if mood is None:
                continue
            lift = (float(driver["delta_per_hour"]) - float(mood["return_per_hour"])) * 24
            if float(driver["delta_per_hour"]) > 0 and lift < float(expectation["min_lift"]):
                cause = driver.get("need") or driver.get("sensation")
                weak.append(f"{driver['mood']} via {cause} ({lift:+.2f}/day)")
        return not weak, (
            "every driver lifts its mood enough" if not weak else "too weak: " + "; ".join(weak)
        )
    return False, f"unknown kind {expectation['kind']}"


def check(folder: Path | None, expectations: Json) -> list[tuple[str, bool, str]]:
    model = load("inhabitants/model.json")
    results: list[tuple[str, bool, str]] = []
    for expectation in cast(list[Json], expectations.get("data", [])):
        ok, text = check_data(expectation, model)
        results.append((str(expectation["id"]), ok, text))
    if folder is not None:
        run = Run(folder)
        for expectation in cast(list[Json], expectations.get("trace", [])):
            ok, text = check_trace(run, expectation, model)
            results.append((str(expectation["id"]), ok, text))
    return results


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path, nargs="?", help="world folder or its trace folder")
    parser.add_argument("--expectations", type=Path, default=EXPECTATIONS)
    args = parser.parse_args(argv)
    expectations = cast(Json, json.loads(cast(Path, args.expectations).read_text(encoding="utf-8")))
    results = check(cast(Path | None, args.folder), expectations)
    for name, ok, text in results:
        print(f"{'ok  ' if ok else 'FAIL'}  {name}: {text}")
    return 0 if all(ok for _, ok, _ in results) else 1


if __name__ == "__main__":
    sys.exit(main())
