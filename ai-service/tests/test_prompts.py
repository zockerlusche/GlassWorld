"""Prompt loading, decider context and prompt snapshots.

Snapshots live in tests/snapshots. After an intended prompt change, regenerate them with
    GW_UPDATE_SNAPSHOTS=1 uv run pytest ai-service/tests/test_prompts.py
and review the diff like code.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any, cast

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


@pytest.mark.parametrize("version", [1, 2, 3, 4, 6, 7, 8, 9, 10])
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
        "decider/decide.v7",
        "decider/decide.v8",
        "decider/decide.v9",
        "decider/decide.v10",
        "question/ask.v1",
        "question/ask.v2",
        "question/ask.v3",
        "question/ask.v4",
        "question/ask.v5",
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


# Settings that are word lists of a check on the answer, not text the model reads.
CHECK_WORD_LISTS = ("grounding", "thought_check")


def _setting_texts(node: object, key: str = "") -> list[str]:
    if key in CHECK_WORD_LISTS:
        return []
    if isinstance(node, str):
        return [node]
    if isinstance(node, dict):
        items = cast(dict[str, object], node).items()
        return [text for k, v in items for text in _setting_texts(v, k)]
    if isinstance(node, list):
        return [text for v in cast(list[object], node) for text in _setting_texts(v, key)]
    return []


# Prompts in use that still name things of the world, each with the open decision (D763).
NAMES_THINGS_PENDING: dict[str, str] = {
    "process/judge.v1": "D763 A3: the judge of processes states physics with fire as prose; "
    "open decision (TL) whether those rules become data",
}


def test_no_prompt_in_use_names_a_thing_of_the_world() -> None:
    """Frame, not outcome (PR2; check D763 by GW3, Marcel W1820): an example in a prompt shows the
    form only. A prompt that names a real thing of the world - 'red berries are poisonous',
    'clay for a vessel' - hands the model knowledge its inhabitant has not found out.

    The head-noun rule forbids plain words like water, wood or stone on purpose: they are things
    of this world. A prompt that must name one goes into NAMES_THINGS_PENDING with its reason; a
    settings table the model never reads goes into CHECK_WORD_LISTS. Known limit: plurals that
    change the vowel (Apfel, Aepfel) are not matched."""
    import re
    import tomllib

    config = tomllib.loads((REPO / "ai-service/config.toml").read_text(encoding="utf-8"))
    flat: dict[str, object] = {}
    for key, value in config.items():
        if isinstance(value, dict):
            flat.update(cast(dict[str, object], value))
        else:
            flat[key] = value
    catalog = json.loads((REPO / "content/objects/start-catalog.json").read_text(encoding="utf-8"))
    things = {t["id"].replace("_", " ") for t in catalog["types"]}
    # ... and what the player's languages call them (review GW3 #246: the first version knew the
    # English ids only, so 'Rote Beere' or 'Feuerstein' in a prompt went unseen).
    for locale in ("de", "en"):
        data = json.loads((REPO / f"content/i18n/{locale}.json").read_text(encoding="utf-8"))
        texts = {e["key"]: e["text"] for e in data["entries"]}
        for thing in catalog["types"]:
            name = texts.get(f"vocab.object.{thing['id']}")
            if isinstance(name, str) and name.strip():
                things.add(name.strip().lower())
    # The head noun of a name alone, so that an inflected form is seen too ('die roten Beeren'
    # for 'Rote Beere', 'red berries' for 'red berry').
    things |= {
        name.split()[-1] for name in things if len(name.split()) > 1 and len(name.split()[-1]) >= 4
    }
    things = sorted(things)
    assert len(things) > 40
    found: dict[str, list[str]] = {}
    checked = 0
    for key, name in sorted(flat.items()):
        if not key.endswith("_prompt") or not isinstance(name, str):
            continue
        version = flat[f"{key}_version"]
        prompt = tomllib.loads((PROMPTS / f"{name}.v{version}.toml").read_text(encoding="utf-8"))
        # The words the model reads: system, user and every text of the settings (their word
        # tables are rendered into the prompt). Lists of allowed or forbidden words of a check
        # are not read by the model and are left out by their key.
        text = "\n".join(
            [prompt["system"], prompt["user"], *_setting_texts(prompt.get("settings", {}))]
        ).lower()
        checked += 1
        hits: list[str] = []
        for thing in things:
            word = re.escape(thing)
            stem = word[:-1] + "(y|ies)" if word.endswith("y") else word + "(s|es|n|en)?"
            if re.search(rf"\b{stem}\b", text):
                hits.append(thing)
        if hits and f"{name}.v{version}" not in NAMES_THINGS_PENDING:
            found[f"{name}.v{version}"] = hits
    assert checked >= 8
    assert found == {}, found


def test_v8_never_shows_a_runtime_type_id() -> None:
    """Since the builder knows its work (#245) an invented type reaches the decider. Its runtime
    id says nothing to the model and is no word of the inhabitant: without a name of its own it
    is a 'made thing' (prompt setting made_thing), in every line that names it."""
    data = json.loads(
        (EXAMPLES / "decision-request.hungry-berries.json").read_text(encoding="utf-8")
    )
    made = "inv_3fa2c19b7e04"
    first = data["perception"]["objects"][0]
    first["type"] = made
    data["inhabitant"]["knowledge"].append(
        {
            "subject": {"kind": "type", "id": made},
            "property": "crafted",
            "value": True,
            "confidence": 1.0,
            "source": "experienced",
            "told_by": None,
            "tick": 1,
        }
    )
    request = DecisionRequest.model_validate(data)
    new = _render(request, version=8)
    assert "inv_3fa2c19b7e04" not in new and "inv 3fa2c19b7e04" not in new
    assert "made thing" in new
    old = _render(request, version=7)
    assert "inv 3fa2c19b7e04" in old  # v7 as it was


def test_v8_shows_the_tone_only_with_made_up_things() -> None:
    """TL #21243: the tone examples come back, but with things that do not exist in the world."""
    system = _render(_request("hungry-berries"), version=8).split("=== user ===")[0]
    assert "Knubbel" in system and "Knolle" in system
    assert "never use these two words" in system


def test_v8_calls_an_invention_what_its_inhabitant_calls_it() -> None:
    """#251: the request carries the inhabitants' own name of an invention (label). v8 shows it in
    the player's language wherever the thing is named - thing, belief, habit, option - and never
    the runtime id."""
    text = _render(_request("own-invention"), version=8)
    user = text.split("=== user ===")[1]
    assert '- made thing [obj-0077] (you call it "Wasserschale"), 3.0 m away' in user
    assert "- thing 'Wasserschale' is crafted (sure 1.0; you tried it yourself)" in user
    assert "(thing 'Wasserschale')" in user  # the habit
    assert '-> made thing [obj-0077] ("Wasserschale")' in user  # the option
    assert "inv_" not in user and "inv 3fa" not in user
    english = DecisionRequest.model_validate(
        {
            **json.loads(
                (EXAMPLES / "decision-request.own-invention.json").read_text(encoding="utf-8")
            ),
            "display_locale": "en",
        }
    )
    assert '(you call it "water bowl")' in _render(english, version=8)


def test_v8_a_name_cannot_break_the_quotes_of_a_line() -> None:
    """Review GW3 #255: an invention's name is generated text; a quote or line break in it must
    not end the quotes of a belief line early."""
    data = json.loads(
        (EXAMPLES / "decision-request.own-invention.json").read_text(encoding="utf-8")
    )
    odd = {"de": 'Mira\'s "Schale"\n  gross', "en": "Mira's bowl"}
    for thing in data["perception"]["objects"]:
        if "label" in thing:
            thing["label"] = odd
    for entry in data["inhabitant"]["knowledge"]:
        if "label" in entry:
            entry["label"] = odd
    user = _render(DecisionRequest.model_validate(data), version=8).split("=== user ===")[1]
    assert "- thing 'Miras Schale gross' is crafted" in user
    assert '(you call it "Miras Schale gross")' in user


def _stuck(locale: str = "de") -> DecisionRequest:
    """The own-invention example as a stuck building (A1373 step 2, D850): the options the core
    leaves open and the building block."""
    data = json.loads((EXAMPLES / "decision-request.own-invention.json").read_text("utf-8"))
    data["display_locale"] = locale
    data["building"] = {
        "name": {"de": "Wasserschale", "en": "water bowl"},
        "missing_properties": ["container", "hard"],
        "escalation": 2,
        "max_escalations": 3,
    }
    data["options"] = [
        {"option_id": "o1", "action": "build_smaller", "target": None, "partners": []},
        {"option_id": "o2", "action": "give_up", "target": None, "partners": []},
    ]
    return DecisionRequest.model_validate(data)


def test_v9_tells_a_stuck_building_by_names_only() -> None:
    """D850: which invention (the player's language, one name per prompt), the missing properties
    by name - never their descriptions (D763 Z21) - and how often; nothing says which way to
    take."""
    user = _render(_stuck(), version=9).split("=== user ===")[1]
    assert "## Your building is stuck" in user
    assert 'You are building "Wasserschale".' in user
    assert "a material that is container and hard" in user
    assert "This is time 2 of 3" in user
    assert "Can hold other things" not in user and "Solid and hard to break" not in user
    assert "water bowl" not in user
    assert (
        "Stop building and leave the thing as far as it is built" in user
    )  # the option's own text
    english = _render(_stuck("en"), version=9).split("=== user ===")[1]
    assert 'You are building "water bowl".' in english
    v8 = _render(_stuck(), version=8)
    assert "Your building is stuck" not in v8  # v8 does not read the block


def test_v9_shows_no_building_block_without_one() -> None:
    assert "Your building is stuck" not in _render(_request("hungry-berries"), version=9)


def test_v9_names_a_need_from_an_invention() -> None:
    """#290: a need that came with an invention has no vocabulary entry - its English name."""
    data = json.loads((EXAMPLES / "decision-request.hungry-berries.json").read_text("utf-8"))
    data["inhabitant"]["needs"].append(
        {"id": "tidiness", "value": 0.4, "label": {"de": "Ordnungssinn", "en": "sense of order"}}
    )
    request = DecisionRequest.model_validate(data)
    assert "- sense of order: 0.40" in _render(request, version=9)
    assert "- tidiness: 0.40" in _render(request, version=8)  # v8 as it was


def _leisure(occasion: str | None = "leisure") -> DecisionRequest:
    """B2663: a free moment - the core names the occasion, the options are the usual ones."""
    data = json.loads((EXAMPLES / "decision-request.hungry-berries.json").read_text("utf-8"))
    if occasion is not None:
        data["occasion"] = occasion
    return DecisionRequest.model_validate(data)


def test_v10_says_a_free_moment_without_naming_an_option() -> None:
    """B2663/B2610: the block says the situation (and that talk may be light chatter), never
    which option to take (PR2); it sits before the options."""
    user = _render(_leisure(), version=10).split("=== user ===")[1]
    assert "## Right now" in user and "a free moment" in user
    assert user.index("## Right now") < user.index("## Options")
    block = user.split("## Right now")[1].split("## Options")[0]
    for option in _leisure().options:
        assert f"option {option.option_id}" not in block and option.option_id not in block.split()


def test_v10_is_v9_without_an_occasion_and_ignores_one_it_has_no_words_for() -> None:
    """An older core sends no occasion: the prompt is word for word v9 (only the name differs)."""
    plain = _render(_leisure(None), version=10).split("=== user ===")[1]
    assert plain == _render(_leisure(None), version=9).split("=== user ===")[1]
    assert _render(_leisure("festival"), version=10).split("=== user ===")[1] == plain
    assert "Right now" not in _render(_leisure(), version=9)  # v9 does not read it
