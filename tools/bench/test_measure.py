"""The benchmark measurement tool (B2-7): judging, report, sampler and guards (no game needed)."""

from __future__ import annotations

import importlib.util
import json
import sys
import time
from pathlib import Path
from types import ModuleType

import pytest


def _module() -> ModuleType:
    path = Path(__file__).with_name("measure.py")
    spec = importlib.util.spec_from_file_location("bench_measure", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


BM = _module()
MARKS = BM.Marks(60.0, 16.7, 120.0, 100, 2560, ("day", "night"), 10.0, "7", "high", "1000")
RES = "800x600"


ENV = (
    "BENCH_ENV size=800x600 mode=Windowed vsync=Disabled max_fps=0 adapter=Karte driver=d_1 "
    "api=1.3 build=abc1234\n"
)


def _pass(
    out: Path,
    name: str,
    frames: list[float],
    *,
    size: str = RES,
    duration: str = "120",
    base: int = 1500,
    peak: int = 3000,
    env: str = ENV,
    warmup: str = "10",
    quality: str = "high",
    run_samples: int = 70,
    last_sample_s: int = 200,
    figures: str = "104",
    revealed: str = "1000",
) -> None:
    """A complete, good pass: CSV, log with the game's own lines, memory samples."""
    rows = ["pass,time_s,frame_ms"] + [f"{name},{i * 0.01:.2f},{ms}" for i, ms in enumerate(frames)]
    (out / f"bench_{RES}_{name}.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")
    mean = 1000.0 * len(frames) / sum(frames)
    p95 = BM.percentile(frames, 0.95)
    (out / f"bench_{RES}_{name}.log").write_text(
        f"BENCH_START seed=7 quality={quality} passes={name} warmup_s={warmup} "
        f"duration_s={duration} "
        f"figures={figures} revealed_permille={revealed} size=640x480 csv=x\n{env}"
        f"BENCH_PASS pass={name} frames={len(frames)} mean_fps={mean:.2f} p95_ms={p95:.3f} "
        f"max_ms={max(frames)} ok=yes size={size} warmup_frames=600\n",
        encoding="utf-8",
    )
    rows = [
        "time_s,phase,used_mib,total_mib",
        f"0.0,baseline,{base},10240",
        f"1.0,baseline,{base},10240",
    ]
    rows += [f"{5 + i}.0,run,{base + 100},10240" for i in range(min(run_samples - 1, 80))]
    rows += [f"{last_sample_s}.0,run,{peak},10240", f"{last_sample_s + 1}.0,run,,"]
    (out / f"vram_{RES}_{name}.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")


GOOD = [10.0] * 95 + [20.0] * 5


def test_the_marks_and_the_memory_budget_come_from_the_data() -> None:
    marks = BM.load_marks()
    assert marks.mean_fps > 0 and marks.p95_ms > 0 and marks.vram_mib > 0 and marks.duration_s > 0
    assert "day" in marks.passes and marks.figures > 0


def test_percentile_is_nearest_rank() -> None:
    assert BM.percentile([float(v) for v in range(1, 101)], 0.95) == 95.0
    assert BM.percentile([5.0], 0.95) == 5.0


def test_a_good_pass_is_judged_from_csv_log_and_memory(tmp_path: Path) -> None:
    _pass(tmp_path, "day", GOOD)
    r = BM.judge(tmp_path, RES, "window", "day", MARKS, 0)
    assert r.frames == 100 and round(r.mean_fps, 1) == 95.2  # 100 frames in 1050 ms
    assert r.p95_ms == 10.0 and r.max_ms == 20.0
    assert (r.baseline_mib, r.peak_mib, r.game_mib, r.samples, r.sample_errors) == (
        1500,
        3000,
        1500,
        70,
        1,
    )
    assert r.size == RES and r.quality == "high" and r.verdict == "erfüllt", r.invalid
    assert (r.window, r.vsync, r.build, r.warmup_frames) == (
        "Windowed",
        "Disabled",
        "abc1234",
        "600",
    )


def test_missed_marks_and_budget_are_missed_not_invalid(tmp_path: Path) -> None:
    _pass(tmp_path, "day", [10.0] * 90 + [20.0] * 10, peak=1500 + 2561)
    r = BM.judge(tmp_path, RES, "window", "day", MARKS, 0)
    assert not r.invalid and r.verdict == "verfehlt"
    assert any("p95 20.00 ms" in m for m in r.missed) and any("2561 MiB" in m for m in r.missed)
    assert BM.exit_code([r]) == BM.EXIT_MISSED


@pytest.mark.parametrize(
    ("change", "reason"),
    [
        ({"size": "2560x1440"}, "gerendert in 2560x1440 statt 800x600"),
        ({"duration": "5"}, "verkürzt: 5 s statt 120 s"),
    ],
)
def test_another_size_or_a_shortened_run_is_invalid(
    tmp_path: Path, change: dict[str, str], reason: str
) -> None:
    _pass(tmp_path, "day", GOOD, **change)
    r = BM.judge(tmp_path, RES, "window", "day", MARKS, 0)
    assert reason in r.invalid and r.verdict == "ungültig"
    assert BM.exit_code([r]) == BM.EXIT_FAILED


def test_what_is_not_measured_is_invalid_and_never_ends_the_tool(tmp_path: Path) -> None:
    _pass(tmp_path, "day", GOOD)
    (tmp_path / f"vram_{RES}_day.csv").write_text("time_s,phase,used_mib,total_mib\n", "utf-8")
    assert (
        "Grafikspeicher nicht gemessen"
        in BM.judge(tmp_path, RES, "window", "day", MARKS, 0).invalid
    )
    (tmp_path / f"bench_{RES}_day.csv").write_text("pass,time_s,frame_ms\nday,10.0\n", "utf-8")
    cut = BM.judge(tmp_path, RES, "window", "day", MARKS, 0)
    assert any("CSV nicht lesbar" in reason for reason in cut.invalid)
    (tmp_path / f"bench_{RES}_day.csv").write_text("pass,time_s,frame_ms\n", "utf-8")
    assert "CSV ohne Bilder" in BM.judge(tmp_path, RES, "window", "day", MARKS, 0).invalid
    gone = BM.judge(tmp_path, RES, "window", "night", MARKS, 1)
    assert gone.invalid[:2] == ["Lauf endete mit Fehler (1)", "keine CSV-Datei"]
    assert "Log ohne BENCH_START/BENCH_PASS" in gone.invalid


def test_the_games_own_numbers_must_agree_with_the_csv(tmp_path: Path) -> None:
    _pass(tmp_path, "day", GOOD)
    log = tmp_path / f"bench_{RES}_day.log"
    log.write_text(log.read_text("utf-8").replace("frames=100", "frames=90"), "utf-8")
    r = BM.judge(tmp_path, RES, "window", "day", MARKS, 0)
    assert "Zahlen des Spiels weichen von der CSV ab" in r.invalid


def test_the_report_counts_against_the_expected_passes_and_names_the_method(tmp_path: Path) -> None:
    _pass(tmp_path, "day", GOOD)
    results = [BM.judge(tmp_path, RES, "window", name, MARKS, 0) for name in MARKS.passes]
    text = BM.report(results, MARKS, {"note": "Export abc", "card": "Karte, 1.0"})
    row = "| 800x600 Fenster | 800x600 | day | high | 100 | 95.24 | 10.00 | 20.00 | 1500 "
    assert row + "| erfüllt | - |" in text
    assert "| nicht gemessen | ungültig |" in text
    assert (
        "**Ergebnis 800x600 (Abnahme-Auflösung):** 1 von 2 Durchgängen erfüllt, nicht bestanden."
        in text
    )
    assert "Export abc" in text and "Karte, 1.0" in text and "Spitze minus" in text
    assert "mit 104 Figuren" in text and "Build abc1234; Karte d_1" in text
    assert "| 800x600 | day | Windowed | 600 | 1500 | 3000 | 70 | 1 |" in text


def test_evaluate_only_end_to_end_keeps_the_earlier_report(tmp_path: Path) -> None:
    out = tmp_path / "run"
    out.mkdir()
    for name in BM.load_marks().passes:
        _pass(out, name, GOOD)
    (out / "run.json").write_text(
        json.dumps({"note": "Export abc", "entries": [[RES, "window"]], "exit": {}}), "utf-8"
    )
    (out / "report.md").write_text("old", encoding="utf-8")
    assert BM.main(["--evaluate-only", "--out", str(out)]) == BM.EXIT_OK
    assert (out / "report.md.alt-1").read_text("utf-8") == "old"
    assert "Export abc" in (out / "report.md").read_text("utf-8")
    _pass(out, "day", [30.0] * 100)
    assert BM.main(["--evaluate-only", "--out", str(out)]) == BM.EXIT_MISSED
    (out / f"bench_{RES}_day.csv").unlink()
    assert BM.main(["--evaluate-only", "--out", str(out)]) == BM.EXIT_FAILED


def test_the_sampler_keeps_sampling_after_a_bad_line_and_marks_the_phases(tmp_path: Path) -> None:
    go = tmp_path / "go"
    script = (
        "import os,sys,time\n"
        "print('1500, 10240', flush=True)\n"
        "while not os.path.exists(sys.argv[1]): time.sleep(0.05)\n"
        "for line in ('error', '1600, 10240', '4000, 10240'): print(line, flush=True)\n"
        "time.sleep(30)\n"
    )
    target = tmp_path / "v.csv"
    sampler = BM.Sampler(target, [sys.executable, "-c", script, str(go)])
    sampler.start()

    def rows() -> int:
        return len(target.read_text("utf-8").splitlines()) - 1 if target.is_file() else 0

    deadline = time.monotonic() + 20
    while rows() < 1 and time.monotonic() < deadline:
        time.sleep(0.05)
    sampler.game_starts()
    go.write_text("go", encoding="utf-8")
    while rows() < 4 and time.monotonic() < deadline:
        time.sleep(0.05)
    sampler.stop()
    assert BM.read_vram(target)[:4] == (1500, 4000, 2, 1)


def test_no_card_tool_means_not_measured(tmp_path: Path) -> None:
    sampler = BM.Sampler(tmp_path / "v.csv", ["no-such-tool-glassworld"])
    sampler.start()
    sampler.stop()
    assert BM.read_vram(tmp_path / "v.csv") == (None, None, 0, 0, 0.0)


def test_guards(tmp_path: Path) -> None:
    binary = tmp_path / "GlassWorld.exe"
    binary.write_text("x", encoding="utf-8")

    def check(out: Path, *args: str) -> str | None:
        parser_args = ["--out", str(out), *args]
        options = type("O", (), {})()
        options.resolutions = ["3840x1600:fullscreen", "1920x1080"]
        options.evaluate_only = "--evaluate-only" in parser_args
        options.screen_queue = "--screen-queue" in parser_args
        options.binary = binary if "--binary" in parser_args else None
        options.note = "n" if "--note" in parser_args else ""
        if "--typo" in parser_args:
            options.resolutions = ["3840x1600:fullscren"]
        return BM.guard(out, options)

    full = ("--screen-queue", "--binary", "--note")
    assert "WxH" in (check(tmp_path / "o", *full, "--typo") or "")
    assert "inside the repository" in (check(BM.ROOT / "out", *full) or "")
    assert "screen-queue" in (check(tmp_path / "o", "--binary", "--note") or "")
    assert "not found" in (check(tmp_path / "o", "--screen-queue", "--note") or "")
    assert "--note" in (check(tmp_path / "o", "--screen-queue", "--binary") or "")
    foreign = tmp_path / "foreign"
    foreign.mkdir()
    (foreign / "notes.txt").write_text("x", encoding="utf-8")
    assert "not a folder of this tool" in (check(foreign, *full) or "")
    assert check(tmp_path / "new", *full) is None
    assert check(foreign, "--evaluate-only") is None
    assert "does not exist" in (check(tmp_path / "nowhere", "--evaluate-only") or "")
    assert BM.archive_name(foreign) == tmp_path / "foreign.alt-1"


@pytest.mark.parametrize(
    ("change", "mode", "reason"),
    [
        ({"env": ""}, "window", "Log ohne BENCH_ENV (Export ohne diese Zeile)"),
        ({}, "fullscreen", "Modus Windowed statt ExclusiveFullscreen"),
        ({"env": ENV.replace("vsync=Disabled", "vsync=Enabled")}, "window", "VSync Enabled"),
        ({"env": ENV.replace("max_fps=0", "max_fps=60")}, "window", "Bildraten-Grenze 60"),
        (
            {"env": ENV.replace("build=abc1234", "build=dev")},
            "window",
            "Build 'dev' ist kein benannter Stand",
        ),
        ({"env": ENV.replace("size=800x600", "size=640x480")}, "window", "Fenster in 640x480"),
        ({"warmup": "0"}, "window", "Aufwärmzeit 0 s statt 10 s"),
        ({"quality": "low"}, "window", "Qualität low statt high"),
        ({"run_samples": 3}, "window", "Grafikspeicher nur 3 Messpunkte"),
        ({"last_sample_s": 90}, "window", "Grafikspeicher nur 85 s lang gemessen"),
        (
            {"env": ENV.replace("build=abc1234", "build=unknown")},
            "window",
            "Build 'unknown' ist kein benannter Stand",
        ),
        ({"figures": "52"}, "window", "nur 52 Figuren statt mindestens 100"),
        ({"revealed": "120"}, "window", "aufgedeckt 120 ‰ statt 1000 ‰"),
        ({"revealed": "none"}, "window", "aufgedeckt none ‰ statt 1000 ‰"),
    ],
)
def test_what_the_run_really_ran_on_must_be_the_acceptance_setup(
    tmp_path: Path, change: dict[str, object], mode: str, reason: str
) -> None:
    _pass(tmp_path, "day", GOOD, **change)  # type: ignore[arg-type]
    r = BM.judge(tmp_path, RES, mode, "day", MARKS, 0)
    assert reason in r.invalid and r.verdict == "ungültig"


def test_a_bash_that_cannot_start_leaves_no_sampler_running(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    stopped: list[bool] = []
    monkeypatch.setattr(BM, "BASELINE_S", 0.0)
    monkeypatch.setattr(BM.Sampler, "start", lambda self: None)
    monkeypatch.setattr(BM.Sampler, "stop", lambda self: stopped.append(True))
    code = BM.run_pass(
        "no-such-bash-glassworld", tmp_path / "x.exe", RES, "window", "day", tmp_path, []
    )
    assert code == 127 and stopped == [True]


def test_extra_scene_arguments_are_shown_and_never_the_acceptance_run(tmp_path: Path) -> None:
    _pass(tmp_path, "day", GOOD)
    r = BM.judge(tmp_path, RES, "window", "day", MARKS, 0, ["--preset=small", "--speed=4"])
    assert r.invalid == ["zusätzliche Szenen-Argumente: --preset=small --speed=4"]
    text = BM.report([r], MARKS, {"extra": ["--preset=small", "--speed=4"]})
    assert "**Zusätzliche Szenen-Argumente:** --preset=small --speed=4" in text
    assert "**Zusätzliche Szenen-Argumente:** keine" in BM.report([r], MARKS, {"extra": []})
    assert "**Zusätzliche Szenen-Argumente:** nicht bekannt" in BM.report([r], MARKS, {})


def _runs(pid: int) -> bool:
    if sys.platform == "win32":
        import subprocess

        listed = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True, check=False
        )
        return str(pid) in listed.stdout
    import os

    try:
        os.kill(pid, 0)
    except OSError:
        return False
    return True


def test_the_group_ends_what_the_started_command_started_even_after_its_parent_is_gone(
    tmp_path: Path,
) -> None:
    """Review GW1 #230 F1: bash ended, `timeout` and the game below it kept running."""
    note = tmp_path / "pid.txt"
    # The command starts a long sleeper, writes its pid and ends: the sleeper's parent is gone.
    script = (
        "import subprocess,sys\n"
        "p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])\n"
        "open(sys.argv[1], 'w').write(str(p.pid))\n"
    )
    group = BM.ProcessGroup()
    import os

    child = group.start([sys.executable, "-c", script, str(note)], dict(os.environ))
    assert child.wait(timeout=30) == 0
    pid = int(note.read_text())
    assert _runs(pid)
    group.end()
    deadline = time.monotonic() + 10
    while _runs(pid) and time.monotonic() < deadline:
        time.sleep(0.1)
    assert not _runs(pid)


def test_no_further_pass_is_started_after_one_ran_over_its_time(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    binary = tmp_path / "GlassWorld.exe"
    binary.write_text("x", encoding="utf-8")
    started: list[str] = []

    def fake(
        bash: str, exe: Path, res: str, mode: str, name: str, out: Path, extra: list[str]
    ) -> int:
        started.append(f"{res}_{name}")
        return 124

    monkeypatch.setattr(BM, "run_pass", fake)
    monkeypatch.setattr(BM, "card", lambda: "Karte, 1.0")
    out = tmp_path / "run"
    code = BM.main(
        [
            "--screen-queue",
            "--binary",
            str(binary),
            "--out",
            str(out),
            "--note",
            "n",
            "--bash",
            "bash",
        ]
    )
    assert code == BM.EXIT_FAILED and len(started) == 1
    assert "Lauf endete mit Fehler (124)" in (out / "report.md").read_text("utf-8")


@pytest.mark.skipif(sys.platform != "win32", reason="job objects are a Windows matter")
def test_without_the_job_the_tool_says_so_and_still_ends_its_child(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    """Review GW1 #232 A: a failed assignment went unnoticed, and end() ended nothing."""
    import os

    group = BM.ProcessGroup()
    monkeypatch.setattr(group._k32, "AssignProcessToJobObject", lambda job, handle: 0)
    child = group.start([sys.executable, "-c", "import time; time.sleep(120)"], dict(os.environ))
    assert "BENCH_MEASURE_WARN" in capsys.readouterr().err
    assert child.poll() is None
    group.end()
    assert child.poll() is not None


def test_without_a_share_in_the_data_any_share_is_accepted(tmp_path: Path) -> None:
    """An export or data from before A1559 (#268): nothing to compare, the report says '?'."""
    old = BM.Marks(60.0, 16.7, 120.0, 100, 2560, ("day", "night"), 10.0, "7", "high")
    _pass(tmp_path, "day", GOOD, revealed="120")
    r = BM.judge(tmp_path, RES, "window", "day", old, 0)
    assert r.revealed == "120" and r.verdict == "erfüllt", r.invalid
