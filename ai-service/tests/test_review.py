"""The nightly look back (A1584, B7 R18-R22, B2687): checks of the service."""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any, cast

import pytest

from glassworld_ai.llm.jobcall import InvalidOutputError
from glassworld_ai.prompts import load_prompt
from glassworld_ai.review import (
    MockReviews,
    build_values,
    known,
    output_schema,
    parse_review,
)
from glassworld_ai.safety import TextGuard
from glassworld_ai.texts import TextCatalog
from glassworld_ai.validation import ContractValidator
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.review-flint.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)
GUARD = TextGuard.from_file(REPO / "content/safety/rules.json")
TEXTS = TextCatalog.load(REPO / "content/i18n")
MOODS = Vocabulary.load(REPO / "content/vocabulary").ids("mood")
TEMPLATE = load_prompt(REPO / "prompts", "review/night", 1)


def _answer(**changes: Any) -> str:
    base: dict[str, Any] = {
        "summary": "Woke on the beach, met Mira. Hazelnuts are good to eat.",
        "kept": [1900],
        "dream": {"text": "Ich laufe über den Strand.", "tone": "joyful", "about": "Haselnuss"},
        "ideas": [
            {
                "grows": None,
                "text": "The sharp stone might open a hazelnut.",
                "roots": ["Feuerstein", "Haselnuss"],
                "thought": "Was, wenn ich die Haselnuss mit dem Stein öffne?",
            }
        ],
        "intentions": [
            {
                "action": "show",
                "about": "Haselnuss",
                "thought": "Heute zeige ich ihr die Haselnüsse.",
            }
        ],
        "morning_mood": "joy",
    }
    base.update(changes)
    return json.dumps(base, ensure_ascii=False)


def _parse(raw: str) -> tuple[dict[str, Any], list[str]]:
    return parse_review(raw, JOB, GUARD, MOODS, TEXTS, TEMPLATE.settings)


def test_a_good_review_is_checked_and_fits_the_contract() -> None:
    result, notes = _parse(_answer())
    assert notes == []
    assert result["ideas"][0]["roots"] == [
        {"kind": "type", "id": "flint"},
        {"kind": "type", "id": "hazelnut"},
    ]
    assert result["intentions"][0]["about"] == {"kind": "type", "id": "hazelnut"}
    assert result["dream"]["about"] == {"kind": "type", "id": "hazelnut"}
    status = {
        "contract_version": "0.2",
        "job_id": JOB_DATA["request_id"],
        "tick": JOB_DATA["tick"],
        "kind": "review",
        "state": "done",
        "result": {"review": result},
        "error": None,
        "provenance": {
            "source": "ollama",
            "model": "qwen3.5:9b",
            "prompt_version": "review/night.v1",
            "seed": 1,
            "latency_ms": 1,
            "attempts": 1,
        },
    }
    assert ContractValidator(REPO / "contracts/schemas").errors("job-status", status) == []


def test_the_model_never_sees_an_id() -> None:
    values = build_values(JOB, TEMPLATE, TEXTS)
    text = "\n".join(values.values())
    assert "flint" not in text and "hazelnut" not in text
    assert "- Feuerstein: is sharp" in values["knowledge"] and "- Feuerstein" in values["things"]


def test_roots_come_only_from_its_own_knowledge() -> None:
    """R29: a root that is not a key of its knowledge or ideas is refused by the schema; the model
    is asked again (one answer naming the truth of the world never goes out)."""
    with pytest.raises(InvalidOutputError):
        _parse(
            _answer(
                ideas=[{"grows": None, "text": "x", "roots": ["fire"], "thought": "Was, wenn?"}]
            )
        )


def test_more_new_ideas_than_allowed_are_left_out() -> None:
    idea = {
        "grows": None,
        "text": "Stone on nut.",
        "roots": ["Feuerstein"],
        "thought": "Was, wenn ich?",
    }
    result, notes = _parse(_answer(ideas=[idea, {**idea, "text": "Another."}]))
    assert len(result["ideas"]) == 1  # max_new_ideas = 1
    assert any("more than 1 new" in n for n in notes)


def test_a_thought_naming_an_unknown_thing_of_the_world_is_left_out_the_idea_stays() -> None:
    """A1613 for the morning thought; R19: the rest of the review holds."""
    idea = {
        "grows": None,
        "text": "Make fire with the stone.",
        "roots": ["Feuerstein"],
        "thought": "Was, wenn ich mit dem Stein Feuer mache?",
    }
    result, notes = _parse(_answer(ideas=[idea]))
    assert result["ideas"][0]["thought"] is None and result["ideas"][0]["roots"]
    assert any("unknown: Feuer" in n for n in notes)


def test_a_dream_the_guard_takes_back_leaves_the_rest() -> None:
    result, notes = _parse(
        _answer(dream={"text": "Ich nehme Koks am Strand.", "tone": "calm", "about": None})
    )
    assert result["dream"] is None and result["summary"]
    assert any(n.startswith("dream left out") for n in notes)


def test_formative_events_must_be_kept() -> None:
    with pytest.raises(InvalidOutputError):
        _parse(_answer(kept=[]))


def test_the_mock_invents_no_idea_and_fits_the_contract() -> None:
    outcome = asyncio.run(MockReviews()(JOB))
    review = cast(dict[str, Any], outcome.result["review"])
    assert review["ideas"] == [] and review["intentions"] == [] and review["kept"] == [1900]
    schema = output_schema(JOB, MOODS)
    assert schema["properties"]["ideas"]["items"]["properties"]["roots"]["items"]["enum"] == list(
        known(JOB)
    )


def test_v2_keeps_an_ideas_roots_to_what_it_is_about() -> None:
    """D936: the model listed everything it knew as roots; v2 allows at most max_roots (3)."""
    from glassworld_ai.review import build_values, output_schema

    v2 = load_prompt(REPO / "prompts", "review/night", 2)
    schema = output_schema(JOB, ["calm"], None, v2.settings)
    roots = schema["properties"]["ideas"]["items"]["properties"]["roots"]
    assert roots["maxItems"] == v2.settings["max_roots"] == 3
    assert "only the 1 to 3 thing(s) the idea itself is" in v2.render(build_values(JOB, v2)).system
    old = output_schema(JOB, ["calm"], None, TEMPLATE.settings)
    assert old["properties"]["ideas"]["items"]["properties"]["roots"]["maxItems"] == 8
