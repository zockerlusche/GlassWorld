"""Builds ai-service/testsets/b3/leisure.v1.json: a free moment asks the decider (B2663, decider v10).

    uv run python tools/testsets/build_leisure_cases.py RUN_LOG_JSONL
    uv run python -m glassworld_ai.testsets decider --cases ai-service/testsets/b3/leisure.v1.json ...

The cases are real decision requests of a headless run (log.jsonl of tools/durchstich/run_d9.py)
whose options include an action with another inhabitant as target. Each becomes a free moment:
needs at least 0.5 (nothing presses), boredom 0.4, the occasion 'leisure'. The options stay what
the core offered then. Every option is plausible - the set measures valid answers, how the
choices spread and the thoughts; it never prescribes one (frame, not outcome).
"""

from __future__ import annotations

import copy
import json
import pathlib
import sys
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "ai-service/testsets/b3/leisure.v1.json"
MAX_CASES = 10


def social(root: pathlib.Path = ROOT) -> set[str]:
    data = json.loads((root / "content/vocabulary/actions.json").read_text("utf-8"))
    return {e["id"] for e in data["entries"] if e.get("target") == "inhabitant"}


def free_moment(request: dict[str, Any], n: int, locale: str) -> dict[str, Any]:
    out: dict[str, Any] = copy.deepcopy(request)
    out["request_id"] = f"00000000-000b-2663-0000-{n:012d}"
    out["seed"] = 26630 + n
    out["display_locale"] = locale
    out["occasion"] = "leisure"
    for need in out["inhabitant"]["needs"]:
        need["value"] = max(float(need["value"]), 0.5)
    moods = out["inhabitant"]["mood"]
    if not any(m["id"] == "boredom" for m in moods):
        moods.append({"id": "boredom", "value": 0.4})
    for mood in moods:
        if mood["id"] == "boredom":
            mood["value"] = max(float(mood["value"]), 0.4)
    out["environment"]["time_of_day"] = "day"
    out.pop("building", None)
    return out


def main(argv: list[str]) -> int:
    log = pathlib.Path(argv[0])
    with_others = social()
    picked: list[dict[str, Any]] = []
    seen: set[tuple[str, int]] = set()
    for line in log.read_text(encoding="utf-8").splitlines():
        entry = json.loads(line)
        if entry.get("log") != "decision" or entry.get("kind") != "request":
            continue
        request = entry["request"]
        if not any(o["action"] in with_others for o in request["options"]):
            continue
        key = (request["inhabitant"]["id"], int(request["tick"]) // 360)  # one per 6 h
        if key in seen:
            continue
        seen.add(key)
        picked.append(request)
        if len(picked) == MAX_CASES:
            break
    cases = []
    for n, request in enumerate(picked, start=1):
        req = free_moment(request, n, "de" if n % 2 else "en")
        cases.append(
            {
                "id": f"leisure_{n:02d}",
                "description": "A free moment (B2663) from a real request of run L924: "
                + ", ".join(sorted({o["action"] for o in req["options"]})),
                "request": req,
                "plausible_option_ids": [o["option_id"] for o in req["options"]],
            }
        )
    OUT.parent.mkdir(parents=True, exist_ok=True)
    data = {
        "id": "b3-leisure",
        "version": 1,
        "purpose": "B2663 (A1601, D871): a free moment asks the decider (v10, occasion leisure); every"
        " offered option is plausible; measured: valid answers, how the choices spread, the thoughts.",
        "cases": cases,
    }
    OUT.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(cases)} cases -> {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
