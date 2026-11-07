"""Shared test helpers: fake probes (no GPU, no network) and an app factory."""

from __future__ import annotations

import asyncio
from collections.abc import Mapping
from dataclasses import dataclass, field
from pathlib import Path

import httpx
import pytest
from fastapi import FastAPI

from glassworld_ai.app import create_app
from glassworld_ai.config import Settings, load_settings
from glassworld_ai.hardware import Gpu, HardwareInfo
from glassworld_ai.probes import DOWN, DependencyState, OllamaState

TEST_HOST = "testserver"
TOKEN = "test-session-token"
SERVICE_DIR = Path(__file__).resolve().parents[1]


@dataclass
class FakeProbes:
    ollama_state: OllamaState = field(
        default_factory=lambda: OllamaState(
            service=DependencyState(up=True, version="0.40.14"),
            model_available=True,
            model_loaded=False,
        )
    )
    comfyui_state: DependencyState = field(
        default_factory=lambda: DependencyState(up=True, version="0.44.0")
    )

    async def ollama(self) -> OllamaState:
        return self.ollama_state

    async def comfyui(self) -> DependencyState:
        return self.comfyui_state


OLLAMA_DOWN = OllamaState(service=DOWN, model_available=False, model_loaded=False)

REFERENCE_PC = HardwareInfo(
    gpus=(Gpu(name="NVIDIA GeForce RTX 3080", vram_mib=10240),),
    cpu_name="AMD Ryzen 5 5600X 6-Core Processor",
    logical_cpus=12,
    ram_mib=32 * 1024,
)


@pytest.fixture
def settings(monkeypatch: pytest.MonkeyPatch) -> Settings:
    monkeypatch.chdir(SERVICE_DIR / "tests")  # no developer .env leaks into tests
    monkeypatch.setenv("GW_AI_TOKEN", TOKEN)
    return load_settings()


def make_app(
    settings: Settings,
    probes: FakeProbes | None = None,
    hardware: HardwareInfo = REFERENCE_PC,
) -> FastAPI:
    return create_app(
        settings,
        allowed_hosts=frozenset({TEST_HOST}),
        probes=probes or FakeProbes(),
        hardware=hardware,
    )


def get(app: FastAPI, path: str, headers: Mapping[str, str] | None = None) -> httpx.Response:
    """In-process GET against the ASGI app (no network, no server)."""

    async def _call() -> httpx.Response:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url=f"http://{TEST_HOST}") as c:
            return await c.get(path, headers=dict(headers or {}))

    return asyncio.run(_call())


@pytest.fixture
def app(settings: Settings) -> FastAPI:
    return make_app(settings)
