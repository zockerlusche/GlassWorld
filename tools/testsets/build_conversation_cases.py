"""Builds ai-service/testsets/conversation/what_doing.v1.json: ten "What are you doing?" talks (W1737).

The player asks what the inhabitant is doing; the answer must fit what it really does, knows and
sees (D675: "Ich lese ein Buch" while it walks). Run with the existing runner:
    uv run python tools/testsets/build_conversation_cases.py
    uv run python -m glassworld_ai.testsets decider \
        --cases ai-service/testsets/conversation/what_doing.v1.json --runs 3 --out <folder>
Forbidden word starts catch invented activities and things that are not in this world; the report
lists every answer for a human look as well.
"""

from __future__ import annotations

import json
import pathlib
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "ai-service" / "testsets" / "conversation" / "what_doing.v1.json"
BASE: dict[str, Any] = json.loads(
    (ROOT / "contracts/examples/valid/conversation-request.what-are-you-doing.json").read_text(
        "utf-8"
    )
)
# Things and activities the start island does not have (D675 and the like), as word starts.
FORBIDDEN = ["buch", "büch", "lese", "lesen", "male", "malen", "zeichne", "haus", "häus", "auto",
             "handy", "fernseh", "schule", "book", "read", "paint", "draw", "house", "car", "phone",
             "school"]  # fmt: skip


def subject(type_id: str) -> dict[str, str]:
    return {"kind": "type", "id": type_id}


def belief(type_id: str, prop: str, value: bool, source: str = "experienced") -> dict[str, Any]:
    return {"subject": subject(type_id), "property": prop, "value": value, "confidence": 1.0,
            "source": source, "told_by": None, "tick": 900}  # fmt: skip


def case(n: int, cid: str, desc: str, locale: str, text: str, doing: dict[str, Any] | None,
         sees: list[str], knowledge: list[dict[str, Any]]) -> dict[str, Any]:  # fmt: skip
    request = json.loads(json.dumps(BASE))
    request["request_id"] = f"00000000-000c-1737-0000-{n:012d}"
    request["seed"] = 17370 + n
    request["display_locale"] = locale
    request["player_text"] = text
    request["speaker"]["understanding"] = 0.8
    request["speaker"]["doing"] = doing
    request["speaker"]["sees"] = sorted(sees)
    request["speaker"]["knowledge"] = knowledge
    return {"id": cid, "description": desc, "kind": "conversation", "request": request,
            "plausible_option_ids": [], "forbidden_words": FORBIDDEN}  # fmt: skip


def doing(action: str, target: str | None = None, then: str | None = None) -> dict[str, Any]:
    planned = {"action": then, "target": subject(target) if target else None} if then else None
    return {"action": action, "target": subject(target) if target else None,
            "since_tick": BASE["tick"] - 10, "planned": planned}  # fmt: skip


SPRING = belief("spring_water", "drinkable", True)
BLUE = belief("blue_berry", "edible", True)
CASES = [
    case(
        1,
        "walking_to_water",
        "Walks to a known spring to drink.",
        "de",
        "Was machst du?",
        doing("move_to", "spring_water", "drink"),
        ["dry_grass", "spring_water"],
        [SPRING],
    ),
    case(
        2,
        "eating_berries",
        "Eats blue berries it knows.",
        "de",
        "Was machst du da?",
        doing("eat", "blue_berry"),
        ["blue_berry", "berry_bush"],
        [BLUE],
    ),
    case(
        3,
        "wandering",
        "Strolls around, nothing special.",
        "de",
        "Was machst du gerade?",
        doing("wander"),
        ["deciduous_tree", "dry_grass"],
        [],
    ),
    case(
        4,
        "resting",
        "Does nothing in particular.",
        "de",
        "Was machst du?",
        None,
        ["fieldstone"],
        [SPRING],
    ),
    case(
        5,
        "inspecting_unknown",
        "Looks at a red berry it does not know.",
        "de",
        "Was tust du?",
        doing("inspect", "red_berry"),
        ["red_berry"],
        [],
    ),
    case(
        6,
        "carrying_wood",
        "Picks up dry wood.",
        "de",
        "Was machst du mit dem Holz?",
        doing("pick_up", "dry_wood"),
        ["dry_wood", "conifer_tree"],
        [],
    ),
    case(
        7,
        "exploring",
        "Walks where it has not been yet.",
        "de",
        "Wohin gehst du?",
        doing("explore"),
        ["fieldstone", "dry_grass"],
        [],
    ),
    case(
        8,
        "drinking_en",
        "Drinks at the spring.",
        "en",
        "What are you doing?",
        doing("drink", "spring_water"),
        ["spring_water"],
        [SPRING],
    ),
    case(
        9,
        "walking_en",
        "Strolls around.",
        "en",
        "What are you up to?",
        doing("wander"),
        ["apple", "fruit_tree"],
        [],
    ),
    case(
        10,
        "sleeping_en",
        "Lies down to sleep.",
        "en",
        "What are you doing?",
        doing("sleep"),
        ["rock_niche"],
        [],
    ),
]


def main() -> None:
    data = {
        "id": "conversation-what-doing",
        "version": 1,
        "purpose": "W1737 (D675): ten 'what are you doing?' talks; the answer must fit what the "
        "inhabitant does, knows and sees; no invented activities or things.",
        "cases": CASES,
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


if __name__ == "__main__":
    main()
