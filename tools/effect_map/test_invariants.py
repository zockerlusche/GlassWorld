"""A1808: the invariant checker finds what each measurable invariant forbids, and names what it cannot see."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
from types import ModuleType
from typing import Any


def _module() -> ModuleType:
    path = Path(__file__).with_name("invariants.py")
    spec = importlib.util.spec_from_file_location("invariants", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


INV = _module()
TPH = 60  # ticks per game hour (content/world/time.json)


def _snap(
    tick: int, people: list[dict[str, Any]], things: list[dict[str, Any]] | None = None
) -> dict[str, Any]:
    day, hour = tick // (24 * TPH) + 1, (tick // TPH) % 24
    return {
        "t": "snapshot",
        "tick": tick,
        "clock": f"{day}/{hour:02d}:00",
        "inhabitants": people,
        "things": things or [],
    }


def _who(action: str | None, **needs: float) -> dict[str, Any]:
    return {
        "id": "inh-000001",
        "at": [1.0, 2.0],
        "action": action,
        "needs": needs or {"hunger": 0.5},
    }


def _event(tick: int, kind: str, **fields: Any) -> dict[str, Any]:
    day, hour, minute = tick // (24 * TPH) + 1, (tick // TPH) % 24, tick % TPH
    return {
        "t": "event",
        "tick": tick,
        "clock": f"{day}/{hour:02d}:{minute:02d}",
        "kind": kind,
        "who": "inh-000001",
        **fields,
    }


def _trace(tmp: Path, lines: list[dict[str, Any]]) -> Path:
    folder = tmp / "trace"
    folder.mkdir()
    (folder / "day-0001.jsonl").write_text(
        "\n".join(json.dumps(x) for x in lines) + "\n", encoding="utf-8"
    )
    return tmp


def _status(folder: Path, ident: str) -> tuple[str, str]:
    data = json.loads(INV.INVARIANTS.read_text(encoding="utf-8"))
    result = next(r for r in INV.check(folder, data) if r[0] == ident)
    return result[1], result[3]


def test_a_value_out_of_range_fails_inv01(tmp_path: Path) -> None:
    folder = _trace(
        tmp_path, [_snap(0, [_who("eat", hunger=0.5)]), _snap(TPH, [_who("eat", hunger=1.2)])]
    )
    status, detail = _status(folder, "inv01")
    assert status == "FAIL" and "hunger=1.2" in detail


def test_the_same_failure_four_times_a_day_fails_inv06_but_a_wake_up_does_not_count(
    tmp_path: Path,
) -> None:
    fails = [
        _event(10 + i, "out_of_reach", action="pick_up", target="obj-1", detail="too_high")
        for i in range(4)
    ]
    woken = [
        _event(20 + i, "action_cancelled", action="sleep", detail="woken_rested") for i in range(9)
    ]
    status, detail = _status(_trace(tmp_path, [_snap(0, [_who("eat")]), *fails, *woken]), "inv06")
    assert status == "FAIL" and "4x pick_up on obj-1" in detail


def test_a_back_and_forth_within_an_hour_fails_inv08(tmp_path: Path) -> None:
    flips = [
        _event(i * 5, "action_started", action=a, target="obj-1")
        for i, a in enumerate(["eat", "drink", "eat", "drink"])
    ]
    status, detail = _status(_trace(tmp_path, [_snap(0, [_who("eat")]), *flips]), "inv08")
    assert status == "FAIL" and "A-B-A-B within an hour: 1" in detail


def test_too_little_sleep_over_three_whole_days_fails_inv12(tmp_path: Path) -> None:
    snaps = [_snap(h * TPH, [_who("sleep" if h % 24 < 2 else "eat")]) for h in range(24 * 5)]
    status, detail = _status(_trace(tmp_path, snaps), "inv12")
    assert status == "FAIL" and "mean 2.0 h" in detail


def test_two_hours_without_an_action_fail_inv14_one_snapshot_does_not(tmp_path: Path) -> None:
    (tmp_path / "one").mkdir()
    (tmp_path / "two").mkdir()
    one = _trace(tmp_path / "one", [_snap(0, [_who(None)]), _snap(TPH, [_who("eat")])])
    two = _trace(tmp_path / "two", [_snap(0, [_who(None)]), _snap(TPH, [_who(None)])])
    assert _status(one, "inv14")[0] == "ok"
    assert _status(two, "inv14")[0] == "FAIL"


def test_a_new_kind_of_thing_without_an_event_fails_inv15(tmp_path: Path) -> None:
    things = [{"id": "obj-9", "type": "inv_abc", "at": [0, 0]}]
    status, detail = _status(_trace(tmp_path, [_snap(0, [_who("eat")], things)]), "inv15")
    assert status == "FAIL" and "inv_abc" in detail


def test_an_invariant_the_trace_cannot_show_is_named_not_failed(tmp_path: Path) -> None:
    status, detail = _status(_trace(tmp_path, [_snap(0, [_who("eat")])]), "inv07")
    assert status == "n/a" and "locks" in detail


def test_every_one_of_the_twenty_invariants_is_listed() -> None:
    data = json.loads(INV.INVARIANTS.read_text(encoding="utf-8"))
    assert [i["id"] for i in data["invariants"]] == [f"inv{n:02d}" for n in range(1, 21)]
    assert all(i["kind"] in INV.CHECKS or i["kind"] == "not_measurable" for i in data["invariants"])


def test_a_run_too_short_for_the_sleep_mean_is_na_not_ok(tmp_path: Path) -> None:
    """Review GW4 #540: not checked is not 'held'."""
    status, detail = _status(_trace(tmp_path, [_snap(0, [_who("sleep")])]), "inv12")
    assert status == "n/a" and "not checked" in detail


def _soul(
    action: str, mood: float = 0.1, will: float = 0.8, traits: float = 0.5, **needs: float
) -> dict[str, Any]:
    """An inhabitant line with moods, traits and the will to live (GW4 #554)."""
    who = _who(action, **(needs or {"hunger": 0.8, "thirst": 0.8, "sleep": 0.8, "warmth": 0.8}))
    who |= {
        "moods": {"frustration": mood, "joy": 0.4},
        "traits": {"patience": traits},
        "will_to_live": will,
    }
    return who


def test_a_feeling_above_the_mark_for_hours_fails_inv05_and_an_old_trace_is_na(
    tmp_path: Path,
) -> None:
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    long = _trace(tmp_path / "a", [_snap(h * TPH, [_soul("eat", mood=0.7)]) for h in range(6)])
    status, detail = _status(long, "inv05")
    assert status == "FAIL" and "5 h (frustration" in detail
    assert _status(_trace(tmp_path / "b", [_snap(0, [_who("eat")])]), "inv05")[0] == "n/a"


def test_frustration_rising_from_cold_alone_fails_inv13_with_hunger_or_a_jump_it_holds(
    tmp_path: Path,
) -> None:
    for name in ("alone", "hungry", "jump"):
        (tmp_path / name).mkdir()
    cold = {
        "hunger": 0.8,
        "thirst": 0.8,
        "sleep": 0.8,
        "warmth": 0.3,
    }  # at the comfort floor = cold (review GW4)

    def run(folder: str, start: float, end: float, needs: dict[str, float]) -> str:
        lines = [_snap(0, [_soul("eat", start, **needs)]), _snap(TPH, [_soul("eat", end, **needs)])]
        return _status(_trace(tmp_path / folder, lines), "inv13")[0]

    assert run("alone", 0.10, 0.18, cold) == "FAIL"
    assert run("hungry", 0.10, 0.18, cold | {"hunger": 0.2}) == "ok"
    assert (
        run("jump", 0.10, 0.30, cold) == "ok"
    )  # +0.2 in an hour is an event (a building given up ...)


def test_love_may_stay_high_for_long(tmp_path: Path) -> None:
    lines = []
    for h in range(8):
        who = _soul("eat")
        who["moods"] = {"love": 0.9, "joy": 0.4}
        lines.append(_snap(h * TPH, [who]))
    assert _status(_trace(tmp_path, lines), "inv05")[0] == "ok"


def test_a_changed_character_fails_inv18(tmp_path: Path) -> None:
    folder = _trace(
        tmp_path, [_snap(0, [_soul("eat", traits=0.5)]), _snap(TPH, [_soul("eat", traits=0.6)])]
    )
    status, detail = _status(folder, "inv18")
    assert status == "FAIL" and "inh-000001" in detail


def test_the_will_changing_at_midday_fails_inv19(tmp_path: Path) -> None:
    noon = 12 * TPH
    folder = _trace(
        tmp_path,
        [_snap(noon, [_soul("eat", will=0.8)]), _snap(noon + TPH, [_soul("eat", will=0.79)])],
    )
    status, detail = _status(folder, "inv19")
    assert status == "FAIL" and "changes by day: 1" in detail
