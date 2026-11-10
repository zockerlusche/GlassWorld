"""Turns a DecisionRequest into the values of the decider prompt.

Deterministic: the same request always gives the same text (stable sorting, fixed number
formatting), so prompts can be snapshot-tested and replays stay comparable. Long lists are cut
to a character budget; options are never cut.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from typing import Any, cast

from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.texts import TextCatalog
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import ContextError as ContextError
from glassworld_ai.words import bands as _bands
from glassworld_ai.words import humanize as _humanize
from glassworld_ai.words import knowledge_line, quote, subject_name
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
    texts: TextCatalog | None = None,
) -> dict[str, str]:
    """Values for every placeholder of the decider prompt.

    From prompt v4 on (W1687) things carry their name in the player's language from the content
    data, so the thought can say "die rote Beere" instead of an English id or "das Objekt".
    """
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
    unknown_raw: object = settings.get("unknown_thing", {})
    unknown = (
        cast(dict[str, str], unknown_raw).get(locale) if isinstance(unknown_raw, dict) else None
    )

    def called(type_id: str) -> str | None:
        """v4: the thing's name in the player's language (data), or the data's 'unknown' word."""
        if template.version < 4 or texts is None:
            return None
        return texts.thing(locale, type_id) or unknown

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
        "options": "\n".join(
            _option_line(request, vocab, o, called, locale if template.version >= 4 else None)
            for o in request.options
        ),
        "expressions": ", ".join(vocab.ids("expression")) or "(none)",
        "thought_language": language,
    }

    if template.version >= 2:
        # v2 parts count against the budget too (review GW1 D2); objects get what is left.
        # v4 (W1687): "someone", not "person" - the model turned "person" into "der Mensch".
        who = "someone" if template.version >= 4 else "person"

        def nickname(label: str) -> str | None:
            """v5 (R11): a feature nickname in the player's language (vocab.nickname.<id>)."""
            if template.version < 5:
                return None
            found = texts.get(locale, f"vocab.nickname.{label}") if texts is not None else None
            return found or _humanize(label)

        values.update(
            _v2_values(
                request, settings, vocab, signs=template.version >= 3, who=who, nickname=nickname
            )
        )
    if template.version >= 3:
        values["body"] = _body(request, settings)
        values["environment"] = _environment(request, vocab, air=True)
    if template.version >= 4:  # W1698: the own name, so it can introduce itself
        own = _proper(request.inhabitant.name)
        values["own_name"] = f'"{own}"' if own else "(none yet)"
    if template.version >= 5:
        values["life"] = _life(request, settings)
        values["culture"] = _culture(request, settings, locale)
    # Fit the objects into what is left of the character budget; option targets always stay.
    used = sum(len(v) for k, v in values.items() if k != "objects")
    lines: list[str] = []
    for obj in objects[:max_objects]:
        line = _object_line(
            _id(obj.id), _id(obj.type), obj.distance_m, obj.known_properties, called(_id(obj.type))
        )
        if _id(obj.id) not in targets and used + len(line) > char_budget:
            break
        lines.append(line)
        used += len(line) + 1
    hidden = len(objects) - len(lines)
    if hidden > 0:
        lines.append(f"- ... and {hidden} more things further away")
    values["objects"] = "\n".join(lines) or NOTHING
    return values


def _v2_values(
    request: DecisionRequest,
    settings: dict[str, Any],
    vocab: Vocabulary,
    signs: bool = False,
    who: str = "person",
    nickname: Callable[[str], str | None] | None = None,
) -> dict[str, str]:
    """Memory, knowledge, habits and people (prompt v2, B4a S3). Only beliefs, never truth."""
    inhabitant = request.inhabitant
    relation_words = _bands(settings, "relation_words")
    habit_words = _bands(settings, "habit_words")
    max_knowledge = int(settings.get("max_knowledge", 12))
    max_recent = int(settings.get("max_recent", 6))
    seen_types = {_id(o.type) for o in request.perception.objects}
    seen_objects = {_id(o.id) for o in request.perception.objects}

    def about_seen(entry: Any) -> bool:
        kind, subject = _id(entry.subject.kind), _id(entry.subject.id)
        return (kind == "type" and subject in seen_types) or (
            kind == "object" and subject in seen_objects
        )

    # Beliefs about what is in sight first, then the newest; stable order for snapshots.
    knowledge = sorted(
        inhabitant.knowledge,
        key=lambda k: (not about_seen(k), -k.tick, _id(k.subject.id), _id(k.property)),
    )[:max_knowledge]
    summary = inhabitant.memory.summary.strip()
    recent = sorted(inhabitant.memory.recent, key=lambda e: -e.tick)[:max_recent]
    memory_lines = [quote(summary)] if summary else []
    memory_lines += [
        f"- recently: {_humanize(_id(e.kind))}"
        + (f" ({subject_name(e.about, settings)})" if e.about is not None else "")
        for e in recent
    ]
    relations = {_id(r.other): r for r in inhabitant.relationships}
    people_lines: list[str] = []
    for other in sorted(request.perception.inhabitants, key=lambda p: (p.distance_m, _id(p.id))):
        other_id = _id(other.id)
        relation = relations.get(other_id)
        if relation is None:
            feeling = "a stranger to you"
        else:
            feeling = (
                f"you like them {_word(relation_words, relation.affection)}, "
                f"trust them {_word(relation_words, relation.trust)}, "
                f"respect them {_word(relation_words, relation.respect)}"
            )
        seen = [_humanize(_id(c)) for c in other.visible_conditions or []]
        looks = f"; you notice: {', '.join(seen)}" if signs and seen else ""  # signs (B2543)
        # B9 (v5 fields, optional): the name this inhabitant knows, a child, someone lying still.
        called = getattr(other, "called", None)
        name = _proper(getattr(called, "name", None)) if called is not None else None
        feature = getattr(called, "label", None) if called is not None else None
        nick = nickname(_id(feature)) if nickname is not None and feature is not None else None
        if name:
            label = f'{who} you call "{name}"'
        elif nick:  # Marcel W1694 (R11): strangers keep the nickname this inhabitant gave them
            label = f'{who} you call "{nick}" (a nickname)'
        else:
            label = who
        if _id(getattr(other, "life_stage", None) or "") in {"baby", "toddler", "child"}:
            label += " (a child)"
        if getattr(other, "lying_still", None):
            looks += "; lies still and does not answer"
        people_lines.append(
            f"- {label} [{other_id}], {other.distance_m:.1f} m away: {feeling}{looks}"
        )
    habit_lines = [
        f"- {_word(habit_words, h.strength)}: {_vocab_line(vocab, 'action', _id(h.action))}"
        + (f" ({subject_name(h.about, settings)})" if h.about is not None else "")
        + (f" at {_humanize(_id(h.time_of_day))}" if h.time_of_day is not None else "")
        for h in sorted(inhabitant.habits, key=lambda h: (-h.strength, _id(h.action)))
    ]
    return {
        "memory": "\n".join(memory_lines) or "(you remember nothing special yet)",
        "knowledge": "\n".join(knowledge_line(k, settings) for k in knowledge)
        or "- (nothing yet beyond what you see)",
        "habits": "\n".join(habit_lines) or "- (no habits yet)",
        "people": "\n".join(people_lines) or "- (nobody)",
    }


def _body(request: DecisionRequest, settings: dict[str, Any]) -> str:
    """Prompt v3 (B2540/B2543): what you wear, how warm you feel, how you feel. Only beliefs and
    felt states, never the truth or the cause."""
    inhabitant = request.inhabitant
    lines: list[str] = []
    felt = inhabitant.felt_temperature_c
    if felt is not None:
        lines.append(f"- You feel {_word(_bands(settings, 'felt_words'), float(felt))}.")
    slots_raw: object = settings.get("slot_names", {})
    slots = cast(dict[str, str], slots_raw) if isinstance(slots_raw, dict) else {}
    if inhabitant.worn == []:  # sent and empty: wears nothing (absent = unknown, no line)
        lines.append("- You wear nothing.")
    for item in inhabitant.worn or []:
        slot = _id(item.slot)
        believed = ", ".join(_humanize(_id(p)) for p in item.properties)
        lines.append(
            f"- You wear {_humanize(_id(item.type_id))} {slots.get(slot, slot)}"
            + (f" (you think it is {believed})" if believed else "")
        )
    severity = _bands(settings, "severity_words")
    for cond in inhabitant.conditions or []:
        where = f" in the {_humanize(_id(cond.body_part))}" if cond.body_part is not None else ""
        feeling = f"{_word(severity, cond.severity)} {_humanize(_id(cond.condition))}"
        lines.append(f"- You feel {feeling}{where}.")
    return "\n".join(lines) or "- (nothing particular to notice)"


def _environment(request: DecisionRequest, vocab: Vocabulary, air: bool = False) -> str:
    env = request.environment
    weather = ", ".join(_vocab_line(vocab, "weather", _id(w)) for w in env.weather)
    time_of_day = _vocab_line(vocab, "time_of_day", _id(env.time_of_day))
    return "\n".join(
        [
            f"- Time of day: {time_of_day}",
            f"- Air temperature: {env.temperature_c:.1f} degrees Celsius."
            if air  # v3: the felt temperature is under "Your body" (review GW1 #116)
            else f"- It feels like {env.temperature_c:.1f} degrees Celsius to you.",
            f"- Weather: {weather}",
        ]
    )


def _object_line(
    object_id: str, kind: str, distance: float, known: Sequence[Any], name: str | None = None
) -> str:
    if known:
        beliefs = "; ".join(
            f"{'is' if k.value else 'is not'} {_humanize(_id(k.property))} "
            f"(sure {k.confidence:.1f})"
            for k in sorted(known, key=lambda k: _id(k.property))
        )
        knowledge = f"you believe it {beliefs}"
    else:
        knowledge = "you know nothing about it yet"
    said = f' (you call it "{name}")' if name else ""
    return f"- {_humanize(kind)} [{object_id}]{said}, {distance:.1f} m away: {knowledge}"


def _option_line(
    request: DecisionRequest,
    vocab: Vocabulary,
    option: Any,
    called: Callable[[str], str | None] | None = None,
    locale: str | None = None,
) -> str:
    option_id = _id(option.option_id)
    action = _vocab_line(vocab, "action", _id(option.action))
    if option.target is None:
        return f"- {option_id}: {action}"
    what = _object_name(request, _id(option.target), called)
    raw_partners: list[object] = list(getattr(option, "partners", None) or [])
    partners = [_object_name(request, _id(p), called) for p in raw_partners]
    together = f" with {', '.join(partners)}" if partners else ""
    about = _topic(getattr(option, "topic", None), called, locale) if locale else ""
    return f"- {option_id}: {action} -> {what}{together}{about}"


def _topic(topic: Any, called: Callable[[str], str | None] | None, locale: str) -> str:
    """v4+ (#156, D625): what a show/teach/warn option passes on - the inhabitant's own belief,
    with the thing's name in the player's language, so the thought can name it."""
    if topic is None:
        return ""
    kind = _id(topic.object_type)
    own: object = getattr(topic.name, locale, None) if topic.name is not None else None
    name = str(own) if own else (called(kind) if called else None)
    thing = f'"{name}" ({_humanize(kind)})' if name else _humanize(kind)
    belief = "is" if topic.value else "is not"
    return f"; about: {thing} - you believe it {belief} {_humanize(_id(topic.property))}"


def _object_name(
    request: DecisionRequest, object_id: str, called: Callable[[str], str | None] | None = None
) -> str:
    found = next((o for o in request.perception.objects if _id(o.id) == object_id), None)
    if found is None:
        return f"[{object_id}]"
    name = called(_id(found.type)) if called else None
    said = f' ("{name}")' if name else ""
    return f"{_humanize(_id(found.type))} [{object_id}]{said}"


def _proper(value: object) -> str | None:
    """A ProperName (pydantic root model or plain string), or None."""
    if value is None:
        return None
    root: object = getattr(value, "root", value)
    return str(root) if root else None


def _names_seen(request: DecisionRequest) -> dict[str, str]:
    """id -> the proper name this inhabitant knows for people in sight (B9 R11)."""
    names: dict[str, str] = {}
    for other in request.perception.inhabitants:
        name = _proper(other.called.name) if other.called is not None else None
        if name:
            names[_id(other.id)] = name
    return names


def _life(request: DecisionRequest, settings: dict[str, Any]) -> str:
    """Prompt v5 (B9 R1/R4/R6/R17): life stage and age, family, reputation, grief. All optional."""
    inhabitant = request.inhabitant
    stages = _table_of(settings, "life_stage_words")
    kin = _table_of(settings, "kin_words")
    names = _names_seen(request)
    lines: list[str] = []
    own = _proper(inhabitant.name)
    if own:  # W1698: so the inhabitant can introduce itself
        lines.append(f'- Your name is "{own}".')
    stage = _id(inhabitant.life_stage) if inhabitant.life_stage is not None else None
    if stage is not None or inhabitant.age_years is not None:
        who = stages.get(stage, _humanize(stage)) if stage is not None else "someone"
        years = f", {inhabitant.age_years} years old" if inhabitant.age_years is not None else ""
        lines.append(f"- You are {who}{years}.")
    for tie in inhabitant.family or []:
        other, relation = _id(tie.other), _id(tie.relation)
        name = f'"{names[other]}"' if other in names else f"[{other}]"
        gone = " - has died" if tie.deceased else ""
        lines.append(f"- {kin.get(relation, _humanize(relation))}: {name}{gone}")
    if inhabitant.reputation is not None:
        how = _word(_bands(settings, "relation_words"), float(inhabitant.reputation))
        lines.append(f"- Others think {how} well of you.")
    severity = _bands(settings, "severity_words")
    for loss in inhabitant.grief or []:
        other = _id(loss.about)
        name = f'"{names[other]}"' if other in names else f"[{other}]"
        lines.append(f"- You miss {name} ({_word(severity, float(loss.strength))} sadness).")
    return "\n".join(lines) or "- (nothing particular)"


def _culture(request: DecisionRequest, settings: dict[str, Any], locale: str) -> str:
    """Prompt v5 (B9 R18/R19/R21): the settlement's sayings and the customs and beliefs known."""
    inhabitant = request.inhabitant
    kinds = _table_of(settings, "culture_words")
    lines: list[str] = []
    sayings = inhabitant.settlement.sayings if inhabitant.settlement is not None else []
    for saying in sayings:
        lines.append(f'- a saying of your people: "{_in(saying, locale)}"')
    for entry in inhabitant.culture or []:
        kind = _id(entry.kind)
        what = kinds.get(kind, _humanize(kind))
        lines.append(f'- {what}: "{_in(entry.text, locale)}" (sure {float(entry.sure):.1f})')
    return "\n".join(lines) or "- (no customs yet)"


def _in(text: Any, locale: str) -> str:
    """A LocalizedName in the player's language (DE/EN), English as the fallback."""
    value: object = getattr(text, locale, None) or getattr(text, "en", "")
    return str(value)


def _table_of(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key, {})
    if not isinstance(raw, dict):
        return {}
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}
