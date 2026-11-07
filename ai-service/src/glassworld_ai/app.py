"""FastAPI application factory."""

from __future__ import annotations

import asyncio
from collections.abc import AsyncGenerator, Callable
from contextlib import asynccontextmanager

import httpx
from fastapi import FastAPI

from glassworld_ai import SERVICE_VERSION
from glassworld_ai.config import Settings
from glassworld_ai.errors import install_error_handlers
from glassworld_ai.hardware import HardwareInfo, detect_hardware, load_tiers, select_tier
from glassworld_ai.health import health_status
from glassworld_ai.probes import HttpProbes, Probes
from glassworld_ai.security import make_access_guard
from glassworld_contracts import HealthStatus

READY_LINE = "AI_SERVICE_READY port={port}"


def create_app(
    settings: Settings,
    *,
    allowed_hosts: frozenset[str],
    probes: Probes | None = None,
    hardware: HardwareInfo | None = None,
    on_started: Callable[[], None] | None = None,
) -> FastAPI:
    """Build the app.

    Tests pass fake probes and fake hardware; the real service probes Ollama/ComfyUI over HTTP
    and detects the hardware once at start.
    """
    tiers = load_tiers(settings.hardware_tiers_file)  # fail fast on a broken data file

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncGenerator[None]:
        async with httpx.AsyncClient() as client:
            if probes is None:
                app.state.probes = HttpProbes(settings, client)
            if hardware is None:
                _set_hardware(app, await asyncio.to_thread(detect_hardware))
            if on_started is not None:
                on_started()
            yield

    app = FastAPI(
        title="GlassWorld AI service",
        version=SERVICE_VERSION,
        lifespan=lifespan,
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )

    def _set_hardware(target: FastAPI, info: HardwareInfo) -> None:
        target.state.hardware = info
        target.state.tier = select_tier(tiers, info)

    if probes is not None:
        app.state.probes = probes
    if hardware is not None:
        _set_hardware(app, hardware)
    install_error_handlers(app)
    token = settings.token.get_secret_value() if settings.token else None
    app.middleware("http")(make_access_guard(allowed_hosts, token))

    @app.get("/health")
    async def health() -> HealthStatus:
        current: Probes = app.state.probes
        info: HardwareInfo = app.state.hardware
        tier: str | None = app.state.tier
        return await health_status(current, settings.decider_model, info, tier)

    return app
