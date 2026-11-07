"""Turns a DecisionRequest into the values of the decider prompt.

Deterministic: the same request always gives the same text (stable sorting, fixed number
formatting), so prompts can be snapshot-tested and replays stay comparable. Long lists are cut
to a character budget; options are never cut.
"""

from __future__ import annotations

from collections.abc import Sequence
from typing import Any, cast

from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import ContextError as ContextError
from glassworld_ai.words import bands as _bands
from glassworld_ai.words import humanize as _humanize
from glassworld_ai.words import plain_id as _id
from glassworld_ai.words import vocab_line as _vocab_line
from glassworld_ai.words import word as _word
from glassworld_contracts import DecisionRequest

NOTHING = "- (nothing)"


def build_values(
    request: DecisionRequest,
    template: PromptTemplate,
    vocab: Vocabulary,
    char_budget: int,
) -> dict[str, str]:
    """Values for every placeholder of the decider prompt."""
    settings = template.settings
    need_words = _bands(settings, "need_words")
    mood_words = _bands(settings, "mood_words")
    trait_words = _bands(settings, "trait_words")
    will_words = _bands(settings, "will_words")
    languages: object = settings.get("language_names", {})
    locale = _id(request.display_locale)
    language: object = (
        cast(dict[str, object], languages).get(locale) if isinstance(languages, dict) else None
    )
    if not isinstance(language, str):
        raise ContextError(f"no language name for locale {locale}")
    max_objects = int(settings.get("max_objects", 12))
    max_moods = int(settings.get("max_moods", 5))

    inhabitant = request.inhabitant
    needs = sorted(inhabitant.needs, key=lambda n: (n.value, _id(n.id)))  # most urgent first
    moods = sorted(inhabitant.mood, key=lambda m: (-m.value, _id(m.id)))[:max_moods]
    traits = sorted(inhabitant.traits, key=lambda t: _id(t.id))
    will = inhabitant.will_to_live

    targets = {_id(o.target) for o in request.options if o.target is not None}
    # Objects that options refer to always stay; the rest by distance, then id.
    objects = sorted(
        request.perception.objects,
        key=lambda o: (_id(o.id) not in targets, o.distance_m, _id(o.id)),
    )

    need_lines = [
        f"- {_vocab_line(vocab, 'need', _id(n.id))}: {n.value:.2f} ({_word(need_words, n.value)})"
        for n in needs
    ]
    mood_lines = [
        f"- {_humanize(_id(m.id))}: {_word(mood_words, m.value)} ({m.value:.2f})" for m in moods
    ]
    trait_lines = [
        f"- {_humanize(_id(t.id))}: {_word(trait_words, t.value)} ({t.value:.2f})" for t in traits
    ]
    values = {
        "will_to_live": f"{will:.2f} ({_word(will_words, will)})",
        "needs": "\n".join(need_lines) or NOTHING,
        "moods": "\n".join(mood_lines) or "- calm, no strong feelings",
        "traits": "\n".join(trait_lines) or NOTHING,
        "environment": _environment(request, vocab),
        "objects": "",
        "options": "\n".join(_option_line(request, vocab, o) for o in request.options),
        "expressions": ", ".join(vocab.ids("expression")) or "(none)",
        "thought_language": language,
    }

    # Fit the objects into what is left of the character budget; option targets always stay.
    used = sum(len(v) for k, v in values.items() if k != "objects")
    lines: list[str] = []
    for obj in objects[:max_objects]:
        line = _object_line(_id(obj.id), _id(obj.type), obj.distance_m, obj.known_properties)
        if _id(obj.id) not in targets and used + len(line) > char_budget:
            break
        lines.append(line)
        used += len(line) + 1
    hidden = len(objects) - len(lines)
    if hidden > 0:
        lines.append(f"- ... and {hidden} more things further away")
    values["objects"] = "\n".join(lines) or NOTHING
    return values


def _environment(request: DecisionRequest, vocab: Vocabulary) -> str:
    env = request.environment
    weather = ", ".join(_vocab_line(vocab, "weather", _id(w)) for w in env.weather)
    time_of_day = _vocab_line(vocab, "time_of_day", _id(env.time_of_day))
    return "\n".join(
        [
            f"- Time of day: {time_of_day}",
            f"- It feels like {env.temperature_c:.1f} degrees Celsius to you.",
            f"- Weather: {weather}",
        ]
    )


def _object_line(object_id: str, kind: str, distance: float, known: Sequence[Any]) -> str:
    if known:
        beliefs = "; ".join(
            f"{'is' if k.value else 'is not'} {_humanize(_id(k.property))} "
            f"(sure {k.confidence:.1f})"
            for k in sorted(known, key=lambda k: _id(k.property))
        )
        knowledge = f"you believe it {beliefs}"
    else:
        knowledge = "you know nothing about it yet"
    return f"- {_humanize(kind)} [{object_id}], {distance:.1f} m away: {knowledge}"


def _option_line(request: DecisionRequest, vocab: Vocabulary, option: Any) -> str:
    option_id = _id(option.option_id)
    action = _vocab_line(vocab, "action", _id(option.action))
    if option.target is None:
        return f"- {option_id}: {action}"
    what = _object_name(request, _id(option.target))
    raw_partners: list[object] = list(getattr(option, "partners", None) or [])
    partners = [_object_name(request, _id(p)) for p in raw_partners]
    together = f" with {', '.join(partners)}" if partners else ""
    return f"- {option_id}: {action} -> {what}{together}"


def _object_name(request: DecisionRequest, object_id: str) -> str:
    found = next((o for o in request.perception.objects if _id(o.id) == object_id), None)
    return f"{_humanize(_id(found.type))} [{object_id}]" if found else f"[{object_id}]"
