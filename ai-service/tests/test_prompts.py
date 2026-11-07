"""Prompt loading, decider context and prompt snapshots.

Snapshots live in tests/snapshots. After an intended prompt change, regenerate them with
    GW_UPDATE_SNAPSHOTS=1 uv run pytest ai-service/tests/test_prompts.py
and review the diff like code.
"""

from __future__ import annotations

import json
import os
from pathlib import Path

import pytest

from glassworld_ai.decider.context import build_values
from glassworld_ai.prompts import PromptError, load_prompt
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import DecisionRequest

REPO = Path(__file__).resolve().parents[2]
PROMPTS = REPO / "prompts"
EXAMPLES = REPO / "contracts" / "examples" / "valid"
SNAPSHOTS = Path(__file__).resolve().parent / "snapshots"
BUDGET = 6000


def _request(case: str) -> DecisionRequest:
    return DecisionRequest.model_validate(
        json.loads((EXAMPLES / f"decision-request.{case}.json").read_text(encoding="utf-8"))
    )


def _vocab() -> Vocabulary:
    return Vocabulary.load(REPO / "content" / "vocabulary")


def _render(request: DecisionRequest, budget: int = BUDGET) -> str:
    template = load_prompt(PROMPTS, "decider/decide", 1)
    rendered = template.render(build_values(request, template, _vocab(), budget))
    return (
        f"[{rendered.version}]\n\n=== system ===\n{rendered.system}\n\n"
        f"=== user ===\n{rendered.user}\n"
    )


@pytest.mark.parametrize("case", ["hungry-berries", "stormy-dusk"])
def test_decider_prompt_snapshot(case: str) -> None:
    text = _render(_request(case))
    snapshot = SNAPSHOTS / f"decider-decide.v1.{case}.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.parent.mkdir(exist_ok=True)
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


def test_rendering_is_deterministic() -> None:
    request = _request("hungry-berries")
    assert _render(request) == _render(request)


def test_prompt_mentions_every_option_and_the_player_language() -> None:
    request = _request("hungry-berries")
    text = _render(request)
    for option in request.options:
        assert f"- {option.option_id}:" in text
    assert "Write the thought in German." in text


def test_needs_are_explained_with_words_most_urgent_first() -> None:
    text = _render(_request("hungry-berries"))
    hunger = text.index("hunger (Need for food.): 0.18 (very urgent)")
    thirst = text.index("thirst (Need for water.): 0.70 (satisfied)")
    assert hunger < thirst


def test_no_season_name_reaches_the_model() -> None:
    text = _render(_request("stormy-dusk")).lower()
    for season in ("winter", "summer", "autumn", "spring season"):
        assert season not in text


def test_small_budget_cuts_far_objects_but_keeps_option_targets() -> None:
    data = json.loads((EXAMPLES / "decision-request.hungry-berries.json").read_text("utf-8"))
    data["perception"]["objects"] += [
        {"id": f"obj-9{i:03d}", "type": "pebble", "distance_m": 30.0 + i, "known_properties": []}
        for i in range(10)
    ]
    text = _render(DecisionRequest.model_validate(data), budget=1500)
    assert "[obj-0042]" in text and "[obj-0007]" in text  # option targets stay
    assert "more things further away" in text
    assert "[obj-9009]" not in text


def test_missing_placeholder_is_an_error(tmp_path: Path) -> None:
    (tmp_path / "decider").mkdir()
    (tmp_path / "decider" / "x.v1.toml").write_text(
        'name = "decider/x"\nversion = 1\nsystem = "Hi $who"\nuser = "u"\n', encoding="utf-8"
    )
    with pytest.raises(PromptError):
        load_prompt(tmp_path, "decider/x", 1).render({})


def test_file_name_and_content_must_match(tmp_path: Path) -> None:
    (tmp_path / "decider").mkdir()
    (tmp_path / "decider" / "x.v2.toml").write_text(
        'name = "decider/x"\nversion = 1\nsystem = "s"\nuser = "u"\n', encoding="utf-8"
    )
    with pytest.raises(PromptError):
        load_prompt(tmp_path, "decider/x", 2)


def test_unknown_prompt_and_bad_name_are_errors() -> None:
    with pytest.raises(PromptError):
        load_prompt(PROMPTS, "decider/does-not-exist", 1)
    with pytest.raises(PromptError):
        load_prompt(PROMPTS, "../secret", 1)


def test_vocabulary_loads_all_kinds_from_content() -> None:
    vocab = _vocab()
    for kind in (
        "action",
        "property",
        "need",
        "mood",
        "trait",
        "expression",
        "time_of_day",
        "weather",
    ):
        assert vocab.ids(kind), kind
    assert vocab.describe("need", "hunger") == "Need for food."
    assert vocab.describe("need", "does_not_exist") is None
