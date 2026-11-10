"""Talk only about what is there (Marcel W1737, D675): the inhabitant's answer to the player may
name only things it sees, knows or is doing, and the world's general words. A sentence that names
something else ("Ich lese ein Buch") is replaced by a stand-in about what it really does.

The check is deterministic and works where nouns can be told apart: in German they start with a
capital letter. Words at the start of a sentence are not judged (they are capitalised anyway). For
English the prompt rule alone applies; the test set measures both. All word lists are prompt
settings (data), the world's names come from the content texts.
"""

from __future__ import annotations

import json
import re
from collections.abc import Iterable
from pathlib import Path
from typing import Any, cast

from glassworld_ai.texts import TextCatalog
from glassworld_ai.words import plain_id
from glassworld_contracts.models.conversation_request_schema import ConversationRequest

# Vocabulary kinds whose display texts are general words of the world (not things to invent).
GENERAL_KINDS = ("need", "mood", "weather", "time_of_day", "biome", "season")
_WORD = re.compile(r"[^\W\d_]+(?:-[^\W\d_]+)*")
_SENTENCE_END = re.compile(r"[.!?…:]+[\"'»«)]*\s*$")


def _stem(word: str) -> str:
    """Lower case without common endings, so 'Blaubeeren' meets 'Blaubeere' and 'Beere'."""
    low = word.lower()
    for ending in ("en", "n", "e", "s"):
        if len(low) > 4 and low.endswith(ending):
            return low[: -len(ending)]
    return low


def known_words(
    request: ConversationRequest,
    texts: TextCatalog | None,
    locale: str,
    names: Iterable[str],
    general: Iterable[str],
) -> set[str]:
    """Stems of everything the inhabitant may name: what it sees, knows and does (their names in
    the player's language), the world's general words, the inhabitants' names and the settings'
    general words."""
    speaker = request.speaker
    types: set[str] = {plain_id(t) for t in speaker.sees or []}
    for entry in speaker.knowledge or []:
        types.add(plain_id(entry.subject.id))
    doing = speaker.doing
    if doing is not None:
        for target in (doing.target, doing.planned.target if doing.planned else None):
            if target is not None:
                types.add(plain_id(target.id))
    words: set[str] = set()
    for type_id in types:
        name = texts.thing(locale, type_id) if texts is not None else None
        words.update(_stem(w) for w in _WORD.findall(name or type_id.replace("_", " ")))
    if texts is not None:
        for key, text in texts.texts.get(locale, {}).items():
            if key.startswith(tuple(f"vocab.{kind}." for kind in GENERAL_KINDS)):
                words.update(_stem(w) for w in _WORD.findall(text))
    words.update(_stem(n) for n in names)
    words.update(_stem(g) for g in general)
    return words


def unknown_nouns(text: str, words: set[str]) -> list[str]:
    """Capitalised words inside a sentence that name nothing the inhabitant may name."""
    found: list[str] = []
    start = True
    for match in re.finditer(r"\S+", text):
        token = match.group(0)
        for word in _WORD.findall(token):
            if not start and word[:1].isupper() and not _known(word, words):
                found.append(word)
            start = False
        if _SENTENCE_END.search(token):
            start = True
    return found


def _known(word: str, words: set[str]) -> bool:
    stem = _stem(word)
    return any(stem == w or (len(w) >= 4 and (w in stem or stem in w)) for w in words)


def settings_words(settings: dict[str, Any], key: str, locale: str) -> list[str]:
    raw: object = settings.get("grounding", {})
    table = cast(dict[str, Any], raw) if isinstance(raw, dict) else {}
    lists: object = table.get(key, {})
    if not isinstance(lists, dict):
        return []
    return [str(w) for w in cast(dict[str, list[Any]], lists).get(locale, [])]


def checks_nouns(settings: dict[str, Any], locale: str) -> bool:
    raw: object = settings.get("grounding", {})
    table = cast(dict[str, Any], raw) if isinstance(raw, dict) else {}
    return locale in [str(x) for x in table.get("noun_locales", [])]


def load_names(content_dir: Path) -> tuple[str, ...]:
    """The inhabitants' own names (content/inhabitants/names.json, W1698); empty without it."""
    path = content_dir / "inhabitants" / "names.json"
    if not path.is_file():
        return ()
    data = json.loads(path.read_text(encoding="utf-8"))
    return tuple(str(n) for n in data.get("names", []))
