"""Reachability check of the content data (A1464, D643).

Builds a graph from the things that can occur on an island through the combination rules and the
processing rules to heat levels, tech levels and basic goals, and names everything that cannot be
reached ("gaps"). It reads data only and decides nothing in the game: it says what is possible,
never how. The test ``contracts/tests/test_reachability.py`` compares the gaps with the accepted
ones in ``content/world/reachability.json``.

What the graph assumes (it is a check of the data, not a simulation):

- A thing is its set of true properties. Quantities, distances and regrowth are not counted:
  what the spawn table, the drink spots or the caves of a preset name can occur.
- A combination rule can fire when every input slot is filled by a reachable thing; it makes its
  ``creates`` types reachable.
- A processing rule can fire when every input and its tool are filled by reachable things and its
  ``heat_min`` is a reachable heat level. Its result is the first input with the added and
  removed properties. The tech level of a rule is not a gate here: a tech level counts as
  reachable when a rule of that level can fire and all levels before it are reachable.
- Heat levels have no source in the data yet (no data says which thing gives which heat), so
  none is reachable; ``Sources.heat_levels`` is the place where that will plug in.

Run ``python -m glassworld_contracts.reachability [content directory]`` for a Markdown report.
"""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, cast

Thing = frozenset[str]

# Upper bound of derived things (property sets); far above what the data can produce today.
MAX_THINGS = 20_000


@dataclass(frozen=True)
class Sources:
    """What an island offers before any rule fires."""

    preset: str
    types: frozenset[str]
    heat_levels: frozenset[str]


@dataclass(frozen=True)
class Reach:
    """Everything reachable for one preset, and the gaps."""

    preset: str
    types: frozenset[str]
    things: frozenset[Thing]
    combinations: frozenset[str]
    processes: frozenset[str]
    properties: frozenset[str]
    heat_levels: frozenset[str]
    tech_levels: tuple[str, ...]
    goals: frozenset[str]
    gaps: tuple[str, ...]


def _load(content: Path, relative: str) -> dict[str, Any]:
    return cast(dict[str, Any], json.loads((content / relative).read_text(encoding="utf-8")))


def _ids(content: Path, vocabulary: str) -> list[str]:
    entries = _load(content, f"vocabulary/{vocabulary}.json")["entries"]
    return [str(entry["id"]) for entry in entries]


def _fits(thing: Thing, condition: dict[str, Any] | None) -> bool:
    if condition is None:
        return True
    return set(condition["properties_all"]) <= thing and not (
        set(condition.get("properties_none", [])) & thing
    )


def sources(content: Path, preset_file: Path) -> Sources:
    """The object types a preset can place: spawn table, drink spots and caves."""
    preset = cast(dict[str, Any], json.loads(preset_file.read_text(encoding="utf-8")))
    types = {str(s["object_type"]) for s in _load(content, "world/spawns.json")["spawns"]}
    types |= {str(d["object_type"]) for d in preset["water"]["drink_spots"]}
    if int(preset["caves"]["max"]) > 0:
        types.add(str(preset["caves"]["object_type"]))
    return Sources(str(preset["id"]), frozenset(types), frozenset())


def analyse(content: Path, start: Sources) -> Reach:
    """The closure of the rules over what the preset offers, and what stays out of reach."""
    catalog = {
        str(t["id"]): frozenset(cast(list[str], t["properties"]))
        for path in sorted((content / "objects").glob("*.json"))
        for t in json.loads(path.read_text(encoding="utf-8"))["types"]
    }
    combinations = [
        cast(dict[str, Any], rule)
        for path in sorted((content / "rules").glob("*.json"))
        for rule in json.loads(path.read_text(encoding="utf-8"))["rules"]
    ]
    process_data = _load(content, "processes/rules.json")
    processes = cast(list[dict[str, Any]], process_data["rules"])
    heat_order = [str(h) for h in process_data["heat_levels"]]
    tech_order = _ids(content, "tech_levels")
    settings = _load(content, "world/reachability.json")

    types = set(start.types & catalog.keys())
    things: set[Thing] = {catalog[t] for t in types}
    fired_combinations: set[str] = set()
    fired_processes: set[str] = set()
    changed = True
    while changed:
        changed = False
        for rule in combinations:
            slots = cast(list[dict[str, Any]], rule["inputs"])
            if all(any(set(slot["properties"]) <= thing for thing in things) for slot in slots):
                fired_combinations.add(str(rule["id"]))
                for created in cast(list[str], rule["creates"]):
                    if created in catalog and created not in types:
                        types.add(created)
                        things.add(catalog[created])
                        changed = True
        for rule in processes:
            heat = rule["heat_min"]
            if heat is not None and not _heat_reached(str(heat), start.heat_levels, heat_order):
                continue
            inputs = cast(list[dict[str, Any]], rule["inputs"])
            if not all(any(_fits(thing, slot) for thing in things) for slot in inputs):
                continue
            if rule["tool"] is not None and not any(_fits(thing, rule["tool"]) for thing in things):
                continue
            fired_processes.add(str(rule["id"]))
            result = cast(dict[str, Any], rule["result"])
            produced = result["produces"]
            if produced is not None and str(produced) in catalog and str(produced) not in types:
                types.add(str(produced))
                things.add(catalog[str(produced)])
                changed = True
            added = set(cast(list[str], result["add_properties"]))
            removed = set(cast(list[str], result["remove_properties"]))
            for thing in [t for t in things if _fits(t, inputs[0])]:
                made = frozenset((thing | added) - removed)
                if made not in things:
                    if len(things) >= MAX_THINGS:
                        raise RuntimeError("The processing rules produce too many things.")
                    things.add(made)
                    changed = True

    properties: set[str] = {p for thing in things for p in thing}
    # Properties an invention carries by the data count as reachable
    # (content/invention/settings.json, implied_properties).
    invention = _load(content, "invention/settings.json")
    properties |= set(cast(list[str], invention["implied_properties"]))

    tech: list[str] = []
    for level in tech_order:
        rules_of_level = [r for r in processes if r["tech_level"] == level]
        if rules_of_level and not any(str(r["id"]) in fired_processes for r in rules_of_level):
            break
        tech.append(level)

    goals = {
        str(goal["id"])
        for goal in cast(list[dict[str, Any]], settings["goals"])
        if any(_fits(thing, cond) for cond in goal["any_of"] for thing in things)
    }

    gaps = [f"type:{t}" for t in catalog if t not in types]
    gaps += [f"combination:{r['id']}" for r in combinations if r["id"] not in fired_combinations]
    gaps += [f"process:{r['id']}" for r in processes if r["id"] not in fired_processes]
    gaps += [f"property:{p}" for p in _ids(content, "properties") if p not in properties]
    gaps += [f"heat:{h}" for h in heat_order if not _heat_reached(h, start.heat_levels, heat_order)]
    gaps += [f"tech:{level}" for level in tech_order if level not in tech]
    gaps += [f"goal:{g['id']}" for g in settings["goals"] if g["id"] not in goals]
    return Reach(
        preset=start.preset,
        types=frozenset(types),
        things=frozenset(things),
        combinations=frozenset(fired_combinations),
        processes=frozenset(fired_processes),
        properties=frozenset(properties),
        heat_levels=frozenset(
            h for h in heat_order if _heat_reached(h, start.heat_levels, heat_order)
        ),
        tech_levels=tuple(tech),
        goals=frozenset(goals),
        gaps=tuple(sorted(gaps)),
    )


def _heat_reached(level: str, reached: frozenset[str], order: list[str]) -> bool:
    """A heat level is there when it or a hotter one is reachable."""
    return any(h in reached and order.index(h) >= order.index(level) for h in order)


def known_gaps(content: Path) -> dict[str, str]:
    """The accepted gaps of content/world/reachability.json with their reasons."""
    entries = cast(list[dict[str, str]], _load(content, "world/reachability.json")["known_gaps"])
    return {entry["gap"]: entry["reason"] for entry in entries}


def presets(content: Path) -> list[Path]:
    """The preset files the check runs over."""
    return sorted((content / "world" / "presets").glob("*.json"))


def report(content: Path) -> str:
    """A Markdown report for people: what is reachable and which gaps are open."""
    known = known_gaps(content)
    lines = ["# Reachability of the content data", ""]
    for preset_file in presets(content):
        reach = analyse(content, sources(content, preset_file))
        lines += [
            f"## Preset `{reach.preset}`",
            "",
            f"- Object types reachable: {len(reach.types)}",
            f"- Different things (property sets) reachable: {len(reach.things)}",
            f"- Combination rules that can fire: {', '.join(sorted(reach.combinations)) or 'none'}",
            f"- Processing rules that can fire: {len(reach.processes)}",
            f"- Heat levels: {', '.join(sorted(reach.heat_levels)) or 'none'}",
            f"- Tech levels: {', '.join(reach.tech_levels) or 'none'}",
            f"- Goals met: {', '.join(sorted(reach.goals)) or 'none'}",
            "",
            "| Gap | Accepted | Reason |",
            "| --- | --- | --- |",
        ]
        lines += [
            f"| `{gap}` | {'yes' if gap in known else 'NO'} | {known.get(gap, 'not listed')} |"
            for gap in reach.gaps
        ]
        lines.append("")
    return "\n".join(lines)


if __name__ == "__main__":
    default = Path(__file__).resolve().parents[4] / "content"
    sys.stdout.write(report(Path(sys.argv[1]) if len(sys.argv) > 1 else default) + "\n")
