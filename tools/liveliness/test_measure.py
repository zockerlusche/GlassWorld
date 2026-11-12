"""Offline tests of the liveliness measurement (A1512)."""

from __future__ import annotations

import importlib.util
import sys
from collections import Counter
from pathlib import Path
from types import ModuleType


def _module() -> ModuleType:
    spec = importlib.util.spec_from_file_location(
        "liveliness_measure", Path(__file__).with_name("measure.py")
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module  # dataclasses look the module up by name
    spec.loader.exec_module(module)
    return module


LM = _module()

LOG = [
    "TICK 1",
    "EVENT tick=10 kind=action_finished inhabitant=inh-1 action=talk_to target=inh-2 detail=-",
    "EVENT tick=20 kind=action_finished inhabitant=inh-1 action=eat target=obj-1 detail=-",
    "EVENT tick=30 kind=action_finished inhabitant=inh-1 action=eat target=obj-1 detail=-",
    "EVENT tick=40 kind=knowledge_gained inhabitant=inh-1 action=- target=obj-1 detail=-",
    "EVENT tick=50 kind=thought inhabitant=inh-1 action=- target=- detail=-",
    "EVENT tick=60 kind=question_opened inhabitant=inh-1 action=- target=q-1 detail=-",
    "EVENT tick=70 kind=leisure_opened inhabitant=inh-1 action=- target=q-2 detail=-",
    "EVENT tick=1500 kind=action_finished inhabitant=inh-1 action=sleep target=- detail=-",
    "EVENT tick=1600 kind=invented inhabitant=inh-1 action=- target=inv_ab detail=-",
    "EVENT tick=0 kind=storm_started inhabitant=- action=- target=- detail=-",
]


def test_events_are_counted_per_day_and_inhabitant() -> None:
    days = LM.measure(LM.parse_events(LOG), 1440, {"talk_to"})
    first = days[(1, "inh-1")].row()
    assert first["actions"] == 3 and first["distinct_actions"] == 2
    assert first["contacts"] == 1 and first["discoveries"] == 1 and first["thoughts"] == 1
    assert first["questions_opened"] == 1
    assert first["free_moments"] == 1  # #313: a free moment is no question
    second = days[(2, "inh-1")].row()
    assert second["actions"] == 1 and second["making"] == 1
    assert all(who != "-" for _, who in days)  # world events belong to nobody


def test_spread_is_zero_for_one_kind_and_one_for_an_even_mix() -> None:
    assert LM.spread(Counter({"eat": 5})) == 0.0
    assert round(LM.spread(Counter({"eat": 2, "drink": 2, "sleep": 2})), 6) == 1.0


def test_the_data_say_which_actions_are_talks_and_how_long_a_day_is() -> None:
    social = LM.social_actions()
    assert "talk_to" in social and "eat" not in social
    assert LM.ticks_per_day() == 1440


def test_the_report_names_quiet_days_without_judging(tmp_path: Path) -> None:
    log = tmp_path / "lauf.log"
    log.write_text("\n".join(LOG) + "\n", encoding="utf-8")
    assert LM.main([str(log), "--out", str(tmp_path / "out"), "--quiet-below", "2"]) == 0
    text = (tmp_path / "out" / "report.md").read_text(encoding="utf-8")
    assert "Ruhige Tage: 1 von 2" in text and "Tag 2 inh-1" in text
    assert "| Kontakte |" in text
    assert (tmp_path / "out" / "liveliness.json").is_file()
    empty = tmp_path / "empty.log"
    empty.write_text("nothing\n", encoding="utf-8")
    assert LM.main([str(empty)]) == 1


def test_a_day_without_events_is_a_row_and_counts_as_quiet() -> None:
    """Review GW3 #305: an inhabitant with nothing finished on a day still gets that day."""
    log = [
        "EVENT tick=10 kind=action_finished inhabitant=inh-1 action=eat target=obj-1 detail=-",
        "EVENT tick=3000 kind=action_finished inhabitant=inh-2 action=eat target=obj-1 detail=-",
    ]
    days = LM.measure(LM.parse_events(log), 1440, set())
    assert set(days) == {
        (1, "inh-1"),
        (1, "inh-2"),
        (2, "inh-1"),
        (2, "inh-2"),
        (3, "inh-1"),
        (3, "inh-2"),
    }
    assert days[(2, "inh-1")].row()["actions"] == 0
