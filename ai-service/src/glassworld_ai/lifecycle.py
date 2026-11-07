"""Service lifecycle (ADR-0006): parent watch, lock file.

The service never kills other processes. A leftover service from a crashed session is reported
(lock file + stdout line) and the game, which owns the processes, replaces it.
"""

from __future__ import annotations

import asyncio
import contextlib
import ctypes
import json
import os
import sys
from collections.abc import Callable
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path

STALE_LINE = "AI_SERVICE_STALE pid={pid} port={port}"
_SYNCHRONIZE = 0x00100000
_QUERY_LIMITED_INFORMATION = 0x00001000
_WAIT_TIMEOUT = 0x00000102


def process_start_marker(pid: int) -> int | None:
    """A value that identifies one process lifetime (creation time); None if unknown.

    Used to tell our old service apart from an unrelated process that got the same pid later.
    """
    if pid <= 0:
        return None
    if sys.platform == "win32":
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        handle = kernel32.OpenProcess(_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return None
        try:
            times = [ctypes.c_ulonglong() for _ in range(4)]
            ok = kernel32.GetProcessTimes(handle, *(ctypes.byref(t) for t in times))
            return int(times[0].value) if ok else None
        finally:
            kernel32.CloseHandle(handle)
    try:
        stat = Path(f"/proc/{pid}/stat").read_text(encoding="utf-8")
        return int(stat.rsplit(")", 1)[1].split()[19])  # field 22: starttime
    except (OSError, ValueError, IndexError):
        return None


def process_alive(pid: int) -> bool:
    """True if a process with this id exists. Never sends a signal on Windows."""
    if pid <= 0:
        return False
    if sys.platform == "win32":
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        handle = kernel32.OpenProcess(_SYNCHRONIZE, False, pid)
        if not handle:
            return False
        try:
            return kernel32.WaitForSingleObject(handle, 0) == _WAIT_TIMEOUT
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)  # POSIX only: signal 0 just checks existence
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


async def watch_parent(
    pid: int,
    on_gone: Callable[[], None],
    interval_s: float = 1.0,
    alive: Callable[[int], bool] = process_alive,
) -> None:
    """Call `on_gone` once when the parent process has ended."""
    while alive(pid):
        await asyncio.sleep(interval_s)
    on_gone()


@dataclass(frozen=True)
class LockInfo:
    pid: int
    port: int
    started_utc: str
    start_marker: int | None


class LockFile:
    def __init__(self, path: Path) -> None:
        self._path = path

    @property
    def path(self) -> Path:
        return self._path

    def read(self) -> LockInfo | None:
        try:
            data = json.loads(self._path.read_text(encoding="utf-8"))
            marker = data.get("start_marker")
            return LockInfo(
                pid=int(data["pid"]),
                port=int(data["port"]),
                started_utc=str(data["started_utc"]),
                start_marker=int(marker) if marker is not None else None,
            )
        except (OSError, ValueError, KeyError, TypeError):
            return None

    def leftover(
        self,
        alive: Callable[[int], bool] = process_alive,
        marker: Callable[[int], int | None] = process_start_marker,
    ) -> LockInfo | None:
        """A still running service from an earlier session, if any.

        Only reported if pid AND start marker match: after pid reuse an unrelated process must
        never be reported (the game would terminate it). Unknown marker = not reported.
        """
        info = self.read()
        if info is None or info.pid == os.getpid() or not alive(info.pid):
            return None
        if info.start_marker is None or marker(info.pid) != info.start_marker:
            return None
        return info

    def write(self, port: int) -> LockInfo:
        info = LockInfo(
            pid=os.getpid(),
            port=port,
            started_utc=datetime.now(UTC).isoformat(timespec="seconds"),
            start_marker=process_start_marker(os.getpid()),
        )
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._path.write_text(json.dumps(asdict(info)), encoding="utf-8")
        return info

    def remove(self) -> None:
        info = self.read()
        if info is not None and info.pid == os.getpid():
            with contextlib.suppress(OSError):
                self._path.unlink()
