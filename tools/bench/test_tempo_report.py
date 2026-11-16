"""A1588: the tempo report counts decisions per game hour, answer time, talks and FPS."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
from types import ModuleType
from typing import Any


def _module() -> ModuleType:
    path = Path(__file__).with_name("tempo_report.py")
    spec = importlib.util.spec_from_file_location("tempo_report", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


TR = _module()


def _ai(tick: int, log: str, kind: str, rid: str, **more: Any) -> str:
    return json.dumps(
        {"t": "ai", "tick": tick, "log": log, "kind": kind, "request_id": rid, **more}
    )


def test_a_run_gives_rates_per_game_hour_answer_times_talks_and_fps(tmp_path: Path) -> None:
    trace = tmp_path / "log" / "welten" / "run-1" / "trace"
    trace.mkdir(parents=True)
    lines = [
        _ai(0, "decision", "Request", "a"),
        _ai(30, "decision", "Response", "a"),
        _ai(60, "decision", "Request", "b"),
        _ai(70, "decision", "Discarded", "b", note="nothing_to_pass"),
        _ai(90, "job", "dialogue", "t1"),
        _ai(110, "job", "JobResult", "t1", answer={"result": {"dialogue": {"turns": []}}}),
        _ai(120, "job", "JobDiscarded", "t2", note="dialogue_too_late"),
    ]
    (trace / "day-0001.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (tmp_path / "godot.log").write_text(
        "RUN_FPS speed=16 frames=900 mean_fps=61.5 p95_ms=19.20 max_ms=40.00\n", encoding="utf-8"
    )

    row = TR.summary(tmp_path)

    assert row["game_hours"] == 2.0
    assert row["decisions_asked_per_h"] == 1.0
    assert row["decisions_answered_per_h"] == 0.5
    assert row["answer_min_median"] == 30.0
    assert row["dropped"] == {"nothing_to_pass": 1, "dialogue_too_late": 1}
    assert (row["talks_asked"], row["talks_with_words"]) == (1, 1)
    assert row["fps"]["mean_fps"] == "61.5" and row["fps"]["speed"] == "16"
    assert "| 16 | 2.0 |" in TR.report([row])
