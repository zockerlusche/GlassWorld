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
