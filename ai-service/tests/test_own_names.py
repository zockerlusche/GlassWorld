"""An invented kind of thing in the prompts (#251): by the name its inhabitant has for it, as a
neutral 'made thing' without one, and never by its runtime id inv_<hex>."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from glassworld_ai.config import Settings
from glassworld_ai.conversation import values as conversation_values
from glassworld_ai.grounding import known_words, spoken_names, unknown_nouns
from glassworld_ai.invention.flow import inventor_values
from glassworld_ai.memory.condense import build_values as memory_values
from glassworld_ai.prompts import load_prompt
from glassworld_ai.questions.context import build_values as question_values
from glassworld_ai.texts import TextCatalog
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import own_names, without_runtime_ids
from glassworld_contracts import GenerationJob
from glassworld_contracts.models.conversation_request_schema import ConversationRequest

REPO = Path(__file__).resolve().parents[2]
EXAMPLES = REPO / "contracts" / "examples" / "valid"
MADE = "inv_3fa2c19b7e04"


def _example(name: str) -> dict[str, Any]:
    return json.loads((EXAMPLES / name).read_text(encoding="utf-8"))


def _texts() -> TextCatalog:
    return TextCatalog.load(REPO / "content" / "i18n")


def _vocab() -> Vocabulary:
    return Vocabulary.load(REPO / "content" / "vocabulary")


def test_the_helper_names_a_known_kind_and_never_shows_an_id() -> None:
    names = {MADE: "Wasserschale"}
    line = f"- thing '{MADE.replace('_', ' ')}' is crafted; inv_aaaaaaaaaaaa lies there"
    assert without_runtime_ids(line, names, "made thing") == (
        "- thing 'Wasserschale' is crafted; made thing lies there"
    )
    plain = f"you see dry grass, {MADE.replace('_', ' ')}"
    assert without_runtime_ids(plain, names, "made thing", everywhere=True) == (
        "you see dry grass, Wasserschale"
    )


def test_a_name_is_one_line_without_quote_characters() -> None:
    class Label:
        de = 'Mira\'s "Schale"\n  gross'

    assert own_names([(MADE, Label()), (MADE, None), (None, Label())], "de") == {
        MADE: "Miras Schale gross"
    }


def test_conversation_v5_speaks_of_its_own_invention_by_name(settings: Settings) -> None:
    request = ConversationRequest.model_validate(
        _example("conversation-request.own-invention.json")
    )
    assert spoken_names(request, "de") == {MADE: "Wasserschale"}
    template = load_prompt(settings.prompts_dir, "conversation/reply", 5)
    user = template.render(conversation_values(request, template, {}, _texts())).user
    assert "Wasserschale" in user
    assert "inv_" not in user and "inv 3fa" not in user
    # ... and it may say that name: the noun check knows it, and still refuses a thing nobody has.
    words = known_words(request, _texts(), "de", (), [])
    assert unknown_nouns("Ich gehe zu meiner Wasserschale.", words) == []
    assert unknown_nouns("Ich gehe zu meinem Sofa.", words) == ["Sofa"]
    # v4 as it was: the id is still shown there.
    old = load_prompt(settings.prompts_dir, "conversation/reply", 4)
    assert "inv 3fa2c19b7e04" in old.render(conversation_values(request, old, {}, _texts())).user


def test_question_v3_keeps_the_thing_asked_about_unnamed(settings: Settings) -> None:
    """The subject's own label is NOT used: an invention's name says what it is for (ask.v2)."""
    data = _example("generation-job.question-made-thing.json")
    job = GenerationJob.model_validate(data)
    template = load_prompt(settings.prompts_dir, "question/ask", 3)
    user = template.render(question_values(job, template, _vocab())).user
    assert "this thing" in user
    assert "Wasserschale" not in user and "water bowl" not in user
    assert "inv_" not in user and "inv 3fa" not in user
    # Another invention the inhabitant knows is named by its own name.
    other = "inv_0123456789ab"
    data["payload"]["question"]["inhabitant"]["knowledge"].append(
        {
            "subject": {"kind": "type", "id": other},
            "property": "crafted",
            "value": True,
            "confidence": 1.0,
            "source": "experienced",
            "told_by": None,
            "tick": 1,
            "label": {"de": "Grabstock", "en": "digging stick"},
        }
    )
    job = GenerationJob.model_validate(data)
    user = template.render(question_values(job, template, _vocab())).user
    assert "Grabstock" in user and other not in user


def test_inventor_v3_shows_a_material_by_id_and_by_its_name(settings: Settings) -> None:
    """The answer must name materials by id, so the id stays; the name is only what it calls it."""
    job = GenerationJob.model_validate(_example("generation-job.invention-made-material.json"))
    template = load_prompt(settings.prompts_dir, "invention/invent", 3)
    user = template.render(inventor_values(job, template, _vocab(), "")).user
    assert f'- {MADE} (you call it "Wasserschale")' in user
    old = load_prompt(settings.prompts_dir, "invention/invent", 2)
    assert "you call it" not in old.render(inventor_values(job, old, _vocab(), "")).user


def test_memory_v6_writes_no_runtime_id_for_an_unnamed_invention(settings: Settings) -> None:
    data = _example("generation-job.memory-condense.json")
    data["payload"]["memory"]["events"].append(
        {"tick": 5000, "kind": "use", "about": {"kind": "type", "id": MADE}, "formative": False}
    )
    job = GenerationJob.model_validate(data)
    template = load_prompt(settings.prompts_dir, "memory/condense", 6)
    user = template.render(memory_values(job, template)).user
    assert "- [tick 5000] use (thing 'made thing')" in user
    assert "inv" not in user.split("## New events")[1].split("## Formative")[0].replace("inh", "")
    old = load_prompt(settings.prompts_dir, "memory/condense", 5)
    assert "inv 3fa2c19b7e04" in old.render(memory_values(job, old)).user
