"""Builds ai-service/testsets/decider/cases.v1.json (20 decider test situations).

Run from the repository root: uv run python tools/testsets/build_decider_cases.py
The output is checked in; rerun after contract changes (e.g. Option.partners).
"""

import json
import pathlib
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "ai-service" / "testsets" / "decider" / "cases.v1.json"
_SCHEMA = json.loads(
    (ROOT / "contracts" / "schemas" / "decision-request.schema.json").read_text("utf-8")
)
HAS_PARTNERS = "partners" in _SCHEMA["$defs"]["Option"]["properties"]

BASE_NEEDS = {
    "hunger": 0.7,
    "thirst": 0.7,
    "sleep": 0.7,
    "warmth": 0.8,
    "safety": 0.8,
    "company": 0.6,
    "fun": 0.6,
    "purpose": 0.6,
}
BASE_TRAITS = {
    "courage": 0.5,
    "curiosity": 0.5,
    "patience": 0.5,
    "sociability": 0.5,
    "diligence": 0.5,
    "creativity": 0.5,
    "orderliness": 0.5,
    "kindness": 0.5,
}


def obj(oid, kind, dist, known=()):
    return {
        "id": oid,
        "type": kind,
        "distance_m": dist,
        "known_properties": [{"property": p, "value": v, "confidence": c} for p, v, c in known],
    }


def case(
    n,
    cid,
    desc,
    locale,
    plausible,
    options,
    objects,
    needs=None,
    moods=None,
    traits=None,
    will=0.7,
    tod="day",
    temp=18.0,
    weather=("clear",),
):
    nd = {**BASE_NEEDS, **(needs or {})}
    tr = {**BASE_TRAITS, **(traits or {})}
    opts = []
    for i, (action, target, *partners) in enumerate(options, start=1):
        option = {"option_id": f"o{i}", "action": action, "target": target}
        if HAS_PARTNERS:
            option["partners"] = sorted(partners[0]) if partners else []
        opts.append(option)
    request = {
        "contract_version": "0.2",
        "request_id": str(uuid.UUID(int=0xA1330_0000_0000_0000_0000 + n)),
        "tick": 1000 + n * 10,
        "seed": 10_000 + n,
        "deadline_ms": 60000,
        "display_locale": locale,
        "inhabitant": {
            "id": "inh-000001",
            "needs": [{"id": k, "value": v} for k, v in sorted(nd.items())],
            "mood": [{"id": k, "value": v} for k, v in sorted((moods or {}).items()) if v > 0],
            "traits": [{"id": k, "value": v} for k, v in sorted(tr.items())],
            "will_to_live": will,
            # contract 0.2: memory, knowledge, relationships and habits come with B4a test sets
            "memory": {"summary": "", "recent": []},
            "knowledge": [],
            "relationships": [],
            "habits": [],
        },
        "environment": {"time_of_day": tod, "temperature_c": temp, "weather": sorted(weather)},
        "perception": {"objects": objects, "inhabitants": []},
        "options": opts,
    }
    plaus = [o["option_id"] for o in opts if o["action"] + ":" + str(o["target"]) in plausible]
    assert plaus, cid
    return {"id": cid, "description": desc, "request": request, "plausible_option_ids": plaus}


EDIBLE = ("edible", True, 1.0)
cases = [
    case(
        1,
        "hunger_unknown_berries_known_apple",
        "Very hungry; unknown berries close, a known apple tree further away.",
        "de",
        {"inspect:obj-01", "move_to:obj-02"},
        [("inspect", "obj-01"), ("eat", "obj-01"), ("move_to", "obj-02"), ("idle", None)],
        [obj("obj-01", "red_berry_bush", 3.0), obj("obj-02", "apple_tree", 20.0, [EDIBLE])],
        needs={"hunger": 0.12},
    ),
    case(
        2,
        "thirst_known_spring",
        "Very thirsty; a spring known to be drinkable is close.",
        "en",
        {"drink:obj-01"},
        [("drink", "obj-01"), ("wander", None), ("idle", None)],
        [obj("obj-01", "spring_water", 4.0, [("drinkable", True, 1.0)])],
        needs={"thirst": 0.1},
    ),
    case(
        3,
        "thirst_salt_water_only_close",
        "Very thirsty; sea water close is known to be salty, the spring is far.",
        "de",
        {"move_to:obj-02"},
        [("drink", "obj-01"), ("move_to", "obj-02"), ("idle", None)],
        [
            obj("obj-01", "sea_water", 2.0, [("salty", True, 1.0), ("drinkable", False, 0.9)]),
            obj("obj-02", "spring_water", 40.0, [("drinkable", True, 1.0)]),
        ],
        needs={"thirst": 0.15},
    ),
    case(
        4,
        "tired_at_night",
        "Exhausted at night.",
        "en",
        {"sleep:None"},
        [("sleep", None), ("wander", None), ("inspect", "obj-01")],
        [obj("obj-01", "flint", 3.0)],
        needs={"sleep": 0.08},
        tod="night",
        temp=12.0,
    ),
    case(
        5,
        "cold_rainy_night",
        "Cold rainy night; a rock niche is close.",
        "de",
        {"move_to:obj-01", "sleep:None"},
        [("move_to", "obj-01"), ("wander", None), ("sleep", None), ("idle", None)],
        [obj("obj-01", "rock_niche", 8.0)],
        needs={"warmth": 0.2, "sleep": 0.3},
        tod="night",
        temp=4.5,
        weather=("rain",),
    ),
    case(
        6,
        "curious_unknown_stone",
        "Needs met, very curious, an unknown stone nearby.",
        "en",
        {"inspect:obj-01", "pick_up:obj-01"},
        [("inspect", "obj-01"), ("pick_up", "obj-01"), ("idle", None), ("sleep", None)],
        [obj("obj-01", "flint", 2.0)],
        traits={"curiosity": 0.9},
        moods={"boredom": 0.4},
    ),
    case(
        7,
        "fearful_unknown_object",
        "Needs met, very fearful; something unknown nearby.",
        "de",
        {"inspect:obj-01", "idle:None", "wander:None"},
        [("inspect", "obj-01"), ("eat", "obj-01"), ("idle", None), ("wander", None)],
        [obj("obj-01", "blue_mushroom", 5.0)],
        traits={"courage": 0.15},
        moods={"fear": 0.5},
    ),
    case(
        8,
        "hungry_known_poison",
        "Hungry; berries close are known to be poisonous, apples further away.",
        "en",
        {"move_to:obj-02", "eat:obj-02"},
        [("eat", "obj-01"), ("move_to", "obj-02"), ("eat", "obj-02"), ("idle", None)],
        [
            obj("obj-01", "red_berry_bush", 2.0, [("poisonous", True, 1.0)]),
            obj("obj-02", "apple_tree", 6.0, [EDIBLE]),
        ],
        needs={"hunger": 0.25},
    ),
    case(
        9,
        "lonely_sociable_other_person",
        "Lonely and sociable; another person is near.",
        "de",
        {"approach:inh-000002", "talk_to:inh-000002"},
        [("approach", "inh-000002"), ("talk_to", "inh-000002"), ("wander", None), ("idle", None)],
        [],
        needs={"company": 0.15},
        traits={"sociability": 0.85},
        moods={"sadness": 0.3},
    ),
    case(
        10,
        "shy_first_sight_of_person",
        "Shy and cautious; sees another person for the first time.",
        "en",
        {"approach:inh-000002", "idle:None", "wander:None"},
        [("approach", "inh-000002"), ("talk_to", "inh-000002"), ("idle", None), ("wander", None)],
        [],
        traits={"sociability": 0.2, "courage": 0.3},
        moods={"fear": 0.2},
    ),
    case(
        11,
        "storm_fear",
        "Storm and fear; a rock niche close.",
        "de",
        {"move_to:obj-01", "idle:None"},
        [("move_to", "obj-01"), ("wander", None), ("idle", None)],
        [obj("obj-01", "rock_niche", 6.0)],
        needs={"safety": 0.2},
        moods={"fear": 0.6},
        temp=9.0,
        weather=("rain", "storm"),
    ),
    case(
        12,
        "bored_needs_met",
        "All needs met and bored.",
        "en",
        {"wander:None", "inspect:obj-01"},
        [("wander", None), ("inspect", "obj-01"), ("sleep", None), ("idle", None)],
        [obj("obj-01", "dry_wood", 7.0)],
        moods={"boredom": 0.7},
        needs={"fun": 0.2},
    ),
    case(
        13,
        "hungry_known_edible_close",
        "Hungry; berries known to be edible are close.",
        "de",
        {"eat:obj-01", "pick_up:obj-01"},
        [("eat", "obj-01"), ("pick_up", "obj-01"), ("wander", None), ("idle", None)],
        [obj("obj-01", "blue_berry_bush", 2.0, [EDIBLE])],
        needs={"hunger": 0.2},
    ),
    case(
        14,
        "cold_flint_and_pyrite",
        "Cold; holds flint and pyrite that are known to make sparks, dry grass nearby.",
        "en",
        {"combine:obj-01", "inspect:obj-03"},
        [
            ("combine", "obj-01", ["obj-02"]),
            ("inspect", "obj-03"),
            ("wander", None),
            ("idle", None),
        ],
        [
            obj("obj-01", "flint", 0.0, [("sparks", True, 0.8)]),
            obj("obj-02", "pyrite", 0.0, [("sparks", True, 0.8)]),
            obj("obj-03", "dry_grass", 1.0, [("burnable", True, 0.7)]),
        ],
        needs={"warmth": 0.25},
        traits={"creativity": 0.7},
        tod="dusk",
        temp=7.0,
    ),
    case(
        15,
        "pain_after_bad_berry",
        "Belly ache and sadness after eating bad berries.",
        "de",
        {"idle:None", "sleep:None"},
        [("idle", None), ("sleep", None), ("eat", "obj-01"), ("wander", None)],
        [obj("obj-01", "red_berry_bush", 3.0, [("poisonous", True, 0.9)])],
        moods={"sadness": 0.5, "fear": 0.3},
        will=0.4,
    ),
    case(
        16,
        "heat_thirst_midday",
        "Hot day, thirsty; spring and a shady tree nearby.",
        "en",
        {"drink:obj-01", "move_to:obj-02"},
        [("drink", "obj-01"), ("move_to", "obj-02"), ("wander", None)],
        [
            obj("obj-01", "spring_water", 10.0, [("drinkable", True, 1.0)]),
            obj("obj-02", "apple_tree", 5.0),
        ],
        needs={"thirst": 0.2},
        temp=33.0,
        weather=("heat",),
    ),
    case(
        17,
        "hungry_only_unknown_mushroom",
        "Very hungry; only an unknown mushroom nearby.",
        "de",
        {"inspect:obj-01", "wander:None"},
        [("eat", "obj-01"), ("inspect", "obj-01"), ("wander", None), ("idle", None)],
        [obj("obj-01", "brown_mushroom", 3.0)],
        needs={"hunger": 0.15},
        traits={"courage": 0.4},
    ),
    case(
        18,
        "rested_dawn",
        "Rested and fed at dawn.",
        "en",
        {"wander:None", "inspect:obj-01"},
        [("wander", None), ("inspect", "obj-01"), ("sleep", None)],
        [obj("obj-01", "dry_grass", 5.0)],
        needs={"sleep": 0.9},
        tod="dawn",
        temp=12.0,
    ),
    case(
        19,
        "tired_dusk",
        "Tired at dusk, warm and fed.",
        "de",
        {"sleep:None", "idle:None"},
        [("sleep", None), ("wander", None), ("inspect", "obj-01")],
        [obj("obj-01", "dry_wood", 4.0)],
        needs={"sleep": 0.2},
        tod="dusk",
        temp=16.0,
    ),
    case(
        20,
        "thirst_unknown_water",
        "Thirsty; unknown water close, nothing known about it.",
        "en",
        {"inspect:obj-01", "drink:obj-01"},
        [("drink", "obj-01"), ("inspect", "obj-01"), ("wander", None), ("idle", None)],
        [obj("obj-01", "pond_water", 3.0)],
        needs={"thirst": 0.25},
    ),
]
OUT.parent.mkdir(parents=True, exist_ok=True)
data = {
    "id": "decider-testset",
    "version": 1,
    "purpose": "B3 S5 (A1330): 20 fixed situations x N runs with the real model; content-valid >= 95 % (acceptance), plausibility and latency are reported (B2487: no fixed time limit).",
    "cases": cases,
}
OUT.write_text(
    json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n"
)
print(len(cases), "cases")
