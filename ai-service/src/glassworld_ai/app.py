"""FastAPI application factory."""

from __future__ import annotations

from collections.abc import AsyncGenerator, Callable
from contextlib import asynccontextmanager

import httpx
from fastapi import FastAPI

from glassworld_ai import SERVICE_VERSION
from glassworld_ai.config import Settings
from glassworld_ai.errors import install_error_handlers
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
    on_started: Callable[[], None] | None = None,
) -> FastAPI:
    """Build the app. Tests pass fake probes; the real service probes Ollama/ComfyUI over HTTP."""

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncGenerator[None]:
        async with httpx.AsyncClient() as client:
            if probes is None:
                app.state.probes = HttpProbes(settings, client)
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
    if probes is not None:
        app.state.probes = probes
    install_error_handlers(app)
    token = settings.token.get_secret_value() if settings.token else None
    app.middleware("http")(make_access_guard(allowed_hosts, token))

    @app.get("/health")
    async def health() -> HealthStatus:
        current: Probes = app.state.probes
        return await health_status(current, settings.decider_model)

    return app
