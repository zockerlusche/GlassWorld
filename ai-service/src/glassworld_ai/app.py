"""FastAPI application factory."""

from __future__ import annotations

import asyncio
import contextlib
import json
import re
import time
from collections.abc import AsyncGenerator, Callable, Mapping
from contextlib import asynccontextmanager
from typing import cast

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
from glassworld_ai.jobs import Handler, JobConflictError, JobRunner, JobsFullError
from glassworld_ai.llm.ollama import (
    OllamaClient,
    OllamaResponseError,
    OllamaUnavailableError,
)
from glassworld_ai.probes import HttpProbes, Probes
from glassworld_ai.prompts import load_prompt
from glassworld_ai.questions.handlers import (
    MockQuestions,
    OllamaQuestions,
    QuestionSettings,
    ReplayQuestions,
)
from glassworld_ai.queue import ModelQueue
from glassworld_ai.security import make_access_guard
from glassworld_ai.validation import ContractValidator
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import (
    CONTRACT_VERSION,
    DecisionRequest,
    GenerationJob,
    HealthStatus,
    JobAccepted,
    JobList,
)

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
    request_exit: Callable[[], None] | None = None,
    transport: httpx.AsyncBaseTransport | None = None,
    job_handlers: Mapping[str, Handler] | None = None,
) -> FastAPI:
    """Build the app.

    Tests pass fake probes and fake hardware; the real service probes Ollama/ComfyUI over HTTP
    and detects the hardware once at start. `request_exit` stops the server (set by __main__);
    POST /shutdown calls it. The decider model is unloaded on every way out (lifespan end).
    """
    tiers = load_tiers(settings.hardware_tiers_file)  # fail fast on a broken data file

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncGenerator[None]:
        async with httpx.AsyncClient(transport=transport) as client:
            ollama = OllamaClient(
                str(settings.ollama_url), settings.decider_model, client, settings.keep_alive
            )
            app.state.ollama = ollama
            if probes is None:
                app.state.probes = HttpProbes(settings, client)
            if hardware is None:
                _set_hardware(app, await asyncio.to_thread(detect_hardware))
            if decider is None:
                app.state.decider = _build_decider(settings, ollama, app.state.queue)
            if job_handlers is None and settings.decider_backend == "ollama":
                app.state.jobs.register("question", _ollama_questions(settings, ollama, app))
            warm_task: asyncio.Task[None] | None = None
            if settings.warm_up and settings.decider_backend == "ollama":
                app.state.warm_up = "starting"
                warm_task = asyncio.create_task(_warm_up(app, ollama, settings.warm_up_retry_s))
            if on_started is not None:
                on_started()
            try:
                yield
            finally:
                if warm_task is not None:
                    warm_task.cancel()
                    with contextlib.suppress(asyncio.CancelledError):
                        await warm_task
                # Every way out (POST /shutdown, parent gone, signal) frees the VRAM: first close
                # the queue (waiting calls end at once, nothing reloads the model), then unload.
                app.state.stopping = True
                await app.state.queue.close()
                await app.state.jobs.close()
                if settings.decider_backend == "ollama":
                    with contextlib.suppress(
                        OllamaUnavailableError, OllamaResponseError, httpx.HTTPError
                    ):
                        await ollama.unload(timeout=10)

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
    # Job kinds get their handlers in their own steps (question: B4a S2, image: B7 S5, ...).
    # The Ollama question handler needs the HTTP client and is registered in the lifespan.
    app.state.jobs = JobRunner(
        job_handlers if job_handlers is not None else _offline_handlers(settings),
        settings.job_capacity,
    )

    def _set_hardware(target: FastAPI, info: HardwareInfo) -> None:
        target.state.hardware = info
        target.state.tier = select_tier(tiers, info)

    if probes is not None:
        app.state.probes = probes
    if hardware is not None:
        _set_hardware(app, hardware)
    if decider is not None:
        app.state.decider = decider
    app.state.warm_up = "done"
    app.state.ollama = None
    app.state.stopping = False
    install_error_handlers(app)
    token = settings.token.get_secret_value() if settings.token else None
    app.middleware("http")(make_access_guard(allowed_hosts, token))

    @app.get("/health")
    async def health() -> HealthStatus:
        current: Probes = app.state.probes
        info: HardwareInfo = app.state.hardware
        tier: str | None = app.state.tier
        return await health_status(
            current,
            settings.decider_model,
            info,
            tier,
            app.state.warm_up,
            tier_gate=settings.decider_backend == "ollama",  # mock/replay need no GPU
        )

    @app.post("/shutdown", status_code=202)
    async def shutdown() -> dict[str, str]:
        """Stop the server (ADR-0006). The model is unloaded when the app ends (lifespan)."""
        app.state.stopping = True  # no new model calls: they would load the model again
        await app.state.queue.close()  # waiting calls end now with model_unavailable
        await app.state.jobs.close()
        if request_exit is not None:
            asyncio.get_running_loop().call_soon(request_exit)
        return {"status": "stopping"}

    @app.post("/jobs")
    async def submit_job(request: Request) -> Response:
        """Accept a generation job (B7 R1); 202 with JobAccepted, also for a known job id."""
        body = await _read_body(request)
        if body is None:
            return error_response("bad_request", f"Request body above {MAX_BODY_BYTES} bytes.")
        try:
            data: object = json.loads(body)
        except (json.JSONDecodeError, UnicodeDecodeError):
            return error_response("bad_request", "Request body is not valid JSON.")
        problems = validator.errors("generation-job", data)
        rid, rtick = _ids_if_valid(data)
        if problems:
            return error_response(
                "bad_request",
                "Invalid GenerationJob: " + "; ".join(problems[:3]),
                request_id=rid,
                tick=rtick,
            )
        job = GenerationJob.model_validate(data)
        kind = str(job.kind.value)
        parts = sorted(job.payload.model_dump(exclude_none=True))
        if parts != [kind]:
            return error_response(
                "bad_request",
                f"payload must hold exactly the part '{kind}', got {parts}",
                request_id=rid,
                tick=rtick,
            )
        runner: JobRunner = app.state.jobs
        if app.state.stopping:
            return error_response(
                "model_unavailable", "Service is stopping.", request_id=rid, tick=rtick
            )
        if settings.decider_backend == "ollama" and getattr(app.state, "tier", "unknown") is None:
            # R9 like /decide: below the minimum tier no AI jobs; the hardware does not change.
            return error_response(
                "model_unavailable",
                "Hardware is below the minimum tier; routine mode only.",
                request_id=rid,
                tick=rtick,
                retryable=False,
            )
        if not runner.supports(kind):
            return error_response(
                "model_unavailable",
                f"Job kind '{kind}' is not supported by this service version.",
                request_id=rid,
                tick=rtick,
                retryable=False,
            )
        try:
            state, _ = runner.submit(job)
        except JobConflictError as error:
            return error_response("bad_request", str(error), request_id=rid, tick=rtick)
        except JobsFullError as error:
            return error_response("overloaded", str(error), request_id=rid, tick=rtick)
        accepted = JobAccepted.model_validate(
            {"contract_version": CONTRACT_VERSION, "job_id": rid, "state": state}
        )
        return JSONResponse(status_code=202, content=accepted.model_dump(mode="json"))

    @app.get("/jobs/{job_id}")
    async def job_status(job_id: str) -> Response:
        runner: JobRunner = app.state.jobs
        status = runner.status(job_id)
        if status is None:
            # Retryable: the service may have restarted; the core sends a new attempt (new id).
            return error_response(
                "not_found",
                "Unknown job id (the service may have restarted).",
                request_id=job_id if _UUID.fullmatch(job_id) else None,
                retryable=True,
            )
        return JSONResponse(status.model_dump(mode="json", exclude_unset=True))

    @app.delete("/jobs/{job_id}", status_code=204)
    async def cancel_job(job_id: str) -> Response:
        """Forget a job (orphaned after loading a save); idempotent, unknown ids are fine."""
        runner: JobRunner = app.state.jobs
        runner.cancel(job_id)
        return Response(status_code=204)

    @app.get("/jobs")
    async def list_jobs(state: str | None = None) -> Response:
        if state is not None and state not in _JOB_STATES:
            return error_response("bad_request", f"Unknown job state '{state}'.")
        runner: JobRunner = app.state.jobs
        listing = JobList.model_validate(
            {"contract_version": CONTRACT_VERSION, "jobs": runner.listing(state)[:1024]}
        )
        return JSONResponse(listing.model_dump(mode="json"))

    @app.post("/decide")
    async def decide(request: Request) -> Response:
        received = time.monotonic()
        body = await _read_body(request)
        if body is None:
            return error_response("bad_request", f"Request body above {MAX_BODY_BYTES} bytes.")
        try:
            data: object = json.loads(body)
        except (json.JSONDecodeError, UnicodeDecodeError):
            return error_response("bad_request", "Request body is not valid JSON.")
        problems = validator.errors("decision-request", data)
        if problems:
            rid, rtick = _ids_if_valid(data)
            return error_response(
                "bad_request",
                "Invalid DecisionRequest: " + "; ".join(problems[:3]),
                request_id=rid,
                tick=rtick,
            )
        decision_request = DecisionRequest.model_validate(data)
        request_id = str(getattr(decision_request.request_id, "root", decision_request.request_id))
        deadline = received + decision_request.deadline_ms / 1000
        if app.state.stopping:
            return error_response(
                "model_unavailable",
                "Service is stopping.",
                request_id=request_id,
                tick=decision_request.tick,
            )
        if settings.decider_backend == "ollama" and getattr(app.state, "tier", "unknown") is None:
            # R9: not retryable - the hardware does not change, the core stays in routine mode.
            return error_response(
                "model_unavailable",
                "Hardware is below the minimum tier; routine mode only.",
                request_id=request_id,
                tick=decision_request.tick,
                retryable=False,
            )
        current: Decider = app.state.decider
        try:
            answer = await current.decide(decision_request, deadline)
        except DecisionError as error:
            return error_response(
                error.code, str(error), request_id=request_id, tick=decision_request.tick
            )
        except Exception as error:
            return error_response(
                "internal", type(error).__name__, request_id=request_id, tick=decision_request.tick
            )
        return JSONResponse(answer.model_dump(mode="json"))

    return app


MAX_BODY_BYTES = 256 * 1024  # largest request; real ones are a few KiB


async def _read_body(request: Request) -> bytes | None:
    """The request body, or None if it is larger than MAX_BODY_BYTES (read stops there)."""
    chunks: list[bytes] = []
    size = 0
    async for chunk in request.stream():
        size += len(chunk)
        if size > MAX_BODY_BYTES:
            return None
        chunks.append(chunk)
    return b"".join(chunks)


_JOB_STATES = frozenset({"queued", "running", "done", "failed", "deferred"})
_UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


def _ids_if_valid(data: object) -> tuple[str | None, int | None]:
    """request_id and tick of an invalid request, if they themselves are valid (for the core)."""
    if not isinstance(data, dict):
        return None, None
    fields = cast(dict[str, object], data)
    rid = fields.get("request_id")
    tick = fields.get("tick")
    valid_rid = rid if isinstance(rid, str) and _UUID.fullmatch(rid) else None
    valid_tick = (
        tick if isinstance(tick, int) and not isinstance(tick, bool) and tick >= 0 else None
    )
    return valid_rid, valid_tick


async def _warm_up(app: FastAPI, ollama: OllamaClient, retry_s: float) -> None:
    """Load the model; on failure stay "failed" (degraded) and try again after `retry_s`."""
    while True:
        try:
            await ollama.warm_up(timeout=300)
            app.state.warm_up = "done"
            return
        except (OllamaUnavailableError, OllamaResponseError, httpx.HTTPError):
            app.state.warm_up = "failed"
        await asyncio.sleep(retry_s)


def _offline_handlers(settings: Settings) -> dict[str, Handler]:
    """Handlers that need no model client: mock and replay."""
    vocab = Vocabulary.load(settings.content_dir / "vocabulary")
    if settings.decider_backend == "mock":
        return {"question": MockQuestions(vocab)}
    if settings.decider_backend == "replay":
        template = load_prompt(
            settings.prompts_dir, settings.question_prompt, settings.question_prompt_version
        )
        return {"question": ReplayQuestions(_question_log(settings), template, vocab)}
    return {}


def _question_log(settings: Settings) -> CallLog:
    return CallLog(settings.log_dir / "questions.jsonl")


def _ollama_questions(settings: Settings, ollama: OllamaClient, app: FastAPI) -> OllamaQuestions:
    return OllamaQuestions(
        client=ollama,
        queue=app.state.queue,
        template=load_prompt(
            settings.prompts_dir, settings.question_prompt, settings.question_prompt_version
        ),
        vocab=Vocabulary.load(settings.content_dir / "vocabulary"),
        settings=QuestionSettings(
            temperature=settings.temperature,
            num_ctx=settings.num_ctx,
            max_retries=settings.max_retries,
            call_timeout_s=settings.question_call_timeout_s,
        ),
        log=_question_log(settings),
    )


def _build_decider(settings: Settings, ollama: OllamaClient, queue: ModelQueue) -> Decider:
    vocab = Vocabulary.load(settings.content_dir / "vocabulary")
    log = CallLog(settings.log_dir / "decider.jsonl")
    if settings.decider_backend == "mock":
        return MockDecider()
    if settings.decider_backend == "replay":
        return ReplayDecider(log, vocab)
    return OllamaDecider(
        client=ollama,
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
