"""Shared wording helpers for prompts: numbers to words, ids to readable text.

Deterministic, so rendered prompts can be snapshot-tested.
"""

from __future__ import annotations

from collections.abc import Sequence
from typing import Any, cast

from glassworld_ai.vocabulary import Vocabulary


class ContextError(Exception):
    """The prompt settings are incomplete."""


def plain_id(value: object) -> str:
    """Plain string of an id or enum (generated contract types may wrap ids in a RootModel)."""
    value = getattr(value, "root", value)
    value = getattr(value, "value", value)
    return str(value)


def bands(settings: dict[str, Any], key: str) -> list[tuple[float, str]]:
    table: object = settings.get(key)
    raw: object = cast(dict[str, object], table).get("bands") if isinstance(table, dict) else None
    if not isinstance(raw, list) or not raw:
        raise ContextError(f"prompt setting {key}.bands missing")
    return [(float(b[0]), str(b[1])) for b in raw]  # pyright: ignore[reportUnknownArgumentType,reportUnknownVariableType]


def word(table: Sequence[tuple[float, str]], value: float) -> str:
    """The word of the first band whose upper bound is above `value` (last band otherwise)."""
    for bound, text in table:
        if value < bound:
            return text
    return table[-1][1]


def humanize(identifier: str) -> str:
    return identifier.replace("_", " ").replace(".", " ")


def vocab_line(vocab: Vocabulary, kind: str, entry_id: str) -> str:
    description = vocab.describe(kind, entry_id)
    return f"{humanize(entry_id)} ({description})" if description else humanize(entry_id)


def table(settings: dict[str, Any], key: str) -> dict[str, str]:
    """A prompt setting that maps ids to words, e.g. [settings.source_words]."""
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def quote(text: str) -> str:
    """Generated text (memory summary) as a quoted data block: every line starts with "> ", so
    nothing in it can look like a heading or an instruction of the prompt."""
    return "\n".join(
        "> " + line.strip().lstrip("#").strip() for line in text.splitlines() if line.strip()
    )


def subject_name(subject: Any, settings: dict[str, Any]) -> str:
    """Readable name of a Subject: "thing 'red berry bush'", "person 'inh-0002'", ..."""
    kinds = table(settings, "subject_kinds")
    kind = plain_id(subject.kind)
    return f"{kinds.get(kind, kind)} '{humanize(plain_id(subject.id))}'"


def knowledge_line(entry: Any, settings: dict[str, Any]) -> str:
    """One belief with its source: what, sure how much, from where (and from whom)."""
    sources = table(settings, "source_words")
    belief = "is" if entry.value else "is not"
    source = plain_id(entry.source)
    how = sources.get(source, source)
    teller = f" ({plain_id(entry.told_by)})" if entry.told_by is not None else ""
    return (
        f"- {subject_name(entry.subject, settings)} {belief} {humanize(plain_id(entry.property))} "
        f"(sure {entry.confidence:.1f}; {how}{teller})"
    )
