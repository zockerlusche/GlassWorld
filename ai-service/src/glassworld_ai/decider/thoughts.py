"""Natural thoughts (W1687, prompt v4): a check on the model's sentence and a stand-in from data.

The player reads the thought, so a sentence that slipped into English, shows an id or rambles is
not shown. Instead of asking the model again (costly), the thought is replaced by the stand-in of
the chosen action from content/i18n (`msg.thought.<action>`), or left out if the data has none.
The rules (word limit, blocked words per language) are prompt settings: data, not code.
"""

from __future__ import annotations

import re
from typing import Any, cast

from glassworld_ai.texts import TextCatalog

_WORD = re.compile(r"[^\W\d_]+", re.UNICODE)
# Ids and code words, not German compounds: "inh-0002", "obj-42", "red_berry".
_ID = re.compile(r"\b[a-z]+-\d+\b|\b[a-z]+_[a-z0-9_]+\b", re.IGNORECASE)


def thought_problem(text: str, locale: str, settings: dict[str, Any]) -> str | None:
    """Why a thought must not be shown (stable reason), or None if it may."""
    raw: object = settings.get("thought_check")
    if not isinstance(raw, dict):
        return None
    rules = cast(dict[str, Any], raw)
    if _ID.search(text):
        return "id"
    words = [w.lower() for w in _WORD.findall(text)]
    if len(words) > int(rules.get("max_words", 12)):
        return "too_long"
    blocked_raw: object = rules.get("blocked_words", {})
    blocked = (
        {str(w).lower() for w in cast(dict[str, list[Any]], blocked_raw).get(locale, [])}
        if isinstance(blocked_raw, dict)
        else set[str]()
    )
    if any(w in blocked for w in words):
        return "blocked_word"
    return None


def stand_in(texts: TextCatalog | None, locale: str, action: str) -> str | None:
    """The data's stand-in thought for an action in the player's language, if there is one."""
    return texts.get(locale, f"msg.thought.{action}") if texts is not None else None
