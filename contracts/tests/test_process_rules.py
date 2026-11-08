"""Basic processing table (B2537, A1449): TL's example chains work by properties alone.

A tiny matcher (properties_all / properties_none, first matching rule per process) plays the
chains through the data; it guards the table against edits that break a chain. The core applies
the rules (P3, GW1); this is only a data check.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

CONTENT = Path(__file__).resolve().parents[2] / "content"
_DATA: dict[str, Any] = json.loads((CONTENT / "processes/rules.json").read_text("utf-8"))


def specificity(rule: dict[str, Any]) -> tuple[int, int, int, int, str]:
    """Tie-break of the schema (and of the core, P3): the most specific matching rule wins."""
    required = sum(len(c["properties_all"]) for c in rule["inputs"])
    tool = rule["tool"] is not None
    heat = rule["heat_min"] is not None
    return (-len(rule["inputs"]), -required, -tool, -heat, rule["id"])


RULES: list[dict[str, Any]] = sorted(_DATA["rules"], key=specificity)
HEAT = json.loads((CONTENT / "processes/rules.json").read_text("utf-8"))["heat_levels"]
CATALOG = {
    t["id"]: set(t["properties"])
    for t in json.loads((CONTENT / "objects/start-catalog.json").read_text("utf-8"))["types"]
}


def _fits(props: set[str], cond: dict[str, Any] | None) -> bool:
    if cond is None:
        return True
    return set(cond["properties_all"]) <= props and not (set(cond["properties_none"]) & props)


def apply(
    process: str, inputs: list[set[str]], tool: set[str] | None = None, heat: str | None = None
) -> set[str]:
    """Properties of the first input after the most specific matching rule of `process`."""
    for rule in RULES:
        if rule["process"] != process or len(rule["inputs"]) != len(inputs):
            continue
        if not all(_fits(p, c) for p, c in zip(inputs, rule["inputs"], strict=True)):
            continue
        if rule["tool"] is not None and (tool is None or not _fits(tool, rule["tool"])):
            continue
        need = rule["heat_min"]
        if need is not None and (heat is None or HEAT.index(heat) < HEAT.index(need)):
            continue
        out = set(inputs[0]) | set(rule["result"]["add_properties"])
        return out - set(rule["result"]["remove_properties"])
    raise AssertionError(f"no {process} rule for {inputs}")


def test_rule_ids_are_unique() -> None:
    ids = [r["id"] for r in RULES]
    assert len(ids) == len(set(ids))


def test_blade_and_axe() -> None:
    blade = apply("split", [CATALOG["slate"]], tool=CATALOG["fieldstone"])
    assert "sharp" in blade
    axe = apply("bind", [blade, CATALOG["dry_wood"], CATALOG["flax"]])
    assert {"sharp", "hard", "portable"} <= axe


def test_clay_vessel() -> None:
    shaped = apply("shape", [CATALOG["clay"]])
    vessel = apply("fire", [shaped], heat="kiln")
    assert {"container", "hard"} <= vessel and "malleable" not in vessel
    assert "hard" not in apply("fire", [shaped], heat="kiln") - vessel  # same result twice


def test_charcoal_ash_and_glass() -> None:
    charcoal = apply("char", [CATALOG["dry_wood"]], heat="embers")
    assert "smoulders" in charcoal
    ash = apply("fire", [CATALOG["dry_grass"]], heat="open_fire")
    assert {"fine_grained", "alkaline"} <= ash and "burnable" not in ash
    sand = {"fine_grained", "hard", "portable"}  # beach sand (catalog later, B2)
    glass = apply("melt", [sand, ash], heat="furnace")
    assert "transparent" in glass


def test_food_chains() -> None:
    flour = apply("grind", [CATALOG["wild_grain"]], tool=CATALOG["fieldstone"])
    dough = apply("mix", [flour, CATALOG["spring_water"]])
    bread = apply("heat", [dough], heat="embers")
    assert {"edible", "cooked"} <= bread and "malleable" not in bread
    assert "cooked" in apply("heat", [CATALOG["hazelnut"]], heat="embers")  # roasting
    assert "long_lasting" in apply("dry", [CATALOG["apple"]])  # dried fruit
    nuts = CATALOG["hazelnut"] | {"fatty"}  # oily nuts (catalog entry by GW1, B2542)
    nut_flour = apply("grind", [nuts], tool=CATALOG["fieldstone"])
    milk = apply("mix", [nut_flour, CATALOG["spring_water"]])
    assert "milky" in milk
    assert "fatty" in apply("strike", [milk])  # butter
    assert "long_lasting" in apply("heat", [milk], heat="warm")  # cheese


def test_dough_is_not_a_clay_pot() -> None:
    flour = apply("grind", [CATALOG["wild_grain"]], tool=CATALOG["fieldstone"])
    dough = apply("mix", [flour, CATALOG["spring_water"]])
    try:
        apply("shape", [dough])
    except AssertionError:
        return
    raise AssertionError("dough must not be shaped into a fired vessel")


def test_the_most_specific_rule_wins() -> None:
    """Review GW1 #100: heating dry food in its own shell fits 'heat_water_container' (1 required
    property) and 'roast_food' (2): roasting is more specific and wins, in the test as in P3."""
    result = apply("heat", [{"edible", "long_lasting", "container"}], heat="open_fire")
    assert "cooked" in result and "hot" not in result
