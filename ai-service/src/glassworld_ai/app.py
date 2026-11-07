"""FastAPI application factory."""

from __future__ import annotations

import asyncio
import json
import time
from collections.abc import AsyncGenerator, Callable
from contextlib import asynccontextmanager

import httpx
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, Response

from glassworld_ai import SERVICE_VERSION
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import (
    Decider,
    DecisionError,
    MockDecider,
    ModelSettings,
    OllamaDecider,
    ReplayDecider,
)
from glassworld_ai.errors import error_response, install_error_handlers
from glassworld_ai.hardware import HardwareInfo, detect_hardware, load_tiers, select_tier
from glassworld_ai.health import health_status
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.probes import HttpProbes, Probes
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.security import make_access_guard
from glassworld_ai.validation import ContractValidator
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import DecisionRequest, HealthStatus

READY_LINE = "AI_SERVICE_READY port={port}"


def create_app(
    settings: Settings,
    *,
    allowed_hosts: frozenset[str],
    probes: Probes | None = None,
    hardware: HardwareInfo | None = None,
    decider: Decider | None = None,
    queue: ModelQueue | None = None,
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
            if decider is None:
                app.state.decider = _build_decider(settings, client, app.state.queue)
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
    validator = ContractValidator(settings.schemas_dir)
    app.state.queue = queue or ModelQueue(settings.queue_size)

    def _set_hardware(target: FastAPI, info: HardwareInfo) -> None:
        target.state.hardware = info
        target.state.tier = select_tier(tiers, info)

    if probes is not None:
        app.state.probes = probes
    if hardware is not None:
        _set_hardware(app, hardware)
    if decider is not None:
        app.state.decider = decider
    install_error_handlers(app)
    token = settings.token.get_secret_value() if settings.token else None
    app.middleware("http")(make_access_guard(allowed_hosts, token))

    @app.get("/health")
    async def health() -> HealthStatus:
        current: Probes = app.state.probes
        info: HardwareInfo = app.state.hardware
        tier: str | None = app.state.tier
        return await health_status(current, settings.decider_model, info, tier)

    @app.post("/decide")
    async def decide(request: Request) -> Response:
        received = time.monotonic()
        try:
            data: object = json.loads(await request.body())
        except (json.JSONDecodeError, UnicodeDecodeError):
            return error_response("bad_request", "Request body is not valid JSON.")
        problems = validator.errors("decision-request", data)
        if problems:
            return error_response(
                "bad_request", "Invalid DecisionRequest: " + "; ".join(problems[:3])
            )
        decision_request = DecisionRequest.model_validate(data)
        request_id = str(getattr(decision_request.request_id, "root", decision_request.request_id))
        deadline = received + decision_request.deadline_ms / 1000
        current: Decider = app.state.decider
        try:
            answer = await current.decide(decision_request, deadline)
        except DecisionError as error:
            return error_response(
                error.code, str(error), request_id=request_id, tick=decision_request.tick
            )
        return JSONResponse(answer.model_dump(mode="json"))

    return app


def _build_decider(settings: Settings, client: httpx.AsyncClient, queue: ModelQueue) -> Decider:
    vocab = Vocabulary.load(settings.content_dir / "vocabulary")
    log = CallLog(settings.log_dir / "decider.jsonl")
    if settings.decider_backend == "mock":
        return MockDecider()
    if settings.decider_backend == "replay":
        return ReplayDecider(log, vocab)
    return OllamaDecider(
        client=OllamaClient(
            str(settings.ollama_url), settings.decider_model, client, settings.keep_alive
        ),
        queue=queue,
        template=load_prompt(
            settings.prompts_dir, settings.decider_prompt, settings.decider_prompt_version
        ),
        vocab=vocab,
        settings=ModelSettings(
            temperature=settings.temperature,
            num_ctx=settings.num_ctx,
            char_budget=settings.prompt_char_budget,
            max_retries=settings.max_retries,
        ),
        log=log,
    )
