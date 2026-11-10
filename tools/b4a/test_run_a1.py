"""B4a A1 judging (A1403): learned by trying, then heard by another inhabitant."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from types import ModuleType


def _module() -> ModuleType:
    spec = importlib.util.spec_from_file_location("run_a1", Path(__file__).with_name("run_a1.py"))
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


A1 = _module()
BERRIES = {"red_berry", "blue_berry"}


def _event(tick: int, who: str, target: str, detail: str) -> str:
    return (
        f"EVENT tick={tick} kind=knowledge_gained inhabitant={who} action=eat "
        f"target={target} detail={detail}"
    )


def test_learned_and_passed_on_counts() -> None:
    lines = [
        "day 1 ...",
        _event(10, "inh-1", "red_berry", "poisonous/experienced"),
        _event(40, "inh-2", "red_berry", "poisonous/heard"),
    ]
    finding = A1.judge(A1.parse_events(lines), BERRIES)
    assert finding.ok
    assert len(finding.learned) == 1


def test_heard_before_anyone_tried_does_not_count() -> None:
    lines = [
        _event(5, "inh-2", "red_berry", "poisonous/heard"),
        _event(10, "inh-1", "red_berry", "poisonous/experienced"),
    ]
    assert not A1.judge(A1.parse_events(lines), BERRIES).ok


def test_the_learner_hearing_it_back_does_not_count() -> None:
    lines = [
        _event(10, "inh-1", "blue_berry", "edible/experienced"),
        _event(20, "inh-1", "blue_berry", "edible/heard"),
    ]
    assert not A1.judge(A1.parse_events(lines), BERRIES).ok


def test_other_things_and_properties_do_not_count() -> None:
    lines = [
        _event(10, "inh-1", "sea_water", "salty/experienced"),
        _event(20, "inh-2", "sea_water", "salty/heard"),
        _event(30, "inh-1", "red_berry", "drinkable/experienced"),
        _event(40, "inh-2", "red_berry", "drinkable/heard"),
        _event(50, "inh-1", "blue_berry", "edible/experienced"),
        _event(60, "inh-2", "red_berry", "edible/heard"),  # another berry
    ]
    assert not A1.judge(A1.parse_events(lines), BERRIES).ok


def test_the_berries_come_from_the_catalog() -> None:
    assert A1.subjects_from_catalog(None) == BERRIES


def test_the_report_counts_runs_and_states_the_goal() -> None:
    ok = A1.judge(
        A1.parse_events(
            [
                _event(10, "inh-1", "red_berry", "edible/experienced"),
                _event(20, "inh-2", "red_berry", "edible/heard"),
            ]
        ),
        BERRIES,
    )
    empty = A1.Finding()
    text = A1.report(1, 3, "live", [(1, 0, ok), (2, 0, empty), (3, 4, ok)])
    assert "1 von 3 Laeufen" in text  # run 3 never ran (exit 4) and does not count
    assert "nicht erfuellt" in text
