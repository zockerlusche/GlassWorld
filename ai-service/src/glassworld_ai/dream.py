# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Dreams in the computing night (B7 S6, R8, A1409): prompt values, output schema, check, handlers.

The model tells a short dream in the player's language from the events of the day and picks a
tone from the allowed list (data) and the subject the dream is mostly about. What a dream does
(mood, ideas) is data in the core, never the model's choice (frame, not outcome). Every dream
passes the content guard (B2466) and must not show raw ids; otherwise the service asks again
(seed + n) and never edits the text itself.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from typing import Any, cast

from jsonschema import Draft202012Validator

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
from glassworld_ai.questions.context import quote, subject_name
from glassworld_ai.queue import ModelQueue
from glassworld_ai.safety import TextGuard
from glassworld_ai.words import ContextError, humanize, plain_id
from glassworld_contracts import GenerationJob

ROLE = "dream"
_ID_LIKE = re.compile(r"[a-z0-9]+(?:[_-][a-z0-9]+)+")


@dataclass(frozen=True)
class _Subject:
    kind: str
    id: str


@dataclass(frozen=True)
class Dream:
    text: str
    tone: str
    about: dict[str, str] | None


def dream_payload(job: GenerationJob) -> Any:
    payload = job.payload.dream
    if payload is None:
        raise ContextError("job has no dream payload")
    return payload


def subjects(job: GenerationJob) -> dict[str, dict[str, str]]:
    """The day's subjects by a neutral key (t1, t2, ... in event order). The model answers with
    the key, so no id ever has to appear in the prompt (live run L711: ids were copied)."""
    seen: list[tuple[str, str]] = []
    for event in dream_payload(job).events:
        if event.about is not None:
            pair = (plain_id(event.about.kind), plain_id(event.about.id))
            if pair not in seen:
                seen.append(pair)
    return {f"t{n}": {"kind": k, "id": i} for n, (k, i) in enumerate(seen, start=1)}


def _name(kind: str, ident: str, settings: dict[str, Any]) -> str:
    """Plain words for a subject: people have no names yet (B9), so 'another person'."""
    if kind == "inhabitant":
        return _table(settings, "subject_kinds").get("inhabitant", "another person")
    return subject_name(_Subject(kind, ident), settings)


def _table(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def build_values(job: GenerationJob, template: PromptTemplate) -> dict[str, str]:
    payload = dream_payload(job)
    settings = template.settings
    locale = plain_id(job.display_locale)
    language = _table(settings, "language_names").get(locale)
    if language is None:
        raise ContextError(f"no language name for {locale}")
    lines: list[str] = []
    for event in sorted(payload.events, key=lambda e: (e.tick, plain_id(e.kind))):
        about = (
            f" ({_name(plain_id(event.about.kind), plain_id(event.about.id), settings)})"
            if event.about is not None
            else ""
        )
        lines.append(f"- {humanize(plain_id(event.kind))}{about}")
    things = [f"- {key}: {_name(s['kind'], s['id'], settings)}" for key, s in subjects(job).items()]
    memory = payload.memory.strip()
    mood = plain_id(payload.mood) if payload.mood is not None else None
    return {
        "language": language,
        "max_chars": str(payload.max_chars),
        "tones": ", ".join(plain_id(t) for t in payload.tones),
        "memory": quote(memory) if memory else "(nothing yet)",
        "mood": humanize(mood) if mood else "(calm)",
        "events": "\n".join(lines),
        "things": "\n".join(things) or "- (none)",
    }


def output_schema(job: GenerationJob) -> dict[str, Any]:
    payload = dream_payload(job)
    keys = list(subjects(job))
    about: dict[str, Any] = (
        {"anyOf": [{"type": "string", "enum": keys}, {"type": "null"}]}
        if keys
        else {"type": "null"}
    )
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["text", "tone", "about"],
        "properties": {
            "text": {"type": "string", "minLength": 1, "maxLength": int(payload.max_chars)},
            "tone": {"type": "string", "enum": [plain_id(t) for t in payload.tones]},
            "about": about,
        },
    }


def parse_dream(raw: str, job: GenerationJob, guard: TextGuard) -> Dream:
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
    values = cast(dict[str, Any], data)
    text = " ".join(str(values["text"]).split())
    if not text:
        raise InvalidOutputError("text is empty")
    verdict = guard.check(text)
    if not verdict.allowed:
        raise InvalidOutputError(f"text blocked by the content guard ({verdict.category})")
    ids = {s["id"] for s in subjects(job).values()} | {
        plain_id(e.kind) for e in dream_payload(job).events
    }
    leaked = sorted(i for i in ids if _ID_LIKE.fullmatch(i) and i in text.lower())
    if leaked:
        raise InvalidOutputError(f"text shows raw ids: {', '.join(leaked)}")
    about_key = values["about"]
    about = subjects(job)[about_key] if isinstance(about_key, str) else None
    return Dream(text, str(values["tone"]), about)


def _result(job: GenerationJob, dream: Dream) -> dict[str, object]:
    return {
        "dream": {
            "text": {"locale": plain_id(job.display_locale), "text": dream.text},
            "tone": dream.tone,
            "about": dream.about,
        }
    }


class OllamaDreams:
    """Tells dreams with the language model (background priority, no wait limit)."""

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
        self._client = client
        self._queue = queue
        self._template = template
        self._settings = settings
        self._log = log
        self._guard = guard

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        rendered = self._template.render(build_values(job, self._template))
        answer = await ask_model(
            role=ROLE,
            job=job,
            rendered=rendered,
            schema=output_schema(job),
            parse=lambda raw: parse_dream(raw, job, self._guard),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
        )
        return JobOutcome(
            result=_result(job, answer.value),
            source="ollama",
            model=self._client.model[:128],
            prompt_version=rendered.version,
            latency_ms=answer.latency_ms,
            attempts=answer.attempts,
        )


class MockDreams:
    """Deterministic stand-in: a calm sentence about the last subject of the day."""

    TEXTS: dict[str, str] = {  # noqa: RUF012 - constant table
        "de": "Ich träume von {thing}, und alles ist ruhig.",
        "en": "I dream of {thing}, and everything is calm.",
    }

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = dream_payload(job)
        found = list(subjects(job).values())
        about = found[-1] if found else None
        thing = humanize(about["id"]) if about else "the island"
        locale = plain_id(job.display_locale)
        text = self.TEXTS.get(locale, self.TEXTS["en"]).format(thing=thing)
        tones = [plain_id(t) for t in payload.tones]
        tone = "calm" if "calm" in tones else tones[0]
        dream = Dream(text[: int(payload.max_chars)], tone, about)
        return JobOutcome(_result(job, dream), "mock", "mock", None, 0, 1)


class ReplayDreams:
    """Answers from the call log: the first valid model answer recorded for the job id."""

    def __init__(self, log: CallLog, guard: TextGuard) -> None:
        self._log = log
        self._guard = guard
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for job {job_id}")
        try:
            dream = parse_dream(str(row["raw_output"]), job, self._guard)
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            result=_result(job, dream),
            source="replay",
            model=str(row.get("model", "replay"))[:128],
            prompt_version=str(row.get("prompt_version", "")) or None,
            latency_ms=0,
            attempts=attempts if isinstance(attempts, int) else 1,
        )
