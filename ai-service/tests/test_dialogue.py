"""A talk between two inhabitants (A1735, B2782, D1119 v3): checks of the service."""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any, cast

import pytest

from glassworld_ai.dialogue import MockDialogues, build_values, output_schema, parse_dialogue
from glassworld_ai.llm.jobcall import InvalidOutputError
from glassworld_ai.prompts import load_prompt
from glassworld_ai.safety import TextGuard
from glassworld_ai.texts import TextCatalog
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.dialogue-elin-joro.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)
GUARD = TextGuard.from_file(REPO / "content/safety/rules.json")
TEXTS = TextCatalog.load(REPO / "content/i18n")
TEMPLATE = load_prompt(REPO / "prompts", "dialogue/talk", 1)


def _turn(
    speaker: str, text: str, act: str = "tell", expression: str | None = None
) -> dict[str, Any]:
    return {"speaker": speaker, "text": text, "act": act, "expression": expression}


def _answer(*turns: dict[str, Any]) -> str:
    return json.dumps({"turns": list(turns)}, ensure_ascii=False)


GOOD = (
    _turn("a", "Joro, hast du die Haselnüsse am Ufer gesehen?", "ask", "wave"),
    _turn("b", "Ja! Die kleinen braunen? Die mag ich.", "answer"),
)


def _parse(raw: str, job: GenerationJob = JOB) -> tuple[dict[str, Any], list[str]]:
    return parse_dialogue(raw, job, GUARD, TEXTS, TEMPLATE.settings)


def test_the_prompt_shows_both_speakers_by_what_they_know_and_call_each_other() -> None:
    values = build_values(JOB, TEMPLATE, TEXTS)
    assert values["speaker_a"].startswith("## A: Elin")
    assert 'calls the other: "Joro"' in values["speaker_a"]
    assert "has no name for the other: says du" in values["speaker_b"]
    assert "Die Haselnüsse schmecken heute süß." in values["speaker_a"]
    rendered = TEMPLATE.render(values)
    assert "A first, then B" in rendered.system and "inh-0000" not in rendered.user
    assert values["minor_rule"] == ""  # two adults


def test_a_good_talk_maps_the_speakers_to_their_ids_and_fits_the_contract() -> None:
    result, notes = _parse(_answer(*GOOD))
    assert notes == []
    assert [t["speaker"] for t in result["turns"]] == ["inh-000001", "inh-000002"]
    assert result["turns"][0]["text"] == {
        "locale": "de",
        "text": "Joro, hast du die Haselnüsse am Ufer gesehen?",
    }
    status = json.loads(
        (REPO / "contracts/examples/valid/job-status.dialogue-elin-joro.json").read_text("utf-8")
    )
    status["result"] = {"dialogue": result}
    assert ContractValidator(REPO / "contracts/schemas").errors("job-status", status) == []


def test_turns_must_alternate_a_first() -> None:
    """Review GW3 #474: the order is checked; the model is asked again."""
    with pytest.raises(InvalidOutputError, match="alternate"):
        _parse(_answer(GOOD[1], GOOD[0]))
    with pytest.raises(InvalidOutputError, match="alternate"):
        _parse(_answer(GOOD[0], _turn("a", "Und noch was.")))


@pytest.mark.parametrize(
    ("bad", "why"),
    [
        ("Lena, wo bist du gewesen? Sie heißt Lena.", "made-up name"),
        ("I schaue mir das an, du?", "language"),
        ("Tick 8453 war schön, oder?", "a number or key"),
        (
            "Das ist ein sehr langer Satz mit viel zu vielen Wörtern"
            " für einen einzigen kurzen Zug heute.",
            "too many words",
        ),
    ],
)
def test_the_talk_ends_before_the_first_turn_that_fails(bad: str, why: str) -> None:
    result, notes = _parse(_answer(GOOD[0], _turn("b", bad), _turn("a", "Schön.")))
    assert len(result["turns"]) == 1  # the first turn stays, the rest is cut
    assert why in notes[0]


def test_with_a_minor_the_reserved_topics_stay_out() -> None:
    """B9: one speaker is young - the prompt says so and a turn about love ends the talk."""
    data = json.loads(json.dumps(JOB_DATA))
    data["payload"]["dialogue"]["speakers"][1]["inhabitant"]["life_stage"] = "youth"
    young = GenerationJob.model_validate(data)
    assert "no talk about love" in build_values(young, TEMPLATE, TEXTS)["minor_rule"]
    result, notes = _parse(_answer(GOOD[0], _turn("b", "Ich bin verliebt in dich.")), young)
    assert len(result["turns"]) == 1 and "reserved topic" in notes[0]


def test_the_schema_limits_turns_and_expressions() -> None:
    schema = output_schema(JOB)
    turns = schema["properties"]["turns"]
    assert (turns["minItems"], turns["maxItems"]) == (2, 4)
    assert turns["items"]["properties"]["expression"]["anyOf"][0]["enum"] == [
        "laugh",
        "cheer",
        "wave",
    ]


def test_the_mock_says_nothing() -> None:
    """B2644: no fixed sentence - the core shows the talk without text."""
    outcome = asyncio.run(MockDialogues()(JOB))
    assert cast(dict[str, Any], outcome.result["dialogue"]) == {"turns": []}


def test_the_bond_shown_is_the_one_to_the_other_speaker() -> None:
    """Review GW3 #476: relationships hold up to 16 sorted by id - the line must be about B."""
    data = json.loads(json.dumps(JOB_DATA))
    a = data["payload"]["dialogue"]["speakers"][0]["inhabitant"]
    a["relationships"] = [
        {"other": "inh-000000", "affection": 0.1, "trust": 0.1, "respect": 0.5},
        {"other": "inh-000002", "affection": 0.9, "trust": 0.9, "respect": 0.5},
    ]
    values = build_values(GenerationJob.model_validate(data), TEMPLATE, TEXTS)
    assert "towards the other: affection high, trust high" in values["speaker_a"]
    a["relationships"] = [{"other": "inh-000009", "affection": 0.1, "trust": 0.1, "respect": 0.5}]
    values = build_values(GenerationJob.model_validate(data), TEMPLATE, TEXTS)
    assert "towards the other" not in values["speaker_a"]  # no bond to B: no line


@pytest.mark.parametrize(
    "text", ["Sie ist verliebte Leute am Ufer.", "Babys sind klein.", "Eine Hochzeit am Strand?"]
)
def test_reserved_stems_find_inflections_and_compounds(text: str) -> None:
    data = json.loads(json.dumps(JOB_DATA))
    data["payload"]["dialogue"]["speakers"][1]["inhabitant"]["life_stage"] = "child"
    young = GenerationJob.model_validate(data)
    result, notes = _parse(_answer(GOOD[0], _turn("b", text)), young)
    assert len(result["turns"]) == 1 and "reserved topic" in notes[0]


def test_a_speaker_uses_only_the_name_it_learned() -> None:
    """Review GW1 #476: B has no name for A (calls_other null) - B saying 'Elin' is refused,
    A saying 'Joro' (its calls_other) is fine."""
    result, notes = _parse(_answer(GOOD[0], _turn("b", "Elin, ja, die mag ich!")))
    assert len(result["turns"]) == 1 and "never learned (Elin)" in notes[0]
    result, notes = _parse(_answer(*GOOD))
    assert notes == [] and len(result["turns"]) == 2


def test_a_speaker_names_only_things_it_knows_itself() -> None:
    """Review GW1 #476: A1613 per speaker - A knows the hazelnut and may name it; B does not and
    may not (the whole job would have let B use A's knowledge)."""
    data = json.loads(json.dumps(JOB_DATA))
    data["payload"]["dialogue"]["speakers"][0]["inhabitant"]["knowledge"].append(
        {
            "subject": {"kind": "type", "id": "hazelnut"},
            "property": "edible",
            "value": True,
            "confidence": 0.9,
            "source": "experienced",
            "told_by": None,
            "tick": 5,
        }
    )
    job = GenerationJob.model_validate(data)
    result, notes = _parse(_answer(_turn("a", "Joro, die Haselnuss mag ich."), GOOD[1]), job)
    assert notes == [] and len(result["turns"]) == 2
    result, notes = _parse(
        _answer(_turn("a", "Joro, wie geht es dir?"), _turn("b", "Die Haselnuss mag ich.")), job
    )
    assert len(result["turns"]) == 1 and "unknown: Haselnuss" in notes[0]


def test_why_a_talk_ended_early_goes_into_the_call_log(monkeypatch: pytest.MonkeyPatch) -> None:
    """A1735 measurement: the notes of the checks (a turn left out and why) become the detail of the
    accepted answer's log line, so a shortened talk can be read back; a full talk has none."""
    import glassworld_ai.dialogue as dialogue
    from glassworld_ai.llm.jobcall import Answer, ModelCallSettings

    seen: dict[str, Any] = {}

    async def fake_ask(**kwargs: Any) -> Answer[Any]:
        seen.update(kwargs)
        return Answer(kwargs["parse"](_answer(*GOOD)), 1, 5)

    monkeypatch.setattr(dialogue, "ask_model", fake_ask)

    class _Client:
        model = "fake"

    handler = dialogue.OllamaDialogues(
        client=_Client(),  # type: ignore[arg-type]
        queue=None,  # type: ignore[arg-type]
        template=TEMPLATE,
        settings=ModelCallSettings(temperature=0.7, num_ctx=4096, max_retries=2, call_timeout_s=30),
        log=None,  # type: ignore[arg-type]
        guard=GUARD,
        texts=TEXTS,
    )
    asyncio.run(handler(JOB))
    detail_of = seen["detail_of"]
    assert detail_of(({"turns": []}, [])) is None
    assert detail_of(({"turns": []}, ["talk ends before turn 2 (unknown: Feuer): x", "y"])) == (
        "talk ends before turn 2 (unknown: Feuer): x; y"
    )
