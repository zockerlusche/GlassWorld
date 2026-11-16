"""Invariants of the parameter core on a run trace (A1808, B2833, D1202 section 3).

Reads ``tools/effect_map/invariants.json`` and checks each invariant the run trace (A1719) can show today:
one line per invariant - ok, FAIL with the numbers, or n/a with what the trace is missing - for the night
report. The limits are data (placeholders for the TL); nothing here decides what an inhabitant should do.

Usage: python tools/effect_map/invariants.py WORLD_OR_TRACE_DIR [--invariants FILE] [--json]
Exit code 1 when an invariant fails (n/a does not fail).
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, cast

sys.path.insert(0, str(Path(__file__).resolve().parent))
from check_run import load, trace_lines  # a tool beside check_run.py, not a package

INVARIANTS = Path(__file__).with_name("invariants.json")

Json = dict[str, Any]
Result = tuple[str, str, str, str]  # id, status (ok / FAIL / n/a), text, detail


def day_of(line: Json) -> int:
    return int(str(line["clock"]).partition("/")[0])


class Trace:
    """The snapshots and events of a run, read once."""

    def __init__(self, folder: Path) -> None:
        self.snapshots: list[Json] = []
        self.events: list[Json] = []
        for line in trace_lines(folder):
            if line.get("t") == "snapshot":
                self.snapshots.append(line)
            elif line.get("t") == "event":
                self.events.append(line)
        self.ticks_per_hour = float(load("world/time.json")["ticks_per_hour"])

    def inhabitants(self) -> list[tuple[Json, Json]]:
        """(snapshot, inhabitant) pairs in order."""
        return [(s, w) for s in self.snapshots for w in cast(list[Json], s.get("inhabitants", []))]


def values_in_range(trace: Trace, _: Json) -> tuple[bool, str]:
    bad: list[str] = []
    for snap, who in trace.inhabitants():
        values = dict(cast(Json, who.get("needs", {})))
        values |= {f"at[{i}]": v for i, v in enumerate(cast(list[float], who.get("at", [])))}
        for name, value in values.items():
            number = float(value)
            out = not math.isfinite(number) or (not name.startswith("at[") and not 0 <= number <= 1)
            if out:
                bad.append(f"{who['id']} {name}={value} at {snap['clock']}")
    checked = len(trace.inhabitants())
    return not bad, f"{checked} inhabitant snapshots" + (
        f"; {len(bad)} out: {bad[0]}" if bad else ""
    )


def max_same_failure_per_day(trace: Trace, invariant: Json) -> tuple[bool, str]:
    failures = cast(Json, load("decider/settings.json").get("failures") or {})
    kinds = {str(k) for k in cast(list[str], failures.get("event_kinds", []))}
    transient = {str(r) for r in cast(list[str], failures.get("transient_reasons", []))}
    counts: Counter[tuple[str, str, str, int]] = Counter()
    for event in trace.events:
        if event.get("kind") not in kinds or not event.get("action"):
            continue
        reason = str(event.get("detail") or "").partition("/")[0]
        if reason in transient:
            continue  # woken, resting ... - not a failed attempt (A1778 data)
        thing = str(event.get("target") or event.get("target_type") or "-")
        counts[(str(event["who"]), str(event["action"]), thing, day_of(event))] += 1
    limit = int(invariant["limit"])
    worst = counts.most_common(1)
    if not worst:
        return True, "no failed actions in the run"
    (who, action, thing, day), count = worst[0]
    over = sum(1 for c in counts.values() if c > limit)
    return over == 0, f"most: {count}x {action} on {thing} ({who}, day {day}); {over} over {limit}"


def max_action_changes_per_hour(trace: Trace, invariant: Json) -> tuple[bool, str]:
    per_hour: Counter[tuple[str, int]] = Counter()
    sequence: dict[str, list[tuple[int, str]]] = defaultdict(list)
    for event in trace.events:
        if event.get("kind") != "action_started":
            continue
        who, tick = str(event["who"]), int(event["tick"])
        per_hour[(who, int(tick // trace.ticks_per_hour))] += 1
        sequence[who].append((tick, f"{event.get('action')}>{event.get('target') or '-'}"))
    limit = int(invariant["limit"])
    flips = 0
    for steps in sequence.values():
        for i in range(len(steps) - 3):
            (t0, a), (_, b), (_, c), (t3, d) = steps[i : i + 4]
            if a == c and b == d and a != b and t3 - t0 <= trace.ticks_per_hour:
                flips += 1
    most = max(per_hour.values(), default=0)
    over = sum(1 for c in per_hour.values() if c > limit)
    return over == 0 and flips == 0, (
        f"most action starts in one hour: {most} (limit {limit}, {over} hours over; plans are not in the trace, "
        f"so action starts stand in); A-B-A-B within an hour: {flips}"
    )


def min_sleep_hours_mean(trace: Trace, invariant: Json) -> tuple[bool, str]:
    hours = float(invariant["hours"])
    window = int(invariant["days"])
    slept: dict[str, dict[int, float]] = defaultdict(lambda: defaultdict(float))
    seen: dict[str, set[int]] = defaultdict(set)
    step = 1.0  # one snapshot per game hour (A1719)
    for snap, who in trace.inhabitants():
        day = day_of(snap)
        seen[str(who["id"])].add(day)
        if who.get("action") == "sleep":
            slept[str(who["id"])][day] += step
    worst: tuple[float, str, int] | None = None
    for ident, days in seen.items():
        # Whole days only: the first and the last day of a run are cut.
        full = sorted(days)[1:-1]
        for i in range(len(full) - window + 1):
            span = full[i : i + window]
            mean = sum(slept[ident][d] for d in span) / window
            if worst is None or mean < worst[0]:
                worst = (mean, ident, span[0])
    if worst is None:
        return True, f"fewer than {window + 2} days in the run (not checked)"
    mean, ident, first = worst
    return (
        mean >= hours,
        f"lowest {window}-day mean {mean:.1f} h ({ident}, from day {first}); at least {hours:.0f} h",
    )


def max_idle_minutes(trace: Trace, invariant: Json) -> tuple[bool, str]:
    # Hourly snapshots: an inhabitant without an action (asleep is the action 'sleep') in two snapshots in a row
    # stood more than an hour.
    run: dict[str, int] = defaultdict(int)
    longest: dict[str, int] = defaultdict(int)
    for _, who in trace.inhabitants():
        ident = str(who["id"])
        action = who.get("action")
        run[ident] = run[ident] + 1 if action is None else 0  # asleep is the action 'sleep'
        longest[ident] = max(longest[ident], run[ident])
    ident, count = max(longest.items(), key=lambda kv: kv[1], default=("-", 0))
    # One snapshot without an action may be a moment between two; two in a row are more than an hour - over any
    # limit below 60 min. A finer check needs the events (an action starts only after the walk, A1719).
    return count < 2, (
        f"longest without an action: {count} hourly snapshot(s) in a row ({ident}); limit {invariant['minutes']} min"
    )


def invented_things_have_an_event(trace: Trace, invariant: Json) -> tuple[bool, str]:
    prefix = str(invariant["prefix"])
    kinds = {str(k) for k in cast(list[str], invariant["events"])}
    types = {
        str(t["type"])
        for s in trace.snapshots
        for t in cast(list[Json], s.get("things", []))
        if str(t.get("type", "")).startswith(prefix)
    }
    logged = {
        str(e.get(k))
        for e in trace.events
        if e.get("kind") in kinds
        for k in ("target", "target_type")
        if e.get(k)
    }
    missing = sorted(types - logged)
    return not missing, f"{len(types)} new kinds in the world" + (
        f"; without an event: {', '.join(missing[:3])}" if missing else ""
    )


CHECKS = {
    "values_in_range": values_in_range,
    "max_same_failure_per_day": max_same_failure_per_day,
    "max_action_changes_per_hour": max_action_changes_per_hour,
    "min_sleep_hours_mean": min_sleep_hours_mean,
    "max_idle_minutes": max_idle_minutes,
    "invented_things_have_an_event": invented_things_have_an_event,
}


def check(folder: Path, invariants: Json) -> list[Result]:
    trace = Trace(folder)
    results: list[Result] = []
    for invariant in cast(list[Json], invariants["invariants"]):
        ident, text, kind = str(invariant["id"]), str(invariant["text"]), str(invariant["kind"])
        if kind == "not_measurable":
            results.append((ident, "n/a", text, str(invariant["missing"])))
            continue
        if not trace.snapshots:
            results.append((ident, "FAIL", text, "no snapshots in the trace"))
            continue
        ok, detail = CHECKS[kind](trace, invariant)
        results.append((ident, "ok" if ok else "FAIL", text, detail))
    return results


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("folder", type=Path, help="world folder or its trace folder")
    parser.add_argument("--invariants", type=Path, default=INVARIANTS)
    parser.add_argument("--json", action="store_true", help="one JSON object instead of lines")
    args = parser.parse_args(argv)
    invariants = cast(Json, json.loads(cast(Path, args.invariants).read_text(encoding="utf-8")))
    results = check(cast(Path, args.folder), invariants)
    if args.json:
        print(
            json.dumps([{"id": i, "status": s, "text": t, "detail": d} for i, s, t, d in results])
        )
    else:
        for ident, status, text, detail in results:
            print(f"{status:<4}  {ident} {text}: {detail}")
    return 1 if any(status == "FAIL" for _, status, _, _ in results) else 0


if __name__ == "__main__":
    sys.exit(main())
