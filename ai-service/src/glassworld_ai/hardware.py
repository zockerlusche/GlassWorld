"""Hardware detection (GPU, CPU, RAM) and tier selection.

Standard library only. NVIDIA GPUs are read via nvidia-smi; if it is missing or fails, the GPU
list is empty and the tier falls back accordingly. Tier thresholds live in
content/hardware/tiers.json, never in code.
"""

from __future__ import annotations

import ctypes
import json
import os
import platform
import subprocess
import sys
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path

from glassworld_contracts import HardwareTiers
from glassworld_contracts.models.hardware_tiers_schema import Requirement

MIB = 1024 * 1024
NVIDIA_SMI_QUERY = [
    "nvidia-smi",
    "--query-gpu=name,memory.total",
    "--format=csv,noheader,nounits",
]

# Runs a command and returns its stdout; raises OSError or subprocess.SubprocessError on failure.
CommandRunner = Callable[[Sequence[str]], str]


@dataclass(frozen=True)
class Gpu:
    name: str
    vram_mib: int


@dataclass(frozen=True)
class HardwareInfo:
    gpus: tuple[Gpu, ...]
    cpu_name: str | None
    logical_cpus: int | None
    ram_mib: int | None


def run_command(command: Sequence[str]) -> str:
    result = subprocess.run(list(command), capture_output=True, text=True, check=True, timeout=10)
    return result.stdout


def parse_nvidia_smi(output: str) -> tuple[Gpu, ...]:
    """Parse `name, memory.total` CSV lines (no header, no units). Bad lines are skipped."""
    gpus: list[Gpu] = []
    for line in output.splitlines():
        name, sep, vram = line.rpartition(",")
        if not sep:
            continue
        try:
            vram_mib = int(float(vram.strip()))
        except ValueError:
            continue
        if name.strip() and vram_mib >= 0:
            gpus.append(Gpu(name=name.strip(), vram_mib=vram_mib))
    return tuple(gpus)


def detect_gpus(runner: CommandRunner = run_command) -> tuple[Gpu, ...]:
    try:
        return parse_nvidia_smi(runner(NVIDIA_SMI_QUERY))
    except (OSError, subprocess.SubprocessError):
        return ()


def _windows_cpu_name() -> str | None:
    try:
        import winreg

        key = winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            r"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
        )
        with key:
            value, _ = winreg.QueryValueEx(key, "ProcessorNameString")
        return str(value).strip() or None
    except OSError:
        return None


def _linux_cpu_name() -> str | None:
    try:
        for line in Path("/proc/cpuinfo").read_text(encoding="utf-8").splitlines():
            if line.startswith("model name"):
                return line.split(":", 1)[1].strip() or None
    except OSError:
        return None
    return None


def detect_cpu_name() -> str | None:
    if sys.platform == "win32":
        return _windows_cpu_name()
    if sys.platform.startswith("linux"):
        return _linux_cpu_name()
    return platform.processor() or None


class _MemoryStatusEx(ctypes.Structure):
    _fields_ = [  # noqa: RUF012 - ctypes API
        ("dwLength", ctypes.c_ulong),
        ("dwMemoryLoad", ctypes.c_ulong),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


def detect_ram_mib() -> int | None:
    if sys.platform == "win32":
        status = _MemoryStatusEx()
        status.dwLength = ctypes.sizeof(_MemoryStatusEx)
        kernel32 = ctypes.WinDLL("kernel32")
        if not kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return None
        return int(status.ullTotalPhys) // MIB
    try:
        return os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES") // MIB
    except (AttributeError, ValueError, OSError):
        return None


def detect_hardware(runner: CommandRunner = run_command) -> HardwareInfo:
    return HardwareInfo(
        gpus=detect_gpus(runner),
        cpu_name=detect_cpu_name(),
        logical_cpus=os.cpu_count(),
        ram_mib=detect_ram_mib(),
    )


def load_tiers(path: Path) -> HardwareTiers:
    return HardwareTiers.model_validate(json.loads(path.read_text(encoding="utf-8")))


def _meets(requirement: Requirement, info: HardwareInfo) -> bool:
    largest_vram = max((g.vram_mib for g in info.gpus), default=0)
    checks = [
        (requirement.min_largest_gpu_vram_mib, largest_vram),
        (requirement.min_gpu_count, len(info.gpus)),
        (requirement.min_ram_mib, info.ram_mib or 0),
    ]
    return all(actual >= minimum for minimum, actual in checks if minimum is not None)


def select_tier(tiers: HardwareTiers, info: HardwareInfo) -> str | None:
    """Highest tier (file order: lowest first) with at least one fully met requirement set."""
    selected: str | None = None
    for tier in tiers.tiers:
        if any(_meets(req, info) for req in tier.requires_any):
            selected = tier.id
    return selected
