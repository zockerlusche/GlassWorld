"""Builds ai-service/testsets/review/cases.v2.json: the nightly look back (A1584) with world-knowledge
baits (A1582, D827 R26a/b, R29).

    uv run python tools/testsets/build_review_bait_cases.py

Every case of cases.v1 (a real saved world) stays as it is. Each bait case is a v1 night whose Fundbuch
also holds materials the inhabitant has really handled (their properties, experienced), chosen so that a
model with world knowledge is tempted to name a thing this world does not have: wood, flint and flax
suggest an axe, a spear or a bow; clay and fire a pot; driftwood, reed and flax a raft, a net or a
basket. The bait terms (DE/EN) are such names - none is a thing of this world (checked against the
content's object names). The cases never prescribe an idea; they measure how often an idea, a thought
or the dream names something the inhabitant cannot know (frame, not outcome).
"""

from __future__ import annotations

import copy
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
V1 = ROOT / "ai-service/testsets/review/cases.v1.json"
OUT = ROOT / "ai-service/testsets/review/cases.v2.json"

# What the inhabitant has handled (type -> true properties it found out) per bait situation.
BAITS: dict[str, dict[str, list[str]]] = {
    "wood_flint_flax": {
        "dry_wood": ["burnable", "splittable", "hard"],
        "flint": ["sharp", "hard"],
        "flax": ["fibrous", "braidable"],
    },
    "clay_fire": {
        "clay": ["malleable", "hardens_in_fire", "fine_grained"],
        "fire": ["hot"],
    },
    "driftwood_reed_flax": {
        "driftwood": ["burnable", "building_material"],
        "reed": ["fibrous", "braidable", "waterproof"],
        "flax": ["fibrous", "braidable"],
    },
}

# Names a model knows from elsewhere; none of them is a thing of this world (checked below). Lower
# case, '*' = also the start of a word ('Speerspitze'), as measure_inventions.bait_terms matches.
BAIT_TERMS: dict[str, list[str]] = {
    "de": [
        "axt*",
        "beil*",
        "speer*",
        "bogen*",
        "pfeil*",
        "messer*",
        "topf*",
        "krug*",
        "ziegel*",
        "schüssel*",
        "floß*",
        "boot*",
        "netz*",
        "korb*",
        "seil*",
        "rad*",
        "karren*",
        "hammer*",
        "säge*",
        "nagel*",
    ],
    "en": [
        "axe",
        "hatchet",
        "spear",
        "bow",
        "arrow*",
        "knife",
        "pot*",
        "jug*",
        "brick*",
        "bowl*",
        "raft*",
        "boat*",
        "net*",
        "basket*",
        "rope*",
        "wheel*",
        "cart*",
        "hammer*",
        "saw",
        "nail*",
    ],
}
BAIT_NIGHTS = 3  # the first nights of v1 get each bait (3 x 3 = 9 bait cases)


def world_names() -> set[str]:
    """Every word of the content's object names in both languages (lower case)."""
    names: set[str] = set()
    for locale in ("de", "en"):
        entries = json.loads((ROOT / f"content/i18n/{locale}.json").read_text(encoding="utf-8"))[
            "entries"
        ]
        for entry in entries:
            if entry["key"].startswith("vocab.object."):
                names.update(word.lower() for word in str(entry["text"]).replace("-", " ").split())
    return names


def bait_case(
    case: dict[str, Any], n: int, bait: str, materials: dict[str, list[str]]
) -> dict[str, Any]:
    job = copy.deepcopy(case["job"])
    review = job["payload"]["review"]
    tick = int(job["tick"]) - 60
    have = {(k["subject"]["id"], k["property"]) for k in review["knowledge"]}
    for type_id, properties in materials.items():
        for prop in properties:
            if (type_id, prop) not in have:
                review["knowledge"].append(
                    {
                        "subject": {"kind": "type", "id": type_id},
                        "property": prop,
                        "value": True,
                        "confidence": 1.0,
                        "source": "experienced",
                        "told_by": None,
                        "tick": tick,
                    }
                )
    job["request_id"] = (
        job["request_id"][:-4] + f"b{n + 1:03d}"
    )  # stable: the bait's place in BAITS
    return {"id": f"{case['id']}+{bait}", "bait": bait, "job": job}


def main(argv: list[str]) -> int:
    del argv
    world = world_names()
    clash = sorted(
        t for terms in BAIT_TERMS.values() for t in terms if t.rstrip("*").lower() in world
    )
    if clash:
        print(f"BAIT_FAIL bait terms that are things of this world: {clash}", file=sys.stderr)
        return 1
    objects = {
        e["key"][len("vocab.object.") :]
        for e in json.loads((ROOT / "content/i18n/de.json").read_text(encoding="utf-8"))["entries"]
        if e["key"].startswith("vocab.object.")
    }
    unknown = sorted(t for m in BAITS.values() for t in m if t not in objects)
    if unknown:
        print(f"BAIT_FAIL materials that are no thing of this world: {unknown}", file=sys.stderr)
        return 1
    v1 = json.loads(V1.read_text(encoding="utf-8"))
    cases = [dict(c, bait=None) for c in v1["cases"]]
    for case in v1["cases"][:BAIT_NIGHTS]:
        cases += [
            bait_case(case, n, bait, materials) for n, (bait, materials) in enumerate(BAITS.items())
        ]
    out = {
        "id": "review",
        "version": 2,
        "purpose": "A1582 (R26a/b, R29): the v1 nights plus world-knowledge baits - materials the inhabitant "
        "handled that tempt a model to name a thing this world does not have. Measured: valid answers, "
        "ideas and thoughts left out, bait terms in idea texts (EN), thoughts and dreams (DE). "
        "Never prescribes an idea.",
        "bait_terms": BAIT_TERMS,
        "cases": cases,
    }
    OUT.write_text(
        json.dumps(out, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n"
    )
    print(
        f"REVIEW_CASES {len(cases)} ({len(cases) - len(v1['cases'])} with a bait) -> {OUT.relative_to(ROOT)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
