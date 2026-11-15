# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false
"""A talk between two inhabitants (A1735, B2782, D1119 v3): job kind dialogue.

One call writes 2 to max_turns turns, the first speaker first, then taking turns. Content only from
what each one knows, remembers and feels and from their bond; the tone from their traits (words of
the prompt's settings, no example sentence, D1025). A talk changes nothing in the world - knowledge
passes only through teach. The service checks every turn before it goes out:
- the order (taking turns, first speaker first) and the schema - else ask again with the reason;
- each text: content guard, the world check of A1613 (no thing of the world it does not know), no
  tick, number or key (A1721), the right language (TL #26281), no made-up name (A1728), the word
  limit, and for a talk with a minor no reserved topic (B9) - the talk ends before the first turn
  that fails; the turns before it stay (R19, B2644).
"""

from __future__ import annotations

import json
from typing import Any, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
from glassworld_ai.decider.thoughts import wrong_language
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
from glassworld_ai.review import made_up_name, not_plain
from glassworld_ai.safety import TextGuard
from glassworld_ai.texts import TextCatalog
from glassworld_ai.words import ContextError, humanize, plain_id, quote
from glassworld_contracts import GenerationJob

ROLE = "dialogue"
ACTS = ["agree", "answer", "ask", "comfort", "greet", "joke", "tell", "wonder"]
SPEAKER_KEYS = ["a", "b"]


def dialogue_payload(job: GenerationJob) -> Any:
    payload = getattr(job.payload, "dialogue", None)
    if payload is None:
        raise ContextError("job has no dialogue payload")
    return payload


def _table(settings: dict[str, Any], key: str) -> dict[str, Any]:
    raw: object = settings.get(key)
    return cast(dict[str, Any], raw) if isinstance(raw, dict) else {}


def _level(value: float) -> str:
    return "low" if value < 0.3 else "medium" if value < 0.7 else "high"


def _name_of(raw: Any, locale: str) -> str | None:
    if raw is None:
        return None
    text = getattr(raw, locale, None) or getattr(raw, "en", None)
    return text.strip() if isinstance(text, str) and text.strip() else None


def minors(job: GenerationJob, settings: dict[str, Any]) -> bool:
    """B9: one of the two is a minor (life stages of the prompt's settings, data)."""
    young = {str(s) for s in cast(list[Any], settings.get("minor_stages", []))}
    return any(
        plain_id(getattr(s.inhabitant, "life_stage", None) or "") in young
        for s in dialogue_payload(job).speakers
    )


def known_names(job: GenerationJob) -> set[str]:
    """The names a turn may use: both speakers' own names and what each calls the other."""
    locale = plain_id(job.display_locale)
    names: set[str] = set()
    for speaker in dialogue_payload(job).speakers:
        own = getattr(speaker.inhabitant, "name", None)
        if isinstance(own, str) and own.strip():
            names.add(own.strip())
        other = _name_of(speaker.calls_other, locale)
        if other:
            names.add(other)
    return names


def _speaker_block(
    speaker: Any, key: str, job: GenerationJob, settings: dict[str, Any], texts: TextCatalog | None
) -> str:
    locale = plain_id(job.display_locale)
    who = speaker.inhabitant
    own = getattr(who, "name", None)
    lines = [f"## {key.upper()}: {own if isinstance(own, str) and own else 'has no name yet'}"]
    stage = getattr(who, "life_stage", None)
    if stage is not None:
        lines.append(f"- life phase: {humanize(plain_id(stage))}")
    other = _name_of(speaker.calls_other, locale)
    lines.append(
        f'- calls the other: "{other}"' if other else "- has no name for the other: says du"
    )
    styles = _table(settings, "trait_styles")
    tone = []
    for trait in who.traits:
        words = _table(styles, plain_id(trait.id))
        word = words.get(_level(float(trait.value)))
        if isinstance(word, str) and word:
            tone.append(word)
    lines.append(f"- way of speaking: {', '.join(tone) or 'plain'}")
    moods = sorted(
        ((plain_id(m.id), float(m.value)) for m in who.mood if float(m.value) > 0.3),
        key=lambda m: -m[1],
    )
    lines.append(f"- feels: {', '.join(humanize(m) for m, _ in moods[:2]) or 'calm'}")
    pressing = [humanize(plain_id(n.id)) for n in who.needs if float(n.value) < 0.3]
    if pressing:
        lines.append(f"- needs: {', '.join(pressing[:2])}")
    bond = next(iter(who.relationships), None)
    if bond is not None:
        lines.append(
            f"- towards the other: affection {_level(float(bond.affection))}, "
            f"trust {_level(float(bond.trust))}"
        )
    thought = getattr(speaker, "last_thought", None)
    if thought is not None and getattr(thought, "text", None):
        lines.append(f"- just thought: {quote(str(thought.text))}")
    memory = str(getattr(who, "memory", "") or "").strip()
    if memory:
        lines.append(f"- remembers: {quote(memory[:400])}")
    beliefs: dict[str, list[str]] = {}
    for entry in who.knowledge:
        if plain_id(entry.subject.kind) != "type":
            continue
        ident = plain_id(entry.subject.id)
        name = (texts.thing(locale, ident) if texts is not None else None) or humanize(ident)
        word = "is" if entry.value else "is not"
        beliefs.setdefault(name, []).append(f"{word} {humanize(plain_id(entry.property))}")
    known = [f"  - {n}: {', '.join(v)}" for n, v in list(beliefs.items())[:8]]
    lines.append("- knows:" if known else "- knows: (little yet)")
    lines.extend(known)
    return "\n".join(lines)


def build_values(
    job: GenerationJob, template: PromptTemplate, texts: TextCatalog | None = None
) -> dict[str, str]:
    payload = dialogue_payload(job)
    settings = template.settings
    locale = plain_id(job.display_locale)
    language = cast(dict[str, str], _table(settings, "language_names")).get(locale)
    if language is None:
        raise ContextError(f"no language name for {locale}")
    blocks = [
        _speaker_block(s, k, job, settings, texts)
        for s, k in zip(payload.speakers, SPEAKER_KEYS, strict=True)
    ]
    minor_rule = str(settings.get("minor_rule", "")) if minors(job, settings) else ""
    return {
        "language": language,
        "max_turns": str(payload.max_turns),
        "max_words": str(payload.max_words),
        "acts": ", ".join(ACTS),
        "expressions": ", ".join(plain_id(e) for e in payload.expressions) or "(none)",
        "minor_rule": minor_rule,
        "speaker_a": blocks[0],
        "speaker_b": blocks[1],
    }


def output_schema(job: GenerationJob) -> dict[str, Any]:
    payload = dialogue_payload(job)
    expressions = [plain_id(e) for e in payload.expressions]
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["turns"],
        "properties": {
            "turns": {
                "type": "array",
                "minItems": 2,
                "maxItems": int(payload.max_turns),
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["speaker", "text", "act", "expression"],
                    "properties": {
                        "speaker": {"type": "string", "enum": SPEAKER_KEYS},
                        "text": {"type": "string", "minLength": 1, "maxLength": 120},
                        "act": {"type": "string", "enum": ACTS},
                        "expression": (
                            {"anyOf": [{"type": "string", "enum": expressions}, {"type": "null"}]}
                            if expressions
                            else {"type": "null"}
                        ),
                    },
                },
            }
        },
    }


class Turns:
    """The checks of one turn's text (the player reads it)."""

    def __init__(
        self,
        job: GenerationJob,
        guard: TextGuard,
        texts: TextCatalog | None,
        settings: dict[str, Any],
    ) -> None:
        self._guard, self._settings = guard, settings
        self._locale = plain_id(job.display_locale)
        self._max_words = int(dialogue_payload(job).max_words)
        self._names = known_names(job)
        self._minor = minors(job, settings)
        self._words: set[str] | None = None
        self._world: set[str] = set()
        if checks_nouns(settings, self._locale):
            self._words = request_words(
                job.model_dump(mode="json"),
                texts,
                self._locale,
                settings_words(settings, "general_words", self._locale),
            ) | {n for n in self._names}
            self._world = world_things(texts, self._locale)

    def problem(self, text: str) -> str | None:
        if not self._guard.check(text).allowed:
            return "content guard"
        if (found := not_plain(text)) is not None:
            return f"a number or key ({found})"
        if (foreign := wrong_language(text, self._locale, self._settings)) is not None:
            return f"language ({foreign})"
        if len(text.split()) > self._max_words:
            return "too many words"
        if (invented := made_up_name(text, self._names)) is not None:
            return f"made-up name ({invented})"
        if self._minor:
            reserved = {
                str(w).lower()
                for w in cast(
                    list[Any], _table(self._settings, "minor_words").get(self._locale, [])
                )
            }
            hit = next(
                (
                    w
                    for w in text.lower().replace("!", " ").replace("?", " ").split()
                    if w.strip(".,;:") in reserved
                ),
                None,
            )
            if hit is not None:
                return f"reserved topic for a minor ({hit.strip('.,;:')})"
        if self._words is not None:
            unknown = unknown_nouns(text, self._words, heads=True, only=self._world)
            if unknown:
                return f"unknown: {', '.join(unknown[:3])}"
        return None


def parse_dialogue(
    raw: str,
    job: GenerationJob,
    guard: TextGuard,
    texts: TextCatalog | None = None,
    settings: dict[str, Any] | None = None,
) -> tuple[dict[str, Any], list[str]]:
    """The checked turns and notes on what was left out (for the log)."""
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    errors = sorted(
        Draft202012Validator(output_schema(job)).iter_errors(data), key=lambda e: list(e.path)
    )
    if errors:
        first = errors[0]
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidOutputError(f"{where}: {first.message}")
    turns = cast(list[dict[str, Any]], cast(dict[str, Any], data)["turns"])
    order = [str(t["speaker"]) for t in turns]
    expected = [SPEAKER_KEYS[i % 2] for i in range(len(turns))]
    if order != expected:
        raise InvalidOutputError(f"the turns must alternate, A first: got {', '.join(order)}")
    ids = [plain_id(s.inhabitant.id) for s in dialogue_payload(job).speakers]
    locale = plain_id(job.display_locale)
    check = Turns(job, guard, texts, settings or {})
    out: list[dict[str, Any]] = []
    notes: list[str] = []
    for n, turn in enumerate(turns, start=1):
        text = " ".join(str(turn["text"]).split())
        problem = check.problem(text)
        if problem is not None:
            notes.append(f"talk ends before turn {n} ({problem}): {text}")
            break
        out.append(
            {
                "speaker": ids[SPEAKER_KEYS.index(str(turn["speaker"]))],
                "text": {"locale": locale, "text": text},
                "act": str(turn["act"]),
                "expression": turn["expression"],
            }
        )
    return {"turns": out}, notes


def _feedback(settings: dict[str, Any]) -> str | None:
    value: object = _table(settings, "retry").get("feedback")
    return value if isinstance(value, str) else None


class OllamaDialogues:
    """A talk with the language model (any time, background priority)."""

    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        settings: ModelCallSettings,
        log: CallLog,
        guard: TextGuard,
        texts: TextCatalog | None = None,
    ) -> None:
        self._client, self._queue, self._template = client, queue, template
        self._settings, self._log, self._guard, self._texts = settings, log, guard, texts

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        rendered = self._template.render(build_values(job, self._template, self._texts))
        settings = self._template.settings
        answer = await ask_model(
            role=ROLE,
            job=job,
            rendered=rendered,
            schema=output_schema(job),
            parse=lambda raw: parse_dialogue(raw, job, self._guard, self._texts, settings),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
            feedback=_feedback(settings),
        )
        result, _ = answer.value
        return JobOutcome(
            result={"dialogue": result},
            source="ollama",
            model=self._client.model[:128],
            prompt_version=rendered.version,
            latency_ms=answer.latency_ms,
            attempts=answer.attempts,
        )


class MockDialogues:
    """Stand-in without a model: no words (B2644 - no fixed sentence); the core shows the talk
    without text, as it does today."""

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        dialogue_payload(job)
        return JobOutcome({"dialogue": {"turns": []}}, "mock", "mock", None, 0, 1)


class ReplayDialogues:
    """Answers from the call log: the first valid model answer recorded for the job id."""

    def __init__(
        self,
        log: CallLog,
        guard: TextGuard,
        texts: TextCatalog | None = None,
        settings: dict[str, Any] | None = None,
    ) -> None:
        self._log, self._guard, self._texts, self._settings = log, guard, texts, settings
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for job {job_id}")
        try:
            result, _ = parse_dialogue(
                str(row["raw_output"]), job, self._guard, self._texts, self._settings
            )
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            result={"dialogue": result},
            source="replay",
            model=str(row.get("model", "replay"))[:128],
            prompt_version=str(row.get("prompt_version", "")) or None,
            latency_ms=0,
            attempts=attempts if isinstance(attempts, int) else 1,
        )
