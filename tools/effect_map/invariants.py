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
from itertools import pairwise
from pathlib import Path
from typing import Any, cast

sys.path.insert(0, str(Path(__file__).resolve().parent))
from check_run import is_daytime, load, trace_lines  # a tool beside check_run.py, not a package

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


def min_sleep_hours_mean(trace: Trace, invariant: Json) -> tuple[bool | None, str]:
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
        return None, f"fewer than {window + 2} days in the run (not checked)"
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


def _series(trace: Trace, field: str) -> dict[str, list[tuple[Json, Json]]]:
    """Per inhabitant its snapshots (snapshot, inhabitant) that carry the field (older traces do not)."""
    rows: dict[str, list[tuple[Json, Json]]] = defaultdict(list)
    for snap, who in trace.inhabitants():
        if field in who:
            rows[str(who["id"])].append((snap, who))
    return rows


def max_hours_mood_above(trace: Trace, invariant: Json) -> tuple[bool | None, str]:
    rows = _series(trace, "moods")
    if not rows:
        return None, "no moods in the snapshots (trace from before GW4 #554)"
    above, limit = float(invariant["above"]), float(invariant["hours"])
    worst = (0, "-", "-")
    for ident, seq in rows.items():
        streak: dict[str, int] = defaultdict(int)
        for _, who in seq:
            for mood, value in cast(Json, who["moods"]).items():
                streak[mood] = streak[mood] + 1 if float(value) > above else 0
                if streak[mood] > worst[0]:
                    worst = (streak[mood], ident, mood)
    hours, ident, mood = worst
    # n snapshots in a row above = at least n - 1 hours above (hourly snapshots).
    return max(
        0, hours - 1
    ) <= limit, f"longest above {above}: {max(0, hours - 1)} h ({mood}, {ident}); limit {limit:g} h"


def cold_frustration_needs_a_second_need(trace: Trace, invariant: Json) -> tuple[bool | None, str]:
    rows = _series(trace, "moods")
    if not rows:
        return None, "no moods in the snapshots (trace from before GW4 #554)"
    mood, cold = str(invariant["mood"]), str(invariant["cold_need"])
    cold_below, second_below, rise = (
        float(invariant["cold_below"]),
        float(invariant["second_below"]),
        float(invariant["rise"]),
    )
    seconds = [str(n) for n in cast(list[str], invariant["second_needs"])]
    cases = alone = 0
    example = ""
    for ident, seq in rows.items():
        for (_, before), (snap, now) in pairwise(seq):
            needs = cast(Json, now.get("needs", {}))
            grew = float(cast(Json, now["moods"]).get(mood, 0)) - float(
                cast(Json, before["moods"]).get(mood, 0)
            )
            if grew > rise and float(needs.get(cold, 1)) < cold_below:
                cases += 1
                if not any(float(needs.get(n, 1)) < second_below for n in seconds):
                    alone += 1
                    example = example or f"{ident} at {snap['clock']}"
    return alone == 0, f"{mood} rose while cold {cases}x, {alone}x with no second need low" + (
        f" (e.g. {example})" if example else ""
    )


def traits_never_change(trace: Trace, _: Json) -> tuple[bool | None, str]:
    rows = _series(trace, "traits")
    if not rows:
        return None, "no traits in the snapshots (trace from before GW4 #554)"
    changed = [
        ident
        for ident, seq in rows.items()
        if len({json.dumps(w["traits"], sort_keys=True) for _, w in seq}) > 1
    ]
    return not changed, f"{len(rows)} inhabitants" + (
        f"; changed: {', '.join(changed[:3])}" if changed else "; every character stayed"
    )


def will_changes_only_at_night(trace: Trace, invariant: Json) -> tuple[bool | None, str]:
    rows = _series(trace, "will_to_live")
    if not rows:
        return None, "no will to live in the snapshots (trace from before GW4 #554)"
    body = [str(n) for n in cast(list[str], invariant["body_needs"])]
    met = float(invariant["met_above"])
    by_day_changes = 0
    sank_while_met = 0
    for seq in rows.values():
        day_needs: dict[int, list[float]] = defaultdict(list)
        for (_, a), (snap_b, b) in pairwise(seq):
            if float(a["will_to_live"]) != float(b["will_to_live"]) and is_daytime(
                day_of(snap_b), str(snap_b["clock"])
            ):
                by_day_changes += 1
        for snap, who in seq:
            needs = cast(Json, who.get("needs", {}))
            day_needs[day_of(snap)].append(min(float(needs.get(n, 1)) for n in body))
        first_of_day = {day_of(s): float(w["will_to_live"]) for s, w in reversed(seq)}
        for day, values in day_needs.items():
            if (
                day + 1 in first_of_day
                and sum(values) / len(values) > met
                and first_of_day[day + 1] < first_of_day[day]
            ):
                sank_while_met += 1
    return by_day_changes == 0 and sank_while_met == 0, (
        f"changes by day: {by_day_changes}; sank over a night after a day with body needs met: {sank_while_met}"
    )


CHECKS = {
    "values_in_range": values_in_range,
    "max_same_failure_per_day": max_same_failure_per_day,
    "max_action_changes_per_hour": max_action_changes_per_hour,
    "min_sleep_hours_mean": min_sleep_hours_mean,
    "max_idle_minutes": max_idle_minutes,
    "invented_things_have_an_event": invented_things_have_an_event,
    "max_hours_mood_above": max_hours_mood_above,
    "cold_frustration_needs_a_second_need": cold_frustration_needs_a_second_need,
    "traits_never_change": traits_never_change,
    "will_changes_only_at_night": will_changes_only_at_night,
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
        # Review GW4 #540: a check that could not look (run too short) is n/a, not ok.
        results.append((ident, "n/a" if ok is None else "ok" if ok else "FAIL", text, detail))
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
