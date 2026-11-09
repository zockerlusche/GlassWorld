"""D9 report logic (D471): parsing the runner's lines and checking the chain in order."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from types import ModuleType

import pytest


def _module() -> ModuleType:
    spec = importlib.util.spec_from_file_location("run_d9", Path(__file__).with_name("run_d9.py"))
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


D9 = _module()

FULL = [
    "day 1 ...",
    "EVENT tick=10 kind=invention_wanted inhabitant=inh-1 action=- target=- detail=-",
    "EVENT tick=40 kind=invented inhabitant=inh-1 action=- target=obj-9 detail=new%20thing",
    "EVENT tick=41 kind=build_planned inhabitant=inh-1 action=- target=obj-9 detail=-",
    "EVENT tick=50 kind=build_step inhabitant=inh-1 action=build target=obj-9 detail=1",
    "EVENT tick=60 kind=build_step inhabitant=inh-1 action=build target=obj-9 detail=2",
    "EVENT tick=61 kind=built inhabitant=inh-1 action=- target=obj-9 detail=-",
    "CHRONICLE tick=40 kind=invented id=1",
    "EVENT tick=90 kind=first_use inhabitant=inh-1 action=use target=obj-9 detail=-",
    "CHRONICLE tick=90 kind=first_use id=2",
    "UNTIL kind=first_use count=1/1 reached tick=90",
    "FINAL hash=abc tick=90",
]


def test_parse_reads_events_chronicle_and_final_hash() -> None:
    run = D9.parse_run(FULL, 0)
    assert run.final_hash == "abc" and run.final_tick == "90"
    assert run.events[1]["detail"] == "new thing"  # percent-decoded
    assert [c["kind"] for c in run.chronicle] == ["invented", "first_use"]
    assert run.until is not None and "reached" in run.until


def test_the_full_chain_passes() -> None:
    rows = D9.chain_check(D9.parse_run(FULL, 0))
    assert all(ok for _, _, ok in rows), rows
    assert [kind for kind, _, _ in rows][-1] == "chronicle"


def test_one_build_step_or_a_used_thing_before_it_was_built_fails() -> None:
    one_step = [line for line in FULL if "detail=2" not in line]
    rows = dict((k, ok) for k, _, ok in D9.chain_check(D9.parse_run(one_step, 0)))
    assert rows["build_step"] is False
    early_use = [
        line.replace("tick=90 kind=first_use", "tick=5 kind=first_use")
        for line in FULL
        if "kind=first_use" in line and line.startswith("EVENT")
    ] + [line for line in FULL if not (line.startswith("EVENT") and "first_use" in line)]
    rows = dict((k, ok) for k, _, ok in D9.chain_check(D9.parse_run(early_use, 0)))
    assert rows["first_use"] is False  # only counts after "built"


def test_assets_are_found_anywhere_in_a_result() -> None:
    result = {"image": {"file": "img-1.png"}, "sounds": ["a.wav", "readme.txt"], "n": 3}
    assert sorted(D9.asset_names(result)) == ["a.wav", "img-1.png"]


def test_output_inside_the_repository_is_refused(tmp_path: Path) -> None:
    assert D9.main(["--ai", "mock", "--out", str(D9.ROOT / "build" / "d9")]) == 1


def _log_fixture() -> list[dict[str, object]]:
    """A recorded log.jsonl in miniature: a decision, an invention job, a sound job."""
    meta = {"source": "model", "model": "qwen3.5:9b", "prompt_version": "decider/decide.v3",
            "latency_ms": 1500, "attempts": 1, "request_tick": 8, "wait_ticks": 2,
            "request_seed": 42}  # fmt: skip
    return [
        {"log": "decision", "kind": "request", "request_id": "d-1", "tick": 8, "note": None,
         "request": {"seed": 42}},
        {"log": "decision", "kind": "response", "request_id": "d-1", "tick": 10, "note": None,
         "response": {}, "meta": meta},
        {"log": "job", "kind": "job_request", "request_id": "j-1", "tick": 20, "note": None,
         "request": {"kind": "invention"}},
        {"log": "job", "kind": "job_result", "request_id": "j-1", "tick": 40, "note": None,
         "response": {"result": {}}, "meta": {"source": "model", "model": "qwen3.5:9b"}},
        {"log": "job", "kind": "job_request", "request_id": "j-2", "tick": 70, "note": None,
         "request": {"kind": "sound"}},
        {"log": "job", "kind": "job_result", "request_id": "j-2", "tick": 80, "note": None,
         "response": {"result": {"file": "sound-1.wav"}}, "meta": {"source": "moss"}},
    ]  # fmt: skip


def test_report_from_a_recorded_run(tmp_path: Path) -> None:
    import argparse

    log = _log_fixture()
    run = D9.parse_run(FULL, 0)
    options = argparse.Namespace(seed=1, ai="live", days=3, until="first_use")
    assets = sorted({a for e in log if e["kind"] == "job_result" for a in D9.asset_names(e)})
    text, ok = D9.report(tmp_path, options, run, 0, "REPLAY_OK hash=abc ticks=90", log, assets)
    assert ok
    assert "Replay-Hash gleich Lauf-Hash: ja" in text and "Kette vollstaendig: ja" in text
    assert "| invention | 1 | job_result 1 |" in text and "| sound | 1 | job_result 1 |" in text
    assert "`d-1` | 10 | 2 | 42 | model | qwen3.5:9b | decider/decide.v3 | 1500 |" in text
    assert "sound-1.wav" in text
    assert "build_step 2" in text and "invented 1" in text  # counts per kind
    assert str(tmp_path) not in text and ":\\" not in text  # no absolute path in the report


def test_a_different_replay_hash_or_a_failed_core_check_is_not_ok(tmp_path: Path) -> None:
    import argparse

    options = argparse.Namespace(seed=1, ai="live", days=3, until="first_use")
    run = D9.parse_run(FULL, 0)
    _, ok = D9.report(tmp_path, options, run, 5, "REPLAY_DIFF tick=40", [], [])
    assert not ok
    failed = D9.parse_run(FULL, 2)
    text, ok = D9.report(tmp_path, options, failed, 0, "REPLAY_OK hash=abc ticks=90", [], [])
    assert not ok and "Kern-Abnahme fehlgeschlagen" in text


def test_exit_4_reports_not_run_and_never_replays(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    calls: list[list[str]] = []

    def fake_run(args: list[str], log: Path, env: dict[str, str]) -> tuple[int, list[str]]:
        calls.append(args)
        return 4, ["AI_SERVICE_UNAVAILABLE a game's AI service is running; close the game first"]

    monkeypatch.setattr(D9, "build_headless", lambda: 0)
    monkeypatch.setattr(D9, "run_cmd", fake_run)
    out = tmp_path / "d9"
    assert D9.main(["--ai", "live", "--gpu-queue", "--out", str(out)]) == 3
    assert len(calls) == 1  # no replay call
    report = (out / "bericht.md").read_text(encoding="utf-8")
    assert "NICHT GELAUFEN" in report and "AI_SERVICE_UNAVAILABLE" in report


def test_live_only_as_a_gpu_queue_run(tmp_path: Path) -> None:
    assert D9.main(["--ai", "live", "--out", str(tmp_path / "d9")]) == 1
    assert not (tmp_path / "d9").exists()
