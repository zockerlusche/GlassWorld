"""Decider backends: real model (Ollama), deterministic mock (CI), replay from the call log.

Every backend returns a DecisionResponse or raises a DecisionError whose code is a ServiceError
code. A backend never makes a substitute decision (ADR-0006).
"""

from __future__ import annotations

import time
from dataclasses import dataclass, replace
from typing import ClassVar, Protocol

import httpx

from glassworld_ai.calllog import CallLog, CallRecord
from glassworld_ai.decider.context import b9_parts, build_values
from glassworld_ai.decider.mock import MockWeights, pick
from glassworld_ai.decider.output import (
    InvalidModelOutputError,
    ModelChoice,
    output_schema,
    parse_choice,
    readable_name,
)
from glassworld_ai.decider.thoughts import ThoughtMemory, stand_in, thought_problem
from glassworld_ai.llm.ollama import (
    ChatMessage,
    ChatResult,
    OllamaClient,
    OllamaRequestError,
    OllamaResponseError,
    OllamaUnavailableError,
)
from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.queue import (
    DeadlineExpiredError,
    ModelQueue,
    Priority,
    QueueClosedError,
    QueueFullError,
)
from glassworld_ai.texts import TextCatalog
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import CONTRACT_VERSION, DecisionRequest, DecisionResponse

ROLE = "decider"


def hidden_ids(request: DecisionRequest) -> frozenset[str]:
    """Entity ids in the request; they must never show in the player-visible thought."""
    ids = {_plain(request.inhabitant.id)}
    ids |= {_plain(o.id) for o in request.perception.objects}
    ids |= {_plain(p.id) for p in request.perception.inhabitants}
    ids |= {_plain(r.other) for r in request.inhabitant.relationships}
    ids |= {_plain(k.told_by) for k in request.inhabitant.knowledge if k.told_by is not None}
    return frozenset(ids)


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
            **({"name_text": choice.name_text} if choice.name_text else {}),
            **({"gossip": choice.gossip} if choice.gossip else {}),
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
        texts: TextCatalog | None = None,
    ) -> None:
        self._client = client
        self._queue = queue
        self._template = template
        self._vocab = vocab
        self._settings = settings
        self._log = log
        self._texts = texts
        self._shown = ThoughtMemory()

    def _b9_fields(self, request: DecisionRequest, choice: ModelChoice) -> ModelChoice:
        """Prompt v5 (B9 R6/R11): a name only with name_person (one readable word), gossip only
        with gossip and only about someone in the request and a known property. The core checks
        both again (R27, per-observer names)."""
        option = next((o for o in request.options if _plain(o.option_id) == choice.option_id), None)
        action = _plain(option.action) if option is not None else ""
        name = readable_name(choice.name_text) if action == "name_person" else None
        gossip = choice.gossip if action == "gossip" else None
        if gossip is not None and (
            str(gossip.get("about")) not in hidden_ids(request)
            or str(gossip.get("property")) not in self._vocab.ids("property")
        ):
            gossip = None
        return replace(choice, name_text=name, gossip=gossip)

    def _natural(self, request: DecisionRequest, choice: ModelChoice) -> tuple[ModelChoice, str]:
        """Prompt v4 (W1687): a thought with foreign words, ids or too many words is replaced
        by the data's stand-in for the chosen action (or left out). Returns the note for the log."""
        if self._template.version < 4 or not choice.thought:
            return choice, ""
        locale = _plain(request.display_locale)
        settings = self._template.settings
        note = ""
        problem = thought_problem(choice.thought, locale, settings)
        if problem is not None:
            option = next(
                (o for o in request.options if _plain(o.option_id) == choice.option_id), None
            )
            action = _plain(option.action) if option is not None else ""
            note = f"thought replaced ({problem}): {choice.thought}"
            choice = replace(choice, thought=stand_in(self._texts, locale, action))
        if choice.thought:
            # W1696: the same inhabitant does not say the same thing again soon - silence instead.
            who = _plain(request.inhabitant.id)
            if self._shown.repeated(who, request.tick, choice.thought, settings):
                return replace(choice, thought=None), (
                    f"{note}; " if note else ""
                ) + f"thought left out (repeat): {choice.thought}"
            self._shown.remember(who, request.tick, choice.thought)
        return choice, note

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
                prompt_digest=self._template.digest or None,
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
            build_values(
                request, self._template, self._vocab, self._settings.char_budget, self._texts
            )
        )
        option_ids = [_plain(o.option_id) for o in request.options]
        schema = output_schema(
            option_ids, self._vocab.ids("expression"), b9=b9_parts(self._template)
        )
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
            except QueueClosedError as error:
                log("unavailable", None, str(error))
                raise DecisionError("model_unavailable", "service is stopping") from error
            except OllamaRequestError as error:
                log("error", None, str(error))
                raise DecisionError("internal", str(error)) from error
            except QueueFullError as error:
                log("overloaded", None, str(error))
                raise DecisionError("overloaded", "model queue is full") from error
            except DeadlineExpiredError as error:
                log("timeout", None, str(error))
                raise DecisionError("timeout", str(error)) from error
            except httpx.TimeoutException as error:
                # The HTTP read timeout to Ollama is set from the deadline: report it as a
                # retryable timeout with request id and tick (B2487: the core keeps the question).
                log("timeout", None, type(error).__name__)
                raise DecisionError("timeout", "model call exceeded the deadline") from error
            except OllamaUnavailableError as error:
                log("unavailable", None, str(error))
                raise DecisionError("model_unavailable", str(error)) from error
            except httpx.HTTPError as error:
                log("unavailable", None, type(error).__name__)
                raise DecisionError(
                    "model_unavailable", f"Ollama error: {type(error).__name__}"
                ) from error
            except OllamaResponseError as error:
                log("error", None, str(error))
                last_problem = str(error)
                continue
            usage = (result.prompt_tokens, result.output_tokens)
            try:
                choice = parse_choice(result.content, schema, hidden_ids(request))
            except InvalidModelOutputError as error:
                log("invalid", result.content, str(error), usage)
                last_problem = str(error)
                continue
            choice, note = self._natural(request, choice)
            choice = self._b9_fields(request, choice)
            log("ok", result.content, note or None, usage)
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
    """Deterministic generic stand-in for CI and core tests (Bogen 34 R26): the seed picks one of
    the options by the data's weights (decider/mock.py, the same rule as the core's mock)."""

    THOUGHTS: ClassVar[dict[str, str]] = {
        "de": "Ich schaue mal, was ich tun kann.",
        "en": "Let me see what I can do.",
    }

    def __init__(self, weights: MockWeights | None = None) -> None:
        self._weights = weights or MockWeights()

    async def decide(self, request: DecisionRequest, deadline: float) -> DecisionResponse:
        options = [(_plain(o.option_id), _plain(o.action)) for o in request.options]
        option_id = pick(options, int(request.seed), self._weights)
        choice = ModelChoice(
            rationale="Mock decider: option chosen by seed and the data's weights.",
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
                try:
                    choice = parse_choice(str(row["raw_output"]), schema, hidden_ids(request))
                except InvalidModelOutputError as error:
                    # Recorded answer does not fit this request (e.g. other options): no
                    # substitute decision, a retryable error instead.
                    raise DecisionError(
                        "model_unavailable", f"recorded answer does not fit request {request_id}"
                    ) from error
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
