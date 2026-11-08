# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Help suggestions for the player (B6 R8/R10, F5238, GW3 #19169): POST /help-suggestions.

A direct request (the player waits in the UI) with priority PLAYER and the request's deadline.
Gifts only from the allowed list and up to the allowed amount; at least one encouraging answer
(own effort first, F5238); every player-visible text passes the content guard (B6 R13). The
core checks again and drops what it does not allow.
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
from glassworld_contracts.models.help_suggestions_request_schema import HelpSuggestionsRequest
from glassworld_contracts.models.help_suggestions_response_schema import HelpSuggestionsResponse

ROLE = "help"
TEXT_MAX = 200


class HelpSuggester(Protocol):
    async def suggest(
        self, request: HelpSuggestionsRequest, deadline: float
    ) -> HelpSuggestionsResponse: ...


def _table(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def output_schema(request: HelpSuggestionsRequest) -> dict[str, Any]:
    gifts = [plain_id(g.type_id) for g in request.allowed_gifts]
    gift: dict[str, Any] = (
        {
            "anyOf": [
                {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["type_id", "amount"],
                    "properties": {
                        "type_id": {"type": "string", "enum": gifts},
                        "amount": {"type": "integer", "minimum": 1, "maximum": 20},
                    },
                },
                {"type": "null"},
            ]
        }
        if gifts
        else {"type": "null"}
    )
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["rationale", "suggestions"],
        "properties": {
            "rationale": {"type": "string", "minLength": 1, "maxLength": 280},
            "suggestions": {
                "type": "array",
                "minItems": 1,
                "maxItems": int(request.max_suggestions),
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["kind", "gift", "text"],
                    "properties": {
                        "kind": {
                            "type": "string",
                            "enum": ["gift", "answer"] if gifts else ["answer"],
                        },
                        "gift": gift,
                        "text": {"type": "string", "minLength": 1, "maxLength": TEXT_MAX},
                    },
                },
            },
        },
    }


def parse(
    raw: str, schema: dict[str, Any], request: HelpSuggestionsRequest, guard: TextGuard
) -> dict[str, Any]:
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
    limits = {plain_id(g.type_id): int(g.max_amount) for g in request.allowed_gifts}
    answers = 0
    for item in values["suggestions"]:
        if item["kind"] == "answer":
            answers += 1
            item["gift"] = None
        elif item["gift"] is None:
            raise InvalidOutputError("a gift suggestion needs a gift")
        elif item["gift"]["type_id"] not in limits:
            raise InvalidOutputError("gift not in the allowed list")
        elif item["gift"]["amount"] > limits[item["gift"]["type_id"]]:
            raise InvalidOutputError("gift amount above what the world allows")
        verdict = guard.check(item["text"])
        if not verdict.allowed:
            raise InvalidOutputError(f"text blocked by the content guard ({verdict.category})")
    if answers == 0:
        raise InvalidOutputError("at least one encouraging answer is needed (F5238)")
    return values


def values(request: HelpSuggestionsRequest, template: PromptTemplate) -> dict[str, str]:
    settings = template.settings
    language = _table(settings, "language_names").get(plain_id(request.display_locale))
    if language is None:
        raise ContextError(f"no language name for {plain_id(request.display_locale)}")
    trigger = _table(settings, "trigger_words").get(plain_id(request.trigger), "$attempts")
    gifts = "\n".join(
        f"- {plain_id(g.type_id)} (at most {g.max_amount})" for g in request.allowed_gifts
    )
    return {
        "trigger": trigger.replace("$attempts", str(request.failed_attempts)),
        "missing": "\n".join(f"- {humanize(plain_id(m))}" for m in request.missing),
        "gifts": gifts or "- (none: only an answer is possible)",
        "max_suggestions": str(int(request.max_suggestions)),
        "language": language,
    }


def _response(
    request: HelpSuggestionsRequest,
    data: dict[str, Any],
    source: str,
    model: str,
    latency_ms: int,
    attempts: int,
) -> HelpSuggestionsResponse:
    locale = plain_id(request.display_locale)
    return HelpSuggestionsResponse.model_validate(
        {
            "contract_version": CONTRACT_VERSION,
            "request_id": plain_id(request.request_id),
            "tick": request.tick,
            "suggestions": [
                {
                    "kind": s["kind"],
                    "gift": s["gift"],
                    "reason": {"locale": locale, "text": s["text"]},
                }
                for s in data["suggestions"]
            ],
            "rationale": data["rationale"][:280],
            "meta": {
                "source": source,
                "model": model[:64],
                "latency_ms": latency_ms,
                "attempts": attempts,
            },
        }
    )


class OllamaHelp:
    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        settings: ModelCallSettings,
        log: CallLog,
        guard: TextGuard,
    ) -> None:
        self._client, self._queue, self._template = client, queue, template
        self._settings, self._log, self._guard = settings, log, guard

    async def suggest(
        self, request: HelpSuggestionsRequest, deadline: float
    ) -> HelpSuggestionsResponse:
        schema = output_schema(request)
        rendered = self._template.render(values(request, self._template))
        answer = await ask_model(
            role=ROLE,
            job=request,
            rendered=rendered,
            schema=schema,
            parse=lambda raw: parse(raw, schema, request, self._guard),
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


class MockHelp:
    """Deterministic stand-in: one encouraging answer, plus the smallest allowed gift if any."""

    TEXTS: ClassVar[dict[str, tuple[str, str]]] = {
        "de": (
            "Schau dich noch einmal in der Nähe um, vielleicht findest du etwas.",
            "Ein kleines Geschenk, bis du selbst etwas findest.",
        ),
        "en": (
            "Look around nearby once more, maybe you will find something.",
            "A small gift until you find something yourself.",
        ),
    }

    async def suggest(
        self, request: HelpSuggestionsRequest, deadline: float
    ) -> HelpSuggestionsResponse:
        answer, gift_text = self.TEXTS.get(plain_id(request.display_locale), self.TEXTS["en"])
        items: list[dict[str, Any]] = [{"kind": "answer", "gift": None, "text": answer}]
        if request.allowed_gifts and int(request.max_suggestions) > 1:
            first = request.allowed_gifts[0]
            items.append(
                {
                    "kind": "gift",
                    "gift": {"type_id": plain_id(first.type_id), "amount": 1},
                    "text": gift_text,
                }
            )
        started = time.monotonic()
        data = {"rationale": "Mock: answer first, smallest gift.", "suggestions": items}
        return _response(request, data, "mock", "mock", int((time.monotonic() - started) * 1000), 1)


class ReplayHelp:
    """Answers from the call log: the first valid model answer recorded for the request id."""

    def __init__(self, log: CallLog, guard: TextGuard) -> None:
        self._log = log
        self._guard = guard
        self._answers: dict[str, dict[str, object]] | None = None

    async def suggest(
        self, request: HelpSuggestionsRequest, deadline: float
    ) -> HelpSuggestionsResponse:
        request_id = plain_id(request.request_id)
        if self._answers is None:  # read once; replays use a finished log
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(request_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for request {request_id}")
        try:
            data = parse(str(row["raw_output"]), output_schema(request), request, self._guard)
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
