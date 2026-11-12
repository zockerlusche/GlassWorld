"""Question job handlers: real model (Ollama), deterministic mock (CI), replay from the call log.

Each handler takes a GenerationJob of kind question and returns a JobOutcome or raises
JobFailedError with a ServiceError code. A handler never invents an answer when the model fails
(ADR-0006): the job fails and the core keeps the question open (B2487).
"""

from __future__ import annotations

from collections.abc import Callable
from typing import ClassVar

from glassworld_ai.calllog import CallLog
from glassworld_ai.jobs import JobOutcome
from glassworld_ai.llm.jobcall import (
    InvalidOutputError,
    ModelCallSettings,
    ask_model,
    fail,
    recorded_answers,
)
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.questions.context import build_values, question_payload
from glassworld_ai.questions.output import ModelAnswer, ModelGuess, output_schema, parse_answer
from glassworld_ai.queue import ModelQueue
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import plain_id
from glassworld_contracts import GenerationJob

ROLE = "question"
QuestionSettings = ModelCallSettings  # name kept for callers


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


def _schema(template: PromptTemplate, vocab: Vocabulary, job: GenerationJob) -> dict[str, object]:
    # A person has no thing properties: no guesses about inhabitants (live probe L663).
    person = plain_id(question_payload(job).subject.kind) == "inhabitant"
    max_guesses = 0 if person else int(template.settings.get("max_guesses", 4))
    return output_schema(vocab.ids("property"), vocab.ids("action"), max_guesses)


def _hidden_ids(job: GenerationJob) -> frozenset[str]:
    """Entity ids in the job that must never appear in the player-visible thought."""
    payload = question_payload(job)
    inhabitant = payload.inhabitant
    ids = {plain_id(inhabitant.id)}
    ids |= {plain_id(r.other) for r in inhabitant.relationships}
    ids |= {plain_id(k.told_by) for k in inhabitant.knowledge if k.told_by is not None}
    if plain_id(payload.subject.kind) in ("inhabitant", "object"):
        ids.add(plain_id(payload.subject.id))
    return frozenset(ids)


class OllamaQuestions:
    """Asks the language model. Runs with background priority behind player and decisions."""

    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        vocab: Vocabulary,
        settings: ModelCallSettings,
        log: CallLog,
        is_night: Callable[[], bool] = lambda: False,
    ) -> None:
        self._client = client
        self._queue = queue
        self._is_night = is_night  # v5: the service's phase (B2652)
        self._template = template
        self._vocab = vocab
        self._settings = settings
        self._log = log

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        rendered = self._template.render(
            build_values(job, self._template, self._vocab, self._is_night())
        )
        schema = _schema(self._template, self._vocab, job)
        hidden = _hidden_ids(job)
        answer = await ask_model(
            role=ROLE,
            job=job,
            rendered=rendered,
            schema=schema,
            parse=lambda raw: parse_answer(raw, schema, hidden),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
        )
        return JobOutcome(
            result=_result(plain_id(job.display_locale), answer.value),
            source="ollama",
            model=self._client.model[:128],
            prompt_version=rendered.version,
            latency_ms=answer.latency_ms,
            attempts=answer.attempts,
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
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:  # read once; replays use a finished log
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for job {job_id}")
        try:
            answer = parse_answer(
                str(row["raw_output"]), _schema(self._template, self._vocab, job), _hidden_ids(job)
            )
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            result=_result(plain_id(job.display_locale), answer),
            source="replay",
            model=str(row.get("model", "replay"))[:128],
            prompt_version=str(row.get("prompt_version", "")) or None,
            latency_ms=0,
            attempts=attempts if isinstance(attempts, int) else 1,
        )
