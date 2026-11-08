"""Help suggestions for the player (B6 R8/R10, F5238): parsing rules, model, replay, endpoint."""

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
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.help import MockHelp, OllamaHelp, ReplayHelp, output_schema, parse, values
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.llm.jobcall import InvalidOutputError, ModelCallSettings
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.safety import TextGuard
from glassworld_ai.validation import ContractValidator
from glassworld_contracts.models.help_suggestions_request_schema import HelpSuggestionsRequest

REPO = Path(__file__).resolve().parents[2]
REQUEST_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/help-suggestions-request.dry-creek.json").read_text("utf-8")
)
REQUEST = HelpSuggestionsRequest.model_validate(REQUEST_DATA)
GUARD = TextGuard.from_file(REPO / "content/safety/rules.json")
SCHEMA = output_schema(REQUEST)
GOOD: dict[str, Any] = {
    "rationale": "Creek is dry; a bowl bridges, a hint keeps own effort.",
    "suggestions": [
        {
            "kind": "answer",
            "gift": None,
            "text": "Vielleicht gibt es hinter dem Hügel eine Quelle.",
        },
        {
            "kind": "gift",
            "gift": {"type_id": "water_bowl", "amount": 1},
            "text": "Eine Schale Wasser.",
        },
    ],
}


def _raw(data: dict[str, Any]) -> str:
    return json.dumps(data, ensure_ascii=False)


def _with(index: int, **changes: Any) -> dict[str, Any]:
    data = json.loads(json.dumps(GOOD))
    data["suggestions"][index].update(changes)
    return data


def test_a_good_answer_parses() -> None:
    assert parse(_raw(GOOD), SCHEMA, REQUEST, GUARD)["suggestions"][1]["gift"]["amount"] == 1


@pytest.mark.parametrize(
    ("data", "problem"),
    [
        (_with(1, gift={"type_id": "water_bowl", "amount": 3}), "above what the world allows"),
        (_with(1, gift={"type_id": "golden_cup", "amount": 1}), "golden_cup"),
        (_with(1, gift=None), "needs a gift"),
        (_with(0, kind="gift", gift={"type_id": "water_bowl", "amount": 1}), "encouraging answer"),
        (_with(0, text="Then kill it."), "content guard"),
    ],
)
def test_bad_answers_are_retried(data: dict[str, Any], problem: str) -> None:
    with pytest.raises(InvalidOutputError, match=problem):
        parse(_raw(data), SCHEMA, REQUEST, GUARD)


def test_an_answer_never_carries_a_gift() -> None:
    data = _with(0, gift={"type_id": "water_bowl", "amount": 1})
    assert parse(_raw(data), SCHEMA, REQUEST, GUARD)["suggestions"][0]["gift"] is None


def test_without_allowed_gifts_only_answers() -> None:
    request = REQUEST.model_copy(update={"allowed_gifts": []})
    schema = output_schema(request)
    assert schema["properties"]["suggestions"]["items"]["properties"]["kind"]["enum"] == ["answer"]
    assert schema["properties"]["suggestions"]["maxItems"] == 3


def test_prompt_values(settings: Settings) -> None:
    template = load_prompt(settings.prompts_dir, settings.help_prompt, settings.help_prompt_version)
    filled = values(REQUEST, template)
    assert filled["language"] == "German"
    assert "4 times" in filled["trigger"]
    assert "water_bowl (at most 2)" in filled["gifts"]
    rendered = template.render(filled)
    assert "$" not in rendered.system + rendered.user


def _client(answers: list[str], seen: list[dict[str, Any]]) -> OllamaClient:
    queue = iter(answers)

    def fake(request: httpx.Request) -> httpx.Response:
        seen.append(json.loads(request.content))
        return httpx.Response(200, json={"message": {"role": "assistant", "content": next(queue)}})

    return OllamaClient(
        "http://ollama.test",
        "qwen3.5:9b",
        httpx.AsyncClient(transport=httpx.MockTransport(fake)),
        keep_alive="5m",
    )


def test_the_model_answer_becomes_a_contract_response_and_replays(settings: Settings) -> None:
    seen: list[dict[str, Any]] = []
    bad = _raw(_with(1, gift={"type_id": "water_bowl", "amount": 9}))
    log = CallLog(settings.log_dir / "help.jsonl")
    helper = OllamaHelp(
        client=_client([bad, _raw(GOOD)], seen),
        queue=ModelQueue(8),
        template=load_prompt(settings.prompts_dir, "help/suggest", 1),
        settings=ModelCallSettings(temperature=0.7, num_ctx=8192, max_retries=2, call_timeout_s=30),
        log=log,
        guard=GUARD,
    )
    validator = ContractValidator(settings.schemas_dir)
    response = asyncio.run(helper.suggest(REQUEST, time.monotonic() + 8))
    dumped = response.model_dump(mode="json")
    assert validator.errors("help-suggestions-response", dumped) == []
    assert dumped["meta"]["attempts"] == 2
    assert [s["options"]["seed"] for s in seen] == [99, 100]
    assert dumped["suggestions"][0]["reason"] == {
        "locale": "de",
        "text": GOOD["suggestions"][0]["text"],
    }

    replayed = asyncio.run(ReplayHelp(log, GUARD).suggest(REQUEST, time.monotonic() + 8))
    assert replayed.model_dump(mode="json")["suggestions"] == dumped["suggestions"]
    other = REQUEST.model_copy(update={"request_id": "00000000-0000-8000-8000-000000000001"})
    with pytest.raises(JobFailedError):
        asyncio.run(ReplayHelp(log, GUARD).suggest(other, time.monotonic() + 8))


def test_mock_answers_first() -> None:
    response = asyncio.run(MockHelp().suggest(REQUEST, time.monotonic() + 8))
    kinds = [s.kind.value for s in response.suggestions]
    assert kinds == ["answer", "gift"]


def test_endpoint(settings: Settings) -> None:
    app = create_app(
        settings.model_copy(update={"decider_backend": "mock"}),
        allowed_hosts=frozenset({TEST_HOST}),
        probes=FakeProbes(),
        decider=MockDecider(),
        helper=MockHelp(),
    )
    validator = ContractValidator(settings.schemas_dir)

    async def scenario() -> None:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            good = await client.post("/help-suggestions", json=REQUEST_DATA)
            assert good.status_code == 200
            assert validator.errors("help-suggestions-response", good.json()) == []
            bad = await client.post(
                "/help-suggestions", json={**REQUEST_DATA, "max_suggestions": 9}
            )
            assert (bad.status_code, bad.json()["code"]) == (422, "bad_request")
            assert bad.json()["request_id"] == REQUEST_DATA["request_id"]

    asyncio.run(scenario())
