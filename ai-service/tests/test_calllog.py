from __future__ import annotations

from pathlib import Path

from glassworld_ai.calllog import CallLog, CallRecord


def _record(attempt: int) -> CallRecord:
    return CallRecord(
        role="decider",
        request_id="3f2b8c1e-5d4a-4b6f-9e21-0c7d8a9b1f00",
        tick=1200,
        prompt_version="decider/decide.v1",
        model="qwen3.5:9b",
        seed=424242 + attempt,
        temperature=0.7,
        attempt=attempt,
        latency_ms=1234,
        outcome="ok",
        raw_output='{"option_id":"o1","thought":"Ä ü"}',
    )


def test_writes_one_json_line_per_call_with_utf8(tmp_path: Path) -> None:
    log = CallLog(tmp_path / "logs" / "calls.jsonl")
    log.write(_record(1))
    log.write(_record(2))
    lines = log.path.read_bytes().split(b"\n")
    assert lines[-1] == b""
    assert len(lines) == 3
    assert b"\r" not in log.path.read_bytes()
    rows = log.read()
    assert [r["attempt"] for r in rows] == [1, 2]
    assert rows[0]["raw_output"] == '{"option_id":"o1","thought":"Ä ü"}'
    assert "time_utc" in rows[0]
