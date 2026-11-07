"""Model residency (B7 R2): which AI models are in VRAM, loading and unloading on purpose.

The budget is the VRAM of the largest GPU minus what the game needs (content/hardware/models.json,
B2514 T6). Before a model is used, `ensure` makes room: models that must never run together
are unloaded first, then the least recently used ones until the new model fits. Two big models
are never loaded at the same time above the budget. The values (VRAM, load time) are data,
measured through the GPU queue (B7 S3); the service only adds and compares them.

Drivers do the actual work per runtime: Ollama (keep_alive), ComfyUI (POST /free) and hooks that
other handlers register (e.g. the sound model of B8). When the planner runs what (day/night) is
B7 S4; this module only answers "make model X resident".
"""

from __future__ import annotations

import asyncio
import json
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

import httpx

from glassworld_ai.hardware import HardwareInfo
from glassworld_contracts.models.model_footprints_schema import ModelFootprints


class ResidencyError(Exception):
    """A model cannot be made resident (unknown, larger than the budget, or a driver failed)."""


@dataclass(frozen=True)
class Footprint:
    model_id: str
    role: str
    runtime: str
    vram_mib: int
    load_s: float
    never_with: frozenset[str]


class Driver(Protocol):
    async def load(self, model_id: str) -> None: ...
    async def unload(self, model_id: str) -> None: ...


def load_footprints(path: Path) -> tuple[int, dict[str, Footprint]]:
    """game_vram_mib and the footprints by model id from content/hardware/models.json."""
    data = ModelFootprints.model_validate(json.loads(path.read_text(encoding="utf-8")))
    models: dict[str, Footprint] = {}
    for m in data.models:
        runtime = str(getattr(m.runtime, "value", m.runtime))
        role = str(getattr(m.role, "value", m.role))
        if m.id in models:
            raise ValueError(f"{path.name}: model {m.id} listed twice")
        models[m.id] = Footprint(m.id, role, runtime, m.vram_mib, m.load_s, frozenset(m.never_with))
    for footprint in models.values():
        unknown = sorted(footprint.never_with - set(models))
        if unknown:
            raise ValueError(
                f"{path.name}: {footprint.model_id} never_with unknown models {unknown}"
            )
    return data.game_vram_mib, models


def budget_mib(hardware: HardwareInfo, game_vram_mib: int) -> int:
    """VRAM the AI models may use: largest GPU minus the game (0 without a GPU)."""
    largest = max((g.vram_mib for g in hardware.gpus), default=0)
    return max(0, largest - game_vram_mib)


class Residency:
    """Keeps the loaded models within the budget. One change at a time (lock)."""

    def __init__(
        self,
        footprints: dict[str, Footprint],
        budget: int,
        drivers: dict[str, Driver],
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._footprints = footprints
        self._budget = budget
        self._drivers = dict(drivers)
        self._clock = clock
        self._loaded: dict[str, float] = {}  # model id -> last use
        self._lock = asyncio.Lock()

    @property
    def budget_mib(self) -> int:
        return self._budget

    def loaded(self) -> list[str]:
        return sorted(self._loaded)

    def used_mib(self) -> int:
        return sum(self._footprints[m].vram_mib for m in self._loaded)

    def register_driver(self, runtime: str, driver: Driver) -> None:
        """Hook for other handlers (e.g. GW3's sound model, runtime "hook")."""
        self._drivers[runtime] = driver

    def mark_loaded(self, model_id: str) -> None:
        """Note a model that is already in VRAM. Refused when it breaks the budget or a
        never-together rule; then load it with `ensure` instead (which makes room first)."""
        wanted = self._footprint(model_id)
        if model_id in self._loaded:
            self._loaded[model_id] = self._clock()
            return
        conflicts = [
            other
            for other in self._loaded
            if other in wanted.never_with or model_id in self._footprint(other).never_with
        ]
        if conflicts or self.used_mib() + wanted.vram_mib > self._budget:
            raise ResidencyError(
                f"{model_id} cannot be marked loaded next to {sorted(self._loaded)}"
            )
        self._loaded[model_id] = self._clock()

    async def ensure(self, model_id: str) -> None:
        """Make `model_id` resident; unloads conflicting and least recently used models first."""
        async with self._lock:
            wanted = self._footprint(model_id)
            if model_id in self._loaded:
                self._loaded[model_id] = self._clock()
                return
            if wanted.vram_mib > self._budget:
                raise ResidencyError(
                    f"{model_id} needs {wanted.vram_mib} MiB, budget is {self._budget} MiB"
                )
            for other in sorted(self._loaded):
                if other in wanted.never_with or model_id in self._footprint(other).never_with:
                    await self._unload(other)
            while self.used_mib() + wanted.vram_mib > self._budget:
                oldest = min(self._loaded, key=lambda m: (self._loaded[m], m))
                await self._unload(oldest)
            await self._driver(wanted).load(model_id)
            self._loaded[model_id] = self._clock()

    async def release(self, model_id: str) -> None:
        """Unload a model now (e.g. the language model at night)."""
        async with self._lock:
            if model_id in self._loaded:
                await self._unload(model_id)

    async def release_all(self) -> None:
        async with self._lock:
            for model_id in sorted(self._loaded):
                await self._unload(model_id)

    def _footprint(self, model_id: str) -> Footprint:
        found = self._footprints.get(model_id)
        if found is None:
            raise ResidencyError(
                f"no footprint for model {model_id} (content/hardware/models.json)"
            )
        return found

    def _driver(self, footprint: Footprint) -> Driver:
        driver = self._drivers.get(footprint.runtime)
        if driver is None:
            raise ResidencyError(
                f"no driver for runtime {footprint.runtime} ({footprint.model_id})"
            )
        return driver

    async def _unload(self, model_id: str) -> None:
        footprint = self._footprint(model_id)
        await self._driver(footprint).unload(model_id)
        del self._loaded[model_id]
        if footprint.runtime == "comfyui":  # POST /free unloads every ComfyUI model
            for other in [m for m in self._loaded if self._footprint(m).runtime == "comfyui"]:
                del self._loaded[other]


# --- drivers ------------------------------------------------------------------------------


class OllamaDriver:
    """Loads with an empty generate call, unloads with keep_alive 0 (Ollama API)."""

    def __init__(self, base_url: str, client: httpx.AsyncClient, keep_alive: str) -> None:
        self._base = base_url.rstrip("/")
        self._client = client
        self._keep_alive = keep_alive

    async def load(self, model_id: str) -> None:
        await self._post(
            {"model": model_id, "prompt": "", "keep_alive": self._keep_alive}, timeout=300
        )

    async def unload(self, model_id: str) -> None:
        await self._post({"model": model_id, "keep_alive": 0}, timeout=30)

    async def _post(self, body: dict[str, object], timeout: float) -> None:
        try:
            response = await self._client.post(
                f"{self._base}/api/generate", json=body, timeout=timeout
            )
        except httpx.HTTPError as error:
            raise ResidencyError(f"Ollama: {type(error).__name__}") from error
        if response.status_code >= 400:
            raise ResidencyError(f"Ollama answered {response.status_code}")


class ComfyUIDriver:
    """ComfyUI loads a model with the first workflow that uses it; POST /free unloads all."""

    def __init__(self, base_url: str, client: httpx.AsyncClient) -> None:
        self._base = base_url.rstrip("/")
        self._client = client

    async def load(self, model_id: str) -> None:
        return None  # loaded by the image job's workflow (B7 S5)

    async def unload(self, model_id: str) -> None:
        try:
            response = await self._client.post(
                f"{self._base}/free",
                json={"unload_models": True, "free_memory": True},
                timeout=30,
            )
        except httpx.HTTPError as error:
            raise ResidencyError(f"ComfyUI: {type(error).__name__}") from error
        if response.status_code >= 400:
            raise ResidencyError(f"ComfyUI answered {response.status_code}")


@dataclass
class HookDriver:
    """Driver from two callables, for models a handler loads itself (sound, B8)."""

    on_load: Callable[[str], Awaitable[None]]
    on_unload: Callable[[str], Awaitable[None]]

    async def load(self, model_id: str) -> None:
        await self.on_load(model_id)

    async def unload(self, model_id: str) -> None:
        await self.on_unload(model_id)
