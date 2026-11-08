"""Client for the MOSS worker process (tools/moss/worker.py) in the model's own environment.

The worker loads the pipeline once (bf16, official VRAM management, D440) and then renders one
request per line of JSON on stdin, answering with one line of JSON on stdout. Starting it is the
"load" of the residency, stopping it the "unload" (B7): the VRAM is free again once it has ended.
"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any


class WorkerError(Exception):
    """The worker could not start or did not answer."""


class MossWorker:
    """One worker process at a time; requests are sent one after another."""

    def __init__(
        self, python: Path, script: Path, model_dir: Path, code_dir: Path, start_timeout_s: float
    ) -> None:
        self._command = [
            str(python),
            str(script),
            "--model-dir",
            str(model_dir),
            "--code-dir",
            str(code_dir),
        ]
        self._start_timeout_s = start_timeout_s
        self._process: asyncio.subprocess.Process | None = None
        self._lock = asyncio.Lock()

    @property
    def running(self) -> bool:
        return self._process is not None and self._process.returncode is None

    async def load(self, model_id: str = "moss-sound") -> None:
        """Starts the worker and waits until it has loaded the model (prints READY)."""
        if self.running:
            return
        self._process = await asyncio.create_subprocess_exec(
            *self._command,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL,
        )
        try:
            line = await asyncio.wait_for(self._read_line(), self._start_timeout_s)
        except TimeoutError as error:
            await self.unload(model_id)
            raise WorkerError("worker did not get ready in time") from error
        if line.strip() != "READY":
            await self.unload(model_id)
            raise WorkerError(f"worker did not start: {line.strip()[:200]}")

    async def unload(self, model_id: str = "moss-sound") -> None:
        """Ends the worker (frees the VRAM)."""
        process, self._process = self._process, None
        if process is None or process.returncode is not None:
            return
        if process.stdin is not None:
            process.stdin.close()
        try:
            await asyncio.wait_for(process.wait(), 20)
        except TimeoutError:
            process.kill()
            await process.wait()

    async def render(self, request: dict[str, Any], timeout_s: float) -> dict[str, Any]:
        """Sends one render request and returns the worker's answer."""
        async with self._lock:
            if not self.running:
                # Only the residency loads models (VRAM budget, never with the language model).
                raise WorkerError(
                    "sound model not loaded (the residency loads it before sound jobs)"
                )
            process = self._process
            assert process is not None and process.stdin is not None
            process.stdin.write((json.dumps(request) + "\n").encode("utf-8"))
            await process.stdin.drain()
            try:
                line = await asyncio.wait_for(self._read_line(), timeout_s)
            except TimeoutError as error:
                await self.unload()
                raise WorkerError("worker did not answer in time") from error
        try:
            answer: dict[str, Any] = json.loads(line)
        except json.JSONDecodeError as error:
            raise WorkerError(f"worker answered no JSON: {line[:200]}") from error
        return answer

    async def _read_line(self) -> str:
        process = self._process
        if process is None or process.stdout is None:
            raise WorkerError("worker is not running")
        raw = await process.stdout.readline()
        if not raw:
            raise WorkerError("worker ended")
        return raw.decode("utf-8", errors="replace")
