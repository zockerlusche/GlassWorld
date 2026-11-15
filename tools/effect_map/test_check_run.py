"""A1720 part 2: the run check finds long neglect, a flat warmth in winter, sleep while rested."""

from __future__ import annotations

import importlib.util
import json
from collections.abc import Callable
from pathlib import Path
from types import ModuleType


def _module() -> ModuleType:
    path = Path(__file__).with_name("check_run.py")
    spec = importlib.util.spec_from_file_location("check_run", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _trace(
    folder: Path,
    days: int,
    needs: Callable[[int, int], dict[str, float]],
    action: Callable[[int, int], str],
) -> Path:
    trace = folder / "trace"
    trace.mkdir(parents=True)
    for day in range(1, days + 1):
        with (trace / f"day-{day:04d}.jsonl").open("w", encoding="utf-8") as handle:
            for hour in range(24):
                line = {
                    "t": "snapshot",
                    "tick": (day * 24 + hour) * 60,
                    "clock": f"{day}/{hour:02d}:00",
                    "inhabitants": [
                        {"id": "inh-1", "action": action(day, hour), "needs": needs(day, hour)}
                    ],
                }
                handle.write(json.dumps(line) + "\n")
    return folder


def _results(module: ModuleType, folder: Path) -> dict[str, bool]:
    expectations = json.loads(module.EXPECTATIONS.read_text(encoding="utf-8"))
    return {name: ok for name, ok, _ in module.check(folder, expectations)}


def test_a_plausible_run_passes_the_trace_checks(tmp_path: Path) -> None:
    module = _module()

    def needs(day: int, hour: int) -> dict[str, float]:
        swing = 0.5 + 0.4 * ((hour % 12) / 12)
        return {
            "hunger": swing,
            "thirst": swing,
            "sleep": swing,
            "warmth": swing,
            "safety": swing,
            "company": swing,
            "fun": swing,
            "purpose": swing,
        }

    folder = _trace(tmp_path, 28, needs, lambda d, h: "sleep" if h < 6 else "wander")
    results = _results(module, folder)
    for name in (
        "no_long_neglect",
        "warmth_felt_in_winter",
        "little_sleep_rested",
        "decaying_needs_move",
    ):
        assert results[name], name


def test_flat_warmth_long_neglect_and_rested_sleep_are_found(tmp_path: Path) -> None:
    module = _module()
    folder = _trace(
        tmp_path,
        28,
        lambda d, h: {"hunger": 0.05, "warmth": 1.0, "sleep": 1.0, "thirst": 0.9},
        lambda d, h: "sleep",
    )
    results = _results(module, folder)
    assert not results["no_long_neglect"]
    assert not results["warmth_felt_in_winter"]
    assert not results["little_sleep_rested"]
    assert not results["decaying_needs_move"]


def test_the_mood_driver_rule_holds_on_the_data() -> None:
    module = _module()
    results = {
        name: ok
        for name, ok, _ in module.check(
            None, json.loads(module.EXPECTATIONS.read_text(encoding="utf-8"))
        )
    }
    assert results["mood_drivers_move_their_mood"]


def test_a_missing_day_ends_a_neglect_streak(tmp_path: Path) -> None:
    """Review GW3 #461: a gap in the trace is no streak - two neglected halves around a missing day file."""
    module = _module()
    folder = _trace(tmp_path, 3, lambda d, h: {"hunger": 0.05}, lambda d, h: "wander")
    (folder / "trace" / "day-0002.jsonl").unlink()
    run = module.Run(folder)
    expectation = {"kind": "max_hours_below_neglect", "hours": 30}
    model = json.loads((module.CONTENT / "inhabitants" / "model.json").read_text(encoding="utf-8"))
    ok, text = module.check_trace(run, expectation, model)
    assert ok, text


def test_resting_through_the_night_counts_only_by_day(tmp_path: Path) -> None:
    """B2780: rested sleep at night is accepted; the same by day is found."""
    module = _module()
    night = _trace(
        tmp_path / "night",
        2,
        lambda d, h: {"sleep": 1.0},
        lambda d, h: "sleep" if h < 5 else "wander",
    )
    assert _results(module, night)["little_sleep_rested"]
    day = _trace(
        tmp_path / "day",
        2,
        lambda d, h: {"sleep": 1.0},
        lambda d, h: "sleep" if 10 <= h < 16 else "wander",
    )
    assert not _results(module, day)["little_sleep_rested"]
