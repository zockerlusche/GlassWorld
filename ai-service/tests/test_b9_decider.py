"""B9 in the decider (R26/R27, A1389): prompt v5 sections, names and gossip by option."""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

import httpx
import pytest

from conftest import make_app, post
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import ModelSettings, OllamaDecider
from glassworld_ai.decider.context import build_values
from glassworld_ai.decider.output import readable_name
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.texts import TextCatalog
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import DecisionRequest

REPO = Path(__file__).resolve().parents[2]
SNAPSHOTS = Path(__file__).resolve().parent / "snapshots"
FAMILY = json.loads(
    (REPO / "contracts/examples/valid/decision-request.b9-family.json").read_text("utf-8")
)


def _render(data: dict[str, Any], version: int = 5) -> str:
    template = load_prompt(REPO / "prompts", "decider/decide", version)
    values = build_values(
        DecisionRequest.model_validate(data),
        template,
        Vocabulary.load(REPO / "content/vocabulary"),
        6000,
        TextCatalog.load(REPO / "content/i18n"),
    )
    rendered = template.render(values)
    return (
        f"[{rendered.version}]\n\n=== system ===\n{rendered.system}\n\n"
        f"=== user ===\n{rendered.user}\n"
    )


def test_v5_snapshot_of_a_family() -> None:
    text = _render(FAMILY)
    snapshot = SNAPSHOTS / "decider-decide.v5.b9-family.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert text == snapshot.read_text(encoding="utf-8")


def test_v5_shows_life_family_customs_and_names() -> None:
    user = _render(FAMILY).split("=== user ===")[1]
    assert "You are an adult, 31 years old." in user
    assert 'your partner: "Mara"' in user  # the name this inhabitant knows
    assert "[inh-0003] - has died" in user
    assert 'a saying of your people: "Wer früh sammelt, isst warm."' in user
    assert "a belief:" in user and "(sure 0.3)" in user
    assert "(a child)" in user  # the perceived child is marked for R7/R27


def test_v5_without_b9_fields_says_nothing_particular() -> None:
    plain = json.loads(
        (REPO / "contracts/examples/valid/decision-request.hungry-berries.json").read_text("utf-8")
    )
    user = _render(plain).split("=== user ===")[1]
    assert "- (nothing particular)" in user and "- (no customs yet)" in user


@pytest.mark.parametrize(
    ("given", "expected"),
    [
        ("tavi", "Tavi"),
        ("Moro", "Moro"),
        ("Ma ra", None),
        ("R2D2", None),
        ("Ölf", None),
        ("", None),
    ],
)
def test_names_are_one_readable_latin_word(given: str, expected: str | None) -> None:
    assert readable_name(given) == expected


def _app(settings: Settings, answer: dict[str, Any]) -> Any:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200, json={"message": {"role": "assistant", "content": json.dumps(answer)}}
        )

    decider = OllamaDecider(
        client=OllamaClient(
            "http://ollama.test",
            "qwen3.5:9b",
            httpx.AsyncClient(transport=httpx.MockTransport(handler)),
            keep_alive="5m",
        ),
        queue=ModelQueue(8),
        template=load_prompt(settings.prompts_dir, "decider/decide", 5),
        vocab=Vocabulary.load(settings.content_dir / "vocabulary"),
        settings=ModelSettings(temperature=0.7, num_ctx=8192, char_budget=6000, max_retries=0),
        log=CallLog(settings.log_dir / "decider.jsonl"),
        texts=TextCatalog.load(settings.content_dir / "i18n"),
    )
    return make_app(settings, decider=decider)


def test_a_name_without_the_naming_option_is_dropped(settings: Settings) -> None:
    """R26: the decider decides only for its own option; a stray name never reaches the core."""
    answer = {
        "rationale": "Look at the berries.",
        "option_id": "o1",
        "expression": None,
        "thought": "Was ist das für eine Beere?",
        "name_text": "Tavi",
        "gossip": {"about": "inh-0002", "property": "edible", "value": True},
    }
    body = post(_app(settings, answer), "/decide", FAMILY).json()
    assert body["option_id"] == "o1"
    assert body.get("name_text") is None and body.get("gossip") is None
