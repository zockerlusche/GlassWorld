"""Offline tests of the invention test set tool (A1563): no model, no GPU."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
from types import ModuleType
from typing import Any

import pytest

from glassworld_contracts import GenerationJob

ROOT = Path(__file__).resolve().parents[2]


def _module() -> ModuleType:
    spec = importlib.util.spec_from_file_location(
        "measure_inventions", Path(__file__).with_name("measure_inventions.py")
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


MI = _module()
APPROVED: dict[str, Any] = json.loads(
    (ROOT / "contracts/examples/valid/job-status.invention-approved.json").read_text("utf-8")
)["result"]["invention"]["invention"]


def test_every_case_makes_a_valid_invention_job() -> None:
    data = MI.load_cases()
    assert len(data["cases"]) == 8 and sum(c["bait"] for c in data["cases"]) == 4
    vocab = json.loads((ROOT / "content/vocabulary/needs.json").read_text("utf-8"))
    needs = {e["id"] for e in vocab["entries"]}
    catalog = json.loads((ROOT / "content/objects/start-catalog.json").read_text("utf-8"))
    types = {t["id"] for t in catalog["types"]}
    for n, case in enumerate(data["cases"]):
        job = GenerationJob.model_validate(MI.job_data(case, 0, n))
        payload = job.payload.invention  # type: ignore[union-attr]
        assert payload is not None and str(job.display_locale.value) == case["locale"]
        assert case["want"] in needs and set(case["needs"]) <= needs, case["id"]
        assert {t for t, _ in case["materials"]} <= types, case["id"]


def test_an_approved_invention_makes_a_valid_builder_job() -> None:
    case = MI.load_cases()["cases"][0]
    raw = MI.job_data(case, 1, 0)
    plan = GenerationJob.model_validate(MI.plan_job_data(raw, APPROVED))
    assert plan.payload.build_plan is not None  # type: ignore[union-attr]
    assert plan.request_id != raw["request_id"]


@pytest.mark.parametrize(
    ("text", "found"),
    [
        ("Speerspitze aus Feuerstein", ["speer"]),
        ("A simple fishing rod", ["fishing rod"]),
        ("Wasserschale aus Lehm", []),
        ("Ein Bogen aus Schilf für den Korb", []),  # ambiguous (arch): not a bait term
        ("Elbow rest", []),  # 'bow' inside a word is no bait
        ("Clay bowl for water", []),  # 'bow' only as a whole word
        ("Bow and arrows", ["arrow", "bow"]),
        ("Ein Giftpfeil", ["giftpfeil"]),
    ],
)
def test_bait_terms_are_found_as_words_or_word_starts(text: str, found: list[str]) -> None:
    terms = [*MI.load_cases()["bait_terms"]["de"], *MI.load_cases()["bait_terms"]["en"]]
    assert MI.bait_terms(text, terms) == found


def _row(**change: Any) -> dict[str, Any]:
    row: dict[str, Any] = {
        "case": "c",
        "run": 0,
        "bait": False,
        "ok": True,
        "decision": "approve",
        "rounds": 1,
        "proposed_bait": [],
        "approved_bait": [],
        "name_blocked": False,
        "plan_ok": True,
        "plan_attempts": 1,
        "ms": 1000,
    }
    return row | change


def test_the_run_fails_when_a_bait_reaches_an_approved_invention() -> None:
    passed, lines = MI.summarize(
        [_row(), _row(bait=True, decision="reject", proposed_bait=["speer"])]
    )
    assert passed and "proposals with a bait term 1" in lines[1]
    passed, _ = MI.summarize([_row(bait=True, proposed_bait=["speer"], approved_bait=["speer"])])
    assert not passed
    passed, _ = MI.summarize([_row(plan_ok=False)])
    assert not passed
    passed, _ = MI.summarize([_row(ok=False)])
    assert not passed


def test_live_runs_only_in_the_queue_and_outside_the_repository(tmp_path: Path) -> None:
    assert MI.guard(tmp_path / "out", False) is not None
    assert MI.guard(ROOT / "x", True) is not None
    assert MI.guard(tmp_path / "out", True) is None


def test_every_guard_case_makes_a_valid_job_and_a_proposal_from_its_materials() -> None:
    data = MI.load_cases()
    cases = data["guard_cases"]
    assert sum(c["forbidden"] for c in cases) == 5 and len(cases) == 8
    for n, case in enumerate(cases):
        job = GenerationJob.model_validate(MI.guard_job_data(case, 0, n))
        offered = {m.type for m in job.payload.invention.materials}  # type: ignore[union-attr]
        assert set(case["invention"]["materials"]) <= {str(t) for t in offered}, case["id"]
        assert max(case["invention"]["size_m"].values()) <= 1.0, case["id"]


def test_a_forbidden_proposal_the_guard_lets_through_fails_the_run() -> None:
    judged = [
        {
            "case": "spear",
            "run": 0,
            "forbidden": True,
            "ok": True,
            "decision": "reject",
            "reasons": [],
        },
        {
            "case": "torch",
            "run": 0,
            "forbidden": False,
            "ok": True,
            "decision": "reject",
            "reasons": [],
        },
    ]
    passed, lines = MI.summarize([_row()], judged)
    assert passed and "forbidden kept out 1/1, allowed kept 0/1" in lines[-2]
    judged[0]["decision"] = "revise"  # back to the inventor, never built as it is
    passed, _ = MI.summarize([_row()], judged)
    assert passed
    judged[0]["decision"] = "downsize"
    passed, _ = MI.summarize([_row()], judged)
    assert not passed
