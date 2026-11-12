"""Kennzahl "lebendig" (A1512, D710 L24): how varied the inhabitants' days are, read from a run.

    uv run python tools/liveliness/measure.py RUN_LOG [--out FOLDER]

RUN_LOG is the output of a headless run with `--events all` (e.g. lauf.log of
tools/durchstich/run_d9.py): one line per core event, `EVENT tick=.. kind=.. inhabitant=.. action=..
target=.. detail=..`. Per game day and inhabitant it counts what a viewer would call "lebendig":

- actions:      finished actions, how many different ones, and their spread (normalised Shannon
                entropy 0..1: 0 = always the same, 1 = all kinds equally often);
- talks:        finished actions with an inhabitant as target (from the action vocabulary: target
                "inhabitant" - no list in this tool);
- discoveries:  knowledge gained, names learned, first meetings;
- making:       inventions wanted, invented, built, first used;
- thoughts and questions (opened, answered, failed).

It judges nothing on its own: the numbers are a measurement for the TL and the M2 runs; a day is
only *marked* "quiet" when it has fewer distinct actions than `--quiet-below` (a reading aid, not
a mark). Ticks per day come from content/world/time.json. Nothing here changes the game.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
DISCOVERY = ("knowledge_gained", "name_learned", "first_meeting")
MAKING = ("invention_wanted", "invented", "built", "first_use")
QUESTIONS = ("question_opened", "question_answered", "question_failed")


def ticks_per_day(root: Path = ROOT) -> int:
    time = json.loads((root / "content/world/time.json").read_text("utf-8"))
    return int(time["ticks_per_hour"]) * int(time["hours_per_day"])


def social_actions(root: Path = ROOT) -> set[str]:
    """Actions whose target is another inhabitant - from the vocabulary, not from a list here."""
    data = json.loads((root / "content/vocabulary/actions.json").read_text("utf-8"))
    return {e["id"] for e in data["entries"] if e.get("target") == "inhabitant"}


def parse_events(lines: list[str]) -> list[dict[str, str]]:
    events: list[dict[str, str]] = []
    for line in lines:
        if not line.startswith("EVENT "):
            continue
        fields: dict[str, str] = {}
        for part in line.split(" ")[1:]:
            if "=" in part:
                key, value = part.split("=", 1)
                fields[key] = unquote(value)
        events.append(fields)
    return events


def spread(counts: Counter[str]) -> float:
    """Normalised Shannon entropy of the action kinds: 0 = one kind only, 1 = all equally often."""
    total = sum(counts.values())
    if total == 0 or len(counts) < 2:
        return 0.0
    h = -sum((n / total) * math.log(n / total) for n in counts.values())
    return h / math.log(len(counts))


@dataclass
class Day:
    actions: Counter[str] = field(default_factory=Counter[str])
    talks: int = 0
    discoveries: int = 0
    making: int = 0
    thoughts: int = 0
    questions: Counter[str] = field(default_factory=Counter[str])

    def row(self) -> dict[str, object]:
        return {
            "actions": sum(self.actions.values()),
            "distinct_actions": len(self.actions),
            "spread": round(spread(self.actions), 2),
            "talks": self.talks,
            "discoveries": self.discoveries,
            "making": self.making,
            "thoughts": self.thoughts,
            "questions_opened": self.questions["question_opened"],
            "questions_answered": self.questions["question_answered"],
            "questions_failed": self.questions["question_failed"],
        }


def measure(
    events: list[dict[str, str]], per_day: int, social: set[str]
) -> dict[tuple[int, str], Day]:
    days: dict[tuple[int, str], Day] = defaultdict(Day)
    for e in events:
        who = e.get("inhabitant", "-")
        if who in ("", "-"):
            continue
        day = days[(int(e.get("tick", "0")) // per_day + 1, who)]
        kind = e.get("kind", "")
        if kind == "action_finished":
            action = e.get("action", "-")
            day.actions[action] += 1
            if action in social:
                day.talks += 1
        elif kind in DISCOVERY:
            day.discoveries += 1
        elif kind in MAKING:
            day.making += 1
        elif kind == "thought":
            day.thoughts += 1
        elif kind in QUESTIONS:
            day.questions[kind] += 1
    return dict(days)


def median(values: list[float]) -> float:
    s = sorted(values)
    if not s:
        return 0.0
    mid = len(s) // 2
    return s[mid] if len(s) % 2 else (s[mid - 1] + s[mid]) / 2


def report(days: dict[tuple[int, str], Day], quiet_below: int, source: str) -> str:
    rows = {key: d.row() for key, d in sorted(days.items())}
    lines = [
        "# Kennzahl lebendig (A1512)",
        "",
        f"Quelle: `{source}`. Je Spieltag und Bewohner; „ruhig“ = weniger als {quiet_below} "
        "verschiedene Handlungen (Lesehilfe, keine Wertung).",
        "",
        "## Je Bewohner und Spieltag (Median über alle)",
        "",
        "| Messgröße | Median | Minimum | Maximum |",
        "|---|---|---|---|",
    ]
    for key in ("actions", "distinct_actions", "spread", "talks", "discoveries", "making",
                "thoughts", "questions_opened", "questions_answered"):  # fmt: skip
        values = [float(r[key]) for r in rows.values()]  # type: ignore[arg-type]
        if values:
            lines.append(f"| {key} | {median(values):g} | {min(values):g} | {max(values):g} |")
    quiet = [k for k, r in rows.items() if int(r["distinct_actions"]) < quiet_below]  # type: ignore[call-overload]
    lines += [
        "",
        f"Ruhige Tage: {len(quiet)} von {len(rows)}"
        + (": " + ", ".join(f"Tag {d} {w}" for d, w in quiet[:12]) if quiet else ""),
        "",
        "## Je Spieltag (alle Bewohner zusammen)",
        "",
        "| Tag | Bewohner | Handlungen | verschieden | Gespräche | Entdeckungen | Gemachtes "
        "| Gedanken | Fragen offen/beantwortet |",
        "|---|---|---|---|---|---|---|---|---|",
    ]
    by_day: dict[int, list[dict[str, object]]] = defaultdict(list)
    for (d, _), r in rows.items():
        by_day[d].append(r)
    for d, rs in sorted(by_day.items()):
        distinct = {a for (dd, _), day in days.items() if dd == d for a in day.actions}

        def total(key: str, rs: list[dict[str, object]] = rs) -> int:
            return sum(int(r[key]) for r in rs)  # type: ignore[call-overload]

        lines.append(
            f"| {d} | {len(rs)} | {total('actions')} | {len(distinct)} | {total('talks')} | "
            f"{total('discoveries')} | {total('making')} | {total('thoughts')} | "
            f"{total('questions_opened')}/{total('questions_answered')} |"
        )
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("run_log", type=Path, help="headless output with --events all")
    parser.add_argument("--out", type=Path, help="folder for report.md and liveliness.json")
    parser.add_argument("--quiet-below", type=int, default=3)
    options = parser.parse_args(argv)
    lines = options.run_log.read_text(encoding="utf-8", errors="replace").splitlines()
    events = parse_events(lines)
    if not events:
        print(
            "LIVELY_FAIL: no EVENT lines (run the headless runner with --events all)",
            file=sys.stderr,
        )
        return 1
    days = measure(events, ticks_per_day(), social_actions())
    text = report(days, options.quiet_below, str(options.run_log))
    if options.out is not None:
        options.out.mkdir(parents=True, exist_ok=True)
        (options.out / "report.md").write_text(text, encoding="utf-8")
        rows = [{"day": d, "inhabitant": w, **day.row()} for (d, w), day in sorted(days.items())]
        (options.out / "liveliness.json").write_text(
            json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8"
        )
    print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
