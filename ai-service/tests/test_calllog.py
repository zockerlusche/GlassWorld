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


def test_a_capped_log_rolls_into_parts_and_reads_them_oldest_first(tmp_path: Path) -> None:
    probe = CallLog(tmp_path / "probe.jsonl")
    probe.write(_record(0))
    line = probe.path.stat().st_size
    log = CallLog(tmp_path / "decider.jsonl", max_bytes=2 * line + 10, keep=2)
    for attempt in range(1, 8):
        log.write(_record(attempt))
    assert log.path.stat().st_size <= 2 * line + 10
    assert log.part(1).exists()
    assert log.part(2).exists()
    assert not log.part(3).exists()
    attempts = [r["attempt"] for r in log.read()]
    assert attempts == [3, 4, 5, 6, 7]


def test_an_uncapped_log_never_rolls(tmp_path: Path) -> None:
    log = CallLog(tmp_path / "calls.jsonl")
    for attempt in range(1, 20):
        log.write(_record(attempt))
    assert not log.part(1).exists()
    assert len(log.read()) == 19
