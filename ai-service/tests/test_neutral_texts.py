"""D1025 (TL TH142): the texts the models read must not steer them to our own results.

In L1591 the judge's new world rule repeated one of its own prompt examples almost word for word
("Striking a hard stone with another hard stone gives it a sharp edge"). These checks keep the
judge examples and the active prompt templates free of the world's things, and the examples of
tries that work free of any property a try may gain - so an example can never be copied into a rule.
"""

from __future__ import annotations

import json
import re
import tomllib
from pathlib import Path

import pytest

from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import humanize

REPO = Path(__file__).resolve().parents[2]
CONTENT = REPO / "content"
# Words of the start world that are no type id of their own: the heads of compound names
# (fieldstone, sandstone, driftwood, hazelnut) and what the data rules make from them. Not 'glass':
# in the prompts it is the pane between the world and the player, no thing in the world.
MATERIAL_WORDS = {"stone", "sand", "nut", "fibre", "cord", "rope", "charcoal", "ash"}
# Words of type names that describe, not name a thing (and 'dry' is a process).
NOT_A_THING = {"dry", "red", "blue", "brown", "wild", "healing"}


def _forms(word: str) -> set[str]:
    forms = {word, word + "s", word + "es"}
    if word.endswith("y"):
        forms.add(word[:-1] + "ies")
    return forms


def world_words() -> set[str]:
    """Every word that names a thing of the start world: type ids, their EN and DE names, the
    single words of those names, and the material words above - with simple plural forms."""
    catalog = json.loads((CONTENT / "objects/start-catalog.json").read_text("utf-8"))
    names: dict[str, str] = {}
    for locale in ("en", "de"):
        entries = json.loads((CONTENT / f"i18n/{locale}.json").read_text("utf-8"))["entries"]
        for entry in entries:
            if entry["key"].startswith("vocab.object."):
                names[f"{locale}/{entry['key']}"] = entry["text"]
    words = set(MATERIAL_WORDS)
    for kind in catalog["types"]:
        type_id = kind["id"]
        words.add(humanize(type_id).lower())
        words |= set(type_id.split("_"))
        for locale in ("en", "de"):
            name = names.get(f"{locale}/vocab.object.{type_id}", "").lower()
            words.add(name)
            words |= set(name.split())
    words -= NOT_A_THING | {""}
    return {form for word in words for form in _forms(word)}


def _pattern(words: set[str]) -> re.Pattern[str]:
    ordered = sorted(map(re.escape, words), key=len, reverse=True)
    return re.compile(r"(?<![\w-])(" + "|".join(ordered) + r")(?![\w-])", re.IGNORECASE)


WORLD = _pattern(world_words())


def _examples() -> dict[str, list[str]]:
    rules = json.loads((CONTENT / "processes/rules.json").read_text("utf-8"))
    return rules["physics"]["examples"]


def test_the_world_words_know_the_things_of_the_old_examples() -> None:
    """The check would have caught L1591's leak: the old examples name stone, wood, grass, water."""
    old = [
        "Heating water in a vessel over a fire makes it hot.",
        "Drying cut grass makes it burnable.",
        "Striking a hard stone with another hard stone gives it a sharp edge.",
        "Wood never melts (it burns or chars).",
        "Stones cannot be braided (only long fibres can).",
    ]
    assert all(WORLD.search(line) for line in old)
    assert not WORLD.search("Leaving wet laundry in the wind makes it dry.")


@pytest.mark.parametrize("kind", ["works", "does_not"])
def test_the_judge_examples_name_no_thing_of_the_world(kind: str) -> None:
    examples = _examples()[kind]
    assert examples, f"physics.examples.{kind} is empty"
    found = {line: sorted({m.group(1).lower() for m in WORLD.finditer(line)}) for line in examples}
    assert not {k: v for k, v in found.items() if v}


def test_the_examples_that_work_name_no_property_a_try_may_gain() -> None:
    """A working example that gains a property of the vocabulary is a rule in disguise: the
    model copies it ('... gives it a sharp edge' became strike -> sharp in L1591)."""
    vocab = Vocabulary.load(CONTENT / "vocabulary")
    words = {humanize(p).lower() for p in vocab.ids("property")} | set(vocab.ids("property"))
    # A property word at the start of a word: 'sharp' also finds 'sharpened'.
    gain = re.compile(
        r"(?<![\w-])(" + "|".join(sorted(map(re.escape, words), key=len, reverse=True)) + r")",
        re.IGNORECASE,
    )
    found = {line: [m.group(1) for m in gain.finditer(line)] for line in _examples()["works"]}
    assert not {k: v for k, v in found.items() if v}


def _active_templates() -> list[tuple[str, dict[str, object]]]:
    config = tomllib.loads((REPO / "ai-service/config.toml").read_text("utf-8"))
    active: list[tuple[str, dict[str, object]]] = []
    for key, name in config.items():
        version = config.get(f"{key}_version")
        if not key.endswith("_prompt") or not isinstance(name, str) or version is None:
            continue
        path = REPO / "prompts" / f"{name}.v{version}.toml"
        active.append((path.relative_to(REPO).as_posix(), tomllib.loads(path.read_text("utf-8"))))
    return active


def test_every_active_prompt_is_checked() -> None:
    names = {name for name, _ in _active_templates()}
    assert "prompts/process/judge.v4.toml" in names
    assert len(names) >= 10


def test_the_active_prompts_name_no_thing_of_the_world() -> None:
    """Only the slots ($inputs, $seen, ...) may bring the world's things into a prompt."""
    found: dict[str, list[str]] = {}
    for name, template in _active_templates():
        for part in ("system", "user"):
            hits = sorted({m.group(1).lower() for m in WORLD.finditer(str(template.get(part, "")))})
            if hits:
                found[f"{name} {part}"] = hits
    assert not found
