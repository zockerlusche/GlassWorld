from __future__ import annotations

import asyncio
from pathlib import Path

from glassworld_ai.decider.backends import DecisionError, MockDecider
from glassworld_ai.testsets import DEFAULT_CASES, load_testset, run_cases, summarize, write_report
from glassworld_contracts import DecisionRequest, DecisionResponse


def test_checked_in_testset_has_20_valid_cases() -> None:
    testset = load_testset(DEFAULT_CASES)
    assert len(testset.cases) == 20
    assert len({c.id for c in testset.cases}) == 20
    for case in testset.cases:
        request = DecisionRequest.model_validate(case.request)
        offered = {str(getattr(o.option_id, "root", o.option_id)) for o in request.options}
        assert set(case.plausible_option_ids) <= offered, case.id
    locales = {c.request["display_locale"] for c in testset.cases}
    assert locales == {"de", "en"}


class FlakyDecider:
    """Fails every third call, otherwise behaves like the mock."""

    def __init__(self) -> None:
        self.calls = 0
        self.mock = MockDecider()

    async def decide(self, request: DecisionRequest, deadline: float) -> DecisionResponse:
        self.calls += 1
        if self.calls % 3 == 0:
            raise DecisionError("invalid_model_output", "test")
        return await self.mock.decide(request, deadline)


def test_runner_measures_validity_plausibility_and_latency(tmp_path: Path) -> None:
    testset = load_testset(DEFAULT_CASES)
    testset = testset.model_copy(update={"cases": testset.cases[:3]})
    results = asyncio.run(run_cases(FlakyDecider(), testset, runs=2))
    assert len(results) == 6
    summary = summarize(results, wall_seconds=60)
    assert summary["total"] == 6
    assert summary["content_valid_rate"] == 4 / 6
    assert summary["errors"] == {"invalid_model_output": 2}
    assert summary["latency_ms_p95"] is not None
    # different sampling seed and request id per run
    first, second = results[0], results[3]
    assert first.case_id == second.case_id and first.run != second.run
    write_report(
        tmp_path,
        summary,
        results,
        {
            "testset": "t",
            "version": 1,
            "model": "mock",
            "prompt": "p",
            "temperature": 0.7,
            "runs": 2,
            "cases": 3,
        },
    )
    assert "Content-valid" in (tmp_path / "report.md").read_text(encoding="utf-8")
    assert (tmp_path / "report.json").exists()


# --- B9 test sets A14/A15 (Bogen 34, A1389) ------------------------------------------------------

B9 = Path(__file__).resolve().parents[1] / "testsets" / "b9"
REPO = Path(__file__).resolve().parents[2]


def test_b9_sets_are_big_enough_and_valid_by_contract() -> None:
    from glassworld_ai.validation import ContractValidator

    validator = ContractValidator(REPO / "contracts" / "schemas")
    a14, a15 = load_testset(B9 / "a14.v1.json"), load_testset(B9 / "a15.v1.json")
    assert len(a14.cases) >= 25 and len(a15.cases) >= 30  # Bogen 34 A14/A15
    for case in [*a14.cases, *a15.cases]:
        schema = "decision-request" if case.kind == "decision" else "conversation-request"
        assert validator.errors(schema, case.request) == [], case.id
    assert {c.kind for c in a15.cases} == {"decision", "conversation"}


def test_forbidden_words_match_only_at_a_word_start() -> None:
    from glassworld_ai.safety import TextGuard
    from glassworld_ai.testsets import TestCase, text_problems

    case = TestCase(
        id="x",
        description="x",
        request={},
        plausible_option_ids=[],
        forbidden_words=["hit", "küss", "blut"],
    )
    guard = TextGuard.from_file(REPO / "content" / "safety" / "rules.json")
    assert text_problems("Das mache ich nicht.", case, guard) == []
    assert text_problems("Komm, wir spielen!", case, guard) == []
    assert text_problems("Ich küsse sie.", case, guard) == ["word:küss"]
    assert text_problems("Da war Blut.", case, guard) == ["word:blut"]


def test_b9_runner_checks_content_with_the_mocks(tmp_path: Path) -> None:
    from glassworld_ai.conversation import MockConversation
    from glassworld_ai.safety import TextGuard

    guard = TextGuard.from_file(REPO / "content" / "safety" / "rules.json")
    testset = load_testset(B9 / "a15.v1.json")
    results = asyncio.run(run_cases(MockDecider(), testset, 1, MockConversation(), guard))
    assert len(results) == len(testset.cases)
    summary = summarize(results, 1.0)
    assert set(summary["problems"]) <= {"silent", "no_readable_name"}  # the mocks say little
    write_report(tmp_path, summary, results, {
        "testset": testset.id, "version": 1, "cases": len(testset.cases), "runs": 1,
        "model": "mock", "prompt": "decider/decide.v5", "temperature": 0.0,
    })  # fmt: skip
    assert "Valid incl. content checks" in (tmp_path / "report.md").read_text(encoding="utf-8")
