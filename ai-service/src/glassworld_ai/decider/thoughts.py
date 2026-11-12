"""Natural thoughts (W1687, prompt v4): a check on the model's sentence.

The player reads the thought, so a sentence that slipped into English, shows an id or rambles is
not shown. The model is not asked again (costly), and no fixed stand-in takes its place: the
thought is left out and the action runs (B2644, Marcel F5296 = B; before, the data's
msg.thought.<action> was shown - those texts stay in content/i18n, unused, PR4).
The rules (word limit, blocked words per language) are prompt settings: data, not code.
"""

from __future__ import annotations

import re
from typing import Any, cast

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


def _words(text: str) -> frozenset[str]:
    return frozenset(w.lower() for w in _WORD.findall(text))


class ThoughtMemory:
    """Recently shown thoughts per inhabitant (Marcel W1696): the same or a very similar sentence
    of the same inhabitant within a time span is not shown again - silence instead of repeating.
    Span (ticks) and similarity (share of shared words) are prompt settings: data, not code.
    Kept in memory only; the core logs what was shown, so replays are not affected."""

    def __init__(self) -> None:
        self._seen: dict[str, list[tuple[int, frozenset[str]]]] = {}
        self._last_tick = -1

    def _follow(self, tick: int) -> None:
        """Time only runs forward within one world: a tick going backwards means a loaded save or a
        new world in the same service session, so nothing said before counts (review GW1 #157)."""
        if tick < self._last_tick:
            self._seen.clear()
        self._last_tick = tick

    def repeated(self, inhabitant: str, tick: int, text: str, settings: dict[str, Any]) -> bool:
        window, similar = _repeat_rules(settings)
        self._follow(tick)
        if window <= 0:
            return False
        words = _words(text)
        recent = [(t, w) for t, w in self._seen.get(inhabitant, []) if 0 <= tick - t <= window]
        self._seen[inhabitant] = recent
        return any(_similarity(words, earlier) >= similar for _, earlier in recent)

    def remember(self, inhabitant: str, tick: int, text: str) -> None:
        self._follow(tick)
        self._seen.setdefault(inhabitant, []).append((tick, _words(text)))


def _repeat_rules(settings: dict[str, Any]) -> tuple[int, float]:
    raw: object = settings.get("thought_check")
    if not isinstance(raw, dict):
        return 0, 1.0
    rules = cast(dict[str, Any], raw)
    return int(rules.get("repeat_window_ticks", 0)), float(rules.get("repeat_similarity", 1.0))


def _similarity(a: frozenset[str], b: frozenset[str]) -> float:
    """Shared words over all words (Jaccard); 1.0 for the same sentence."""
    union = a | b
    return len(a & b) / len(union) if union else 1.0
