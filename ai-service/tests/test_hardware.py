from __future__ import annotations

import shutil
import subprocess
from collections.abc import Sequence
from pathlib import Path

import pytest

from glassworld_ai.config import Settings
from glassworld_ai.hardware import (
    NVIDIA_SMI_QUERY,
    Gpu,
    HardwareInfo,
    detect_gpus,
    detect_hardware,
    load_tiers,
    parse_nvidia_smi,
    select_tier,
)

TIERS_FILE = Path(__file__).resolve().parents[2] / "content" / "hardware" / "tiers.json"
GIB = 1024


def info(*vram_mib: int, ram_mib: int = 32 * GIB) -> HardwareInfo:
    gpus = tuple(Gpu(name=f"GPU {i}", vram_mib=v) for i, v in enumerate(vram_mib))
    return HardwareInfo(gpus=gpus, cpu_name="CPU", logical_cpus=8, ram_mib=ram_mib)


# --- nvidia-smi parsing (fake outputs, no GPU needed) ---------------------------------------


def test_parse_single_gpu_reference_pc() -> None:
    assert parse_nvidia_smi("NVIDIA GeForce RTX 3080, 10240\n") == (
        Gpu(name="NVIDIA GeForce RTX 3080", vram_mib=10240),
    )


def test_parse_two_gpus_and_windows_line_endings() -> None:
    output = "NVIDIA GeForce RTX 4090, 24564\r\nNVIDIA GeForce RTX 3060, 12288\r\n"
    assert [g.vram_mib for g in parse_nvidia_smi(output)] == [24564, 12288]


def test_parse_skips_garbage_lines() -> None:
    output = "No devices were found\nNVIDIA T1000, [N/A]\nNVIDIA A2000, 6138\n"
    assert parse_nvidia_smi(output) == (Gpu(name="NVIDIA A2000", vram_mib=6138),)


def test_name_with_comma_keeps_full_name() -> None:
    assert parse_nvidia_smi("Vendor, Model X, 8192")[0].name == "Vendor, Model X"


def test_missing_nvidia_smi_means_no_gpu() -> None:
    def missing(_: Sequence[str]) -> str:
        raise FileNotFoundError("nvidia-smi")

    assert detect_gpus(missing) == ()


def test_failing_nvidia_smi_means_no_gpu() -> None:
    def failing(command: Sequence[str]) -> str:
        raise subprocess.CalledProcessError(9, list(command))

    assert detect_gpus(failing) == ()


def test_detect_uses_the_documented_query() -> None:
    seen: list[Sequence[str]] = []

    def runner(command: Sequence[str]) -> str:
        seen.append(command)
        return "NVIDIA GeForce RTX 3080, 10240"

    assert detect_hardware(runner).gpus[0].vram_mib == 10240
    assert seen == [NVIDIA_SMI_QUERY]


# --- tier selection against content/hardware/tiers.json ------------------------------------


@pytest.mark.parametrize(
    ("hardware", "expected"),
    [
        (info(), None),  # no NVIDIA GPU
        (info(6 * GIB), None),  # below minimum
        (info(8 * GIB), "minimum"),
        (info(10 * GIB), "standard"),  # reference PC: RTX 3080 10 GB
        (info(12 * GIB), "standard"),
        (info(24 * GIB), "comfort"),
        (info(8 * GIB, 8 * GIB), "comfort"),  # two cards (concept)
    ],
)
def test_select_tier(hardware: HardwareInfo, expected: str | None) -> None:
    assert select_tier(load_tiers(TIERS_FILE), hardware) == expected


def test_tiers_file_is_ordered_from_lowest_to_highest() -> None:
    tiers = load_tiers(TIERS_FILE).tiers
    ids = [t.id for t in tiers]
    assert len(ids) == len(set(ids))
    first_vram = [t.requires_any[0].min_largest_gpu_vram_mib or 0 for t in tiers]
    assert first_vram == sorted(first_vram)


def test_settings_point_to_the_content_tiers_file(settings: Settings) -> None:
    assert settings.hardware_tiers_file == TIERS_FILE


# --- real machine (skipped without nvidia-smi, e.g. in CI) ---------------------------------


@pytest.mark.skipif(shutil.which("nvidia-smi") is None, reason="no NVIDIA driver on this machine")
def test_real_detection_finds_a_gpu() -> None:
    detected = detect_hardware()
    assert detected.gpus
    assert detected.ram_mib is not None and detected.ram_mib > 0
    assert detected.logical_cpus is not None and detected.logical_cpus > 0
