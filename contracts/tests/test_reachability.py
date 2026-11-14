"""Reachability of the content data (A1464, D643).

The graph of ``glassworld_contracts.reachability`` runs over every world preset. The test fails
when something cannot be reached that ``content/world/reachability.json`` does not list as a
known gap, and when a listed gap is none any more. So a new rule without its raw material, a new
property nothing can have or a new object type that occurs nowhere is noticed at once, and the
list of known gaps always shows the real state.
"""

from __future__ import annotations

import json
import shutil
from pathlib import Path
from typing import Any

import pytest

from glassworld_contracts.reachability import Sources, analyse, known_gaps, presets, sources

CONTENT = Path(__file__).resolve().parents[2] / "content"
PRESETS = presets(CONTENT)


def test_there_is_a_preset_to_check() -> None:
    assert PRESETS, "no world preset under content/world/presets"


@pytest.mark.parametrize("preset", PRESETS, ids=lambda p: p.stem)
def test_every_gap_is_a_known_one(preset: Path) -> None:
    reach = analyse(CONTENT, sources(CONTENT, preset))
    new = sorted(set(reach.gaps) - known_gaps(CONTENT).keys())
    assert not new, (
        f"Not reachable on '{reach.preset}': {', '.join(new)}. Add what is missing to the data "
        "(a raw material, a rule, an occurrence), or list the gap with its reason in "
        "content/world/reachability.json if it is accepted for now."
    )


@pytest.mark.parametrize("preset", PRESETS, ids=lambda p: p.stem)
def test_every_known_gap_is_still_a_gap(preset: Path) -> None:
    reach = analyse(CONTENT, sources(CONTENT, preset))
    closed = sorted(known_gaps(CONTENT).keys() - set(reach.gaps))
    assert not closed, (
        f"Reachable now on '{reach.preset}': {', '.join(closed)}. "
        "Remove these entries from known_gaps in content/world/reachability.json."
    )


def test_a_known_gap_is_listed_once() -> None:
    entries = json.loads((CONTENT / "world/reachability.json").read_text("utf-8"))["known_gaps"]
    gaps = [entry["gap"] for entry in entries]
    assert len(gaps) == len(set(gaps))


def test_fire_is_reachable_from_what_the_island_offers() -> None:
    # D333: flint and pyrite give sparks, sparks and dry grass give fire.
    reach = analyse(CONTENT, sources(CONTENT, PRESETS[0]))
    assert {"spark", "fire"} <= reach.types
    assert {"spark_from_pyrite_and_flint", "fire_from_spark_and_dry_grass"} <= reach.combinations


def test_the_basic_goals_of_the_start_island_are_met() -> None:
    reach = analyse(CONTENT, sources(CONTENT, PRESETS[0]))
    assert {"food", "water", "shelter", "cooling", "warmth"} <= reach.goals


# --- the check itself notices what it should (on a changed copy of the data) ---


def _copy(tmp_path: Path) -> Path:
    """The data files the check reads, as a copy that a test may change."""
    target = tmp_path / "content"
    for folder in ("objects", "rules", "processes", "vocabulary", "invention", "world"):
        shutil.copytree(CONTENT / folder, target / folder)
    return target


def _edit(file: Path, change: Any) -> None:
    data = json.loads(file.read_text("utf-8"))
    change(data)
    file.write_text(json.dumps(data), encoding="utf-8")


def _gaps(content: Path) -> set[str]:
    return set(analyse(content, sources(content, presets(content)[0])).gaps)


def test_a_type_that_occurs_nowhere_is_a_gap(tmp_path: Path) -> None:
    content = _copy(tmp_path)

    def add_type(data: dict[str, Any]) -> None:
        data["types"].append({"id": "moon_rock", "description": "x", "properties": ["stone"]})

    _edit(content / "objects/start-catalog.json", add_type)
    assert "type:moon_rock" in _gaps(content)


def test_a_rule_without_its_raw_material_is_a_gap(tmp_path: Path) -> None:
    content = _copy(tmp_path)

    def add_rule(data: dict[str, Any]) -> None:
        data["rules"].append(
            {
                "id": "glue_two_transparent_things",
                "action": "combine",
                "inputs": [{"properties": ["transparent"]}, {"properties": ["sticky"]}],
                "creates": [],
                "consumes": [],
            }
        )

    _edit(content / "rules/combinations.json", add_rule)
    assert "combination:glue_two_transparent_things" in _gaps(content)


def test_without_the_spark_maker_fire_is_out_of_reach(tmp_path: Path) -> None:
    content = _copy(tmp_path)

    def drop_pyrite(data: dict[str, Any]) -> None:
        data["spawns"] = [s for s in data["spawns"] if s["object_type"] != "pyrite"]

    _edit(content / "world/spawns.json", drop_pyrite)
    gaps = _gaps(content)
    assert {"type:pyrite", "type:spark", "type:fire"} <= gaps
    assert "combination:fire_from_spark_and_dry_grass" in gaps
    # Warmth no longer hangs on fire alone: insulating found things (wool, feather; K4) keep warm.
    assert "goal:warmth" not in gaps


def test_a_heat_source_opens_the_rules_that_need_heat() -> None:
    # Once the data link things to heat levels, the rules behind them open: the hook for it.
    start = sources(CONTENT, PRESETS[0])
    with_heat = analyse(CONTENT, Sources(start.preset, start.types, frozenset({"flame"})))
    assert {"roast_food", "burn_to_ash", "heat_water_container"} <= with_heat.processes
    # A kiln is hotter than an open fire, and cooking needs the hard vessel only a kiln gives.
    assert {"fire_shaped_clay", "cook_food"}.isdisjoint(with_heat.processes)
    assert {"heat:warm", "heat:glowing", "heat:flame"}.isdisjoint(with_heat.gaps)
    assert "heat:strong_heat" in with_heat.gaps


def test_a_tech_level_needs_the_levels_before_it(tmp_path: Path) -> None:
    content = _copy(tmp_path)

    def strip_tool_rules(data: dict[str, Any]) -> None:
        for rule in data["rules"]:
            if rule["tech_level"] == "shaping":
                rule["inputs"][0]["properties_all"] = ["transparent"]

    _edit(content / "processes/rules.json", strip_tool_rules)
    gaps = _gaps(content)
    assert {"tech:shaping", "tech:building", "tech:processing", "tech:smelting"} <= gaps
