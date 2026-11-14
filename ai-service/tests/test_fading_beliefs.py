"""D1017 G1 (GW1 D1077): a fading belief comes with sure: false and every prompt that lists
beliefs says 'you think so, but are no longer sure'. Absent means sure - nothing changes."""

from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any

from glassworld_ai.invention.flow import (
    _knowledge_text,  # pyright: ignore[reportPrivateUsage]
)
from glassworld_ai.prompts import load_prompt
from glassworld_ai.review import build_values
from glassworld_ai.texts import TextCatalog
from glassworld_ai.validation import ContractValidator
from glassworld_ai.words import UNSURE, knowledge_line
from glassworld_contracts import DecisionRequest, GenerationJob

REPO = Path(__file__).resolve().parents[2]
VALID = REPO / "contracts/examples/valid"
SETTINGS: dict[str, Any] = {
    "source_words": {"heard": "you were told", "experienced": "you tried it yourself"},
    "subject_kinds": {"type": "thing", "object": "thing", "inhabitant": "person"},
}


def _load(name: str) -> dict[str, Any]:
    return json.loads((VALID / name).read_text("utf-8"))


def test_the_contract_takes_sure_false_and_an_old_request_stays_valid() -> None:
    validator = ContractValidator(REPO / "contracts/schemas")
    assert validator.errors("decision-request", _load("decision-request.fading-belief.json")) == []
    assert validator.errors("decision-request", _load("decision-request.hungry-berries.json")) == []


def test_the_knowledge_line_says_the_inhabitant_is_no_longer_sure() -> None:
    fading = DecisionRequest.model_validate(_load("decision-request.fading-belief.json"))
    sure = DecisionRequest.model_validate(_load("decision-request.hungry-berries.json"))
    line = knowledge_line(fading.inhabitant.knowledge[0], SETTINGS)
    assert line == (
        f"- thing 'apple tree' is edible ({UNSURE}; sure 0.6; you were told (inh-0002))"
    )
    assert UNSURE not in knowledge_line(sure.inhabitant.knowledge[0], SETTINGS)


def test_the_inventor_and_the_night_review_say_it_too() -> None:
    data = _load("generation-job.invention-water-vessel.json")
    data["payload"]["invention"]["inhabitant"]["knowledge"] = [
        {
            "subject": {"kind": "type", "id": "clay_lump"},
            "property": "malleable",
            "value": True,
            "confidence": 0.5,
            "source": "experienced",
            "told_by": None,
            "tick": 10,
            "sure": False,
        }
    ]
    job = GenerationJob.model_validate(data)
    text = _knowledge_text(job.payload.invention, SETTINGS)  # type: ignore[union-attr]
    assert text == f"- clay_lump is malleable ({UNSURE}; you tried it yourself)"

    review = _load("generation-job.review-flint.json")
    faded = copy.deepcopy(review)
    for entry in faded["payload"]["review"]["knowledge"]:
        entry["sure"] = False
    template = load_prompt(REPO / "prompts", "review/night", 1)
    texts = TextCatalog.load(REPO / "content/i18n")
    before = build_values(GenerationJob.model_validate(review), template, texts)["knowledge"]
    after = build_values(GenerationJob.model_validate(faded), template, texts)["knowledge"]
    assert UNSURE not in before
    assert f"- Feuerstein: is sharp ({UNSURE})" in after
