# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Talking with an inhabitant (B6 R13, A1342): POST /conversation.

The player waits for the answer: priority PLAYER and the request's deadline. The answer is only a
proposal (B2462): the model picks an outcome (reply, gesture, not understood, decline), a short
sentence in the player's language for a reply, and an expression from the vocabulary. Understanding
is the frame (gestures, single words, sentences) - the core computes every change to it. Every
reply passes the content guard; the core checks again.
"""

from __future__ import annotations

import json
import time
from typing import Any, ClassVar, Protocol, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
from glassworld_ai.llm.jobcall import (
    InvalidOutputError,
    ModelCallSettings,
    ask_model,
    fail,
    recorded_answers,
)
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.queue import ModelQueue, Priority
from glassworld_ai.safety import TextGuard
from glassworld_ai.words import ContextError, humanize, plain_id
from glassworld_contracts import CONTRACT_VERSION
from glassworld_contracts.models.conversation_request_schema import ConversationRequest
from glassworld_contracts.models.conversation_response_schema import ConversationResponse

ROLE = "conversation"
REPLY_MAX = 200
OUTCOMES = ("reply", "gesture", "not_understood", "decline")


class Conversationalist(Protocol):
    async def answer(
        self, request: ConversationRequest, deadline: float
    ) -> ConversationResponse: ...


def _table(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def output_schema(expressions: list[str]) -> dict[str, Any]:
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["outcome", "reply", "expression", "rationale"],
        "properties": {
            "outcome": {"type": "string", "enum": list(OUTCOMES)},
            "reply": {
                "anyOf": [
                    {"type": "string", "minLength": 1, "maxLength": REPLY_MAX},
                    {"type": "null"},
                ]
            },
            "expression": {
                "anyOf": [{"type": "string", "enum": expressions}, {"type": "null"}]
                if expressions
                else {"type": "null"}
            },
            "rationale": {"type": "string", "minLength": 1, "maxLength": 280},
        },
    }


def parse(raw: str, schema: dict[str, Any], guard: TextGuard) -> dict[str, Any]:
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        raise InvalidOutputError(
            f"{'/'.join(str(p) for p in first.path) or '(root)'}: {first.message}"
        )
    values = cast(dict[str, Any], data)
    if values["outcome"] == "reply":
        if not values["reply"]:
            raise InvalidOutputError("a reply needs a sentence")
        verdict = guard.check(values["reply"])
        if not verdict.allowed:
            raise InvalidOutputError(f"reply blocked by the content guard ({verdict.category})")
    else:
        values["reply"] = None  # the contract: a sentence only with outcome reply
    return values


def _level(value: float, bands: dict[str, str]) -> str:
    """The words of the first band whose upper bound is not below the value."""
    for upper, words in sorted(((float(k), v) for k, v in bands.items()), key=lambda b: b[0]):
        if value <= upper:
            return words
    return list(bands.values())[-1]


def values(
    request: ConversationRequest, template: PromptTemplate, expressions: dict[str, str]
) -> dict[str, str]:
    settings = template.settings
    locale = plain_id(request.display_locale)
    language = _table(settings, "language_names").get(locale)
    if language is None:
        raise ContextError(f"no language name for {locale}")
    speaker = request.speaker
    relation = speaker.relation_to_player

    def levels(items: Any) -> str:
        return ", ".join(f"{humanize(plain_id(i.id))} {float(i.value):.1f}" for i in items) or "-"

    lines = "\n".join(
        f"- {'player' if plain_id(line.author) == 'player' else 'you'}: {line.text.text}"
        for line in request.recent_lines
    )
    return {
        "language": language,
        "understanding": _level(
            float(speaker.understanding), _table(settings, "understanding_bands")
        ),
        "trust": f"{float(relation.trust):.1f}",
        "awe": f"{float(relation.awe):.1f}",
        "fear": f"{float(relation.fear):.1f}",
        "needs": levels(speaker.needs),
        "mood": levels(speaker.mood),
        "traits": levels(speaker.traits),
        "recent": lines or "- (nothing yet)",
        "player_text": request.player_text,
        "expressions": "\n".join(f"- {k}: {v}" for k, v in sorted(expressions.items()))
        or "- (none)",
        "reply_max": str(REPLY_MAX),
    }


def _response(
    request: ConversationRequest,
    data: dict[str, Any],
    source: str,
    model: str,
    latency_ms: int,
    attempts: int,
) -> ConversationResponse:
    locale = plain_id(request.display_locale)
    return ConversationResponse.model_validate(
        {
            "contract_version": CONTRACT_VERSION,
            "request_id": plain_id(request.request_id),
            "tick": request.tick,
            "outcome": data["outcome"],
            "reply": {"locale": locale, "text": data["reply"]} if data["reply"] else None,
            "expression": data["expression"],
            "rationale": data["rationale"][:280],
            "meta": {
                "source": source,
                "model": model[:64],
                "latency_ms": latency_ms,
                "attempts": attempts,
            },
        }
    )


class OllamaConversation:
    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        settings: ModelCallSettings,
        log: CallLog,
        guard: TextGuard,
        expressions: dict[str, str],
    ) -> None:
        self._client, self._queue, self._template = client, queue, template
        self._settings, self._log, self._guard = settings, log, guard
        self._expressions = expressions

    async def answer(self, request: ConversationRequest, deadline: float) -> ConversationResponse:
        schema = output_schema(sorted(self._expressions))
        rendered = self._template.render(values(request, self._template, self._expressions))
        answer = await ask_model(
            role=ROLE,
            job=request,
            rendered=rendered,
            schema=schema,
            parse=lambda raw: parse(raw, schema, self._guard),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
            priority=Priority.PLAYER,
            deadline=deadline,
        )
        return _response(
            request, answer.value, "model", self._client.model, answer.latency_ms, answer.attempts
        )


class MockConversation:
    """Deterministic stand-in, the same rule as the core's MockConversationPort.

    Little understanding gives gestures or not understood, more a short reply in the player's
    language.
    """

    TEXTS: ClassVar[dict[str, str]] = {"de": "Hm, ja.", "en": "Hm, yes."}

    async def answer(self, request: ConversationRequest, deadline: float) -> ConversationResponse:
        started = time.monotonic()
        roll = (int(request.seed) % 100) / 100
        understanding = float(request.speaker.understanding)
        if roll > understanding + 0.6:
            outcome, reply, expression = "not_understood", None, None
        elif understanding < 0.3:
            outcome, reply, expression = "gesture", None, "wave"
        else:
            locale = plain_id(request.display_locale)
            outcome, reply, expression = "reply", self.TEXTS.get(locale, self.TEXTS["en"]), None
        data = {"outcome": outcome, "reply": reply, "expression": expression, "rationale": "mock"}
        return _response(request, data, "mock", "mock", int((time.monotonic() - started) * 1000), 1)


class ReplayConversation:
    """Answers from the call log: the first valid model answer recorded for the request id."""

    def __init__(self, log: CallLog, guard: TextGuard, expressions: dict[str, str]) -> None:
        self._log, self._guard, self._expressions = log, guard, expressions
        self._answers: dict[str, dict[str, object]] | None = None

    async def answer(self, request: ConversationRequest, deadline: float) -> ConversationResponse:
        request_id = plain_id(request.request_id)
        if self._answers is None:  # read once; replays use a finished log
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(request_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for request {request_id}")
        try:
            data = parse(
                str(row["raw_output"]), output_schema(sorted(self._expressions)), self._guard
            )
        except InvalidOutputError as error:
            raise fail(
                "model_unavailable", f"recorded answer does not fit request {request_id}"
            ) from error
        attempts = row.get("attempt", 1)
        return _response(
            request,
            data,
            "replay",
            str(row.get("model", "replay")),
            0,
            attempts if isinstance(attempts, int) else 1,
        )
