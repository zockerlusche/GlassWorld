"""D9 report logic (D471): parsing the runner's lines and checking the chain in order."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from types import ModuleType


def _module() -> ModuleType:
    spec = importlib.util.spec_from_file_location("run_d9", Path(__file__).with_name("run_d9.py"))
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


D9 = _module()

FULL = [
    "day 1 ...",
    "EVENT tick=10 kind=invention_wanted inhabitant=inh-1 action=- target=- detail=-",
    "EVENT tick=40 kind=invented inhabitant=inh-1 action=- target=obj-9 detail=new%20thing",
    "EVENT tick=41 kind=build_planned inhabitant=inh-1 action=- target=obj-9 detail=-",
    "EVENT tick=50 kind=build_step inhabitant=inh-1 action=build target=obj-9 detail=1",
    "EVENT tick=60 kind=build_step inhabitant=inh-1 action=build target=obj-9 detail=2",
    "EVENT tick=61 kind=built inhabitant=inh-1 action=- target=obj-9 detail=-",
    "CHRONICLE tick=40 kind=invented id=1",
    "EVENT tick=90 kind=first_use inhabitant=inh-1 action=use target=obj-9 detail=-",
    "CHRONICLE tick=90 kind=first_use id=2",
    "UNTIL kind=first_use count=1/1 reached tick=90",
    "FINAL hash=abc tick=90",
]


def test_parse_reads_events_chronicle_and_final_hash() -> None:
    run = D9.parse_run(FULL, 0)
    assert run.final_hash == "abc" and run.final_tick == "90"
    assert run.events[1]["detail"] == "new thing"  # percent-decoded
    assert [c["kind"] for c in run.chronicle] == ["invented", "first_use"]
    assert run.until is not None and "reached" in run.until


def test_the_full_chain_passes() -> None:
    rows = D9.chain_check(D9.parse_run(FULL, 0))
    assert all(ok for _, _, ok in rows), rows
    assert [kind for kind, _, _ in rows][-1] == "chronicle"


def test_one_build_step_or_a_used_thing_before_it_was_built_fails() -> None:
    one_step = [line for line in FULL if "detail=2" not in line]
    rows = dict((k, ok) for k, _, ok in D9.chain_check(D9.parse_run(one_step, 0)))
    assert rows["build_step"] is False
    early_use = [
        line.replace("tick=90 kind=first_use", "tick=5 kind=first_use")
        for line in FULL
        if "kind=first_use" in line and line.startswith("EVENT")
    ] + [line for line in FULL if not (line.startswith("EVENT") and "first_use" in line)]
    rows = dict((k, ok) for k, _, ok in D9.chain_check(D9.parse_run(early_use, 0)))
    assert rows["first_use"] is False  # only counts after "built"


def test_assets_are_found_anywhere_in_a_result() -> None:
    result = {"image": {"file": "img-1.png"}, "sounds": ["a.wav", "readme.txt"], "n": 3}
    assert sorted(D9.asset_names(result)) == ["a.wav", "img-1.png"]


def test_output_inside_the_repository_is_refused(tmp_path: Path) -> None:
    assert D9.main(["--ai", "mock", "--out", str(D9.ROOT / "build" / "d9")]) == 1
