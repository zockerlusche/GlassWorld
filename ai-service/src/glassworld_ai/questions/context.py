"""Turns a question job into the values of the question prompt.

Deterministic like the decider context: stable sorting and fixed number formatting, so the
rendered prompt can be snapshot-tested. Only what the inhabitant knows or notices goes in; the
world's truth never does (ADR-0005).
"""

from __future__ import annotations

from string import Template
from typing import Any

from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import (
    ContextError,
    bands,
    humanize,
    knowledge_line,
    plain_id,
    quote,
    subject_name,
    table,
    vocab_line,
    word,
)
from glassworld_contracts import GenerationJob

NOTHING = "- (nothing)"


def question_payload(job: GenerationJob) -> Any:
    payload = job.payload.question
    if payload is None:
        raise ContextError("job has no question payload")
    return payload


def build_values(job: GenerationJob, template: PromptTemplate, vocab: Vocabulary) -> dict[str, str]:
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
    return {
        "question": Template(question_text).safe_substitute(
            subject=subject_name(payload.subject, settings)
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
        "knowledge": "\n".join(knowledge_line(k, settings) for k in knowledge)
        or "- (you know nothing about it yet)",
        "perception": "\n".join(
            f"- {vocab_line(vocab, 'property', plain_id(p))}"
            for p in sorted(payload.perception, key=plain_id)
        )
        or "- (nothing special)",
        "properties": "\n".join(
            f"- {vocab_line(vocab, 'property', p)}" for p in vocab.ids("property")
        ),
        "actions": ", ".join(vocab.ids("action")) or "(none)",
        "max_guesses": str(max_guesses),
        "thought_language": language,
    }
