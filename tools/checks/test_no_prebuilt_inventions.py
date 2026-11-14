"""B2741 (PR2 sharpened): what the inhabitants could invent never exists in advance.

Names of possible inventions (data: possible_inventions.json) must not appear in content/, game/ or
the active prompts - no type, recipe, name, animation, sound or prompt example. A known place is
listed under "allowed" with owner, status (ok = not an invention, open = to be fixed) and reason;
anything else fails, and so does an allowed entry that no longer matches (keep the list honest).
"""

from __future__ import annotations

import json
import re
import tomllib
from collections.abc import Iterator
from pathlib import Path
from typing import Any, cast

ROOT = Path(__file__).resolve().parents[2]
DATA: dict[str, Any] = json.loads(
    (Path(__file__).parent / "possible_inventions.json").read_text("utf-8")
)
SKIP_DIRS = {".godot", "bin", "obj", ".import"}
GAME_TEXT = {".tscn", ".tres", ".json", ".cs"}
CS_STRING = re.compile(r'"(?:[^"\\\n]|\\.)*"')
PROMPT_META = {"name", "version", "description"}


def _pattern(words: list[str], ignore_case: bool, suffix: str) -> re.Pattern[str]:
    alternatives = "|".join(re.escape(w) for w in sorted(words, key=len, reverse=True))
    flags = re.IGNORECASE if ignore_case else 0
    return re.compile(
        rf"(?<![A-Za-zÄÖÜäöüß])({alternatives})(?:{suffix})?(?![A-Za-zÄÖÜäöüß])", flags
    )


EN = _pattern(DATA["words"]["en"], ignore_case=True, suffix="s|es")
DE = _pattern(DATA["words"]["de"], ignore_case=False, suffix="e|en|n|s|er|es")


def words_in(text: str) -> set[str]:
    """The listed names in a text, lower case. Ids count too: climb_ladder, fire-bowl."""
    plain = text.replace("_", " ").replace("-", " ")
    return {m.group(1).lower() for p in (EN, DE) for m in p.finditer(plain)}


def _active_prompts() -> list[Path]:
    config = tomllib.loads((ROOT / "ai-service/config.toml").read_text("utf-8"))
    return sorted(
        ROOT / "prompts" / f"{value}.v{config[f'{key}_version']}.toml"
        for key, value in config.items()
        if isinstance(value, str) and f"{key}_version" in config
    )


def _strings(node: Any, top: bool = True) -> Iterator[str]:
    if isinstance(node, str):
        yield node
    elif isinstance(node, dict):
        table = cast(dict[str, Any], node)
        for key, value in table.items():
            if not (top and key in PROMPT_META):  # metadata, never rendered
                yield from _strings(value, top=False)
    elif isinstance(node, list):
        for item in cast(list[Any], node):
            yield from _strings(item, top=False)


def _files(top: str) -> Iterator[Path]:
    for path in sorted((ROOT / top).rglob("*")):
        if path.is_file() and not SKIP_DIRS.intersection(path.relative_to(ROOT).parts):
            yield path


def findings() -> set[tuple[str, str]]:
    """(path, word) for every place a possible invention is named."""
    found: set[tuple[str, str]] = set()

    def add(path: Path, words: set[str]) -> None:
        found.update((path.relative_to(ROOT).as_posix(), w) for w in words)

    for path in _files("content"):
        add(path, words_in(path.stem))
        if path.suffix == ".json":
            add(path, words_in(path.read_text("utf-8")))
    for path in _files("game"):
        add(path, words_in(path.stem))
        if path.suffix not in GAME_TEXT:
            continue
        text = path.read_text("utf-8", errors="replace")
        if path.suffix == ".cs":  # code identifiers are no names; what the code writes is
            text = "\n".join(CS_STRING.findall(text))
        add(path, words_in(text))
    for path in _active_prompts():
        add(path, words_in("\n".join(_strings(tomllib.loads(path.read_text("utf-8"))))))
    return found


def _allowed() -> set[tuple[str, str]]:
    return {(a["path"], a["word"].lower()) for a in DATA["allowed"]}


def test_the_names_match_as_meant() -> None:
    assert words_in("climb_ladder, two boats, a fire-bowl") == {"ladder", "boat", "bowl"}
    assert words_in("Ein Floß und zwei Hütten") == {"floß", "hütte"}
    # German nouns only capitalised; no part of a longer word.
    assert words_in("a herd of goats, eine Nussschale, honeycomb, bridges") == {"bridge"}


def test_every_allowed_entry_has_owner_status_and_reason() -> None:
    for entry in DATA["allowed"]:
        assert entry["status"] in ("ok", "open"), entry
        assert entry["owner"] and entry["reason"], entry
        assert (ROOT / entry["path"]).exists(), entry


def test_no_possible_invention_exists_in_advance() -> None:
    new = sorted(findings() - _allowed())
    assert new == [], (
        "B2741: names of possible inventions in content/, game/ or an active prompt - "
        f"remove them or list them in possible_inventions.json with a reason: {new}"
    )


def test_the_allowed_list_has_no_stale_entries() -> None:
    stale = sorted(_allowed() - findings())
    assert stale == [], f"allowed entries that no longer match - remove them: {stale}"
