# jsonschema/referencing ship incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Memory condensation (B4a R5): prompt values, output schema, answer check, handlers.

The summary is internal English text; the decider reads it as a quoted data block. The budget
(max_chars) and which events are formative come from the core (data). The service checks that
the new summary keeps the budget and names every formative event in "kept"; otherwise it asks
again (seed + n) and never shortens or invents text itself.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from string import Template
from typing import Any, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
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
from glassworld_ai.words import (
    ContextError,
    humanize,
    plain_id,
    quote,
    subject_name,
    table,
)
from glassworld_contracts import GenerationJob

ROLE = "memory"
# Technical floor, not a balancing value: a formative event needs at least a short sentence.
MIN_CHARS_PER_FORMATIVE = 20


@dataclass(frozen=True)
class Condensed:
    summary: str
    kept: tuple[int, ...]


def memory_payload(job: GenerationJob) -> Any:
    payload = job.payload.memory
    if payload is None:
        raise ContextError("job has no memory payload")
    return payload


def formative_ticks(job: GenerationJob) -> list[int]:
    return sorted({e.tick for e in memory_payload(job).events if e.formative})


def _event_text(event: Any, settings: dict[str, Any]) -> str:
    """One event as words. From v3 (prompt setting outcome_phrases) with what the core says came of
    it, how often it happened and the name the inhabitant has for a person or invention."""
    phrases = cast(dict[str, str], settings.get("outcome_phrases", {}))
    words = cast(dict[str, Any], settings.get("events", {}))
    about = ""
    if event.about is not None:
        name = subject_name(event.about, settings)
        if phrases:
            label = getattr(event, "label", None)
            kind = plain_id(event.about.kind)
            if label is not None:
                # The summary is English and internal; names are the same in both languages.
                name = f"{table(settings, 'subject_kinds').get(kind, kind)} '{label.en}'"
            elif kind == "inhabitant" and "unnamed_person" in words:
                name = str(words["unnamed_person"])  # never an id in a text later prompts read
        about = f" ({name})"
    what = f"{humanize(plain_id(event.kind))}{about}"
    if not phrases:
        return what
    outcome = getattr(event, "outcome", None)
    if outcome is not None:
        phrase = phrases.get(plain_id(outcome))
        if phrase is None:
            raise ContextError(f"no outcome phrase for {plain_id(outcome)}")
        what = Template(phrase).safe_substitute(what=what)
    times = getattr(event, "times", None)
    if times is not None and times > 1 and "times" in words:
        what = Template(str(words["times"])).safe_substitute(what=what, times=times)
    return what


def build_values(job: GenerationJob, template: PromptTemplate) -> dict[str, str]:
    payload = memory_payload(job)
    settings = template.settings
    lines: list[str] = []
    ordered = sorted(payload.events, key=lambda e: (e.tick, plain_id(e.kind)))
    # v2: what happened at one tick shares one line - ticks are the ids of "kept", and two lines
    # with the same tick made the model name it twice and leave another out (run L838).
    per_tick = bool(cast(dict[str, Any], settings.get("events", {})).get("one_line_per_tick"))
    groups: dict[Any, list[Any]] = {}
    for n, event in enumerate(ordered):
        groups.setdefault(event.tick if per_tick else n, []).append(event)
    for events in groups.values():
        parts = [_event_text(e, settings) for e in events]
        mark = " - FORMATIVE, keep it" if any(e.formative for e in events) else ""
        lines.append(f"- [tick {events[0].tick}] {'; '.join(parts)}{mark}")
    formative = formative_ticks(job)
    summary = payload.summary.strip()
    return {
        "summary": quote(summary) if summary else "(nothing yet)",
        "events": "\n".join(lines),
        "max_chars": str(payload.max_chars),
        "formative": ", ".join(str(tick) for tick in formative) or "(none)",
        "formative_count": str(len(formative)),
    }


def output_schema(max_chars: int, formative: list[int]) -> dict[str, Any]:
    kept: dict[str, Any] = (
        {
            "type": "array",
            "items": {"type": "integer", "enum": formative},
            "minItems": len(formative),
            "maxItems": len(formative),
            "uniqueItems": True,
        }
        if formative
        else {"type": "array", "maxItems": 0}
    )
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["summary", "kept"],
        "properties": {
            "summary": {"type": "string", "minLength": 1, "maxLength": max_chars},
            "kept": kept,
        },
    }


def parse_condensed(raw: str, schema: dict[str, Any], formative: list[int]) -> Condensed:
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    if isinstance(data, dict) and isinstance(cast(dict[str, Any], data).get("kept"), list):
        # Ticks are the ids, and the core may send several events of one tick (seen with jobs from
        # the real core, review of #227): the model then names that tick once per event. "kept" is
        # a set of ticks, so a repeated tick is the same answer, not a wrong one.
        values_in = cast(dict[str, Any], data)
        values_in["kept"] = list(dict.fromkeys(cast(list[Any], values_in["kept"])))
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidOutputError(f"{where}: {first.message}")
    values = cast(dict[str, Any], data)
    summary = str(values["summary"]).strip()
    kept = tuple(sorted(int(t) for t in values["kept"]))
    if not summary:
        raise InvalidOutputError("summary is empty")
    if list(kept) != formative:
        raise InvalidOutputError(f"kept {list(kept)} must name all formative events {formative}")
    return Condensed(summary, kept)


def _result(condensed: Condensed) -> dict[str, object]:
    return {"memory": {"summary": condensed.summary, "kept": list(condensed.kept)}}


class OllamaMemory:
    """Condenses with the language model (background priority, no wait limit)."""

    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        settings: ModelCallSettings,
        log: CallLog,
    ) -> None:
        self._client = client
        self._queue = queue
        self._template = template
        self._settings = settings
        self._log = log

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        formative = formative_ticks(job)
        max_chars = memory_payload(job).max_chars
        if len(formative) * MIN_CHARS_PER_FORMATIVE > max_chars:
            # Cannot be met by any model: not retryable, the core must split the events or
            # raise the budget (review GW1 M2; otherwise it would resend forever, B2487).
            raise JobFailedError(
                "bad_request",
                f"{len(formative)} formative events do not fit into {max_chars} characters",
                retryable=False,
            )
        schema = output_schema(max_chars, formative)
        rendered = self._template.render(build_values(job, self._template))
        answer = await ask_model(
            role=ROLE,
            job=job,
            rendered=rendered,
            schema=schema,
            parse=lambda raw: parse_condensed(raw, schema, formative),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
        )
        return JobOutcome(
            result=_result(answer.value),
            source="ollama",
            model=self._client.model[:128],
            prompt_version=rendered.version,
            latency_ms=answer.latency_ms,
            attempts=answer.attempts,
        )


class MockMemory:
    """Deterministic stand-in: formative events first, then the old summary, then the rest; cut
    to the budget. Not a real summary, only for wiring and core tests."""

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = memory_payload(job)
        events = sorted(payload.events, key=lambda e: (not e.formative, e.tick))
        parts = [f"{humanize(plain_id(e.kind))} (tick {e.tick})." for e in events]
        old = payload.summary.strip()
        text = " ".join(
            [*parts[: len(formative_ticks(job))], old, *parts[len(formative_ticks(job)) :]]
        )
        summary = " ".join(text.split())[: payload.max_chars].strip() or "Nothing special."
        condensed = Condensed(summary, tuple(formative_ticks(job)))
        return JobOutcome(_result(condensed), "mock", "mock", None, 0, 1)


class ReplayMemory:
    """Answers from the call log: the first valid model answer recorded for the job id."""

    def __init__(self, log: CallLog) -> None:
        self._log = log
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for job {job_id}")
        formative = formative_ticks(job)
        schema = output_schema(memory_payload(job).max_chars, formative)
        try:
            condensed = parse_condensed(str(row["raw_output"]), schema, formative)
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            result=_result(condensed),
            source="replay",
            model=str(row.get("model", "replay"))[:128],
            prompt_version=str(row.get("prompt_version", "")) or None,
            latency_ms=0,
            attempts=attempts if isinstance(attempts, int) else 1,
        )
