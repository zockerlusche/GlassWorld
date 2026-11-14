"""Base functions name only what a thing does, never a thing (B2741, TL B2752).

No invention exists beforehand: a base function's description and its display texts (DE/EN) must
not name a kind of thing. The check runs against every object type of the start catalog (id and
display names) - there is deliberately no list of invention names here, a fixture with such names
would itself be one (B2741). The snapshot shows every change of these texts in the review; update
it with ``GW_UPDATE_SNAPSHOTS=1``.
"""

from __future__ import annotations

import json
import os
import re
from pathlib import Path
from typing import Any

CONTENT = Path(__file__).resolve().parents[2] / "content"
SNAPSHOT = Path(__file__).resolve().parent / "snapshots" / "base_functions.txt"

# A function may name the world thing it works with (not what it is made of): heat_source holds a
# fire (reviewed in #403). Every exception is listed here with its reason.
WORKS_WITH: dict[str, set[str]] = {"heat_source": {"fire"}}


def _load(relative: str) -> Any:
    return json.loads((CONTENT / relative).read_text(encoding="utf-8"))


def _texts() -> dict[str, str]:
    texts: dict[str, str] = {}
    for path in sorted((CONTENT / "i18n").glob("*.json")):
        for entry in json.loads(path.read_text(encoding="utf-8"))["entries"]:
            texts[f"{path.stem}:{entry['key']}"] = entry["text"]
    return texts


def _locales() -> list[str]:
    return sorted(p.stem for p in (CONTENT / "i18n").glob("*.json"))


def _function_texts() -> list[tuple[str, str]]:
    texts = _texts()
    found: list[tuple[str, str]] = []
    for function in _load("invention/base_functions.json")["functions"]:
        found.append((function["id"], function["description"]))
        for locale in _locales():
            found.append((function["id"], texts[f"{locale}:vocab.base_function.{function['id']}"]))
    return found


def _thing_words() -> set[str]:
    """Every object type of the start catalog: its id, the id's parts and its display names."""
    texts = _texts()
    words: set[str] = set()
    for kind in _load("objects/start-catalog.json")["types"]:
        words.add(kind["id"].lower())
        words.update(part for part in kind["id"].lower().split("_") if len(part) >= 3)
        for locale in _locales():
            if (name := texts.get(f"{locale}:vocab.object.{kind['id']}")) is not None:
                words.add(name.lower())
    return words


def test_base_function_texts_name_no_thing() -> None:
    things = _thing_words()
    named = [
        (function, text, thing)
        for function, text in _function_texts()
        for thing in sorted(things)
        if thing not in WORKS_WITH.get(function, set())
        and re.search(rf"(?<!\w){re.escape(thing)}(?!\w)", text.lower())
    ]
    assert not named, f"base function texts name a thing (B2741): {named}"


def test_base_function_texts_snapshot() -> None:
    lines = [f"{function} | {text}" for function, text in _function_texts()]
    current = "\n".join(lines) + "\n"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        SNAPSHOT.parent.mkdir(parents=True, exist_ok=True)
        SNAPSHOT.write_bytes(current.encode("utf-8"))
    assert SNAPSHOT.exists(), f"missing snapshot {SNAPSHOT.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert SNAPSHOT.read_bytes().decode("utf-8") == current, (
        "base function texts changed - check them against B2741 (function only), "
        "then update with GW_UPDATE_SNAPSHOTS=1"
    )
