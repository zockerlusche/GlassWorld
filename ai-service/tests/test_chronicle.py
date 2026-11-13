"""The chronicle's sentence for a milestone (R35, B2598, A1583, D962)."""

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
    MockChronicle,
    OllamaChronicle,
    build_values,
    parse_sentence,
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
    (REPO / "contracts/examples/valid/generation-job.chronicle-first-fire.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)
TEMPLATE = load_prompt(REPO / "prompts", "chronicle/milestone", 1)
TEXTS = TextCatalog.load(REPO / "content/i18n")
GUARD = TextGuard.from_file(REPO / "content/safety/rules.json")


def _raw(sentence: str) -> str:
    return json.dumps({"sentence": sentence})


def test_prompt_snapshot() -> None:
    rendered = TEMPLATE.render(build_values(JOB, TEMPLATE, TEXTS))
    assert "Feuer" in rendered.user and "Mira, Joro" in rendered.user
    text = f"=== SYSTEM ===\n{rendered.system}\n\n=== USER ===\n{rendered.user}\n"
    snapshot = SNAPSHOTS / "chronicle-milestone.v1.first-fire.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


def test_a_calm_sentence_with_the_given_names_passes() -> None:
    checks = Checks(JOB, GUARD, TEXTS, TEMPLATE.settings)
    said = "Am Abend schlugen Mira und Joro Funken, bis ihr erstes Feuer brannte."
    assert parse_sentence(_raw(said), JOB, checks) == said


@pytest.mark.parametrize(
    ("sentence", "problem"),
    [
        ("Mira und Joro machten am Ufer mit Pyrit ein Feuer.", "not given"),  # a thing not given
        ("Mira machte first_fire am Abend.", "raw id"),
        ("x" * 141, "sentence"),  # longer than max_chars
    ],
)
def test_what_does_not_pass_is_asked_again(sentence: str, problem: str) -> None:
    checks = Checks(JOB, GUARD, TEXTS, TEMPLATE.settings)
    with pytest.raises(InvalidOutputError, match=problem):
        parse_sentence(_raw(sentence), JOB, checks)


def test_no_passing_answer_is_no_sentence(monkeypatch: pytest.MonkeyPatch) -> None:
    """R24/B2644: when no answer passes the checks, the milestone keeps no sentence."""

    async def no_answer(**_: object) -> object:
        raise JobFailedError("invalid_model_output", "no valid answer", retryable=False)

    monkeypatch.setattr(chronicle, "ask_model", no_answer)

    class _Client:
        model = "fake"

    handler = OllamaChronicle(
        client=_Client(),  # type: ignore[arg-type]
        queue=None,  # type: ignore[arg-type]
        template=TEMPLATE,
        settings=ModelCallSettings(temperature=0.7, num_ctx=4096, max_retries=2, call_timeout_s=30),
        log=None,  # type: ignore[arg-type]
        guard=GUARD,
        texts=TEXTS,
    )
    outcome = asyncio.run(handler(JOB))
    assert outcome.result == {"chronicle": {"sentence": None}}
    mock = asyncio.run(MockChronicle()(JOB))
    assert mock.result == {"chronicle": {"sentence": None}}


def test_the_service_answers_the_chronicle_kind(settings: Settings) -> None:
    assert (settings.chronicle_prompt, settings.chronicle_prompt_version) == (
        "chronicle/milestone",
        1,
    )
    night = json.loads((REPO / "content/hardware/night.json").read_text("utf-8"))
    plan = next(k for k in night["kinds"] if k["kind"] == "chronicle")
    assert plan["when"] == "any"  # B2598: in the moment it is reached
