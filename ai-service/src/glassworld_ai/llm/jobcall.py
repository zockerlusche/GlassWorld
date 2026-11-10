"""One structured model call for a generation job, with retries (questions, memory, ...).

Shared by the job handlers that ask the language model: render once, call with an output schema,
check the answer, retry with seed + n on invalid output (at most two retries, handbook), log
every attempt. Waiting in the model queue has no limit (B2487); one call is limited by
`call_timeout_s`. Failures become JobFailedError with a ServiceError code; there is never a
substitute answer (ADR-0006).
"""

from __future__ import annotations

import asyncio
import time
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any, Protocol

import httpx

from glassworld_ai.calllog import CallLog, CallRecord
from glassworld_ai.errors import RETRYABLE_CODES
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.llm.ollama import (
    ChatMessage,
    ChatResult,
    OllamaClient,
    OllamaRequestError,
    OllamaResponseError,
    OllamaUnavailableError,
)
from glassworld_ai.prompts import RenderedPrompt
from glassworld_ai.queue import (
    DeadlineExpiredError,
    ModelQueue,
    Priority,
    QueueClosedError,
    QueueFullError,
)
from glassworld_ai.words import plain_id

NO_WAIT_LIMIT_S = 7 * 24 * 3600.0  # queue wait for jobs: practically unlimited (B2487)


class InvalidOutputError(Exception):
    """The model answer does not satisfy the output schema or the extra rules of the kind."""


@dataclass(frozen=True)
class ModelCallSettings:
    temperature: float
    num_ctx: int
    max_retries: int
    call_timeout_s: float  # per model call; waiting in the queue has no limit (B2487)


@dataclass(frozen=True)
class Answer[T]:
    value: T
    attempts: int
    latency_ms: int


def fail(code: str, message: str) -> JobFailedError:
    return JobFailedError(code, message, retryable=code in RETRYABLE_CODES)


class Asked(Protocol):
    """What a model call needs from its request: ids for the log, the seed and a dump."""

    @property
    def request_id(self) -> Any: ...
    @property
    def tick(self) -> int: ...
    @property
    def seed(self) -> int: ...
    def model_dump(self, *, mode: str = ...) -> dict[str, Any]: ...


async def ask_model[T](
    *,
    role: str,
    job: Asked,
    rendered: RenderedPrompt,
    schema: dict[str, Any],
    parse: Callable[[str], T],
    client: OllamaClient,
    queue: ModelQueue,
    settings: ModelCallSettings,
    log: CallLog,
    priority: Priority = Priority.BACKGROUND,
    deadline: float | None = None,
) -> Answer[T]:
    """Ask until `parse` accepts an answer (1 + max_retries attempts) or a hard error occurs.

    Jobs wait without limit (background, B2487); a player request passes priority PLAYER and its
    absolute deadline (time.monotonic()).
    """
    started = time.monotonic()
    messages = [ChatMessage("system", rendered.system), ChatMessage("user", rendered.user)]
    attempts = 1 + settings.max_retries
    last_problem = ""
    for attempt in range(1, attempts + 1):
        seed = (job.seed + attempt - 1) % 2_147_483_648
        call_started = time.monotonic()

        def record(
            outcome: str,
            raw: str | None,
            detail: str | None = None,
            tokens: tuple[int | None, int | None] = (None, None),
            *,
            seed: int = seed,
            attempt: int = attempt,
            at: float = call_started,
        ) -> None:
            log.write(
                CallRecord(
                    role=role,
                    request_id=plain_id(job.request_id),
                    tick=job.tick,
                    prompt_version=rendered.version,
                    prompt_digest=rendered.digest or None,
                    model=client.model,
                    seed=seed,
                    temperature=settings.temperature,
                    attempt=attempt,
                    latency_ms=int((time.monotonic() - at) * 1000),
                    outcome=outcome,
                    raw_output=raw,
                    request=job.model_dump(mode="json") if attempt == 1 else None,
                    detail=detail,
                    prompt_tokens=tokens[0],
                    output_tokens=tokens[1],
                )
            )

        async def call(seed: int = seed) -> ChatResult:
            return await client.chat(
                messages,
                schema=schema,
                seed=seed,
                temperature=settings.temperature,
                num_ctx=settings.num_ctx,
                timeout=settings.call_timeout_s
                if deadline is None
                else max(0.1, min(settings.call_timeout_s, deadline - time.monotonic())),
            )

        # The queue deadline covers waiting AND the call; the call itself is limited by the
        # HTTP timeout (call_timeout_s), so the wait is practically unlimited.
        until = deadline if deadline is not None else time.monotonic() + NO_WAIT_LIMIT_S
        try:
            result = await queue.run(priority, until, call)
        except asyncio.CancelledError:
            record("cancelled", None, "job cancelled (DELETE or shutdown)")
            raise
        except QueueClosedError as error:
            record("unavailable", None, str(error))
            raise fail("model_unavailable", "service is stopping") from error
        except OllamaRequestError as error:
            record("error", None, str(error))
            raise fail("internal", str(error)) from error
        except QueueFullError as error:
            record("overloaded", None, str(error))
            raise fail("overloaded", "model queue is full") from error
        except (DeadlineExpiredError, httpx.TimeoutException) as error:
            record("timeout", None, type(error).__name__)
            raise fail("timeout", "model call took too long") from error
        except OllamaUnavailableError as error:
            record("unavailable", None, str(error))
            raise fail("model_unavailable", str(error)) from error
        except httpx.HTTPError as error:
            record("unavailable", None, type(error).__name__)
            raise fail("model_unavailable", f"Ollama error: {type(error).__name__}") from error
        except OllamaResponseError as error:
            record("error", None, str(error))
            last_problem = str(error)
            continue
        usage = (result.prompt_tokens, result.output_tokens)
        try:
            value = parse(result.content)
        except InvalidOutputError as error:
            record("invalid", result.content, str(error), usage)
            last_problem = str(error)
            continue
        record("ok", result.content, None, usage)
        return Answer(value, attempt, int((time.monotonic() - started) * 1000))
    raise fail("invalid_model_output", f"no valid answer after {attempts} attempts: {last_problem}")


def recorded_answers(log: CallLog, role: str) -> dict[str, dict[str, object]]:
    """First valid recorded answer per job id (for replay handlers; read once)."""
    answers: dict[str, dict[str, object]] = {}
    for row in log.read():
        job_id = row.get("request_id")
        if (
            row.get("role") == role
            and isinstance(job_id, str)
            and row.get("outcome") == "ok"
            and isinstance(row.get("raw_output"), str)
        ):
            answers.setdefault(job_id, row)
    return answers
