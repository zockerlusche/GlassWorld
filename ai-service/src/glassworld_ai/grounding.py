"""Talk only about what is there (Marcel W1737, D675): the inhabitant's answer to the player may
name only things it sees, knows or is doing, and the world's general words. A sentence that names
something else ("Ich lese ein Buch") is not said: the reply is left out (B2644, Marcel
F5296 = B); no stand-in sentence takes its place.

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
from glassworld_ai.words import own_names, plain_id
from glassworld_contracts.models.conversation_request_schema import ConversationRequest

# Vocabulary kinds whose display texts are general words of the world (not things to invent).
GENERAL_KINDS = ("need", "mood", "weather", "time_of_day", "biome", "season")
_WORD = re.compile(r"[^\W\d_]+(?:-[^\W\d_]+)*")
_SENTENCE_END = re.compile(r"[.!?…:]+[\"'»«)]*\s*$")


_UMLAUTS = str.maketrans({"ä": "a", "ö": "o", "ü": "u", "ß": "ss"})


def stem(word: str) -> str:
    """Lower case without umlauts and common endings, so 'Blaubeeren' meets 'Blaubeere' and 'Beere',
    'Nüsse' meets 'Haselnuss' and 'Heilkräuter' meets 'Heilkraut' (night
    measurements A1735/A1582: inflected forms of known things were taken for unknown ones). Both
    sides of a comparison go through here, so folding the same way keeps them equal. At least four
    letters stay ('Gras' and 'Gräser' both 'gras')."""
    low = word.lower().translate(_UMLAUTS)
    for endings in (("en", "er", "em", "es", "n", "e", "s"), ("en", "er", "em", "n", "e")):
        # The second round takes no genitive -s: 'Feuers' meets 'Feuer', and
        # 'Feldstein-Vase' keeps its 's' like 'Vasen'.
        for ending in endings:
            double_s = ending == "s" and low.endswith("ss")
            if len(low) - len(ending) >= 4 and low.endswith(ending) and not double_s:
                low = low[: -len(ending)]
                break
        else:
            break
    return low


def spoken_names(request: ConversationRequest, locale: str) -> dict[str, str]:
    """Type id -> the speaker's own name for an invented kind of thing, from every place the
    request labels (#251): beliefs, what it sees, what it does and plans."""
    speaker = request.speaker
    pairs: list[tuple[object, object]] = [
        (entry.subject.id, getattr(entry, "label", None)) for entry in speaker.knowledge or []
    ]
    seen: list[Any] = list(getattr(speaker, "sees_labels", None) or [])
    pairs += [(item.type, item.label) for item in seen]
    doing: Any = speaker.doing
    for step in (doing, doing.planned if doing is not None else None):
        if step is not None and step.target is not None:
            pairs.append((step.target.id, getattr(step, "label", None)))
    return own_names(pairs, locale)


def known_words(
    request: ConversationRequest,
    texts: TextCatalog | None,
    locale: str,
    names: Iterable[str],
    general: Iterable[str],
) -> set[str]:
    """Stems of everything the inhabitant may name: what it sees, knows and does (their names in
    the player's language), the world's general words, the inhabitants' names, the settings'
    general words - and every word that was said in this conversation: asked about a bed, the
    inhabitant may ask back "Bett? Was ist das?" (PR2 check D763 G2)."""
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
        words.update(stem(w) for w in _WORD.findall(name or type_id.replace("_", " ")))
    if texts is not None:
        for key, text in texts.texts.get(locale, {}).items():
            if key.startswith(tuple(f"vocab.{kind}." for kind in GENERAL_KINDS)):
                words.update(stem(w) for w in _WORD.findall(text))
    said = [request.player_text, *(line.text for line in request.recent_lines or [])]
    for text in said:
        words.update(stem(w) for w in _WORD.findall(str(text)))
    for own in spoken_names(request, locale).values():
        words.update(stem(w) for w in _WORD.findall(own))  # its own name for a made thing
    words.update(stem(n) for n in names)
    words.update(stem(g) for g in general)
    return words


def unknown_nouns(
    text: str, words: set[str], heads: bool = False, only: set[str] | None = None
) -> list[str]:
    """Capitalised words inside a sentence that name nothing the inhabitant may name. With
    `heads` a word counts as known only as itself, as a compound containing a known word, or as
    the head of a known compound ('Stein' for 'Feuerstein') - never as its first part ('Feuer'
    for 'Feuerstein', W1937)."""
    found: list[str] = []
    start = True
    for match in re.finditer(r"\S+", text):
        token = match.group(0)
        for word in _WORD.findall(token):
            if (
                not start
                and word[:1].isupper()
                and (only is None or stem(word) in only)
                and not _known(word, words, heads)
            ):
                found.append(word)
            start = False
        if _SENTENCE_END.search(token):
            start = True
    return found


def _known(word: str, words: set[str], heads: bool = False) -> bool:
    own = stem(word)
    if heads:
        return any(
            own == w or (len(w) >= 4 and w in own) or (len(own) >= 4 and w.endswith(own))
            for w in words
        )
    return any(own == w or (len(w) >= 4 and (w in own or own in w)) for w in words)


_NAME_KEYS = frozenset({"name", "called", "own_name", "text", "summary", "label"})


def world_things(texts: TextCatalog | None, locale: str) -> set[str]:
    """Stems of the words in the names of every kind of thing of this world (content texts
    vocab.object.*): 'Rote Beere' gives 'rot' and 'beere'. A thought's noun is judged only when it
    is one of them (A1613): a thing of this world the inhabitant does not know is a leak of
    knowledge; a wish for something the world does not have, or an abstract word, is not."""
    if texts is None:
        return set()
    return {
        stem(w)
        for key, text in texts.texts.get(locale, {}).items()
        if key.startswith("vocab.object.")
        for w in _WORD.findall(text)
        if len(w) >= 3
    }


def request_words(
    data: object, texts: TextCatalog | None, locale: str, general: Iterable[str]
) -> set[str]:
    """Stems of everything a decision request lets the inhabitant name (TL #22567, W1937): every
    kind of thing the request mentions (seen, known, an option's target or topic, a building) by
    its name in the player's language, every name it carries (own names of made things, people,
    itself), the words of its memory, the world's general words (needs, moods, weather ...) and
    the prompt's general words. Walks the request as JSON, so a new field is covered too."""
    words: set[str] = set()

    def add(text: str) -> None:
        words.update(stem(w) for w in _WORD.findall(text))

    def walk(node: object, key: str = "") -> None:
        if isinstance(node, dict):
            table = cast(dict[str, object], node)
            if set(table) >= {"de", "en"} and isinstance(table.get(locale), str):
                add(str(table[locale]))  # a name in both languages
            for k, v in table.items():
                walk(v, k)
        elif isinstance(node, list):
            for v in cast(list[object], node):
                walk(v, key)
        elif isinstance(node, str):
            thing = texts.thing(locale, node) if texts is not None else None
            if thing:
                add(thing)  # a kind of thing by its name
            elif key in _NAME_KEYS:
                add(node)

    walk(data)
    if texts is not None:
        for key, text in texts.texts.get(locale, {}).items():
            if key.startswith(tuple(f"vocab.{kind}." for kind in GENERAL_KINDS)):
                add(text)
    for g in general:
        add(g)
    return words


def judged_things(
    settings: dict[str, Any], texts: TextCatalog | None, locale: str
) -> set[str] | None:
    """Which nouns a sentence check judges (prompt data [settings.grounding] judge): 'world' (the
    default, A1613) only names of things of this world; 'all' (A1797, Marcel W2243 'Kaffee')
    every noun - the inhabitants know nothing from elsewhere, so a noun must name something the
    inhabitant knows, sees or remembers, or be one of the general words. None = every noun."""
    raw: object = settings.get("grounding", {})
    table = cast(dict[str, Any], raw) if isinstance(raw, dict) else {}
    return None if table.get("judge") == "all" else world_things(texts, locale)


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
