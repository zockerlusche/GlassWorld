"""Decider backends: real model (Ollama), deterministic mock (CI), replay from the call log.

Every backend returns a DecisionResponse or raises a DecisionError whose code is a ServiceError
code. A backend never makes a substitute decision (ADR-0006).
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import ClassVar, Protocol

from glassworld_ai.calllog import CallLog, CallRecord
from glassworld_ai.decider.context import build_values
from glassworld_ai.decider.output import (
    InvalidModelOutputError,
    ModelChoice,
    output_schema,
    parse_choice,
)
from glassworld_ai.llm.ollama import (
    ChatMessage,
    ChatResult,
    OllamaClient,
    OllamaResponseError,
    OllamaUnavailableError,
)
from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.queue import DeadlineExpiredError, ModelQueue, Priority, QueueFullError
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import CONTRACT_VERSION, DecisionRequest, DecisionResponse

ROLE = "decider"


class DecisionError(Exception):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code  # ServiceError code


class Decider(Protocol):
    async def decide(self, request: DecisionRequest, deadline: float) -> DecisionResponse: ...


def _plain(value: object) -> str:
    value = getattr(value, "root", value)
    return str(getattr(value, "value", value))


def _response(
    request: DecisionRequest,
    choice: ModelChoice,
    *,
    source: str,
    model: str,
    latency_ms: int,
    attempts: int,
) -> DecisionResponse:
    locale = _plain(request.display_locale)
    return DecisionResponse.model_validate(
        {
            "contract_version": CONTRACT_VERSION,
            "request_id": _plain(request.request_id),
            "tick": request.tick,
            "option_id": choice.option_id,
            "expression": choice.expression,
            "rationale": choice.rationale[:280],
            "thought": {"locale": locale, "text": choice.thought} if choice.thought else None,
            "meta": {
                "source": source,
                "model": model[:64],
                "latency_ms": latency_ms,
                "attempts": attempts,
            },
        }
    )


@dataclass(frozen=True)
class ModelSettings:
    temperature: float
    num_ctx: int
    char_budget: int
    max_retries: int


class OllamaDecider:
    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        vocab: Vocabulary,
        settings: ModelSettings,
        log: CallLog,
    ) -> None:
        self._client = client
        self._queue = queue
        self._template = template
        self._vocab = vocab
        self._settings = settings
        self._log = log

    def _record(
        self,
        request: DecisionRequest,
        prompt_version: str,
        seed: int,
        attempt: int,
        call_started: float,
        outcome: str,
        raw: str | None,
        detail: str | None = None,
        tokens: tuple[int | None, int | None] = (None, None),
    ) -> None:
        self._log.write(
            CallRecord(
                role=ROLE,
                request_id=_plain(request.request_id),
                tick=request.tick,
                prompt_version=prompt_version,
                model=self._client.model,
                seed=seed,
                temperature=self._settings.temperature,
                attempt=attempt,
                latency_ms=int((time.monotonic() - call_started) * 1000),
                outcome=outcome,
                raw_output=raw,
                request=request.model_dump(mode="json") if attempt == 1 else None,
                detail=detail,
                prompt_tokens=tokens[0],
                output_tokens=tokens[1],
            )
        )

    async def decide(self, request: DecisionRequest, deadline: float) -> DecisionResponse:
        started = time.monotonic()
        rendered = self._template.render(
            build_values(request, self._template, self._vocab, self._settings.char_budget)
        )
        option_ids = [_plain(o.option_id) for o in request.options]
        schema = output_schema(option_ids, self._vocab.ids("expression"))
        messages = [ChatMessage("system", rendered.system), ChatMessage("user", rendered.user)]
        attempts = 1 + self._settings.max_retries
        last_problem = ""
        for attempt in range(1, attempts + 1):
            seed = (request.seed + attempt - 1) % 2_147_483_648
            call_started = time.monotonic()

            def log(
                outcome: str,
                raw: str | None,
                detail: str | None = None,
                tokens: tuple[int | None, int | None] = (None, None),
                *,
                seed: int = seed,
                attempt: int = attempt,
                at: float = call_started,
            ) -> None:
                self._record(
                    request, rendered.version, seed, attempt, at, outcome, raw, detail, tokens
                )

            async def call(seed: int = seed) -> ChatResult:
                return await self._client.chat(
                    messages,
                    schema=schema,
                    seed=seed,
                    temperature=self._settings.temperature,
                    num_ctx=self._settings.num_ctx,
                    timeout=max(0.1, deadline - time.monotonic()),
                )

            try:
                result = await self._queue.run(Priority.DECISION, deadline, call)
            except QueueFullError as error:
                log("overloaded", None, str(error))
                raise DecisionError("overloaded", "model queue is full") from error
            except DeadlineExpiredError as error:
                log("timeout", None, str(error))
                raise DecisionError("timeout", str(error)) from error
            except OllamaUnavailableError as error:
                log("unavailable", None, str(error))
                raise DecisionError("model_unavailable", str(error)) from error
            except OllamaResponseError as error:
                log("error", None, str(error))
                last_problem = str(error)
                continue
            usage = (result.prompt_tokens, result.output_tokens)
            try:
                choice = parse_choice(result.content, schema)
            except InvalidModelOutputError as error:
                log("invalid", result.content, str(error), usage)
                last_problem = str(error)
                continue
            log("ok", result.content, None, usage)
            return _response(
                request,
                choice,
                source="model",
                model=self._client.model,
                latency_ms=int((time.monotonic() - started) * 1000),
                attempts=attempt,
            )
        raise DecisionError(
            "invalid_model_output", f"no valid answer after {attempts} attempts: {last_problem}"
        )


class MockDecider:
    """Deterministic stand-in for CI and core tests: the seed picks one of the options."""

    THOUGHTS: ClassVar[dict[str, str]] = {
        "de": "Ich schaue mal, was ich tun kann.",
        "en": "Let me see what I can do.",
    }

    async def decide(self, request: DecisionRequest, deadline: float) -> DecisionResponse:
        options = sorted(_plain(o.option_id) for o in request.options)
        option_id = options[request.seed % len(options)]
        choice = ModelChoice(
            rationale="Mock decider: option chosen by seed.",
            option_id=option_id,
            expression=None,
            thought=self.THOUGHTS.get(_plain(request.display_locale)),
        )
        return _response(request, choice, source="mock", model="mock", latency_ms=0, attempts=1)


class ReplayDecider:
    """Answers from a call log: the first valid model answer recorded for the request id."""

    def __init__(self, log: CallLog, vocab: Vocabulary) -> None:
        self._log = log
        self._vocab = vocab

    async def decide(self, request: DecisionRequest, deadline: float) -> DecisionResponse:
        request_id = _plain(request.request_id)
        option_ids = [_plain(o.option_id) for o in request.options]
        schema = output_schema(option_ids, self._vocab.ids("expression"))
        for row in self._log.read():
            if (
                row.get("role") == ROLE
                and row.get("request_id") == request_id
                and row.get("outcome") == "ok"
                and isinstance(row.get("raw_output"), str)
            ):
                choice = parse_choice(str(row["raw_output"]), schema)
                model = str(row.get("model", "replay"))
                attempts = row.get("attempt", 1)
                return _response(
                    request,
                    choice,
                    source="replay",
                    model=model,
                    latency_ms=0,
                    attempts=attempts if isinstance(attempts, int) else 1,
                )
        raise DecisionError("model_unavailable", f"no recorded answer for request {request_id}")
