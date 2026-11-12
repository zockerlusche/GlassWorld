"""Turns a question job into the values of the question prompt.

Deterministic like the decider context: stable sorting and fixed number formatting, so the
rendered prompt can be snapshot-tested. Only what the inhabitant knows or notices goes in; the
world's truth never does (ADR-0005).
"""

from __future__ import annotations

import re
from string import Template
from typing import Any, cast

from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import (
    ContextError,
    bands,
    humanize,
    knowledge_line,
    own_names,
    plain_id,
    quote,
    subject_name,
    table,
    vocab_line,
    without_names,
    without_runtime_ids,
    word,
)
from glassworld_contracts import GenerationJob

NOTHING = "- (nothing)"


def question_payload(job: GenerationJob) -> Any:
    payload = job.payload.question
    if payload is None:
        raise ContextError("job has no question payload")
    return payload


def _unname(text: str, subject_id: str, instead: str) -> str:
    """`text` without the name of the thing asked about: its id and its readable form, with or
    without an article, whatever the case ("a Flint", "sea_water", "the sea water")."""
    readable = re.escape(humanize(subject_id)).replace(r"\ ", r"[ _-]")
    pattern = rf"(?:\b(?:a|an|the|some)\s+)?\b{readable}(?:e?s)?\b"
    return re.sub(pattern, instead, text, flags=re.IGNORECASE)


def _moment(settings: dict[str, Any], night: bool) -> dict[str, str]:
    """v5 (B2652): the words for the moment of the question, from [settings.moment.night|day].
    Older versions have no such table and get nothing."""
    raw: object = settings.get("moment")
    if not isinstance(raw, dict):
        return {}
    part: object = cast(dict[str, object], raw).get("night" if night else "day")
    if not isinstance(part, dict):
        raise ContextError("prompt setting moment needs a night and a day table")
    return {str(k): str(v) for k, v in cast(dict[object, object], part).items()}


def build_values(
    job: GenerationJob, template: PromptTemplate, vocab: Vocabulary, night: bool = False
) -> dict[str, str]:
    """Values for every placeholder of the question prompt."""
    settings = template.settings
    payload = question_payload(job)
    inhabitant = payload.inhabitant
    locale = plain_id(job.display_locale)
    language = table(settings, "language_names").get(locale)
    if language is None:
        raise ContextError(f"no language name for locale {locale}")
    question_kind = plain_id(payload.question)
    question_text = table(settings, "questions").get(question_kind)
    if question_text is None:
        raise ContextError(f"no wording for question kind {question_kind}")
    need_words = bands(settings, "need_words")
    mood_words = bands(settings, "mood_words")
    trait_words = bands(settings, "trait_words")
    will_words = bands(settings, "will_words")
    max_moods = int(settings.get("max_moods", 5))
    max_knowledge = int(settings.get("max_knowledge", 12))
    max_guesses = int(settings.get("max_guesses", 4))

    needs = sorted(inhabitant.needs, key=lambda n: (n.value, plain_id(n.id)))
    moods = sorted(inhabitant.mood, key=lambda m: (-m.value, plain_id(m.id)))[:max_moods]
    traits = sorted(inhabitant.traits, key=lambda t: plain_id(t.id))
    will = inhabitant.will_to_live

    subject_key = (plain_id(payload.subject.kind), plain_id(payload.subject.id))
    # Knowledge about the subject itself first, then the rest (newest first), cut to the limit.
    knowledge = sorted(
        inhabitant.knowledge,
        key=lambda k: (
            (plain_id(k.subject.kind), plain_id(k.subject.id)) != subject_key,
            -k.tick,
            plain_id(k.subject.id),
            plain_id(k.property),
        ),
    )[:max_knowledge]

    summary = inhabitant.memory.summary.strip()
    # v2 (B4a R10): the thing asked about has no name here - a name like 'flint' or 'sea water'
    # gives away what the inhabitant has not found out. People keep their label.
    unnamed = (
        table(settings, "unnamed_subject").get(subject_key[0])
        if "unnamed_subject" in settings
        else None
    )
    if "unnamed_subject" in settings and subject_key[0] in ("type", "object") and not unnamed:
        raise ContextError(f"prompt setting unnamed_subject.{subject_key[0]} is empty")
    if unnamed:
        # The story so far must not name it either (review GW1 #215: the condensed memory
        # writes thing names, 'Found a flint'): the name becomes 'this thing' there too.
        summary = _unname(summary, plain_id(payload.subject.id), f"this {unnamed}")
        # ... and not by the name the inhabitant itself has for it (review GW1 #256): the
        # memory writes an invention by its label, and that name says what it is for.
        label: Any = getattr(payload, "label", None)
        if label is not None:
            own_names_of_it = [
                str(n) for n in (getattr(label, "de", None), getattr(label, "en", None)) if n
            ]
            summary = without_names(summary, own_names_of_it, f"this {unnamed}")

    def about(entry: Any) -> str:
        line = knowledge_line(entry, settings)
        own = (plain_id(entry.subject.kind), plain_id(entry.subject.id)) == subject_key
        return (
            line.replace(subject_name(entry.subject, settings), f"this {unnamed}", 1)
            if unnamed and own
            else line
        )

    def prop(p: str) -> str:
        # v4 (A1556, D763 Z21): a property by its name only. The descriptions of the vocabulary
        # tell cause and effect ('throws sparks when struck against a suitable stone', 'helps
        # sand melt into glass') - world knowledge the inhabitant has not found out.
        if settings.get("property_words") == "name":
            return humanize(p)
        return vocab_line(vocab, "property", p)

    moment = _moment(settings, night)
    values = {
        "question": Template(question_text).safe_substitute(
            subject=unnamed or subject_name(payload.subject, settings)
        ),
        "will_to_live": f"{will:.2f} ({word(will_words, will)})",
        "needs": "\n".join(
            f"- {vocab_line(vocab, 'need', plain_id(n.id))}: {n.value:.2f} "
            f"({word(need_words, n.value)})"
            for n in needs
        )
        or NOTHING,
        "moods": "\n".join(
            f"- {humanize(plain_id(m.id))}: {word(mood_words, m.value)} ({m.value:.2f})"
            for m in moods
        )
        or "- calm, no strong feelings",
        "traits": "\n".join(
            f"- {humanize(plain_id(t.id))}: {word(trait_words, t.value)} ({t.value:.2f})"
            for t in traits
        )
        or NOTHING,
        "memory": quote(summary) if summary else "(you remember nothing special yet)",
        "knowledge": "\n".join(about(k) for k in knowledge) or "- (you know nothing about it yet)",
        "perception": "\n".join(
            f"- {prop(plain_id(p))}" for p in sorted(payload.perception, key=plain_id)
        )
        or "- (nothing special)",
        "properties": "\n".join(f"- {prop(p)}" for p in vocab.ids("property")),
        "actions": ", ".join(vocab.general_ids("action")) or "(none)",
        "max_guesses": str(max_guesses),
        "thought_language": language,
        **moment,
    }
    made: object = settings.get("made_thing")
    if isinstance(made, str):
        # v3 (#251): other invented things by the inhabitant's own name; never the name of
        # the thing asked about, and never a runtime id.
        own = own_names(
            ((k.subject.id, getattr(k, "label", None)) for k in inhabitant.knowledge), locale
        )
        own.pop(subject_key[1], None)
        values = {
            key: without_runtime_ids(text, own, made, everywhere=True)
            for key, text in values.items()
        }
    return values
