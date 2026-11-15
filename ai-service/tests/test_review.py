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
TEMPLATE_V2 = load_prompt(REPO / "prompts", "review/night", 2)
TEMPLATE_V3 = load_prompt(REPO / "prompts", "review/night", 3)


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


def _parse_v2(raw: str) -> tuple[dict[str, Any], list[str]]:
    return parse_review(raw, JOB, GUARD, MOODS, TEXTS, TEMPLATE_V2.settings)


def _answer_v2(**changes: Any) -> str:
    data = json.loads(_answer(**changes))
    data.pop("kept", None)
    return json.dumps(data, ensure_ascii=False)


def test_v2_shows_no_tick_and_keeps_the_formative_events_itself() -> None:
    """A1721 (Marcel's test 14.11.: 'Tick 8453 showed me an egg'): no tick reaches the model, and
    the model is not asked for them - the service keeps every formative event."""
    values = build_values(JOB, TEMPLATE_V2, TEXTS)
    rendered = TEMPLATE_V2.render(values)
    assert "[tick" not in rendered.user and "ticks of" not in rendered.system
    assert "1900" not in rendered.system + rendered.user
    assert "FORMATIVE" in values["events"]
    schema = output_schema(JOB, MOODS, TEXTS, TEMPLATE_V2.settings)
    assert "kept" not in schema["properties"] and "kept" not in schema["required"]
    result, notes = _parse_v2(_answer_v2())
    assert notes == [] and result["kept"] == [1900]


@pytest.mark.parametrize(
    "summary",
    [
        "Tick 8453 showed me an egg.",
        "Ticks 15362, 18155 marked my steps.",
        "Driftwood t2 and t3 waited by the shore.",
        "Formative moments kept: 28654.",
    ],
)
def test_v2_refuses_a_summary_with_a_tick_a_key_or_a_number(summary: str) -> None:
    with pytest.raises(InvalidOutputError, match="names a tick, a number or a key"):
        _parse_v2(_answer_v2(summary=summary))


def test_v2_a_dream_with_a_key_is_left_out_the_rest_holds() -> None:
    result, notes = _parse_v2(
        _answer_v2(dream={"text": "Ich sehe t2 am Strand.", "tone": "calm", "about": None})
    )
    assert result["dream"] is None and result["summary"]
    assert any("a number or key (t2)" in n for n in notes)


def test_v1_still_asks_for_kept() -> None:
    """Released versions stay as they were: v1 keeps its ticks and its 'kept'."""
    assert "[tick 1900]" in build_values(JOB, TEMPLATE, TEXTS)["events"]
    assert "kept" in output_schema(JOB, MOODS, TEXTS, TEMPLATE.settings)["required"]


def _job_with_people() -> GenerationJob:
    """Joro is known by name (the core's label), the other person is not."""
    data = json.loads(json.dumps(JOB_DATA))
    events = data["payload"]["review"]["events"]
    events.append(
        {
            "tick": 1600,
            "kind": "name_learned",
            "about": {"kind": "inhabitant", "id": "inh-000002"},
            "formative": True,
            "label": {"de": "Joro", "en": "Joro"},
        }
    )
    events.append(
        {
            "tick": 1700,
            "kind": "first_meeting",
            "about": {"kind": "inhabitant", "id": "inh-000003"},
            "formative": False,
        }
    )
    return GenerationJob.model_validate(data)


def test_v3_names_people_as_learned_and_others_someone() -> None:
    """A1728 (night run 15.11.: 'I met another person named Lena'): a person appears by the name
    the inhabitant learned, else as 'someone' - the model has no reason to make one up."""
    job = _job_with_people()
    values = build_values(job, TEMPLATE_V3, TEXTS)
    assert "name learned (Joro)" in values["events"]
    assert "first meeting (someone)" in values["events"]
    assert "Joro" not in build_values(job, TEMPLATE_V2, TEXTS)["events"]  # v2 as released


def test_v3_refuses_a_summary_that_makes_up_a_name() -> None:
    job = _job_with_people()
    answer = json.loads(_answer())
    answer.pop("kept")
    answer["summary"] = "Today I met another person named Lena by the shore."
    with pytest.raises(InvalidOutputError, match="'Lena', a name it never learned"):
        parse_review(json.dumps(answer), job, GUARD, MOODS, TEXTS, TEMPLATE_V3.settings)
    answer["summary"] = "Today I talked with someone called Joro by the shore."
    result, _ = parse_review(json.dumps(answer), job, GUARD, MOODS, TEXTS, TEMPLATE_V3.settings)
    assert result["summary"].startswith("Today I talked")


@pytest.mark.parametrize(
    ("summary", "refused"),
    [
        ("Heute traf ich eine Person namens Lena am Ufer.", "Lena"),
        ("Sie heißt Lena und mag Beeren.", "Lena"),
        ("Er wurde Lena genannt.", None),  # 'genannt' after the name: no name follows
        ("Heute sprach ich mit jemandem, der Joro heißt.", None),
        ("My name is Elin and I like the shore.", None),
        ("Ich heiße Elin und mag das Ufer.", None),
        ("I am called Elin by nobody.", None),
    ],
)
def test_v3_name_forms_german_and_its_own_name(summary: str, refused: str | None) -> None:
    """Review GW1 #462: German forms count; speaking of itself is no made-up person."""
    from glassworld_ai.review import made_up_name

    assert made_up_name(summary, {"Joro"}) == refused
