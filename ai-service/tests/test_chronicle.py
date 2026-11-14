"""The chronicle's day as a short narrative (B8 R10, A1355)."""

from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path
from typing import Any

import pytest

from glassworld_ai import chronicle
from glassworld_ai.chronicle import (
    Checks,
    MockChronicles,
    OllamaChronicles,
    base_line,
    build_values,
    parse_narrative,
)
from glassworld_ai.config import Settings
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.llm.jobcall import InvalidOutputError, ModelCallSettings
from glassworld_ai.prompts import load_prompt
from glassworld_ai.safety import TextGuard
from glassworld_ai.texts import TextCatalog
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
SNAPSHOTS = Path(__file__).resolve().parent / "snapshots"
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.chronicle-day.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)
TEMPLATE = load_prompt(REPO / "prompts", "chronicle/day", 1)
TEXTS = TextCatalog.load(REPO / "content/i18n")
GUARD = TextGuard.from_file(REPO / "content/safety/rules.json")


def _raw(narrative: str) -> str:
    return json.dumps({"narrative": narrative})


def test_the_base_lines_read_as_the_chronicle_shows_them() -> None:
    entries = JOB.payload.chronicle.entries  # type: ignore[union-attr]
    assert base_line(entries[0], TEXTS, "de") == "Joro und Rena sind sich zum ersten Mal begegnet."
    assert base_line(entries[1], TEXTS, "de") == "Rena benutzt zum ersten Mal Feuerstein."


def test_prompt_snapshot() -> None:
    rendered = TEMPLATE.render(build_values(JOB, TEMPLATE, TEXTS))
    assert "Joro und Rena" in rendered.user and "Feuerstein, Joro, Rena" in rendered.user
    text = f"=== SYSTEM ===\n{rendered.system}\n\n=== USER ===\n{rendered.user}\n"
    snapshot = SNAPSHOTS / "chronicle-day.v1.first-meeting.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


def test_a_calm_narrative_with_the_given_names_passes() -> None:
    checks = Checks(JOB, GUARD, TEXTS, TEMPLATE.settings)
    told = (
        "Am Morgen begegneten sich Joro und Rena zum ersten Mal. "
        "Später nahm Rena den Feuerstein in die Hand."
    )
    assert parse_narrative(_raw(told), JOB, checks) == told


@pytest.mark.parametrize(
    ("narrative", "problem"),
    [
        (
            "Joro und Rena trafen sich am Ufer und sammelten Pyrit.",
            "not given",
        ),  # a thing not given
        ("Joro traf inh-000002 am Morgen.", "raw id"),
        ("x" * 281, "narrative"),  # longer than max_chars
    ],
)
def test_what_does_not_pass_is_asked_again(narrative: str, problem: str) -> None:
    checks = Checks(JOB, GUARD, TEXTS, TEMPLATE.settings)
    with pytest.raises(InvalidOutputError, match=problem):
        parse_narrative(_raw(narrative), JOB, checks)


def test_no_passing_answer_is_no_narrative(monkeypatch: pytest.MonkeyPatch) -> None:
    """R24/B2644: when no answer passes the checks, the day keeps its base lines alone."""

    async def no_answer(**_: object) -> object:
        raise JobFailedError("invalid_model_output", "no valid answer", retryable=False)

    monkeypatch.setattr(chronicle, "ask_model", no_answer)

    class _Client:
        model = "fake"

    handler = OllamaChronicles(
        client=_Client(),  # type: ignore[arg-type]
        queue=None,  # type: ignore[arg-type]
        template=TEMPLATE,
        settings=ModelCallSettings(temperature=0.7, num_ctx=4096, max_retries=2, call_timeout_s=30),
        log=None,  # type: ignore[arg-type]
        guard=GUARD,
        texts=TEXTS,
    )
    outcome = asyncio.run(handler(JOB))
    assert outcome.result == {"chronicle": {"narrative": None}}
    mock = asyncio.run(MockChronicles()(JOB))
    assert mock.result == {"chronicle": {"narrative": None}}


def test_the_service_answers_the_chronicle_kind_at_night(settings: Settings) -> None:
    assert (settings.chronicle_prompt, settings.chronicle_prompt_version) == ("chronicle/day", 1)
    night = json.loads((REPO / "content/hardware/night.json").read_text("utf-8"))
    plan = next(k for k in night["kinds"] if k["kind"] == "chronicle")
    assert plan["when"] == "night"  # B8 R10: written in the computing night
