"""A1719: the trace query answers where, what, why, kinds and at from day files, packed or plain."""

from __future__ import annotations

import gzip
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from query import at, kinds, main, what, where, why


def write_day(folder: Path, day: int, rows: list[dict[str, object]], packed: bool = False) -> None:
    text = "".join(json.dumps(r) + "\n" for r in rows)
    if packed:
        with gzip.open(folder / f"day-{day:04d}.jsonl.gz", "wt", encoding="utf-8") as handle:
            handle.write(text)
    else:
        (folder / f"day-{day:04d}.jsonl").write_text(text + "{broken", encoding="utf-8")


def make_trace(tmp_path: Path) -> Path:
    folder = tmp_path / "trace"
    folder.mkdir()
    snapshot = {
        "t": "snapshot",
        "tick": 10,
        "clock": "1/01:00",
        "inhabitants": [],
        "things": [{"id": "obj-7", "type": "stone", "at": [1.0, 2.0]}],
    }
    request = {
        "t": "ai",
        "tick": 11,
        "clock": "1/01:05",
        "log": "decision",
        "kind": "Request",
        "request_id": "r1",
        "who": "inh-1",
        "options": [{"option_id": "o1"}, {"option_id": "o2"}],
    }
    answer = {
        "t": "ai",
        "tick": 12,
        "clock": "1/01:06",
        "log": "decision",
        "kind": "Response",
        "request_id": "r1",
        "option_id": "o2",
        "rationale": "thirsty",
        "thought": "water",
    }
    event = {
        "t": "event",
        "tick": 13,
        "clock": "1/01:07",
        "kind": "action_started",
        "who": "inh-1",
        "action": "pick_up",
        "target": "obj-7",
        "at": [1.0, 2.0],
    }
    write_day(folder, 1, [snapshot, request, answer, event], packed=True)
    later = {
        "t": "snapshot",
        "tick": 300,
        "clock": "2/03:00",
        "inhabitants": [],
        "things": [{"id": "obj-7", "type": "stone", "at": [4.0, 5.0], "held_by": "inh-1"}],
    }
    write_day(folder, 2, [later])
    return tmp_path


def test_where_follows_a_thing_through_packed_and_plain_days(tmp_path: Path) -> None:
    found = list(where(make_trace(tmp_path) / "trace", "obj-7"))
    assert len(found) == 3
    assert found[1].startswith("1/01:07  action_started  by inh-1")
    assert "held by inh-1" in found[2]


def test_what_shows_the_choice_and_its_reason(tmp_path: Path) -> None:
    found = list(what(make_trace(tmp_path) / "trace", "inh-1"))
    assert "options: o1, o2" in found[0]
    assert "chose     o2  because: thirsty" in found[1]
    assert "pick_up -> obj-7" in found[2]


def test_why_kinds_and_at(tmp_path: Path) -> None:
    folder = make_trace(tmp_path) / "trace"
    assert len(list(why(folder, "r1"))) == 2
    assert next(iter(kinds(folder))) == "day 1: ai 2, event:action_started 1, snapshot 1"
    assert '"held_by": "inh-1"' in next(at(folder, "2/02:30"))
    assert main([str(tmp_path), "kinds", "--from", "2"]) == 0


def test_why_joins_the_service_calls_by_request_id(tmp_path: Path) -> None:
    folder = make_trace(tmp_path) / "trace"
    logs = tmp_path / "logs"
    logs.mkdir()
    calls = [
        {
            "request_id": "r1",
            "role": "decider",
            "attempt": 1,
            "outcome": "invalid",
            "raw_output": "x",
        },
        {"request_id": "r1", "role": "decider", "attempt": 2, "outcome": "ok", "raw_output": "{}"},
        {"request_id": "r2", "role": "decider", "attempt": 1, "outcome": "ok"},
    ]
    (logs / "decider.jsonl").write_text(
        "".join(json.dumps(c) + "\n" for c in calls), encoding="utf-8"
    )
    found = list(why(folder, "r1", logs))
    assert len(found) == 4
    assert found[2].startswith("service ") and '"outcome": "invalid"' in found[2]
