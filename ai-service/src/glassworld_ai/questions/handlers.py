"""Question job handlers: real model (Ollama), deterministic mock (CI), replay from the call log.

Each handler takes a GenerationJob of kind question and returns a JobOutcome or raises
JobFailedError with a ServiceError code. A handler never invents an answer when the model fails
(ADR-0006): the job fails and the core keeps the question open (B2487).
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import ClassVar

import httpx

from glassworld_ai.calllog import CallLog, CallRecord
from glassworld_ai.errors import RETRYABLE_CODES
from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.llm.ollama import (
    ChatMessage,
    ChatResult,
    OllamaClient,
    OllamaRequestError,
    OllamaResponseError,
    OllamaUnavailableError,
)
from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.questions.context import build_values, question_payload
from glassworld_ai.questions.output import (
    InvalidQuestionOutputError,
    ModelAnswer,
    ModelGuess,
    output_schema,
    parse_answer,
)
from glassworld_ai.queue import (
    DeadlineExpiredError,
    ModelQueue,
    Priority,
    QueueClosedError,
    QueueFullError,
)
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import plain_id
from glassworld_contracts import GenerationJob

ROLE = "question"


def _fail(code: str, message: str) -> JobFailedError:
    return JobFailedError(code, message, retryable=code in RETRYABLE_CODES)


def _result(job_locale: str, answer: ModelAnswer) -> dict[str, object]:
    return {
        "question": {
            "understood": answer.understood,
            "guesses": [
                {"property": g.property, "value": g.value, "confidence": round(g.confidence, 4)}
                for g in answer.guesses
            ],
            "idea": answer.idea,
            "thought": {"locale": job_locale, "text": answer.thought} if answer.thought else None,
            "rationale": answer.rationale[:280],
        }
    }


def _schema(template: PromptTemplate, vocab: Vocabulary) -> dict[str, object]:
    max_guesses = int(template.settings.get("max_guesses", 4))
    return output_schema(vocab.ids("property"), vocab.ids("action"), max_guesses)


@dataclass(frozen=True)
class QuestionSettings:
    temperature: float
    num_ctx: int
    max_retries: int
    call_timeout_s: float  # per model call; the question itself has no deadline (B2487)


class OllamaQuestions:
    """Asks the language model. Runs with background priority behind player and decisions."""

    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        vocab: Vocabulary,
        settings: QuestionSettings,
        log: CallLog,
    ) -> None:
        self._client = client
        self._queue = queue
        self._template = template
        self._vocab = vocab
        self._settings = settings
        self._log = log

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        started = time.monotonic()
        rendered = self._template.render(build_values(job, self._template, self._vocab))
        schema = _schema(self._template, self._vocab)
        messages = [ChatMessage("system", rendered.system), ChatMessage("user", rendered.user)]
        attempts = 1 + self._settings.max_retries
        last_problem = ""
        for attempt in range(1, attempts + 1):
            seed = (job.seed + attempt - 1) % 2_147_483_648
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
                self._log.write(
                    CallRecord(
                        role=ROLE,
                        request_id=plain_id(job.request_id),
                        tick=job.tick,
                        prompt_version=rendered.version,
                        model=self._client.model,
                        seed=seed,
                        temperature=self._settings.temperature,
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
                return await self._client.chat(
                    messages,
                    schema=schema,
                    seed=seed,
                    temperature=self._settings.temperature,
                    num_ctx=self._settings.num_ctx,
                    timeout=self._settings.call_timeout_s,
                )

            deadline = time.monotonic() + self._settings.call_timeout_s
            try:
                result = await self._queue.run(Priority.BACKGROUND, deadline, call)
            except QueueClosedError as error:
                log("unavailable", None, str(error))
                raise _fail("model_unavailable", "service is stopping") from error
            except OllamaRequestError as error:
                log("error", None, str(error))
                raise _fail("internal", str(error)) from error
            except QueueFullError as error:
                log("overloaded", None, str(error))
                raise _fail("overloaded", "model queue is full") from error
            except (DeadlineExpiredError, httpx.TimeoutException) as error:
                log("timeout", None, type(error).__name__)
                raise _fail("timeout", "model call took too long") from error
            except OllamaUnavailableError as error:
                log("unavailable", None, str(error))
                raise _fail("model_unavailable", str(error)) from error
            except httpx.HTTPError as error:
                log("unavailable", None, type(error).__name__)
                raise _fail("model_unavailable", f"Ollama error: {type(error).__name__}") from error
            except OllamaResponseError as error:
                log("error", None, str(error))
                last_problem = str(error)
                continue
            usage = (result.prompt_tokens, result.output_tokens)
            try:
                answer = parse_answer(result.content, schema)
            except InvalidQuestionOutputError as error:
                log("invalid", result.content, str(error), usage)
                last_problem = str(error)
                continue
            log("ok", result.content, None, usage)
            return JobOutcome(
                result=_result(plain_id(job.display_locale), answer),
                source="ollama",
                model=self._client.model[:128],
                prompt_version=rendered.version,
                latency_ms=int((time.monotonic() - started) * 1000),
                attempts=attempt,
            )
        raise _fail(
            "invalid_model_output", f"no valid answer after {attempts} attempts: {last_problem}"
        )


class MockQuestions:
    """Deterministic stand-in for CI and core tests.

    seed % 3 == 0: not understood. Otherwise one cautious guess about the first thing noticed
    (or no guess), idea "inspect".
    """

    THOUGHTS: ClassVar[dict[str, tuple[str, str]]] = {
        "de": ("Was ist das nur? Ich verstehe es nicht.", "Hm, vielleicht... ich schaue genauer."),
        "en": ("What is this? I do not understand it.", "Hm, maybe... I will look closer."),
    }

    def __init__(self, vocab: Vocabulary) -> None:
        self._vocab = vocab

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = question_payload(job)
        locale = plain_id(job.display_locale)
        puzzled, guessing = self.THOUGHTS.get(locale, self.THOUGHTS["en"])
        if job.seed % 3 == 0:
            answer = ModelAnswer("Mock: not understood (seed).", False, (), None, puzzled)
        else:
            noticed = sorted(plain_id(p) for p in payload.perception)
            guesses = (ModelGuess(noticed[0], True, 0.3),) if noticed else ()
            idea = "inspect" if "inspect" in self._vocab.ids("action") else None
            answer = ModelAnswer("Mock: cautious guess (seed).", True, guesses, idea, guessing)
        return JobOutcome(_result(locale, answer), "mock", "mock", None, 0, 1)


class ReplayQuestions:
    """Answers from the call log: the first valid model answer recorded for the job id."""

    def __init__(self, log: CallLog, template: PromptTemplate, vocab: Vocabulary) -> None:
        self._log = log
        self._template = template
        self._vocab = vocab

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        schema = _schema(self._template, self._vocab)
        for row in self._log.read():
            if (
                row.get("role") == ROLE
                and row.get("request_id") == job_id
                and row.get("outcome") == "ok"
                and isinstance(row.get("raw_output"), str)
            ):
                try:
                    answer = parse_answer(str(row["raw_output"]), schema)
                except InvalidQuestionOutputError as error:
                    raise _fail(
                        "model_unavailable", f"recorded answer does not fit job {job_id}"
                    ) from error
                attempts = row.get("attempt", 1)
                return JobOutcome(
                    result=_result(plain_id(job.display_locale), answer),
                    source="replay",
                    model=str(row.get("model", "replay"))[:128],
                    prompt_version=str(row.get("prompt_version", "")) or None,
                    latency_ms=0,
                    attempts=attempts if isinstance(attempts, int) else 1,
                )
        raise _fail("model_unavailable", f"no recorded answer for job {job_id}")
