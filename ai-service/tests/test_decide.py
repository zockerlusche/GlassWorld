"""POST /decide end to end (in-process), with mock, fake Ollama and replay backends."""

from __future__ import annotations

import asyncio
import json
from collections.abc import Callable
from pathlib import Path
from typing import Any

import httpx
import pytest
from fastapi import FastAPI

from conftest import TOKEN, get, make_app, post
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import (
    ModelSettings,
    OllamaDecider,
    ReplayDecider,
)
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.validation import ContractValidator
from glassworld_ai.vocabulary import Vocabulary

REPO = Path(__file__).resolve().parents[2]
EXAMPLES = REPO / "contracts" / "examples"
REQUEST = json.loads(
    (EXAMPLES / "valid" / "decision-request.hungry-berries.json").read_text("utf-8")
)

Handler = Callable[[httpx.Request], Any]


def _good(option: str = "o1", thought: str | None = "Was sind das fuer Beeren?") -> str:
    return json.dumps(
        {
            "rationale": "Unknown berries, check first.",
            "option_id": option,
            "expression": "ponder",
            "thought": thought,
        }
    )


def _ollama_answer(content: str) -> httpx.Response:
    return httpx.Response(200, json={"message": {"role": "assistant", "content": content}})


def _ollama_app(
    settings: Settings, handler: Handler, *, retries: int = 2
) -> tuple[FastAPI, CallLog]:
    log = CallLog(settings.log_dir / "decider.jsonl")
    client = OllamaClient(
        "http://ollama.test",
        "qwen3.5:9b",
        httpx.AsyncClient(transport=httpx.MockTransport(handler)),
        keep_alive="5m",
    )
    decider = OllamaDecider(
        client=client,
        queue=ModelQueue(8),
        template=load_prompt(settings.prompts_dir, "decider/decide", 1),
        vocab=Vocabulary.load(settings.content_dir / "vocabulary"),
        settings=ModelSettings(
            temperature=0.7, num_ctx=8192, char_budget=6000, max_retries=retries
        ),
        log=log,
    )
    return make_app(settings, decider=decider), log


def _assert_contract(body: object, schema: str, settings: Settings) -> None:
    assert ContractValidator(settings.schemas_dir).errors(schema, body) == []


# --- mock backend -------------------------------------------------------------------------


def test_mock_decider_returns_a_valid_response(settings: Settings) -> None:
    response = post(make_app(settings), "/decide", REQUEST)
    assert response.status_code == 200
    body = response.json()
    _assert_contract(body, "decision-response", settings)
    assert body["request_id"] == REQUEST["request_id"]
    assert body["tick"] == REQUEST["tick"]
    assert body["option_id"] in {o["option_id"] for o in REQUEST["options"]}
    assert body["meta"]["source"] == "mock"
    assert body["thought"]["locale"] == "de"


def test_mock_decider_is_deterministic(settings: Settings) -> None:
    app = make_app(settings)
    assert post(app, "/decide", REQUEST).json() == post(app, "/decide", REQUEST).json()


# --- request validation and access --------------------------------------------------------


def test_invalid_json_is_bad_request(settings: Settings) -> None:
    response = post(make_app(settings), "/decide", b"{not json")
    assert response.status_code == 422
    assert response.json()["code"] == "bad_request"


@pytest.mark.parametrize(
    "example",
    [
        "decision-request.duplicate-weather",
        "decision-request.no-options",
        "decision-request.season-not-allowed",
        "decision-request.wrong-contract-version",
    ],
)
def test_invalid_examples_are_rejected_before_the_model(settings: Settings, example: str) -> None:
    body = json.loads((EXAMPLES / "invalid" / f"{example}.json").read_text("utf-8"))
    response = post(make_app(settings), "/decide", body)
    assert response.status_code == 422, example
    _assert_contract(response.json(), "service-error", settings)


def test_decide_needs_the_session_token(settings: Settings) -> None:
    assert post(make_app(settings), "/decide", REQUEST, headers={}).status_code == 403
    assert (
        post(make_app(settings), "/decide", REQUEST, headers={"x-gw-token": TOKEN}).status_code
        == 200
    )


# --- Ollama backend against a fake Ollama ---------------------------------------------------


def test_model_answer_becomes_a_response_and_is_logged(settings: Settings) -> None:
    seen: list[dict[str, Any]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(json.loads(request.content))
        return _ollama_answer(_good())

    app, log = _ollama_app(settings, handler)
    body = post(app, "/decide", REQUEST).json()
    _assert_contract(body, "decision-response", settings)
    assert body["option_id"] == "o1"
    assert body["expression"] == "ponder"
    assert body["thought"] == {"locale": "de", "text": "Was sind das fuer Beeren?"}
    assert body["meta"]["source"] == "model" and body["meta"]["attempts"] == 1
    # the model was constrained to exactly the offered option ids
    schema = seen[0]["format"]
    assert schema["properties"]["option_id"]["enum"] == ["o1", "o2", "o3", "o4"]
    assert seen[0]["options"]["seed"] == REQUEST["seed"]
    rows = log.read()
    assert rows[0]["outcome"] == "ok"
    assert rows[0]["prompt_version"] == "decider/decide.v1"
    assert rows[0]["request"] is not None


def test_invalid_answer_is_retried_with_next_seed(settings: Settings) -> None:
    answers = iter([_ollama_answer('{"option_id": "o9"}'), _ollama_answer(_good("o3", None))])
    seeds: list[int] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seeds.append(json.loads(request.content)["options"]["seed"])
        return next(answers)

    app, log = _ollama_app(settings, handler)
    body = post(app, "/decide", REQUEST).json()
    assert body["option_id"] == "o3"
    assert body["thought"] is None
    assert body["meta"]["attempts"] == 2
    assert seeds == [REQUEST["seed"], REQUEST["seed"] + 1]
    assert [r["outcome"] for r in log.read()] == ["invalid", "ok"]


def test_three_invalid_answers_give_invalid_model_output(settings: Settings) -> None:
    app, log = _ollama_app(settings, lambda _: _ollama_answer("not json at all"))
    response = post(app, "/decide", REQUEST)
    assert response.status_code == 502
    body = response.json()
    _assert_contract(body, "service-error", settings)
    assert body["code"] == "invalid_model_output"
    assert body["request_id"] == REQUEST["request_id"] and body["tick"] == REQUEST["tick"]
    assert body["retryable"] is True
    assert len(log.read()) == 3


def test_unreachable_ollama_is_model_unavailable(settings: Settings) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused", request=request)

    app, _ = _ollama_app(settings, handler)
    response = post(app, "/decide", REQUEST)
    assert response.status_code == 503
    assert response.json()["code"] == "model_unavailable"


def test_slow_model_is_a_timeout_without_substitute_decision(settings: Settings) -> None:
    async def handler(_: httpx.Request) -> httpx.Response:
        await asyncio.sleep(1.0)
        return _ollama_answer(_good())

    app, _ = _ollama_app(settings, handler)
    response = post(app, "/decide", {**REQUEST, "deadline_ms": 200})
    assert response.status_code == 504
    assert response.json()["code"] == "timeout"


# --- replay backend -------------------------------------------------------------------------


def test_replay_answers_like_the_recorded_model_call(settings: Settings) -> None:
    app, log = _ollama_app(settings, lambda _: _ollama_answer(_good("o2", "Ich probiere es.")))
    recorded = post(app, "/decide", REQUEST).json()
    replay_app = make_app(
        settings, decider=ReplayDecider(log, Vocabulary.load(settings.content_dir / "vocabulary"))
    )
    replayed = post(replay_app, "/decide", REQUEST).json()
    assert replayed["meta"]["source"] == "replay"
    for key in ("option_id", "expression", "thought", "rationale", "request_id", "tick"):
        assert replayed[key] == recorded[key]


def test_replay_without_record_is_model_unavailable(settings: Settings) -> None:
    log = CallLog(settings.log_dir / "empty.jsonl")
    app = make_app(
        settings, decider=ReplayDecider(log, Vocabulary.load(settings.content_dir / "vocabulary"))
    )
    assert post(app, "/decide", REQUEST).json()["code"] == "model_unavailable"


def test_ollama_read_timeout_is_a_retryable_timeout_with_ids(settings: Settings) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ReadTimeout("slow", request=request)

    app, log = _ollama_app(settings, handler)
    response = post(app, "/decide", REQUEST)
    assert response.status_code == 504
    body = response.json()
    assert (body["code"], body["retryable"]) == ("timeout", True)
    assert (body["request_id"], body["tick"]) == (REQUEST["request_id"], REQUEST["tick"])
    assert [r["outcome"] for r in log.read()] == ["timeout"]


def test_replay_with_unfitting_recorded_answer_is_retryable(settings: Settings) -> None:
    app, log = _ollama_app(settings, lambda _: _ollama_answer(_good("o4")))
    post(app, "/decide", REQUEST)
    fewer = {**REQUEST, "options": REQUEST["options"][:2]}  # o4 no longer offered
    vocab = Vocabulary.load(settings.content_dir / "vocabulary")
    replay_app = make_app(settings, decider=ReplayDecider(log, vocab))
    response = post(replay_app, "/decide", fewer)
    assert response.status_code == 503
    assert response.json()["retryable"] is True


def test_below_minimum_tier_gives_model_unavailable(settings: Settings) -> None:
    from glassworld_ai.hardware import HardwareInfo

    weak = HardwareInfo(gpus=(), cpu_name=None, logical_cpus=4, ram_mib=8192)
    app = make_app(settings, hardware=weak)
    response = post(app, "/decide", REQUEST)
    assert response.status_code == 503
    assert response.json()["code"] == "model_unavailable"
    health = get(app, "/health").json()
    assert health["status"] == "degraded" and "minimum tier" in health["detail"]
