"""Builds ai-service/testsets/b4b/build_stuck.v1.json: a stuck building asks the decider (A1373 step 2,
D850, decider v9).

    uv run python tools/testsets/build_b4b_stuck_cases.py
    uv run python -m glassworld_ai.testsets decider --cases ai-service/testsets/b4b/build_stuck.v1.json ...

Each case is the own-invention example with a building block and only the options the core leaves
open on that occasion. Every offered option is plausible - the test set measures that the answers
are valid and how the choices spread; it never prescribes one (frame, not outcome).
"""

from __future__ import annotations

import copy
import json
import pathlib
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[2]
BASE = ROOT / "contracts/examples/valid/decision-request.own-invention.json"
OUT = ROOT / "ai-service/testsets/b4b/build_stuck.v1.json"


def option(oid: str, action: str, target: str | None = None) -> dict[str, Any]:
    return {"option_id": oid, "action": action, "target": target, "partners": []}


def case(
    n: int, cid: str, desc: str, options: list[dict[str, Any]], missing: list[str], locale: str
) -> dict[str, Any]:
    request: dict[str, Any] = copy.deepcopy(json.loads(BASE.read_text("utf-8")))
    request["request_id"] = f"00000000-000b-1373-0000-{n:012d}"
    request["seed"] = 13730 + n
    request["display_locale"] = locale
    request["building"] = {
        "name": {"de": "Wasserschale", "en": "water bowl"},
        "missing_properties": missing,
        "escalation": n,
        "max_escalations": 3,
    }
    request["options"] = options
    return {
        "id": cid,
        "description": desc,
        "request": request,
        "plausible_option_ids": [o["option_id"] for o in options],
    }


def main() -> None:
    base = json.loads(BASE.read_text("utf-8"))
    thing = base["perception"]["objects"][0]["id"]  # a thing in sight as the believed substitute
    cases = [
        case(
            1,
            "substitute_in_sight_step_done",
            "Stuck at the second step; a thing in sight it believes has the missing properties.",
            [
                option("o1", "use_instead", thing),
                option("o2", "build_smaller"),
                option("o3", "give_up"),
            ],
            ["container"],
            "de",
        ),
        case(
            2,
            "nothing_in_sight_step_done",
            "Stuck at the second step; nothing in sight has the missing properties.",
            [option("o1", "build_smaller"), option("o2", "give_up")],
            ["container", "hard"],
            "en",
        ),
        case(
            3,
            "substitute_in_sight_nothing_built",
            "Stuck at the first step (nothing built yet); a believed substitute in sight.",
            [option("o1", "use_instead", thing), option("o2", "give_up")],
            ["malleable"],
            "de",
        ),
    ]
    data = {
        "id": "b4b-build-stuck",
        "version": 1,
        "purpose": "B4b R26 (A1373 step 2, D850): a stuck building asks the decider (v9); every "
        "offered option is plausible; measured: valid answers and how the choices spread.",
        "cases": cases,
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


if __name__ == "__main__":
    main()
