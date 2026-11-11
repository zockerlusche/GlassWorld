"""The benchmark measurement tool (B2-7): evaluation, report and guards (no game needed)."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from types import ModuleType


def _module() -> ModuleType:
    path = Path(__file__).with_name("measure.py")
    spec = importlib.util.spec_from_file_location("bench_measure", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


BM = _module()
MARKS = BM.Marks(60.0, 16.7, ("day", "night"))


def _csv(path: Path, frames: list[float]) -> None:
    rows = ["pass,time_s,frame_ms"] + [f"day,{i * 0.01:.2f},{ms}" for i, ms in enumerate(frames)]
    path.write_text("\n".join(rows) + "\n", encoding="utf-8")


def test_the_marks_come_from_the_benchmark_data() -> None:
    marks = BM.load_marks()
    assert marks.mean_fps > 0 and marks.p95_ms > 0 and "day" in marks.passes


def test_percentile_is_nearest_rank() -> None:
    assert BM.percentile([float(v) for v in range(1, 101)], 0.95) == 95.0
    assert BM.percentile([5.0], 0.95) == 5.0


def test_a_pass_is_evaluated_from_its_frame_times(tmp_path: Path) -> None:
    _csv(tmp_path / "bench_800x600_day.csv", [10.0] * 95 + [20.0] * 5)
    result = BM.evaluate_csv(tmp_path / "bench_800x600_day.csv", "800x600", "day", MARKS)
    assert result.frames == 100
    assert round(result.mean_fps, 1) == 95.2  # 100 frames in 1050 ms
    assert result.p95_ms == 10.0 and result.max_ms == 20.0 and result.ok
    _csv(tmp_path / "slow.csv", [10.0] * 90 + [20.0] * 10)
    assert not BM.evaluate_csv(tmp_path / "slow.csv", "800x600", "day", MARKS).ok  # p95 20 ms


def test_missing_passes_are_named_and_the_report_says_so(tmp_path: Path) -> None:
    _csv(tmp_path / "bench_800x600_day.csv", [10.0] * 10)
    (tmp_path / "vram_800x600.csv").write_text(
        "time_s,used_mib,total_mib\n0.0,1500,10240\n1.0,4200,10240\n", encoding="utf-8"
    )
    results, missing = BM.evaluate(tmp_path, ["800x600"], MARKS)
    assert [r.name for r in results] == ["day"] and missing == ["800x600 night"]
    peaks = BM.vram_peaks(tmp_path, ["800x600"])
    assert peaks == {"800x600": (4200, 10240)}
    text = BM.report(results, missing, peaks, MARKS, "Build abc")
    assert "| 800x600 | day | 10 | 100.0 | 10.0 | 10.0 | erfüllt |" in text
    assert "**Ohne Messwerte:** 800x600 night." in text
    assert "| 800x600 | 4200 | 10240 |" in text and "1 von 1 Durchgängen" in text


def test_a_run_needs_the_screen_queue_a_binary_and_a_folder_outside_the_repository(
    tmp_path: Path,
) -> None:
    binary = tmp_path / "GlassWorld.exe"
    binary.write_text("x", encoding="utf-8")
    assert "inside the repository" in BM.guard(BM.ROOT / "out", True, False, binary)
    assert "screen-queue" in BM.guard(tmp_path / "out", False, False, binary)
    assert "not found" in BM.guard(tmp_path / "out", True, False, tmp_path / "none.exe")
    foreign = tmp_path / "foreign"
    foreign.mkdir()
    (foreign / "notes.txt").write_text("x", encoding="utf-8")
    assert "not a folder of this tool" in BM.guard(foreign, True, False, binary)
    assert BM.guard(tmp_path / "new", True, False, binary) is None
    assert BM.guard(foreign, False, True, None) is None  # evaluating CSVs needs no screen
    assert "does not exist" in BM.guard(tmp_path / "nowhere", False, True, None)


def test_an_earlier_run_is_kept_under_a_new_name(tmp_path: Path) -> None:
    out = tmp_path / "b"
    out.mkdir()
    assert BM.archive_name(out) == tmp_path / "b.alt-1"
