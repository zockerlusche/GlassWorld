"""Prompt loading, decider context and prompt snapshots.

Snapshots live in tests/snapshots. After an intended prompt change, regenerate them with
    GW_UPDATE_SNAPSHOTS=1 uv run pytest ai-service/tests/test_prompts.py
and review the diff like code.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

import pytest

from glassworld_ai.decider.context import build_values
from glassworld_ai.prompts import PromptError, load_prompt
from glassworld_ai.texts import TextCatalog
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


def _render(request: DecisionRequest, budget: int = BUDGET, version: int = 1) -> str:
    template = load_prompt(PROMPTS, "decider/decide", version)
    texts = TextCatalog.load(REPO / "content" / "i18n")
    rendered = template.render(build_values(request, template, _vocab(), budget, texts))
    return (
        f"[{rendered.version}]\n\n=== system ===\n{rendered.system}\n\n"
        f"=== user ===\n{rendered.user}\n"
    )


@pytest.mark.parametrize("version", [1, 2, 3, 4, 6])
@pytest.mark.parametrize("case", ["hungry-berries", "stormy-dusk"])
def test_decider_prompt_snapshot(case: str, version: int) -> None:
    text = _render(_request(case), version=version)
    snapshot = SNAPSHOTS / f"decider-decide.v{version}.{case}.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.parent.mkdir(exist_ok=True)
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


@pytest.mark.parametrize("case", ["feeling-sick", "cold-wearing-tunic"])
def test_decider_v3_body_snapshot(case: str) -> None:
    """Prompt v3 (B2540/B2543): clothing, felt temperature, felt conditions, visible signs."""
    text = _render(_request(case), version=3)
    snapshot = SNAPSHOTS / f"decider-decide.v3.{case}.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")
    assert "inh-0002" not in text.split("=== user ===")[-1].split("## People")[0]


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


def test_v2_shows_beliefs_with_source_people_and_habits() -> None:
    text = _render(_request("hungry-berries"), version=2)
    assert "> Woke up alone on a beach." in text  # memory as quoted data
    assert "- recently: drank (thing 'spring water')" in text
    assert "thing 'apple tree' is edible (sure 0.6; someone told you (inh-0002))" in text
    assert "person [inh-0002], 15.0 m away: you like them some, trust them some" in text
    assert "- sometimes: drink" in text
    assert "Write the thought in German, as one short sentence." in text


def test_v2_without_memory_and_people_says_so() -> None:
    text = _render(_request("stormy-dusk"), version=2)
    assert "(you remember nothing special yet)" in text
    assert "- (nobody)" in text
    assert "- (no habits yet)" in text


def test_v2_parts_count_against_the_budget() -> None:
    data = json.loads(
        (EXAMPLES / "decision-request.hungry-berries.json").read_text(encoding="utf-8")
    )
    extra: list[dict[str, Any]] = [
        {"id": f"obj-{9000 + i}", "type": "pebble", "distance_m": 30.0 + i, "known_properties": []}
        for i in range(8)
    ]
    data["perception"]["objects"] += extra
    request = DecisionRequest.model_validate(data)
    v1, v2 = _render(request, budget=1700, version=1), _render(request, budget=1700, version=2)
    # Same budget: v2 has memory, knowledge, habits and people, so fewer far objects fit.
    assert v2.count("pebble [") < v1.count("pebble [")
    assert "red berry bush [obj-0042]" in v2  # option targets always stay


def test_every_knowledge_source_has_words_in_the_prompts_that_name_sources() -> None:
    """A source without words would reach the model as a raw id (review GW2 #142: 'seen')."""
    import tomllib

    sources = {
        e["id"]
        for e in json.loads(
            (REPO / "content/vocabulary/knowledge_sources.json").read_text(encoding="utf-8")
        )["entries"]
    }
    sources |= {"seen"}  # added with #142 (curiosity); harmless before it lands
    for name in (
        "decider/decide.v2",
        "decider/decide.v3",
        "decider/decide.v4",
        "decider/decide.v5",
        "decider/decide.v6",
        "question/ask.v1",
    ):
        settings = tomllib.loads((PROMPTS / f"{name}.toml").read_text(encoding="utf-8"))
        words = settings["settings"]["source_words"]
        assert sources <= set(words), (name, sorted(sources - set(words)))


def test_v4_names_things_in_the_players_language() -> None:
    """W1687: the model sees what the inhabitant calls a thing, not only an English id."""
    text = _render(_request("hungry-berries"), version=4)
    assert '(you call it "' in text
    assert "at most 10 words" in text
    v3 = _render(_request("hungry-berries"), version=3)
    assert '(you call it "' not in v3  # older versions unchanged


def test_v4_options_say_what_is_shown_taught_or_warned_about() -> None:
    """#156/D625: the topic of show/teach/warn reaches the model with the thing's own name."""
    text = _render(_request("passing-topics"), version=4)
    assert "you believe it is drinkable" in text
    assert '"Steinschale" (inv 3fa2c19b7e04) - you believe it is not edible' in text
    assert "about:" not in _render(_request("passing-topics"), version=3)  # older ones unchanged
    assert "Careful, the red berries are poisonous!" in text


def test_the_prompt_digest_tells_in_place_edits_apart(tmp_path: Path) -> None:
    """Review GW1 #169: an edited prompt gets another digest in the logs; line ends do not count."""
    (tmp_path / "decider").mkdir()
    file = tmp_path / "decider" / "x.v1.toml"
    file.write_bytes(b'name = "decider/x"\nversion = 1\nsystem = "s"\nuser = "u"\n')
    first = load_prompt(tmp_path, "decider/x", 1).digest
    file.write_bytes(b'name = "decider/x"\r\nversion = 1\r\nsystem = "s"\r\nuser = "u"\r\n')
    assert load_prompt(tmp_path, "decider/x", 1).digest == first
    file.write_bytes(b'name = "decider/x"\nversion = 1\nsystem = "s2"\nuser = "u"\n')
    assert load_prompt(tmp_path, "decider/x", 1).digest != first
    assert len(first) == 12
    template = load_prompt(PROMPTS, "decider/decide", 4)
    texts = TextCatalog.load(REPO / "content/i18n")
    values = build_values(_request("hungry-berries"), template, _vocab(), BUDGET, texts)
    assert template.render(values).digest == template.digest


def test_v4_knows_its_own_name_to_introduce_itself() -> None:
    """W1698: under the default prompt the inhabitant can say 'Ich bin Tavi'."""
    data = json.loads((EXAMPLES / "decision-request.b9-family.json").read_text(encoding="utf-8"))
    assert 'Your name: "Tavi"' in _render(DecisionRequest.model_validate(data), version=4)
    assert "Your name: (none yet)" in _render(_request("hungry-berries"), version=4)


def test_v6_says_how_far_the_inhabitant_sees() -> None:
    """B2594: v6 tells the model how far the inhabitant sees (fog, rain, snow shorten it)."""
    request = _request("stormy-dusk")  # visibility_m 30
    v6 = _render(request, version=6)
    assert "- How far you can see: a fair way (about 30 m)" in v6
    assert "hidden right now" in v6
    near = request.model_copy(deep=True)
    near.environment.visibility_m = 8.0
    assert "- How far you can see: only what is very close (about 8 m)" in _render(near, version=6)
    # The released versions stay as they were.
    for version in (3, 4, 5):
        assert "How far you can see" not in _render(request, version=version)


def test_v6_keeps_the_b9_parts_of_v5_out() -> None:
    """v6 builds on v4: no life, family, customs, names or gossip (prompt and answer)."""
    from glassworld_ai.decider.context import b9_parts

    v6 = load_prompt(PROMPTS, "decider/decide", 6)
    assert not b9_parts(v6)
    assert b9_parts(load_prompt(PROMPTS, "decider/decide", 5))
    assert not b9_parts(load_prompt(PROMPTS, "decider/decide", 4))
    v4_text = _render(_request("hungry-berries"), version=4).split("=== user ===")[1]
    v6_text = _render(_request("hungry-berries"), version=6).split("=== user ===")[1]
    assert v4_text == v6_text  # no visibility in this example: the user part is v4's
