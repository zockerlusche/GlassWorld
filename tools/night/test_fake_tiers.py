"""Tests of the forced tiers for check runs (A1586)."""

import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent))
from fake_tiers import TIERS, make

from glassworld_ai.hardware import Gpu, HardwareInfo, load_tiers, select_tier


def _ids() -> list[str]:
    return [t["id"] for t in json.loads(TIERS.read_text(encoding="utf-8"))["tiers"]]


@pytest.mark.parametrize("tier", _ids())
@pytest.mark.parametrize("vram", [0, 8192, 24576])
def test_the_service_selects_the_forced_tier_on_any_card(
    tmp_path: Path, tier: str, vram: int
) -> None:
    forced = load_tiers(make(tier, tmp_path / "tiers.json"))
    gpus = (Gpu(name="any", vram_mib=vram),) if vram else ()
    info = HardwareInfo(gpus=gpus, cpu_name=None, logical_cpus=None, ram_mib=16384)
    assert select_tier(forced, info) == tier


def test_only_the_copy_changes_and_the_rest_of_the_data_stays(tmp_path: Path) -> None:
    before = TIERS.read_bytes()
    out = make("minimum", tmp_path / "tiers.json")
    assert TIERS.read_bytes() == before
    original = json.loads(before)
    copy = json.loads(out.read_text(encoding="utf-8"))
    assert [t["id"] for t in copy["tiers"]] == [t["id"] for t in original["tiers"]]
    assert [t.get("learn_by_day") for t in copy["tiers"]] == [
        t.get("learn_by_day") for t in original["tiers"]
    ]


def test_an_unknown_tier_is_refused(tmp_path: Path) -> None:
    with pytest.raises(SystemExit, match="unknown tier"):
        make("gigantic", tmp_path / "tiers.json")
