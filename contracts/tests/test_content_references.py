"""Cross-checks between data files and the vocabulary (B2-1, Bogen 26 S1, Python side).

The schemas check each file on its own. These tests check what the schemas cannot:
- every vocabulary id has a display text in every language (``vocab.<kind>.<id>``), and no
  display text names an unknown id (same rule as the C# catalog tests, for the AI service);
- ids that data files use for biomes, epochs, weather, day phases and properties exist in the
  vocabulary;
- the tables that *define* something per vocabulary id (biomes, epochs, weather states, day
  phases) cover every id of their kind exactly once.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, cast

import pytest

CONTENT = Path(__file__).resolve().parents[2] / "content"


def _load(relative: str) -> Any:
    return json.loads((CONTENT / relative).read_text(encoding="utf-8"))


def _vocabulary() -> dict[str, list[str]]:
    kinds: dict[str, list[str]] = {}
    for path in sorted((CONTENT / "vocabulary").glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        kinds.setdefault(data["kind"], []).extend(entry["id"] for entry in data["entries"])
    return kinds


VOCABULARY = _vocabulary()
LOCALES = sorted(p.stem for p in (CONTENT / "i18n").glob("*.json"))


def _values(node: Any, path: str) -> list[Any]:
    """Values at a simple path: ``a.b[].c`` (``[]`` = every list element)."""
    current: list[Any] = [node]
    for part in path.split("."):
        each = part.endswith("[]")
        key = part.removesuffix("[]")
        found: list[Any] = []
        for item in current:
            if isinstance(item, dict) and key in item:
                found.append(cast(dict[str, Any], item)[key])
        if each:
            found = [element for item in found for element in cast(list[Any], item)]
        current = found
    return current


# (data file under content/, path to the ids, vocabulary kind)
REFERENCES = [
    ("world/biomes.json", "biomes[].id", "biome"),
    ("world/biomes.json", "fallback_biome", "biome"),
    ("world/biomes.json", "rules[].biome", "biome"),
    ("world/spawns.json", "spawns[].biome", "biome"),
    ("world/spawns.json", "mandatory[].properties_all[]", "property"),
    ("world/spawns.json", "mandatory[].properties_none[]", "property"),
    ("climate/temperate.json", "weather.states[].weather", "weather"),
    ("climate/temperate.json", "weather.seasons[].transitions[].from_weather", "weather"),
    ("climate/temperate.json", "weather.seasons[].transitions[].to[].weather", "weather"),
    ("look/epochs.json", "epochs[].id", "epoch"),
    ("look/daylight.json", "phases[].phase", "time_of_day"),
]

# Tables that define one entry per vocabulary id of their kind.
DEFINITIONS = [
    ("world/biomes.json", "biomes[].id", "biome"),
    ("look/epochs.json", "epochs[].id", "epoch"),
    ("climate/temperate.json", "weather.states[].weather", "weather"),
    ("look/daylight.json", "phases[].phase", "time_of_day"),
]


@pytest.mark.parametrize("locale", LOCALES)
def test_every_vocabulary_id_has_a_display_text(locale: str) -> None:
    keys = {entry["key"] for entry in _load(f"i18n/{locale}.json")["entries"]}
    expected = {f"vocab.{kind}.{i}" for kind, ids in VOCABULARY.items() for i in ids}
    assert sorted(expected - keys) == [], f"{locale}: vocabulary ids without display text"
    stale = {k for k in keys if k.startswith("vocab.")} - expected
    assert sorted(stale) == [], f"{locale}: display texts for unknown vocabulary ids"


@pytest.mark.parametrize(("file", "path", "kind"), REFERENCES)
def test_data_uses_known_vocabulary_ids(file: str, path: str, kind: str) -> None:
    used = _values(_load(file), path)
    assert used, f"{file} {path}: path finds nothing (data layout changed?)"
    unknown = sorted({str(v) for v in used} - set(VOCABULARY[kind]))
    assert unknown == [], f"{file} {path}: not in vocabulary '{kind}'"


@pytest.mark.parametrize(("file", "path", "kind"), DEFINITIONS)
def test_definition_tables_cover_their_vocabulary_once(file: str, path: str, kind: str) -> None:
    defined = [str(v) for v in _values(_load(file), path)]
    duplicates = sorted({v for v in defined if defined.count(v) > 1})
    assert duplicates == [], f"{file}: defined twice"
    missing = sorted(set(VOCABULARY[kind]) - set(defined))
    assert missing == [], f"{file}: no entry for these '{kind}' ids"
