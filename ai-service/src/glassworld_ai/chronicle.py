# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""The chronicle's day as a short narrative (B8 R10, A1355): job kind chronicle, at night.

At nightfall the core sends the chronicle entries of the day (kind, text key, names and ids). The
model words them as a few calm sentences for the family book in the player's language, telling only
what the entries say, by the names given. The base lines stay the truth; the narrative only stands
above them. Every narrative passes the content guard, shows no raw id and, in German, names no thing
of this world it was not given (A1613, like the milestone sentence); otherwise the service asks
again, and when no answer passes, the result is no narrative (R24/B2644: nothing in its place).
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

ROLE = "chronicle"
_ID_LIKE = re.compile(r"[a-z0-9]+(?:[_-][a-z0-9]+)+")
_SOMETHING = "ui.chronicle.something"
_SOMEONE = "ui.chronicle.someone"


def chronicle_payload(job: GenerationJob) -> Any:
    payload = job.payload.chronicle
    if payload is None:
        raise ContextError("job has no chronicle payload")
    return payload


def _table(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def _text(texts: TextCatalog | None, locale: str, key: str, fallback: str) -> str:
    found = texts.get(locale, key) if texts is not None else None
    return found or fallback


def base_line(entry: Any, texts: TextCatalog | None, locale: str) -> str:
    """The entry's base line as the chronicle shows it: its text with the names filled in."""
    something = _text(texts, locale, _SOMETHING, "something")
    target = entry.target.label if entry.target is not None else None
    values = {
        "inhabitant": str(entry.inhabitant.label),
        "target": target or something,
        "other": target or _text(texts, locale, _SOMEONE, "someone"),
        "action": (
            _text(texts, locale, f"vocab.action.{plain_id(entry.action)}", plain_id(entry.action))
            if entry.action is not None
            else something
        ),
    }
    line = _text(texts, locale, str(entry.text_key), plain_id(entry.kind).replace("_", " "))
    for key, value in values.items():
        line = line.replace("{" + key + "}", value)
    return line


def _labels(payload: Any) -> list[str]:
    labels: list[str] = []
    for entry in payload.entries:
        labels.append(str(entry.inhabitant.label))
        if entry.target is not None:
            labels.append(str(entry.target.label))
    return labels


def build_values(
    job: GenerationJob, template: PromptTemplate, texts: TextCatalog | None = None
) -> dict[str, str]:
    payload = chronicle_payload(job)
    locale = plain_id(job.display_locale)
    language = _table(template.settings, "language_names").get(locale)
    if language is None:
        raise ContextError(f"no language name for {locale}")
    lines = [f"- {base_line(e, texts, locale)}" for e in payload.entries]
    names = sorted(set(_labels(payload)), key=str.casefold)
    return {
        "language": language,
        "max_chars": str(payload.max_chars),
        "day": str(payload.day),
        "lines": "\n".join(lines),
        "names": ", ".join(names),
    }


def output_schema(job: GenerationJob) -> dict[str, Any]:
    payload = chronicle_payload(job)
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["narrative"],
        "properties": {
            "narrative": {"type": "string", "minLength": 1, "maxLength": int(payload.max_chars)}
        },
    }


class Checks:
    """What a day narrative must pass: content guard, no raw id, and (German) no thing of this world
    it was not given (A1613)."""

    def __init__(
        self,
        job: GenerationJob,
        guard: TextGuard,
        texts: TextCatalog | None,
        settings: dict[str, Any],
    ) -> None:
        payload = chronicle_payload(job)
        self._guard = guard
        locale = plain_id(job.display_locale)
        self._ids: set[str] = set()
        for entry in payload.entries:
            self._ids |= {plain_id(entry.kind), plain_id(entry.inhabitant.id)}
            if entry.target is not None:
                self._ids.add(plain_id(entry.target.id))
            if entry.action is not None:
                self._ids.add(plain_id(entry.action))
        self._words: set[str] | None = None
        self._world: set[str] = set()
        if checks_nouns(settings, locale):
            # Besides the general words: the day's own lines and the names it was given.
            general = set(settings_words(settings, "general_words", locale))
            for entry in payload.entries:
                general |= set(base_line(entry, texts, locale).replace(".", " ").split())
            general |= {word for label in _labels(payload) for word in label.split()}
            self._words = request_words(job.model_dump(mode="json"), texts, locale, general)
            self._world = world_things(texts, locale)

    def problem(self, text: str) -> str | None:
        verdict = self._guard.check(text)
        if not verdict.allowed:
            return f"content guard ({verdict.category})"
        lowered = text.lower()
        if any(_ID_LIKE.fullmatch(i) and i in lowered for i in self._ids):
            return "raw id"
        if self._words is not None:
            unknown = unknown_nouns(text, self._words, heads=True, only=self._world)
            if unknown:
                return f"names what it was not given: {', '.join(unknown[:3])}"
        return None


def parse_narrative(raw: str, job: GenerationJob, checks: Checks) -> str:
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
    text = " ".join(str(cast(dict[str, Any], data)["narrative"]).split())
    if not text:
        raise InvalidOutputError("narrative is empty")
    problem = checks.problem(text)
    if problem is not None:
        raise InvalidOutputError(f"narrative rejected: {problem}")
    return text


def _result(job: GenerationJob, narrative: str | None) -> dict[str, object]:
    locale = plain_id(job.display_locale)
    told = None if narrative is None else {"locale": locale, "text": narrative}
    return {"chronicle": {"narrative": told}}


class OllamaChronicles:
    """Writes the day narrative with the language model (night, low priority, one per game day)."""

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
                parse=lambda raw: parse_narrative(raw, job, checks),
                client=self._client,
                queue=self._queue,
                settings=self._settings,
                log=self._log,
            )
        except JobFailedError as error:
            if error.code != "invalid_model_output":
                raise
            # R24/B2644: no answer passed the checks - no narrative, the base lines alone.
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


class MockChronicles:
    """Deterministic stand-in: no narrative (the base lines alone), so core tests stay calm."""

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        chronicle_payload(job)
        return JobOutcome(_result(job, None), "mock", "mock", None, 0, 1)


class ReplayChronicles:
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
            # A recorded run with no valid answer gave no narrative; replay the same.
            return JobOutcome(_result(job, None), "replay", "replay", None, 0, 1)
        checks = Checks(job, self._guard, self._texts, self._template.settings)
        try:
            narrative = parse_narrative(str(row["raw_output"]), job, checks)
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            result=_result(job, narrative),
            source="replay",
            model=str(row.get("model", "replay"))[:128],
            prompt_version=str(row.get("prompt_version", "")) or None,
            latency_ms=0,
            attempts=attempts if isinstance(attempts, int) else 1,
        )
