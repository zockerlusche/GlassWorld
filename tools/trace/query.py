"""Ask the run trace of a game (A1719, B2767/B2768).

Where was a thing when, what did an inhabitant do and why.

The trace is one JSON-lines file per game day next to the save
(``<world>/trace/day-NNNN.jsonl``, older days ``.jsonl.gz``). Each line has ``t``
(event, ai, snapshot, or a game line such as sound), ``tick`` and ``clock``
(``day/hh:mm``).

    python tools/trace/query.py DIR where obj-123      # every place obj-123 was seen
    python tools/trace/query.py DIR what inh-000001    # what it did, the AI's choice and reason
    python tools/trace/query.py DIR why REQUEST_ID     # the request, its options and the answer
    python tools/trace/query.py DIR kinds              # how many lines of which kind, per day
    python tools/trace/query.py DIR at 3/14:00         # the snapshot nearest to day 3, 14:00

DIR is the world folder or its trace folder. ``--from`` / ``--to`` limit the days.
"""

from __future__ import annotations

import argparse
import gzip
import json
import os
import re
import sys
from collections import Counter
from collections.abc import Iterator
from pathlib import Path
from typing import Any

SERVICE_FIELDS = (
    "role",
    "attempt",
    "outcome",
    "model",
    "prompt_version",
    "latency_ms",
    "raw_output",
    "detail",
)


def default_service_logs() -> Path | None:
    """The service's call logs (GW2 #25922/#25989): a headless run's GLASSWORLD_HEADLESS_DATA/logs
    when that is set, else the game's <userdata>/ai-service/logs; None if neither exists."""
    candidates: list[Path] = []
    if data := os.environ.get("GLASSWORLD_DATA"):
        candidates.append(Path(data) / "ai-service" / "logs")
    if headless := os.environ.get("GLASSWORLD_HEADLESS_DATA"):
        candidates.append(Path(headless) / "logs")
    if appdata := os.environ.get("APPDATA"):
        candidates.append(
            Path(appdata) / "Godot" / "app_userdata" / "GlassWorld" / "ai-service" / "logs"
        )
    return next((folder for folder in candidates if folder.is_dir()), None)


def service_calls(logs: Path, request_id: str) -> Iterator[dict[str, Any]]:
    """Every model call of the service for one request (one line per attempt, one file per role)."""
    for path in sorted(logs.glob("*.jsonl")):
        with path.open(encoding="utf-8") as handle:
            for raw in handle:
                if request_id not in raw:
                    continue
                try:
                    line = json.loads(raw)
                except json.JSONDecodeError:
                    continue
                if line.get("request_id") == request_id:
                    yield line


DAY_FILE = re.compile(r"^day-(\d+)\.jsonl(\.gz)?$")


def trace_folder(path: Path) -> Path:
    """The folder that holds the day files: the given one or its ``trace`` folder."""
    if (path / "trace").is_dir():
        return path / "trace"
    return path


def day_files(
    folder: Path, first: int | None = None, last: int | None = None
) -> list[tuple[int, Path]]:
    """The day files in day order; a day both packed and plain (a loaded world) yields both."""
    found: list[tuple[int, Path]] = []
    for path in folder.iterdir():
        match = DAY_FILE.match(path.name)
        if not match:
            continue
        day = int(match.group(1))
        if (first is None or day >= first) and (last is None or day <= last):
            found.append((day, path))
    return sorted(found, key=lambda item: (item[0], item[1].suffix != ".gz"))


def lines(
    folder: Path, first: int | None = None, last: int | None = None
) -> Iterator[dict[str, Any]]:
    """Every line in file order; a broken line (a game killed mid-write) is skipped."""
    for _, path in day_files(folder, first, last):
        opener = gzip.open if path.suffix == ".gz" else open
        with opener(path, "rt", encoding="utf-8") as handle:
            for raw in handle:
                raw = raw.strip()
                if not raw:
                    continue
                try:
                    yield json.loads(raw)
                except json.JSONDecodeError:
                    continue


def where(folder: Path, thing: str, **days: int | None) -> Iterator[str]:
    """Every place the thing was seen: events about it and snapshots of it."""
    for line in lines(folder, **days):
        if line["t"] == "snapshot":
            for item in line.get("things", []):
                if item.get("id") == thing:
                    held = f" held by {item['held_by']}" if item.get("held_by") else ""
                    yield f"{line['clock']}  snapshot  {item['type']} at {item['at']}{held}"
        elif line["t"] == "event" and thing in (
            line.get("target"),
            line.get("held"),
            line.get("worn"),
        ):
            action = line.get("action", "-")
            at_place = line.get("at")
            yield f"{line['clock']}  {line['kind']}  by {line['who']} ({action}) at {at_place}"


def what(folder: Path, who: str, **days: int | None) -> Iterator[str]:
    """What the inhabitant did: its events, and for each decision the choice and its reason."""
    asked: dict[str, str] = {}
    for line in lines(folder, **days):
        if line["t"] == "ai" and line.get("log") == "decision":
            if line.get("kind") == "Request" and line.get("who") == who:
                asked[line["request_id"]] = line["clock"]
                options = ", ".join(str(o.get("option_id")) for o in line.get("options", []))
                yield f"{line['clock']}  asked     {line['request_id']}  options: {options}"
            elif line["request_id"] in asked and line.get("option_id"):
                reason = line.get("rationale") or ""
                thought = line.get("thought") or ""
                chose = line["option_id"]
                yield f"{line['clock']}  chose     {chose}  because: {reason}  thought: {thought}"
        elif line["t"] == "event" and line.get("who") == who:
            target = f" -> {line['target']}" if line.get("target") else ""
            outcome = f" [{line['outcome']}]" if line.get("outcome") else ""
            text = f'  "{line["text"]}"' if line.get("text") else ""
            action = line.get("action", "")
            yield f"{line['clock']}  {line['kind']:<9} {action}{target}{outcome}{text}"


def why(
    folder: Path, request_id: str, logs: Path | None = None, **days: int | None
) -> Iterator[str]:
    """Every AI line of one request: what was offered, what came back; with the service's call
    logs also each model call (attempt, outcome, the model's raw answer)."""
    for line in lines(folder, **days):
        if line["t"] == "ai" and line.get("request_id") == request_id:
            yield json.dumps(line, ensure_ascii=False)
    if logs is not None:
        for call in service_calls(logs, request_id):
            yield "service " + json.dumps(
                {k: call[k] for k in SERVICE_FIELDS if k in call}, ensure_ascii=False
            )


def kinds(folder: Path, **days: int | None) -> Iterator[str]:
    """How many lines of which kind per day."""
    per_day: dict[int, Counter[str]] = {}
    for line in lines(folder, **days):
        day = int(str(line.get("clock", "0/")).split("/")[0])
        name = line["t"] if line["t"] != "event" else f"event:{line.get('kind')}"
        per_day.setdefault(day, Counter())[name] += 1
    for day in sorted(per_day):
        counts = ", ".join(f"{name} {count}" for name, count in sorted(per_day[day].items()))
        yield f"day {day}: {counts}"


def at(folder: Path, clock: str, **days: int | None) -> Iterator[str]:
    """The snapshot nearest to day/hh:mm."""
    day_text, _, time_text = clock.partition("/")
    hours, _, minutes = (time_text or "0:0").partition(":")
    wanted = int(day_text) * 1440 + int(hours) * 60 + int(minutes or 0)
    best: dict[str, Any] | None = None
    best_gap = None
    for line in lines(folder, first=int(day_text) - 1, last=int(day_text) + 1):
        if line["t"] != "snapshot":
            continue
        d, _, hm = line["clock"].partition("/")
        h, _, m = hm.partition(":")
        gap = abs(int(d) * 1440 + int(h) * 60 + int(m) - wanted)
        if best_gap is None or gap < best_gap:
            best, best_gap = line, gap
    if best is not None:
        yield json.dumps(best, ensure_ascii=False, indent=1)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("folder", type=Path)
    parser.add_argument("command", choices=["where", "what", "why", "kinds", "at"])
    parser.add_argument("subject", nargs="?")
    parser.add_argument("--from", dest="first", type=int)
    parser.add_argument("--to", dest="last", type=int)
    parser.add_argument(
        "--service-logs",
        type=Path,
        help="the AI service's call logs (default: GLASSWORLD_HEADLESS_DATA/logs, else the game's"
        " <userdata>/ai-service/logs)",
    )
    args = parser.parse_args(argv)
    folder = trace_folder(args.folder)
    if not folder.is_dir():
        print(f"no trace folder: {folder}", file=sys.stderr)
        return 2
    if args.command != "kinds" and not args.subject:
        print(f"{args.command} needs a subject", file=sys.stderr)
        return 2
    days = {"first": args.first, "last": args.last}
    commands = {
        "where": lambda: where(folder, args.subject, **days),
        "what": lambda: what(folder, args.subject, **days),
        "why": lambda: why(
            folder, args.subject, args.service_logs or default_service_logs(), **days
        ),
        "kinds": lambda: kinds(folder, **days),
        "at": lambda: at(folder, args.subject),
    }
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
    for text in commands[args.command]():
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
