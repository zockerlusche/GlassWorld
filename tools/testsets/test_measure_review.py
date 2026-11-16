"""A1582: the look-back measurement counts leaks in what a player sees and in the internal idea text."""

from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path
from types import ModuleType

HERE = Path(__file__).resolve().parent


def _module(name: str) -> ModuleType:
    sys.path.insert(0, str(HERE))
    spec = importlib.util.spec_from_file_location(name, HERE / f"{name}.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_bait_terms_count_in_shown_texts_and_in_the_english_idea_text() -> None:
    measure = _module("measure_review")
    review = {
        "dream": {"text": "Ich baue ein Floß am Strand.", "tone": "calm", "about": None},
        "ideas": [
            {
                "text": "Tie reeds into a raft.",
                "thought": {"locale": "de", "text": "Was, wenn ich Schilf binde?"},
            },
            {"text": "Open the hazelnut with the stone.", "thought": None},
        ],
        "intentions": [],
    }
    terms = {"de": ["floß*", "boot*"], "en": ["raft*", "boat*"]}
    row = measure.judge(review, ["idea thought left out (unknown: Floß)"], terms, "de")
    assert row["bait_shown"] == ["floß"]
    assert row["bait_idea_text"] == ["raft"]
    assert (row["ideas"], row["idea_thoughts_left_out"], row["unknown_left_out"]) == (2, 1, 1)


def test_the_bait_cases_keep_v1_and_name_no_thing_of_this_world() -> None:
    cases = json.loads(
        (HERE.parents[1] / "ai-service/testsets/review/cases.v2.json").read_text(encoding="utf-8")
    )
    v1 = json.loads(
        (HERE.parents[1] / "ai-service/testsets/review/cases.v1.json").read_text(encoding="utf-8")
    )
    plain = [c for c in cases["cases"] if c["bait"] is None]
    assert [c["job"] for c in plain] == [c["job"] for c in v1["cases"]]
    baited = [c for c in cases["cases"] if c["bait"]]
    assert len(baited) == 9
    assert len({c["job"]["request_id"] for c in cases["cases"]}) == len(cases["cases"])
    builder = _module("build_review_bait_cases")
    world = builder.world_names()
    assert not [
        t for ts in cases["bait_terms"].values() for t in ts if t.rstrip("*").lower() in world
    ]
