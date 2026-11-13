"""Builds ai-service/testsets/review/cases.v1.json: nightly look-back jobs (A1584) from a saved world.

    uv run python tools/testsets/build_review_cases.py WORLD.sqlite

Per inhabitant and chosen night the job carries what the core would send: the day's remembered
events, the memory summary, its beliefs (not the truth) as Fundbuch, no ideas yet, its needs and
character. Read only. The cases measure valid answers, ideas built only on its own knowledge and the
thoughts; they never prescribe an idea (frame, not outcome).
"""

from __future__ import annotations

import json
import sqlite3
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "ai-service/testsets/review/cases.v1.json"
TPD = 1440
NIGHTS = range(1, 41)  # every night with remembered events; up to MAX_CASES spread over the run
MAX_CASES = 12
ACTIONS = ["inspect", "show", "teach", "explore", "use", "talk_to", "wander", "eat", "drink"]


def main(argv: list[str]) -> int:
    db = sqlite3.connect(f"file:{argv[0]}?mode=ro", uri=True)
    inhabitants = db.execute("select id, needs, traits from inhabitants").fetchall()
    memories = dict(db.execute("select inhabitant, summary from memories").fetchall())
    cases: list[dict[str, Any]] = []
    for night in NIGHTS:
        lo, hi = (night - 1) * TPD, night * TPD
        for n, (inh, needs, traits) in enumerate(inhabitants):
            events = []
            for tick, kind, about_kind, about_id, formative, times, outcome in db.execute(
                "select tick, kind, about_kind, about_id, formative, times, outcome from memory_events"
                " where inhabitant = ? and tick >= ? and tick < ? order by tick",
                (inh, lo, hi),
            ):
                event: dict[str, Any] = {
                    "tick": tick,
                    "kind": kind,
                    "about": {"kind": about_kind, "id": about_id} if about_id else None,
                    "formative": bool(formative),
                }
                if times and times > 1:
                    event["times"] = times
                if outcome:
                    event["outcome"] = outcome
                events.append(event)
            if not events:
                continue
            knowledge = [
                {
                    "subject": {"kind": "type", "id": typ},
                    "property": prop,
                    "value": bool(val),
                    "confidence": round(float(conf), 2),
                    "source": source,
                    "told_by": told,
                    "tick": tick,
                }
                for typ, prop, val, conf, source, told, tick in db.execute(
                    "select type, property, value, confidence, source, told_by, tick from beliefs"
                    " where inhabitant = ? and tick < ? and source != 'seen' order by tick desc limit 40",
                    (inh, hi),
                )
            ]
            need_rows = json.loads(needs) if needs else {}
            trait_rows = json.loads(traits) if traits else {}

            def levels(raw: Any) -> list[dict[str, Any]]:
                items = (
                    raw.items() if isinstance(raw, dict) else [(r["id"], r["value"]) for r in raw]
                )
                return [{"id": k, "value": round(float(v), 2)} for k, v in sorted(items)]

            job = {
                "contract_version": "0.2",
                "tick": hi - 1,
                "seed": 26870 + 10 * night + n,
                "display_locale": "de" if n % 2 == 0 else "en",
                "request_id": f"00000000-1584-4000-8000-{night:04d}{n:08d}",
                "kind": "review",
                "priority": 20,
                "inhabitant": inh,
                "object": None,
                "payload": {
                    "review": {
                        "events": events[-32:],
                        "memory": (memories.get(inh) or "")[:1200],
                        "memory_max_chars": 600,
                        "dream_max_chars": 220,
                        "mood": None,
                        "tones": ["calm", "joyful", "curious", "uneasy"],
                        "knowledge": knowledge,
                        "ideas": [],
                        "needs": levels(need_rows),
                        "traits": levels(trait_rows),
                        "actions": ACTIONS,
                        "max_new_ideas": 1,
                        "max_intentions": 2,
                    }
                },
            }
            cases.append({"id": f"night{night:02d}_{inh}", "job": job})
    if len(cases) > MAX_CASES:  # spread over the run
        step = len(cases) / MAX_CASES
        cases = [cases[int(i * step)] for i in range(MAX_CASES)]
    data = {
        "id": "review-night",
        "version": 1,
        "purpose": "A1584: nightly look back from a real saved world (Marcel's test 12.11., 40 days); measured: valid answers, ideas only from own knowledge, shown texts kept.",
        "cases": cases,
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(cases)} cases -> {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
