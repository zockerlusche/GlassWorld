"""Deterministic content check for player and inhabitant texts (B6 R13, B2472, ages 12+).

The same algorithm runs in the core (``Simulation.Core/Safety/TextGuard.cs``) on the same data
(``content/safety/rules.json``); both are tested against ``tests/safety/cases.json``. Subtle cases
are left to the AI rule guard. Algorithm:

1. Fold (platform-independent, no Unicode normalisation library): full-width forms become
   ASCII, a/o/u + combining diaeresis become umlauts, other combining marks and invisible format
   characters (e.g. zero-width space) are removed, control characters become spaces; then lower
   case. Longer than ``max_length`` ->
   blocked (``too_long``).
2. Category patterns (e-mail, phone, links, prompt markers) run on that text.
3. Words are letters/digits runs; ``*`` inside a word matches any one letter. Variants: plain,
   leetspeak-mapped, and both with runs of single-letter words joined ("s e x" -> "sex").
4. Allowed phrases are removed; then every category term (DE and EN) is matched as a whole-word
   sequence. The longest match decides the category (tie: order in the data).
"""

from __future__ import annotations

import json
import re
import unicodedata
from dataclasses import dataclass
from pathlib import Path

from glassworld_contracts.models.safety_rules_schema import SafetyRules


@dataclass(frozen=True)
class Verdict:
    """Result of a check. ``reason`` is ``term``, ``pattern`` or ``too_long`` when blocked."""

    allowed: bool
    category: str | None = None
    reason: str | None = None
    match: str | None = None


ALLOWED = Verdict(allowed=True)
_UMLAUT = {"a": "ä", "o": "ö", "u": "ü", "A": "Ä", "O": "Ö", "U": "Ü"}


def _words(text: str) -> list[str]:
    words: list[str] = []
    current: list[str] = []
    for ch in text:
        if ch.isalnum() or ch == "*":
            current.append(ch)
        elif current:
            words.append("".join(current))
            current = []
    if current:
        words.append("".join(current))
    return words


def _join_single_letters(words: list[str]) -> list[str]:
    result: list[str] = []
    run: list[str] = []
    for word in words:
        if len(word) == 1 and word.isalpha():
            run.append(word)
            continue
        result.extend(["".join(run)] if len(run) > 1 else run)
        run = []
        result.append(word)
    result.extend(["".join(run)] if len(run) > 1 else run)
    return result


def _word_equals(token: str, term_word: str) -> bool:
    if len(token) != len(term_word):
        return False
    return all(t == w or (t == "*" and w.isalpha()) for t, w in zip(token, term_word, strict=True))


def _contains(tokens: list[str], term: list[str]) -> bool:
    for start in range(len(tokens) - len(term) + 1):
        if all(_word_equals(tokens[start + i], term[i]) for i in range(len(term))):
            return True
    return False


def _remove_phrase(tokens: list[str], phrase: list[str]) -> list[str]:
    result: list[str] = []
    i = 0
    while i < len(tokens):
        if tokens[i : i + len(phrase)] == phrase:
            i += len(phrase)
            continue
        result.append(tokens[i])
        i += 1
    return result


class TextGuard:
    """Checks texts against the rules; immutable after construction."""

    def __init__(self, rules: SafetyRules) -> None:
        self._max_length = rules.max_length
        self._leet = {m.source: m.replacement for m in rules.leet}
        self._allowed = [phrase.split() for phrase in rules.allowed_words]
        self._categories = [
            (
                c.id.value,
                [t.split() for t in [*c.terms_de, *c.terms_en]],
                [re.compile(p) for p in c.patterns],
            )
            for c in rules.categories
        ]

    @classmethod
    def from_file(cls, path: Path) -> TextGuard:
        """Loads ``content/safety/rules.json`` (validated by the generated model)."""
        data = json.loads(path.read_text(encoding="utf-8"))
        return cls(SafetyRules.model_validate(data))

    @staticmethod
    def normalize(text: str) -> str:
        """Fold as described in the module docstring, then lower case."""
        result: list[str] = []
        for ch in text:
            code = ord(ch)
            category = unicodedata.category(ch)
            if 0xFF01 <= code <= 0xFF5E:
                result.append(chr(code - 0xFEE0))
            elif code == 0x0308 and result and result[-1] in _UMLAUT:
                result[-1] = _UMLAUT[result[-1]]
            elif category in ("Mn", "Cf"):
                continue
            elif category == "Cc":
                result.append(" ")
            else:
                result.append(ch)
        return "".join(result).lower()

    def check(self, text: str) -> Verdict:
        """Deterministic verdict for one text."""
        normalized = self.normalize(text)
        if len(normalized) > self._max_length:
            return Verdict(allowed=False, reason="too_long")

        best: tuple[int, int, Verdict] | None = None
        plain = _words(normalized)
        leet = _words("".join(self._leet.get(ch, ch) for ch in normalized))
        variants = [plain, leet, _join_single_letters(plain), _join_single_letters(leet)]
        for phrase in self._allowed:
            variants = [_remove_phrase(v, phrase) for v in variants]

        for order, (category, terms, patterns) in enumerate(self._categories):
            for pattern in patterns:
                found = pattern.search(normalized)
                if found and (best is None or (1, -order) > best[:2]):
                    best = (1, -order, Verdict(False, category, "pattern", found.group(0)))
            for term in terms:
                if any(_contains(v, term) for v in variants) and (
                    best is None or (len(term), -order) > best[:2]
                ):
                    best = (len(term), -order, Verdict(False, category, "term", " ".join(term)))
        return ALLOWED if best is None else best[2]
