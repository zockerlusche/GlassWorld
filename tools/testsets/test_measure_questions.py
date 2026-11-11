"""The question measurement tool (B4a R10): guards, cases and the summary (no model needed)."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from types import ModuleType

from glassworld_contracts import GenerationJob


def _module() -> ModuleType:
    path = Path(__file__).with_name("measure_questions.py")
    spec = importlib.util.spec_from_file_location("measure_questions", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


MQ = _module()


def test_every_case_is_a_valid_question_job() -> None:
    for case in MQ.CASES:
        job = GenerationJob.model_validate(MQ.job_data(case, 0))
        assert job.payload.question is not None


def test_two_cases_carry_the_name_in_the_memory() -> None:
    named = [c for c in MQ.CASES if c[6]]
    assert {c[0] for c in named} == {"stone_named_in_memory", "water_named_in_memory"}
    assert "flint" in named[0][6]


def test_a_live_run_needs_the_gpu_queue_and_a_folder_outside_the_repository(tmp_path: Path) -> None:
    assert "GPU-queue" in MQ.guard(tmp_path / "out", False)
    assert "inside the repository" in MQ.guard(MQ.ROOT / "out", True)
    foreign = tmp_path / "foreign"
    foreign.mkdir()
    (foreign / "notes.txt").write_text("x", encoding="utf-8")
    assert "not a folder of this tool" in MQ.guard(foreign, True)
    assert MQ.guard(tmp_path / "new", True) is None


def test_an_earlier_run_is_kept_under_a_new_name(tmp_path: Path) -> None:
    out = tmp_path / "q"
    out.mkdir()
    out.with_name("q.alt-1").mkdir()
    assert MQ.archive_name(out) == tmp_path / "q.alt-2"


def test_the_summary_fails_on_an_invalid_answer_or_thing_guesses_about_a_person() -> None:
    good = {"case": "a", "ok": True, "person": False, "notices": ["stone"], "attempts": 1,
            "understood": True,
            "guesses": [{"property": "stone", "value": True, "confidence": 0.5},
                        {"property": "sparks", "value": True, "confidence": 0.9}]}  # fmt: skip
    passed, lines = MQ.summarize([good], 0.6)
    assert passed
    assert "beyond what it notices 1" in lines[0] and "surer than the core's cap 0.6: 1" in lines[0]
    assert lines[1].strip() == "a: sparks=True x1"
    person = {**good, "person": True}
    assert not MQ.summarize([person], 0.6)[0]
    assert not MQ.summarize(
        [good, {"case": "b", "ok": False, "person": False, "notices": []}], 0.6
    )[0]
