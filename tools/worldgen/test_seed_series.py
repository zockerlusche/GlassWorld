"""The seed series tool (B2 acceptance): judging canned `worldgen --json` lines (no game needed)."""

from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from pathlib import Path
from types import ModuleType
from typing import Any

import pytest


def _module() -> ModuleType:
    path = Path(__file__).with_name("seed_series.py")
    spec = importlib.util.spec_from_file_location("worldgen_seed_series", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


SS = _module()
BIOMES = ("beach_sea", "meadow", "field", "forest", "freshwater", "rocks_hills", "swamp")
LINES = (
    ("food", False),
    ("drinking_water", False),
    ("spark_striker", False),
    ("report/start_supply/food", True),
    ("start_supply/drink", False),
    ("start_dry_ground", False),
)
BUDGET = 10000


def island(seed: int, **change: Any) -> dict[str, Any]:
    """A sound island as `worldgen --json` prints it (the fields the series reads), then changed."""
    made: dict[str, Any] = {
        "seed": seed,
        "preset": "start_island",
        "generator_version": 5,
        "hash": f"{seed:064x}",
        "passed": True,
        "attempts": 1,
        "time_ms": 800 + seed,
        "biome_cells": {b: 100 + seed for b in BIOMES},
        "check": [
            {
                "id": line_id,
                "reachable": 4,
                "total": 4,
                "min": 1,
                "max": 100,
                "passed": True,
                "report_only": report_only,
            }
            for line_id, report_only in LINES
        ],
        "cliff_cells": 140,
        "hydrology": {"stream_cells": 400, "mouths": 20, "lakes": 3, "stream_dead_ends": 0},
        "faces": {"caves": 2, "caves_reachable": 1},
    }
    for key, value in change.items():
        if isinstance(value, dict) and isinstance(made.get(key), dict):
            made[key] = {**made[key], **value}
        else:
            made[key] = value
    return made


def failing(seed: int, line_id: str) -> dict[str, Any]:
    """An island that used up its attempts because one line of the check failed."""
    made = island(seed, passed=False, attempts=8)
    for line in made["check"]:
        if line["id"] == line_id:
            line.update(passed=False, reachable=0, total=0)
    return made


def judge(reports: list[dict[str, Any]], expected: tuple[int, int] | None = None) -> dict[str, Any]:
    seeds = [int(r["seed"]) for r in reports]
    first = min(seeds, default=1)
    return SS.evaluate(reports, expected or (first, max(seeds)), BUDGET, [first])


def holds(figures: dict[str, Any]) -> dict[str, bool | None]:
    return {v["id"]: v["holds"] for v in figures["verdicts"]}


def test_a_sound_series_holds_every_verdict() -> None:
    figures = judge([island(s) for s in range(1, 11)])
    assert figures["all_hold"]
    assert set(holds(figures).values()) == {True}
    assert set(holds(figures)) == {
        "mandatory_check",
        "check_lines",
        "distinct_worlds",
        "biomes",
        "cliff_and_cave",
        "streams",
        "time",
    }
    assert figures["seeds"] == {"from": 1, "to": 10, "expected": 10, "reported": 10, "missing": []}
    assert (figures["passed"], figures["failed_seeds"]) == (10, [])
    assert figures["attempts"] == {"1": 10}
    assert figures["distinct_hashes"] == 10
    assert [(line["id"], line["passed"]) for line in figures["check_lines"]] == [
        (line_id, 10) for line_id, _ in LINES
    ]
    assert figures["time_ms"] == {
        "budget_ms": BUDGET,
        "over_budget_seeds": [],
        "p50_ms": 805,
        "p95_ms": 810,
        "max_ms": 810,
        "max_seed": 10,
    }
    assert figures["warm_up_seeds"] == [1]
    assert (figures["generator_versions"], figures["presets"]) == ([5], ["start_island"])


def test_an_island_without_flint_fails_the_series_and_names_the_line() -> None:
    figures = judge([island(1), failing(2, "spark_striker"), island(3, attempts=3)])
    assert holds(figures)["mandatory_check"] is False
    assert holds(figures)["check_lines"] is False
    assert not figures["all_hold"]
    assert (figures["passed"], figures["failed_seeds"]) == (2, [2])
    assert figures["attempts"] == {"1": 1, "3": 1, "8": 1}
    line = next(line for line in figures["check_lines"] if line["id"] == "spark_striker")
    assert (line["passed"], line["failed_seeds"], line["report_only"]) == (2, [2], False)
    text = SS.report(figures, {})
    verdict = "| Jede Startinsel besteht die Pflichtprüfung | NICHT erfüllt | 2 von 3 bestanden |"
    assert verdict in text
    assert "| spark_striker | 2 von 3 | 2 |" in text
    assert "| 8 | 1 |" in text


def test_a_report_only_line_is_counted_but_decides_nothing() -> None:
    short = island(2)
    short["check"][3]["passed"] = False
    figures = judge([island(1), short])
    assert figures["all_hold"]
    line = next(line for line in figures["check_lines"] if line["id"] == "report/start_supply/food")
    assert (line["passed"], line["failed_seeds"], line["report_only"]) == (1, [2], True)
    assert "| report/start_supply/food (nur Bericht) | 1 von 2 | 2 |" in SS.report(figures, {})


def test_a_missing_check_line_counts_as_not_passed() -> None:
    old = island(2)
    old["check"] = [line for line in old["check"] if line["id"] != "start_dry_ground"]
    figures = judge([island(1), old])
    assert holds(figures)["check_lines"] is False
    line = next(line for line in figures["check_lines"] if line["id"] == "start_dry_ground")
    assert line["failed_seeds"] == [2]

    # A series whose islands never had the line (generator before version 5) does not hold either.
    first = island(1)
    first["check"] = [line for line in first["check"] if line["id"] != "start_dry_ground"]
    assert holds(judge([first, old]))["check_lines"] is False


@pytest.mark.parametrize(
    ("change", "verdict", "where"),
    [
        ({"biome_cells": {"swamp": 0}}, "biomes", ("biomes", "seeds_missing_any")),
        ({"cliff_cells": 0}, "cliff_and_cave", ("cliffs", "seeds_without")),
        ({"faces": {"caves": 0}}, "cliff_and_cave", ("caves", "seeds_without")),
        ({"hydrology": {"stream_cells": 0}}, "streams", ("streams", "seeds_without")),
        ({"hydrology": {"stream_dead_ends": 3}}, "streams", ("streams", "seeds_with_dead_ends")),
        ({"time_ms": BUDGET + 1}, "time", ("time_ms", "over_budget_seeds")),
    ],
)
def test_one_island_off_fails_exactly_its_verdict(
    change: dict[str, Any], verdict: str, where: tuple[str, str]
) -> None:
    figures = judge([island(1), island(2, **change), island(3)])
    assert [key for key, value in holds(figures).items() if value is not True] == [verdict]
    assert holds(figures)[verdict] is False
    assert figures[where[0]][where[1]] == [2]
    assert not figures["all_hold"]


def test_a_missing_biome_is_listed_by_biome() -> None:
    figures = judge([island(1, biome_cells={"swamp": 0}), island(2, biome_cells={"field": 0})])
    assert figures["biomes"]["missing_seeds"]["swamp"] == [1]
    assert figures["biomes"]["missing_seeds"]["field"] == [2]
    assert figures["biomes"]["missing_seeds"]["meadow"] == []
    assert figures["biomes"]["seeds_missing_any"] == [1, 2]
    text = SS.report(figures, {})
    assert "| swamp | 1 | 1 |" in text
    assert "| Jedes Biom der Tabelle kommt auf jeder Insel vor | NICHT erfüllt |" in text


def test_a_cave_out_of_reach_is_reported_but_the_island_has_its_cave() -> None:
    figures = judge([island(1), island(2, faces={"caves_reachable": 0})])
    assert holds(figures)["cliff_and_cave"] is True
    assert figures["caves"] == {"seeds_without": [], "seeds_without_reachable": [2]}


def test_the_same_hash_twice_is_no_series_of_different_worlds() -> None:
    figures = judge([island(1), island(2, hash=f"{1:064x}"), island(3)])
    assert holds(figures)["distinct_worlds"] is False
    assert (figures["distinct_hashes"], figures["repeated_hashes"]) == (2, [f"{1:064x}"])


def test_without_the_dead_end_figure_the_streams_are_not_judged() -> None:
    old = island(1)
    del old["hydrology"]["stream_dead_ends"]
    figures = judge([old, island(2)])
    assert holds(figures)["streams"] is None
    assert not figures["all_hold"]
    assert "nicht gemessen" in SS.report(figures, {})


def test_a_series_with_missing_seeds_is_judged_nowhere() -> None:
    figures = judge([island(1), island(2), island(4)], (1, 5))
    assert figures["seeds"]["missing"] == [3, 5]
    assert set(holds(figures).values()) == {None}
    assert not figures["all_hold"]
    assert "**Unvollständig: es fehlen die Seeds 3, 5.**" in SS.report(figures, {})
    assert set(holds(judge([], (1, 2))).values()) == {None}


def test_percentiles_are_nearest_rank() -> None:
    values = list(range(1, 101))
    assert (SS.percentile(values, 0.5), SS.percentile(values, 0.95)) == (50, 95)
    assert (SS.percentile([7], 0.5), SS.percentile([7], 0.95)) == (7, 7)
    assert SS.percentile([30, 10, 20], 0.95) == 30


def test_ranges_and_their_parts() -> None:
    assert SS.parse_range("1-1000") == (1, 1000)
    assert SS.parse_range("7-7") == (7, 7)
    for bad in ("7", "8-7", "a-b", "1-2-3", "-3", ""):
        with pytest.raises(argparse.ArgumentTypeError):
            SS.parse_range(bad)
    assert SS.chunks(1, 1000, 1) == [(1, 1000)]
    assert SS.chunks(1, 10, 3) == [(1, 4), (5, 7), (8, 10)]
    assert SS.chunks(5, 6, 8) == [(5, 5), (6, 6)]


def test_long_seed_lists_are_cut_in_the_report() -> None:
    assert SS.listed([]) == "-"
    assert SS.listed([3, 4]) == "3, 4"
    assert SS.listed(list(range(1, 26))).endswith("19, 20 und 5 weitere")


def _folder(tmp_path: Path, reports: list[dict[str, Any]], run: dict[str, Any]) -> Path:
    out = tmp_path / "series"
    out.mkdir()
    lines = "".join(json.dumps(r) + "\n" for r in reports)
    (out / SS.RAW).write_text(lines, encoding="utf-8")
    (out / SS.FIGURES).write_text(json.dumps({"run": run}), encoding="utf-8")
    return out


def test_evaluate_only_judges_the_raw_lines_again(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    run = {"seeds": [1, 3], "warm_up_seeds": [1], "note": "main abc1234", "seconds": 4.2, "jobs": 1}
    out = _folder(tmp_path, [island(s) for s in (1, 2, 3)], run)
    assert SS.main(["--out", str(out), "--evaluate-only"]) == SS.EXIT_OK
    figures = json.loads((out / SS.FIGURES).read_text("utf-8"))
    assert figures["all_hold"] and figures["run"] == run
    text = (out / SS.REPORT).read_text("utf-8")
    assert "- Seeds: 1 bis 3 (3 von 3 gemeldet)" in text
    assert "- Stand: main abc1234" in text
    assert "generation_budget_ms): 10000" in text
    assert "SEED_SERIES mandatory_check: holds" in capsys.readouterr().out


def test_evaluate_only_tells_a_failed_verdict_and_a_missing_seed_apart(tmp_path: Path) -> None:
    run = {"seeds": [1, 3], "warm_up_seeds": [1]}
    missed = _folder(tmp_path, [island(1), failing(2, "spark_striker"), island(3)], run)
    assert SS.main(["--out", str(missed), "--evaluate-only"]) == SS.EXIT_MISSED
    (missed / SS.RAW).write_text(json.dumps(island(1)) + "\n", encoding="utf-8")
    assert SS.main(["--out", str(missed), "--evaluate-only"]) == SS.EXIT_FAILED


def test_the_output_folder_must_be_outside_the_repository_and_exist_for_judging(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    inside = SS.ROOT / "tools" / "worldgen" / "series-out"
    assert SS.main(["--out", str(inside), "--evaluate-only"]) == SS.EXIT_FAILED
    assert not inside.exists()
    assert "outside the repository" in capsys.readouterr().err
    assert SS.main(["--out", str(tmp_path / "nothing"), "--evaluate-only"]) == SS.EXIT_FAILED


class _FakeWorldgen:
    """Stands in for `dotnet <dll> worldgen --seeds A-B --json`: prints one line per seed."""

    codes: dict[int, int]
    commands: list[list[str]]

    def __init__(self, command: list[str], **_: Any) -> None:
        type(self).commands.append(command)
        first, last = (int(n) for n in command[command.index("--seeds") + 1].split("-"))
        self.first = first
        self.stdout = iter([json.dumps(island(s)) + "\n" for s in range(first, last + 1)])
        self.stderr = self

    def __enter__(self) -> _FakeWorldgen:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def read(self) -> str:
        return "boom"

    def wait(self) -> int:
        return type(self).codes.get(self.first, 0)


def _dotnet(_: str) -> str:
    return "dotnet"


def _run(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, codes: dict[int, int]
) -> tuple[int, Path]:
    assembly = tmp_path / SS.ASSEMBLY
    assembly.write_text("", encoding="utf-8")
    _FakeWorldgen.codes, _FakeWorldgen.commands = codes, []
    monkeypatch.setattr(SS.subprocess, "Popen", _FakeWorldgen)
    monkeypatch.setattr(SS.shutil, "which", _dotnet)
    out = tmp_path / "out"
    arguments = ["--out", str(out), "--seeds", "1-6", "--jobs", "2", "--assembly", str(assembly)]
    return SS.main([*arguments, "--note", "test"]), out


def test_a_run_splits_the_range_keeps_the_raw_lines_and_an_earlier_report(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # Exit code 2 of worldgen only says that an island failed its check; the lines carry that.
    code, out = _run(tmp_path, monkeypatch, {1: 2})
    assert code == SS.EXIT_OK
    ranges = sorted(c[c.index("--seeds") + 1] for c in _FakeWorldgen.commands)
    assert ranges == ["1-3", "4-6"]
    program = ["dotnet", str((tmp_path / SS.ASSEMBLY).resolve()), "worldgen"]
    assert all(c[:3] == program for c in _FakeWorldgen.commands)
    assert all("--json" in c and "--preset" in c for c in _FakeWorldgen.commands)
    raw = (out / SS.RAW).read_text("utf-8").splitlines()
    assert sorted(json.loads(line)["seed"] for line in raw) == [1, 2, 3, 4, 5, 6]
    figures = json.loads((out / SS.FIGURES).read_text("utf-8"))
    assert figures["run"]["warm_up_seeds"] == [1, 4]
    assert (figures["run"]["jobs"], figures["run"]["note"]) == (2, "test")
    assert "Erster Seed je Prozess" in (out / SS.REPORT).read_text("utf-8")

    # A second run keeps the first one's files under a new name.
    assert _run(tmp_path, monkeypatch, {})[0] == SS.EXIT_OK
    kept = sorted(p.name for p in out.iterdir() if ".before-" in p.name)
    assert [name.split(".before-")[0] for name in kept] == ["seed_series"] * 3
    assert {Path(name).suffix for name in kept} == {".json", ".jsonl", ".md"}


def test_a_crashed_process_fails_the_run(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    code, _ = _run(tmp_path, monkeypatch, {4: 1})
    assert code == SS.EXIT_FAILED
    assert "seeds 4-6: worldgen ended with 1: boom" in capsys.readouterr().err


def test_without_a_build_the_tool_says_how_to_build(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    arguments = ["--out", str(tmp_path / "out"), "--assembly", str(tmp_path / "missing.dll")]
    assert SS.main(arguments) == SS.EXIT_FAILED
    assert "dotnet build src/Simulation.Headless -c Release -m:1" in capsys.readouterr().err
    assert not (tmp_path / "out").exists()
