"""Talking with an inhabitant (B6 R13, A1342): parsing, prompt, model, replay, mock, endpoint."""

from __future__ import annotations

import asyncio
import json
import time
from pathlib import Path
from typing import Any

import httpx
import pytest

from conftest import TEST_HOST, TOKEN, FakeProbes
from glassworld_ai.app import create_app
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.conversation import (
    MockConversation,
    OllamaConversation,
    ReplayConversation,
    output_schema,
    parse,
    values,
)
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.llm.jobcall import InvalidOutputError, ModelCallSettings
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.safety import TextGuard
from glassworld_ai.validation import ContractValidator
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts.models.conversation_request_schema import ConversationRequest

REPO = Path(__file__).resolve().parents[2]
REQUEST_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/conversation-request.greeting.json").read_text("utf-8")
)
REQUEST = ConversationRequest.model_validate(REQUEST_DATA)
GUARD = TextGuard.from_file(REPO / "content/safety/rules.json")
VOCAB = Vocabulary.load(REPO / "content/vocabulary")
EXPRESSIONS = {e: VOCAB.describe("expression", e) or e for e in VOCAB.ids("expression")}
SCHEMA = output_schema(sorted(EXPRESSIONS))
GOOD: dict[str, Any] = {
    "outcome": "reply",
    "reply": "A little cold, yes.",
    "expression": "wave",
    "rationale": "Understands simple sentences; answers shortly.",
}


def _raw(data: dict[str, Any]) -> str:
    return json.dumps(data, ensure_ascii=False)


def test_a_good_answer_parses() -> None:
    assert parse(_raw(GOOD), SCHEMA, GUARD)["reply"] == "A little cold, yes."


@pytest.mark.parametrize(
    ("changes", "problem"),
    [
        ({"reply": None}, "needs a sentence"),
        ({"reply": "Then kill it."}, "content guard"),
        ({"expression": "fly_away"}, "expression"),
        ({"outcome": "obey"}, "outcome"),
        ({"reply": "x" * 201}, "reply"),
    ],
)
def test_bad_answers_are_retried(changes: dict[str, Any], problem: str) -> None:
    with pytest.raises(InvalidOutputError, match=problem):
        parse(_raw({**GOOD, **changes}), SCHEMA, GUARD)


@pytest.mark.parametrize("outcome", ["gesture", "not_understood", "decline"])
def test_only_a_reply_carries_a_sentence(outcome: str) -> None:
    assert parse(_raw({**GOOD, "outcome": outcome}), SCHEMA, GUARD)["reply"] is None


def test_prompt_values(settings: Settings) -> None:
    template = load_prompt(
        settings.prompts_dir, settings.conversation_prompt, settings.conversation_prompt_version
    )
    filled = values(REQUEST, template, EXPRESSIONS)
    assert filled["language"] == "English"
    assert filled["understanding"].startswith("a little")  # 0.35: single words
    assert filled["player_text"] == "Hello, are you cold?"
    assert "player: Hello." in filled["recent"]
    assert "wave" in filled["expressions"]
    rendered = template.render(filled)
    assert "$" not in rendered.system + rendered.user


def _client(answers: list[str]) -> OllamaClient:
    queue = iter(answers)

    def fake(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"message": {"role": "assistant", "content": next(queue)}})

    return OllamaClient(
        "http://ollama.test",
        "qwen3.5:9b",
        httpx.AsyncClient(transport=httpx.MockTransport(fake)),
        keep_alive="5m",
    )


def test_the_model_answer_becomes_a_contract_response_and_replays(settings: Settings) -> None:
    log = CallLog(settings.log_dir / "conversation.jsonl")
    talker = OllamaConversation(
        client=_client([_raw({**GOOD, "reply": "Then kill it."}), _raw(GOOD)]),
        queue=ModelQueue(8),
        template=load_prompt(settings.prompts_dir, "conversation/reply", 1),
        settings=ModelCallSettings(temperature=0.7, num_ctx=8192, max_retries=2, call_timeout_s=30),
        log=log,
        guard=GUARD,
        expressions=EXPRESSIONS,
    )
    validator = ContractValidator(settings.schemas_dir)
    response = asyncio.run(talker.answer(REQUEST, time.monotonic() + 8))
    dumped = response.model_dump(mode="json")
    assert validator.errors("conversation-response", dumped) == []
    assert dumped["meta"]["attempts"] == 2  # the guard sent the first answer back
    assert dumped["reply"] == {"locale": "en", "text": "A little cold, yes."}

    replayed = asyncio.run(
        ReplayConversation(log, GUARD, EXPRESSIONS).answer(REQUEST, time.monotonic() + 8)
    )
    assert replayed.model_dump(mode="json")["reply"] == dumped["reply"]
    other = REQUEST.model_copy(update={"request_id": "00000000-0000-8000-8000-000000000001"})
    with pytest.raises(JobFailedError):
        asyncio.run(ReplayConversation(log, GUARD, EXPRESSIONS).answer(other, time.monotonic() + 8))


@pytest.mark.parametrize(
    ("understanding", "seed", "outcome"),
    [(0.1, 10, "gesture"), (0.35, 10, "reply"), (0.1, 99, "not_understood")],
)
def test_the_mock_follows_the_cores_rule(understanding: float, seed: int, outcome: str) -> None:
    speaker = REQUEST.speaker.model_copy(update={"understanding": understanding})
    request = REQUEST.model_copy(update={"speaker": speaker, "seed": seed})
    response = asyncio.run(MockConversation().answer(request, time.monotonic() + 8))
    assert response.model_dump(mode="json")["outcome"] == outcome


def test_endpoint(settings: Settings) -> None:
    app = create_app(
        settings.model_copy(update={"decider_backend": "mock"}),
        allowed_hosts=frozenset({TEST_HOST}),
        probes=FakeProbes(),
        decider=MockDecider(),
        conversation=MockConversation(),
    )
    validator = ContractValidator(settings.schemas_dir)

    async def scenario() -> None:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            good = await client.post("/conversation", json=REQUEST_DATA)
            assert good.status_code == 200
            assert validator.errors("conversation-response", good.json()) == []
            bad = await client.post("/conversation", json={**REQUEST_DATA, "player_text": ""})
            assert (bad.status_code, bad.json()["code"]) == (422, "bad_request")
            assert bad.json()["request_id"] == REQUEST_DATA["request_id"]

    asyncio.run(scenario())
