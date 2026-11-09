"""Natural thoughts (W1687/W1688, decider prompt v4): the check and the stand-in from data."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import httpx
import pytest

from conftest import make_app, post
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import ModelSettings, OllamaDecider
from glassworld_ai.decider.thoughts import stand_in, thought_problem
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.texts import TextCatalog
from glassworld_ai.vocabulary import Vocabulary

REPO = Path(__file__).resolve().parents[2]
REQUEST = json.loads(
    (REPO / "contracts/examples/valid/decision-request.hungry-berries.json").read_text("utf-8")
)
V4 = load_prompt(REPO / "prompts", "decider/decide", 4).settings


@pytest.mark.parametrize(
    ("text", "reason"),
    [
        ("Ich zeige dem Menschen hearby etwas, das ich weiss.", "blocked_word"),
        ("Ich gehe zu inh-0002 hin.", "id"),
        ("Die red_berry sieht gut aus.", "id"),
        (
            "Ich gehe zum anderen und zeige ihm jetzt wirklich das ganz komische Ding hier.",
            "too_long",
        ),
        ("Boah, hab ich einen Hunger.", None),
        ("Ob die Ost-Seite schoener ist?", None),  # a German compound is no id
        ("Puh, bin ich müde.", None),
        ("Ich zeige dem Menschen die Beeren.", "blocked_word"),  # TL F5289
        ("Ob die Person das kennt?", "blocked_word"),
        ("Ob die andere am Bach das kennt?", None),  # W1693: only the prompt forbids descriptions
        # ("andere" stays allowed in the check: "die anderen Beeren" is plain German)
    ],
)
def test_the_check_finds_what_a_player_must_not_read(text: str, reason: str | None) -> None:
    assert thought_problem(text, "de", V4) == reason


def test_english_thoughts_are_fine_in_english() -> None:
    assert thought_problem("Let me look at this thing here.", "en", V4) is None


def test_older_prompts_have_no_check() -> None:
    v3 = load_prompt(REPO / "prompts", "decider/decide", 3).settings
    assert thought_problem("the object hearby", "de", v3) is None


def test_every_action_has_a_stand_in_in_both_languages() -> None:
    texts = TextCatalog.load(REPO / "content/i18n")
    vocab = Vocabulary.load(REPO / "content/vocabulary")
    for action in vocab.ids("action"):
        for locale in ("de", "en"):
            assert stand_in(texts, locale, action), (locale, action)


def _decider(settings: Settings, content: str) -> tuple[Any, CallLog]:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"message": {"role": "assistant", "content": content}})

    log = CallLog(settings.log_dir / "decider.jsonl")
    decider = OllamaDecider(
        client=OllamaClient(
            "http://ollama.test",
            "qwen3.5:9b",
            httpx.AsyncClient(transport=httpx.MockTransport(handler)),
            keep_alive="5m",
        ),
        queue=ModelQueue(8),
        template=load_prompt(settings.prompts_dir, "decider/decide", 4),
        vocab=Vocabulary.load(settings.content_dir / "vocabulary"),
        settings=ModelSettings(temperature=0.7, num_ctx=8192, char_budget=6000, max_retries=0),
        log=log,
        texts=TextCatalog.load(settings.content_dir / "i18n"),
    )
    return make_app(settings, decider=decider), log


def test_a_bad_thought_is_replaced_without_asking_again(settings: Settings) -> None:
    """One model call, the decision stays, only the bubble text is the data's stand-in."""
    answer = json.dumps(
        {
            "rationale": "Check the berries.",
            "option_id": "o1",
            "expression": None,
            "thought": "Ich zeige dem Menschen hearby etwas, um uns zu verbinden.",
        }
    )
    app, log = _decider(settings, answer)
    body = post(app, "/decide", REQUEST).json()
    action = next(o["action"] for o in REQUEST["options"] if o["option_id"] == "o1")
    texts = TextCatalog.load(settings.content_dir / "i18n")
    assert body["option_id"] == "o1"
    assert body["thought"]["text"] == stand_in(texts, "de", action)
    rows = list(log.read())
    assert len(rows) == 1 and "thought replaced (blocked_word)" in str(rows[0]["detail"])


def test_a_good_thought_stays(settings: Settings) -> None:
    answer = json.dumps(
        {"rationale": "Hungry.", "option_id": "o1", "expression": None,
         "thought": "Hm, die Beeren sehen lecker aus."}
    )  # fmt: skip
    app, _ = _decider(settings, answer)
    assert post(app, "/decide", REQUEST).json()["thought"]["text"] == (
        "Hm, die Beeren sehen lecker aus."
    )


def test_the_same_inhabitant_does_not_repeat_itself_soon() -> None:
    """Marcel W1696: silence instead of the same sentence again within the span."""
    from glassworld_ai.decider.thoughts import ThoughtMemory

    memory = ThoughtMemory()
    said = "Ich gehe zu dem vertrauenswürdigen Menschen."
    assert not memory.repeated("inh-1", 100, said, V4)
    memory.remember("inh-1", 100, said)
    assert memory.repeated("inh-1", 200, said, V4)  # within 240 ticks
    assert memory.repeated("inh-1", 200, "Ich gehe jetzt zu dem vertrauenswürdigen Menschen.", V4)
    assert not memory.repeated("inh-1", 200, "Boah, hab ich einen Hunger.", V4)
    assert not memory.repeated("inh-2", 200, said, V4)  # another inhabitant may say it
    assert not memory.repeated("inh-1", 400, said, V4)  # the span is over


def test_a_repeated_thought_is_left_out_but_the_decision_stays(settings: Settings) -> None:
    answer = json.dumps(
        {"rationale": "Hungry.", "option_id": "o1", "expression": None,
         "thought": "Hm, die Beeren sehen lecker aus."}
    )  # fmt: skip
    app, log = _decider(settings, answer)
    first = post(app, "/decide", REQUEST).json()
    later = dict(
        REQUEST, request_id="0b7f2d4e-6c1a-4f3b-9d2e-1a2b3c4d5e6f", tick=REQUEST["tick"] + 10
    )
    second = post(app, "/decide", later).json()
    assert first["thought"]["text"] == "Hm, die Beeren sehen lecker aus."
    assert second["option_id"] == "o1" and second["thought"] is None
    assert "thought left out (repeat)" in str(list(log.read())[-1]["detail"])
