# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""The chronicle's sentence for a milestone (R35, B2598, A1583, D962): job kind milestone.

When the core reaches a milestone, the model writes one calm sentence for the chronicle in the
player's language, naming only the inhabitants and the thing it is given, by their names. The
base line (text key) stays the truth; the sentence only goes under it. Every sentence passes the
content guard, shows no raw id and, in German, names no thing of this world it was not given
(A1613); otherwise the service asks again, and when no answer passes, the result is no sentence
(R24/B2644: nothing in its place) - a milestone keeps no sentence rather than a made-up one.
"""

from __future__ import annotations

import json
import re
from typing import Any, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
from glassworld_ai.grounding import (
    checks_nouns,
    request_words,
    settings_words,
    unknown_nouns,
    world_things,
)
from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.llm.jobcall import (
    InvalidOutputError,
    ModelCallSettings,
    ask_model,
    fail,
    recorded_answers,
)
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.queue import ModelQueue
from glassworld_ai.safety import TextGuard
from glassworld_ai.texts import TextCatalog
from glassworld_ai.words import ContextError, plain_id
from glassworld_contracts import GenerationJob

ROLE = "milestone"
_ID_LIKE = re.compile(r"[a-z0-9]+(?:[_-][a-z0-9]+)+")


def milestone_payload(job: GenerationJob) -> Any:
    payload = job.payload.milestone
    if payload is None:
        raise ContextError("job has no milestone payload")
    return payload


def _table(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def _milestone_text(payload: Any, texts: TextCatalog | None, locale: str) -> str:
    key = str(payload.text_key)
    found = texts.get(locale, key) if texts is not None else None
    return found or plain_id(payload.milestone).replace("_", " ")


def build_values(
    job: GenerationJob, template: PromptTemplate, texts: TextCatalog | None = None
) -> dict[str, str]:
    payload = milestone_payload(job)
    locale = plain_id(job.display_locale)
    language = _table(template.settings, "language_names").get(locale)
    if language is None:
        raise ContextError(f"no language name for {locale}")
    made = payload.made
    return {
        "language": language,
        "max_chars": str(payload.max_chars),
        "milestone": _milestone_text(payload, texts, "en"),
        "base_line": _milestone_text(payload, texts, locale),
        "made": made.label if made is not None else "(nothing - a way of doing it)",
        "who": ", ".join(i.label for i in payload.inhabitants),
        "things": ", ".join(t.label for t in (payload.things or [])) or "(none)",
        "day": str(payload.day),
    }


def output_schema(job: GenerationJob) -> dict[str, Any]:
    payload = milestone_payload(job)
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["sentence"],
        "properties": {
            "sentence": {"type": "string", "minLength": 1, "maxLength": int(payload.max_chars)}
        },
    }


class Checks:
    """What a chronicle sentence must pass: content guard, no raw id, and (German) no thing of
    this world it was not given (A1613)."""

    def __init__(
        self,
        job: GenerationJob,
        guard: TextGuard,
        texts: TextCatalog | None,
        settings: dict[str, Any],
    ) -> None:
        payload = milestone_payload(job)
        self._guard = guard
        locale = plain_id(job.display_locale)
        self._ids = {plain_id(payload.milestone)} | {plain_id(i.id) for i in payload.inhabitants}
        if payload.made is not None:
            self._ids.add(plain_id(payload.made.id))
        self._ids |= {plain_id(t.id) for t in (payload.things or [])}
        self._words: set[str] | None = None
        self._world: set[str] = set()
        if checks_nouns(settings, locale):
            # Besides the general words: the milestone's own line and the names it was given.
            labels = [i.label for i in payload.inhabitants] + (
                [payload.made.label] if payload.made is not None else []
            )
            labels += [t.label for t in (payload.things or [])]
            general = set(settings_words(settings, "general_words", locale))
            general |= set(_milestone_text(payload, texts, locale).split())
            general |= {word for label in labels for word in str(label).split()}
            self._words = request_words(job.model_dump(mode="json"), texts, locale, general)
            self._world = world_things(texts, locale)

    def problem(self, text: str) -> str | None:
        verdict = self._guard.check(text)
        if not verdict.allowed:
            return f"content guard ({verdict.category})"
        if any(_ID_LIKE.fullmatch(i) and i in text.lower() for i in self._ids):
            return "raw id"
        if self._words is not None:
            unknown = unknown_nouns(text, self._words, heads=True, only=self._world)
            if unknown:
                return f"names what it was not given: {', '.join(unknown[:3])}"
        return None


def parse_sentence(raw: str, job: GenerationJob, checks: Checks) -> str:
    schema = output_schema(job)
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidOutputError(f"{where}: {first.message}")
    text = " ".join(str(cast(dict[str, Any], data)["sentence"]).split())
    if not text:
        raise InvalidOutputError("sentence is empty")
    problem = checks.problem(text)
    if problem is not None:
        raise InvalidOutputError(f"sentence rejected: {problem}")
    return text


def _result(job: GenerationJob, sentence: str | None) -> dict[str, object]:
    said = None if sentence is None else {"locale": plain_id(job.display_locale), "text": sentence}
    return {"milestone": {"sentence": said}}


class OllamaMilestones:
    """Writes the sentence with the language model (low priority, 13 per world at most)."""

    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        settings: ModelCallSettings,
        log: CallLog,
        guard: TextGuard,
        texts: TextCatalog | None,
    ) -> None:
        self._client, self._queue, self._template = client, queue, template
        self._settings, self._log, self._guard, self._texts = settings, log, guard, texts

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        rendered = self._template.render(build_values(job, self._template, self._texts))
        checks = Checks(job, self._guard, self._texts, self._template.settings)
        try:
            answer = await ask_model(
                role=ROLE,
                job=job,
                rendered=rendered,
                schema=output_schema(job),
                parse=lambda raw: parse_sentence(raw, job, checks),
                client=self._client,
                queue=self._queue,
                settings=self._settings,
                log=self._log,
            )
        except JobFailedError as error:
            if error.code != "invalid_model_output":
                raise
            # R24/B2644: no answer passed the checks - no sentence, nothing in its place.
            return JobOutcome(
                _result(job, None), "ollama", self._client.model[:128], rendered.version, 0, 1
            )
        return JobOutcome(
            result=_result(job, answer.value),
            source="ollama",
            model=self._client.model[:128],
            prompt_version=rendered.version,
            latency_ms=answer.latency_ms,
            attempts=answer.attempts,
        )


class MockMilestones:
    """Deterministic stand-in: no sentence (the base line alone), so core tests stay calm."""

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        milestone_payload(job)
        return JobOutcome(_result(job, None), "mock", "mock", None, 0, 1)


class ReplayMilestones:
    """Answers from the call log: the first valid model answer recorded for the job id."""

    def __init__(
        self,
        log: CallLog,
        guard: TextGuard,
        texts: TextCatalog | None,
        template: PromptTemplate,
    ) -> None:
        self._log, self._guard, self._texts, self._template = log, guard, texts, template
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            # A recorded run with no valid answer gave no sentence; replay the same.
            return JobOutcome(_result(job, None), "replay", "replay", None, 0, 1)
        checks = Checks(job, self._guard, self._texts, self._template.settings)
        try:
            sentence = parse_sentence(str(row["raw_output"]), job, checks)
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            result=_result(job, sentence),
            source="replay",
            model=str(row.get("model", "replay"))[:128],
            prompt_version=str(row.get("prompt_version", "")) or None,
            latency_ms=0,
            attempts=attempts if isinstance(attempts, int) else 1,
        )
