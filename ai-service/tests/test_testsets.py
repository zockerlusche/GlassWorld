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
