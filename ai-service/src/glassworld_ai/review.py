# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""The nightly look back of one inhabitant (A1584, B7 R18-R22, B2592, B2687): job kind review.

One call per inhabitant and night replaces memory and dream: from the day's events, the memory,
what the inhabitant knows (its Fundbuch, B2589) and its ideas, the model writes the new memory
summary, a dream, new or grown ideas ("What if I ...?", B2578), intentions for the morning and the
morning mood. What ideas and intentions do is data in the core (frame, not outcome).

The model never sees an id: it names the things it knows and the day's things by their names as the
prompt lists them (measurement L1287: with neutral keys k1, k2 ... it mixed them up), its ideas by
keys i1, i2, .... The service maps names back and checks before anything goes out:
- the summary keeps the budget and names every formative event (as memory; else ask again);
- roots of an idea and the subject of an intention come only from the input - only its own
  knowledge, its own ideas (R29 "can it come up with it"); an idea or intention that names
  anything else is dropped, the rest stays;
- every text shown to the player (dream, thoughts) passes the content guard and the world check
  of A1613 (no thing of this world it does not know); a text that fails is left out (R19, B2644),
  the rest of the review holds.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
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
from glassworld_ai.queue import ModelQueue
from glassworld_ai.safety import TextGuard
from glassworld_ai.texts import TextCatalog
from glassworld_ai.words import (
    UNSURE,
    ContextError,
    humanize,
    plain_id,
    quote,
    subject_name,
    unsure,
)
from glassworld_contracts import GenerationJob

ROLE = "review"
_ID_LIKE = re.compile(r"[a-z0-9]+(?:[_-][a-z0-9]+)+")


@dataclass(frozen=True)
class _Subject:
    kind: str
    id: str


def review_payload(job: GenerationJob) -> Any:
    payload = getattr(job.payload, "review", None)
    if payload is None:
        raise ContextError("job has no review payload")
    return payload


def _keyed(
    pairs: list[tuple[str, str]],
    prefix: str,
    job: GenerationJob,
    texts: TextCatalog | None,
    settings: dict[str, Any] | None,
) -> dict[str, dict[str, str]]:
    """Subjects by the name the prompt shows (player's language); a name two subjects share, or
    none, falls back to a neutral key (prefix + number), so every key names exactly one subject."""
    locale = plain_id(job.display_locale)
    named = [
        (_name(k, i, settings, texts, locale) if settings is not None else "", k, i)
        for k, i in pairs
    ]
    counts: dict[str, int] = {}
    for name, _, _ in named:
        counts[name] = counts.get(name, 0) + 1
    out: dict[str, dict[str, str]] = {}
    for n, (name, k, i) in enumerate(named, start=1):
        key = name if name and counts[name] == 1 else f"{prefix}{n}"
        out[key] = {"kind": k, "id": i}
    return out


def known(
    job: GenerationJob, texts: TextCatalog | None = None, settings: dict[str, Any] | None = None
) -> dict[str, dict[str, str]]:
    """What the inhabitant knows, by name (or k1, k2, ... where a name is missing or shared)."""
    seen: list[tuple[str, str]] = []
    for entry in review_payload(job).knowledge:
        pair = (plain_id(entry.subject.kind), plain_id(entry.subject.id))
        if pair not in seen:
            seen.append(pair)
    return _keyed(seen, "k", job, texts, settings)


def ideas(job: GenerationJob) -> dict[str, Any]:
    """The inhabitant's ideas by a neutral key (i1, i2, ...)."""
    return {f"i{n}": idea for n, idea in enumerate(review_payload(job).ideas, start=1)}


def day_things(
    job: GenerationJob, texts: TextCatalog | None = None, settings: dict[str, Any] | None = None
) -> dict[str, dict[str, str]]:
    """The day's subjects for the dream's 'about', by name (or t1, t2, ...)."""
    seen: list[tuple[str, str]] = []
    for event in review_payload(job).events:
        if event.about is not None:
            pair = (plain_id(event.about.kind), plain_id(event.about.id))
            if pair not in seen:
                seen.append(pair)
    return _keyed(seen, "t", job, texts, settings)


def _table(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def _name(
    kind: str, ident: str, settings: dict[str, Any] | None, texts: TextCatalog | None, locale: str
) -> str:
    settings = settings or {}
    if kind == "inhabitant":
        return _table(settings, "subject_kinds").get("inhabitant", "another person")
    found = texts.thing(locale, ident) if texts is not None and kind == "type" else None
    if found:
        return found
    if ident.startswith("inv_"):
        return str(settings.get("made_thing", "a made thing"))
    return subject_name(_Subject(kind, ident), settings)


def _level(value: float) -> str:
    return "low" if value < 0.3 else "medium" if value < 0.7 else "high"


def build_values(
    job: GenerationJob, template: PromptTemplate, texts: TextCatalog | None = None
) -> dict[str, str]:
    payload = review_payload(job)
    settings = template.settings
    locale = plain_id(job.display_locale)
    language = _table(settings, "language_names").get(locale)
    if language is None:
        raise ContextError(f"no language name for {locale}")

    def name(raw: Any) -> str:
        return _name(plain_id(raw.kind), plain_id(raw.id), settings, texts, locale)

    events = []
    formative = sorted({e.tick for e in payload.events if e.formative})
    for event in sorted(payload.events, key=lambda e: (e.tick, plain_id(e.kind))):
        about = f" ({name(event.about)})" if event.about is not None else ""
        outcome = getattr(event, "outcome", None)
        came = f", {humanize(plain_id(outcome))}" if outcome is not None else ""
        mark = " - FORMATIVE, keep it" if event.formative else ""
        events.append(f"- [tick {event.tick}] {humanize(plain_id(event.kind))}{about}{came}{mark}")
    keys = known(job, texts, settings)
    beliefs: dict[str, list[str]] = {}
    for entry in payload.knowledge:
        pair = {"kind": plain_id(entry.subject.kind), "id": plain_id(entry.subject.id)}
        key = next(k for k, s in keys.items() if s == pair)
        word = "is" if entry.value else "is not"
        fading = f" ({UNSURE})" if unsure(entry) else ""
        beliefs.setdefault(key, []).append(f"{word} {humanize(plain_id(entry.property))}{fading}")
    knowledge = [f"- {key}: {', '.join(beliefs.get(key, []))}" for key in keys]
    own = [
        f"- {key}: {idea.text} (grown {float(idea.strength):.1f})"
        for key, idea in ideas(job).items()
    ]
    things = [f"- {key}" for key in day_things(job, texts, settings)]
    needs = ", ".join(f"{humanize(plain_id(n.id))} {_level(float(n.value))}" for n in payload.needs)
    traits = ", ".join(
        f"{humanize(plain_id(t.id))} {_level(float(t.value))}" for t in payload.traits
    )
    memory = payload.memory.strip()
    mood = plain_id(payload.mood) if payload.mood is not None else None
    return {
        "language": language,
        "memory": quote(memory) if memory else "(nothing yet)",
        "memory_max_chars": str(payload.memory_max_chars),
        "dream_max_chars": str(payload.dream_max_chars),
        "formative": ", ".join(str(t) for t in formative) or "(none)",
        "mood": humanize(mood) if mood else "(calm)",
        "tones": ", ".join(plain_id(t) for t in payload.tones),
        "events": "\n".join(events),
        "things": "\n".join(things) or "- (none)",
        "knowledge": "\n".join(knowledge) or "- (nothing yet)",
        "ideas": "\n".join(own) or "- (none yet)",
        "needs": needs or "-",
        "traits": traits or "-",
        "actions": ", ".join(plain_id(a) for a in payload.actions),
        "max_new_ideas": str(payload.max_new_ideas),
        "max_intentions": str(payload.max_intentions),
    }


def output_schema(
    job: GenerationJob,
    moods: list[str],
    texts: TextCatalog | None = None,
    settings: dict[str, Any] | None = None,
) -> dict[str, Any]:
    payload = review_payload(job)
    formative = sorted({e.tick for e in payload.events if e.formative})
    roots = list(known(job, texts, settings)) + list(ideas(job))
    day = list(day_things(job, texts, settings))
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

    def one_of(keys: list[str]) -> dict[str, Any]:
        return (
            {"anyOf": [{"type": "string", "enum": keys}, {"type": "null"}]}
            if keys
            else {"type": "null"}
        )

    root_items: dict[str, Any] = (
        {"type": "string", "enum": roots} if roots else {"type": "string", "enum": ["none"]}
    )
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["summary", "kept", "dream", "ideas", "intentions", "morning_mood"],
        "properties": {
            "summary": {
                "type": "string",
                "minLength": 1,
                "maxLength": int(payload.memory_max_chars),
            },
            "kept": kept,
            "dream": {
                "type": "object",
                "additionalProperties": False,
                "required": ["text", "tone", "about"],
                "properties": {
                    "text": {
                        "type": "string",
                        "minLength": 1,
                        "maxLength": int(payload.dream_max_chars),
                    },
                    "tone": {"type": "string", "enum": [plain_id(t) for t in payload.tones]},
                    "about": one_of(day),
                },
            },
            "ideas": {
                "type": "array",
                "maxItems": 3,
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["grows", "text", "roots", "thought"],
                    "properties": {
                        "grows": one_of(list(ideas(job))),
                        "text": {"type": "string", "minLength": 1, "maxLength": 160},
                        "roots": {
                            "type": "array",
                            "minItems": 1,
                            "maxItems": 8,
                            "items": root_items,
                        },
                        "thought": {"type": "string", "minLength": 1, "maxLength": 160},
                    },
                },
            },
            "intentions": {
                "type": "array",
                "maxItems": int(payload.max_intentions),
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["action", "about", "thought"],
                    "properties": {
                        "action": {
                            "type": "string",
                            "enum": [plain_id(a) for a in payload.actions],
                        },
                        "about": one_of(list(known(job, texts, settings))),
                        "thought": {"type": "string", "minLength": 1, "maxLength": 160},
                    },
                },
            },
            "morning_mood": {"anyOf": [{"type": "string", "enum": moods}, {"type": "null"}]}
            if moods
            else {"type": "null"},
        },
    }


class Shown:
    """The checks of a text the player reads: content guard, no raw id, and (German, A1613) no
    thing of this world the inhabitant does not know."""

    def __init__(
        self,
        job: GenerationJob,
        guard: TextGuard,
        texts: TextCatalog | None,
        settings: dict[str, Any],
    ) -> None:
        self._job, self._guard = job, guard
        self._locale = plain_id(job.display_locale)
        self._ids = {s["id"] for s in known(job).values()} | {
            s["id"] for s in day_things(job).values()
        }
        self._words: set[str] | None = None
        self._world: set[str] = set()
        if checks_nouns(settings, self._locale):
            self._words = request_words(
                job.model_dump(mode="json"),
                texts,
                self._locale,
                settings_words(settings, "general_words", self._locale),
            )
            self._world = world_things(texts, self._locale)

    def problem(self, text: str) -> str | None:
        if not self._guard.check(text).allowed:
            return "content guard"
        if any(_ID_LIKE.fullmatch(i) and i in text.lower() for i in self._ids):
            return "raw id"
        if self._words is not None:
            unknown = unknown_nouns(text, self._words, heads=True, only=self._world)
            if unknown:
                return f"unknown: {', '.join(unknown[:3])}"
        return None


def parse_review(
    raw: str,
    job: GenerationJob,
    guard: TextGuard,
    moods: list[str],
    texts: TextCatalog | None = None,
    settings: dict[str, Any] | None = None,
) -> tuple[dict[str, Any], list[str]]:
    """The checked result and notes on what was left out (for the log)."""
    schema = output_schema(job, moods, texts, settings)
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    if isinstance(data, dict) and isinstance(cast(dict[str, Any], data).get("kept"), list):
        values_in = cast(dict[str, Any], data)
        values_in["kept"] = list(dict.fromkeys(cast(list[Any], values_in["kept"])))
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidOutputError(f"{where}: {first.message}")
    values = cast(dict[str, Any], data)
    summary = " ".join(str(values["summary"]).split())
    if not summary:
        raise InvalidOutputError("summary is empty")
    payload = review_payload(job)
    formative = sorted({e.tick for e in payload.events if e.formative})
    kept = sorted(int(t) for t in values["kept"])
    if kept != formative:
        raise InvalidOutputError(f"kept {kept} must name all formative events {formative}")
    locale = plain_id(job.display_locale)
    shown = Shown(job, guard, texts, settings or {})
    notes: list[str] = []

    def text(value: str, what: str) -> dict[str, str] | None:
        clean = " ".join(value.split())
        problem = shown.problem(clean)
        if problem is not None:
            notes.append(f"{what} left out ({problem}): {clean}")
            return None
        return {"locale": locale, "text": clean}

    dream_raw = cast(dict[str, Any], values["dream"])
    dream_text = text(str(dream_raw["text"]), "dream")
    things = day_things(job, texts, settings)
    dream = (
        {
            "text": dream_text,
            "tone": str(dream_raw["tone"]),
            "about": things.get(str(dream_raw["about"])) if dream_raw["about"] else None,
        }
        if dream_text is not None
        else None
    )
    keys, own = known(job, texts, settings), ideas(job)
    out_ideas: list[dict[str, Any]] = []
    new = 0
    for item in cast(list[dict[str, Any]], values["ideas"]):
        grows = item.get("grows")
        if grows is None:
            if new >= int(payload.max_new_ideas):
                notes.append(
                    f"idea left out (more than {payload.max_new_ideas} new): {item['text']}"
                )
                continue
            new += 1
        roots: list[dict[str, str]] = []
        for key in cast(list[str], item["roots"]):
            if key in keys:
                roots.append(keys[key])
            elif key in own:
                roots.append({"kind": "idea", "id": plain_id(own[key].id)})
        if not roots:
            notes.append(f"idea left out (no root of its own knowledge): {item['text']}")
            continue
        out_ideas.append(
            {
                "id": plain_id(own[grows].id) if isinstance(grows, str) and grows in own else None,
                "text": " ".join(str(item["text"]).split())[:160],
                "roots": roots,
                "thought": text(str(item["thought"]), "idea thought"),
            }
        )
    intentions = [
        {
            "action": str(item["action"]),
            "about": keys.get(str(item["about"])) if item.get("about") else None,
            "thought": text(str(item["thought"]), "intention thought"),
        }
        for item in cast(list[dict[str, Any]], values["intentions"])
    ]
    result = {
        "summary": summary,
        "kept": kept,
        "dream": dream,
        "ideas": out_ideas,
        "intentions": intentions,
        "morning_mood": values["morning_mood"],
    }
    return result, notes


class OllamaReviews:
    """The nightly look back with the language model (night kind, background priority)."""

    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        settings: ModelCallSettings,
        log: CallLog,
        guard: TextGuard,
        moods: list[str],
        texts: TextCatalog | None = None,
    ) -> None:
        self._client, self._queue, self._template = client, queue, template
        self._settings, self._log, self._guard = settings, log, guard
        self._moods, self._texts = moods, texts

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        rendered = self._template.render(build_values(job, self._template, self._texts))
        settings = self._template.settings
        answer = await ask_model(
            role=ROLE,
            job=job,
            rendered=rendered,
            schema=output_schema(job, self._moods, self._texts, settings),
            parse=lambda raw: parse_review(
                raw, job, self._guard, self._moods, self._texts, settings
            ),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
        )
        result, _ = answer.value
        return JobOutcome(
            result={"review": result},
            source="ollama",
            model=self._client.model[:128],
            prompt_version=rendered.version,
            latency_ms=answer.latency_ms,
            attempts=answer.attempts,
        )


class MockReviews:
    """Deterministic stand-in: the summary grows by the day's kinds, a calm dream, no idea, no
    intention (ideas and intentions are the model's; the mock never invents them)."""

    DREAMS: dict[str, str] = {  # noqa: RUF012 - constant table
        "de": "Ich träume, und alles ist ruhig.",
        "en": "I dream, and everything is calm.",
    }

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = review_payload(job)
        formative = sorted({e.tick for e in payload.events if e.formative})
        kinds = ", ".join(dict.fromkeys(humanize(plain_id(e.kind)) for e in payload.events))
        summary = (payload.memory.strip() + f" Today: {kinds}.").strip()[
            -int(payload.memory_max_chars) :
        ]
        locale = plain_id(job.display_locale)
        tones = [plain_id(t) for t in payload.tones]
        dream = {
            "text": {"locale": locale, "text": self.DREAMS.get(locale, self.DREAMS["en"])},
            "tone": "calm" if "calm" in tones else tones[0],
            "about": None,
        }
        result = {
            "summary": summary,
            "kept": formative,
            "dream": dream,
            "ideas": [],
            "intentions": [],
            "morning_mood": None,
        }
        return JobOutcome({"review": result}, "mock", "mock", None, 0, 1)


class ReplayReviews:
    """Answers from the call log: the first valid model answer recorded for the job id."""

    def __init__(
        self,
        log: CallLog,
        guard: TextGuard,
        moods: list[str],
        texts: TextCatalog | None = None,
        settings: dict[str, Any] | None = None,
    ) -> None:
        self._log, self._guard, self._moods = log, guard, moods
        self._texts, self._settings = texts, settings
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for job {job_id}")
        try:
            result, _ = parse_review(
                str(row["raw_output"]), job, self._guard, self._moods, self._texts, self._settings
            )
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            result={"review": result},
            source="replay",
            model=str(row.get("model", "replay"))[:128],
            prompt_version=str(row.get("prompt_version", "")) or None,
            latency_ms=0,
            attempts=attempts if isinstance(attempts, int) else 1,
        )
