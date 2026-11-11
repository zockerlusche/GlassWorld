"""Builds ai-service/testsets/b4a/cases.v1.json and cases.v2.json: B4a test situations (Bogen 28
R10, A1403).

Knowledge with its source and trust, passing it on and the first encounter, for the decider:
    uv run python tools/testsets/build_b4a_cases.py
    uv run python -m glassworld_ai.testsets decider --cases ai-service/testsets/b4a/cases.v2.json ...
The output is checked in. Plausible options are the ones a person in that situation might choose;
they never prescribe one answer (frame, not outcome). Situations the decider set v1 already has
(unknown berries when hungry, known poison, shy first sight) are not repeated here.

v2 adds a sharper pair for trust (D690): very hungry, the warned-about berries are the only food
in sight - once warned by a trusted person, once by someone hardly trusted. Both are in the same
language, so the pair differs only in trust; the measure is how often each one eats. v1 stays as
it was.
"""

from __future__ import annotations

import json
import pathlib
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "ai-service" / "testsets" / "b4a" / "cases.v{version}.json"
NEEDS = dict(
    hunger=0.7, thirst=0.7, sleep=0.7, warmth=0.8, safety=0.8, company=0.6, fun=0.6, purpose=0.6
)
TRAITS = dict(courage=0.5, creativity=0.5, curiosity=0.5, diligence=0.5, kindness=0.5,
              orderliness=0.5, patience=0.5, sociability=0.5)  # fmt: skip
ME, OTHER = "inh-000001", "inh-000002"


def belief(kind: str, subject: str, prop: str, value: bool, confidence: float, source: str,
           told_by: str | None = None) -> dict[str, Any]:  # fmt: skip
    return {"subject": {"kind": kind, "id": subject}, "property": prop, "value": value,
            "confidence": confidence, "source": source, "told_by": told_by, "tick": 900}  # fmt: skip


def option(oid: str, action: str, target: str | None = None, topic: dict[str, Any] | None = None):
    data: dict[str, Any] = {"option_id": oid, "action": action, "target": target, "partners": []}
    if topic is not None:
        data["topic"] = topic
    return data


def topic(object_type: str, prop: str, value: bool) -> dict[str, Any]:
    return {"object_type": object_type, "name": None, "property": prop, "value": value}


def case(n: int, cid: str, desc: str, plausible: list[str], options: list[dict[str, Any]],
         objects: list[dict[str, Any]], knowledge: list[dict[str, Any]],
         needs: dict[str, float] | None = None, traits: dict[str, float] | None = None,
         people: list[dict[str, Any]] | None = None,
         relations: list[dict[str, Any]] | None = None,
         locale: str | None = None) -> dict[str, Any]:  # fmt: skip
    request: dict[str, Any] = {
        "contract_version": "0.2",
        "request_id": f"00000000-000b-1403-0000-{n:012d}",
        "tick": 1000 + n,
        "seed": 14030 + n,
        "deadline_ms": 60000,
        "display_locale": locale or ("de" if n % 2 else "en"),
        "inhabitant": {
            "id": ME,
            "needs": [{"id": k, "value": v} for k, v in sorted({**NEEDS, **(needs or {})}.items())],
            "mood": [],
            "traits": [
                {"id": k, "value": v} for k, v in sorted({**TRAITS, **(traits or {})}.items())
            ],
            "will_to_live": 0.7,
            "memory": {"summary": "", "recent": []},
            "knowledge": knowledge,
            "relationships": relations or [],
            "habits": [],
        },
        "environment": {"time_of_day": "day", "temperature_c": 18.0, "weather": ["clear"]},
        "perception": {"objects": objects, "inhabitants": people or []},
        "options": options,
    }
    return {"id": cid, "description": desc, "request": request, "plausible_option_ids": plausible}


def thing(oid: str, kind: str, dist: float, known: list[tuple[str, bool, float]] | None = None):
    return {"id": oid, "type": kind, "distance_m": dist,
            "known_properties": [{"property": p, "value": v, "confidence": c} for p, v, c in known or []]}  # fmt: skip


def relation(trust: float) -> list[dict[str, Any]]:
    return [{"other": OTHER, "affection": 0.5, "trust": trust, "respect": 0.5}]


HUNGRY = {"hunger": 0.15}
RED = thing("obj-0001", "red_berry", 3.0)
APPLE = thing("obj-0002", "apple", 25.0, [("edible", True, 1.0)])
EAT_OPTIONS = [option("o1", "eat", "obj-0001"), option("o2", "move_to", "obj-0002"),
               option("o3", "inspect", "obj-0001"), option("o4", "idle")]  # fmt: skip

CASES = [
    case(
        1,
        "heard_warning_trusted",
        "Hungry; a trusted person warned that the red berries close by are poisonous; a known apple is further away.",
        ["o2"],
        EAT_OPTIONS,
        [
            {
                **RED,
                "known_properties": [{"property": "poisonous", "value": True, "confidence": 0.81}],
            },
            APPLE,
        ],
        [belief("type", "red_berry", "poisonous", True, 0.81, "heard", OTHER)],
        needs=HUNGRY,
        relations=relation(0.9),
    ),
    case(
        2,
        "heard_warning_mistrusted",
        "Hungry; someone it hardly trusts said the red berries are poisonous; a known apple is further away.",
        ["o1", "o2", "o3"],
        EAT_OPTIONS,
        [
            {
                **RED,
                "known_properties": [{"property": "poisonous", "value": True, "confidence": 0.09}],
            },
            APPLE,
        ],
        [belief("type", "red_berry", "poisonous", True, 0.09, "heard", OTHER)],
        needs=HUNGRY,
        relations=relation(0.1),
    ),
    case(
        3,
        "warn_friend_about_poison",
        "Knows from own bad experience that red berries are poisonous; a trusted person is next to red berries.",
        ["o1", "o2"],
        [
            option("o1", "warn", OTHER, topic("red_berry", "poisonous", True)),
            option("o2", "approach", OTHER),
            option("o3", "wander"),
            option("o4", "idle"),
        ],
        [RED],
        [belief("type", "red_berry", "poisonous", True, 1.0, "experienced")],
        traits={"kindness": 0.8},
        people=[{"id": OTHER, "distance_m": 4.0}],
        relations=relation(0.8),
    ),
    case(
        4,
        "show_food_to_hungry_friend",
        "Knows blue berries are edible; a liked person is nearby; blue berries close.",
        ["o1", "o2", "o3"],
        [
            option("o1", "show", OTHER, topic("blue_berry", "edible", True)),
            option("o2", "teach", OTHER, topic("blue_berry", "edible", True)),
            option("o3", "eat", "obj-0003"),
            option("o4", "idle"),
        ],
        [thing("obj-0003", "blue_berry", 2.0, [("edible", True, 1.0)])],
        [belief("type", "blue_berry", "edible", True, 1.0, "experienced")],
        traits={"kindness": 0.8, "sociability": 0.7},
        people=[{"id": OTHER, "distance_m": 5.0}],
        relations=relation(0.7),
    ),
    case(
        5,
        "heard_but_tried_otherwise",
        "Was told blue berries are poisonous, but ate them itself without harm; hungry, blue berries close.",
        ["o1", "o3"],
        [
            option("o1", "eat", "obj-0003"),
            option("o2", "idle"),
            option("o3", "move_to", "obj-0002"),
            option("o4", "wander"),
        ],
        [thing("obj-0003", "blue_berry", 2.0, [("edible", True, 1.0)]), APPLE],
        [belief("type", "blue_berry", "edible", True, 1.0, "experienced")],
        needs=HUNGRY,
    ),
    case(
        6,
        "first_encounter_sociable",
        "Sociable and curious; sees another person for the first time.",
        ["o1", "o2", "o3"],
        [
            option("o1", "approach", OTHER),
            option("o2", "introduce", OTHER),
            option("o3", "talk_to", OTHER),
            option("o4", "idle"),
        ],
        [],
        [],
        traits={"sociability": 0.85, "curiosity": 0.8},
        people=[{"id": OTHER, "distance_m": 12.0}],
    ),
]

STARVING = {"hunger": 0.05}
ONLY_FOOD_OPTIONS = [option("o1", "eat", "obj-0001"), option("o2", "inspect", "obj-0001"),
                     option("o3", "wander"), option("o4", "idle")]  # fmt: skip


def only_food(n: int, cid: str, trust: float, confidence: float, who: str) -> dict[str, Any]:
    eats_plausible = ["o1"] if trust < 0.5 else []
    return case(
        n,
        cid,
        f"Very hungry; {who} said the red berries close by are poisonous; no other food in sight.",
        [*eats_plausible, "o2", "o3"],
        ONLY_FOOD_OPTIONS,
        [
            {
                **RED,
                "known_properties": [
                    {"property": "poisonous", "value": True, "confidence": confidence}
                ],
            }
        ],
        [belief("type", "red_berry", "poisonous", True, confidence, "heard", OTHER)],
        needs=STARVING,
        relations=relation(trust),
        locale="de",
    )


CASES_V2 = [
    *CASES,
    only_food(7, "only_food_warning_trusted", 0.9, 0.81, "a trusted person"),
    only_food(8, "only_food_warning_mistrusted", 0.1, 0.09, "someone it hardly trusts"),
]


def write(version: int, cases: list[dict[str, Any]]) -> None:
    data = {
        "id": "b4a-testset",
        "version": version,
        "purpose": "B4a R10 (A1403): knowledge with source and trust, passing it on, first encounter; "
        "x N runs with the real model, content-valid >= 95 %, plausibility reported.",
        "cases": cases,
    }
    out = pathlib.Path(str(OUT).format(version=version))
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


def main() -> None:
    write(1, CASES)
    write(2, CASES_V2)


if __name__ == "__main__":
    main()
