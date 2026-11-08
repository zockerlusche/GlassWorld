"""FastAPI application factory."""

from __future__ import annotations

import asyncio
import contextlib
import json
import re
import time
from collections.abc import AsyncGenerator, Awaitable, Callable, Mapping
from contextlib import asynccontextmanager
from typing import cast

import httpx
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, Response

from glassworld_ai import SERVICE_VERSION
from glassworld_ai.benchmark import Benchmark, HandlerProbe, OllamaLanguageProbe
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
from glassworld_ai.dream import MockDreams, OllamaDreams, ReplayDreams
from glassworld_ai.errors import error_response, install_error_handlers
from glassworld_ai.hardware import HardwareInfo, detect_hardware, load_tiers, select_tier
from glassworld_ai.health import health_status
from glassworld_ai.help import HelpSuggester, MockHelp, OllamaHelp, ReplayHelp
from glassworld_ai.image import ComfyImages, MockImages, load_image_settings
from glassworld_ai.invention.flow import (
    MockInventions,
    OllamaInventions,
    ReplayInventions,
    safety_categories,
    tech_levels,
)
from glassworld_ai.jobs import Handler, JobConflictError, JobFailedError, JobRunner, JobsFullError
from glassworld_ai.llm.jobcall import ModelCallSettings
from glassworld_ai.llm.ollama import (
    OllamaClient,
    OllamaResponseError,
    OllamaUnavailableError,
)
from glassworld_ai.memory.condense import MockMemory, OllamaMemory, ReplayMemory
from glassworld_ai.model3d import Model3dJobs, load_model3d_settings
from glassworld_ai.planner import Planner, load_night_plan
from glassworld_ai.probes import HttpProbes, Probes
from glassworld_ai.prompts import PromptTemplate, load_prompt
from glassworld_ai.questions.handlers import MockQuestions, OllamaQuestions, ReplayQuestions
from glassworld_ai.queue import ModelQueue
from glassworld_ai.residency import (
    ComfyUIDriver,
    OllamaDriver,
    Residency,
    ResidencyError,
    budget_mib,
    load_footprints,
)
from glassworld_ai.safety import TextGuard
from glassworld_ai.security import make_access_guard
from glassworld_ai.sound import MockSound, MossSound, MossWorker, load_generation_settings
from glassworld_ai.validation import ContractValidator
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import (
    CONTRACT_VERSION,
    DecisionRequest,
    GenerationJob,
    HealthStatus,
    HelpSuggestionsRequest,
    JobAccepted,
    JobList,
    PhaseChange,
    PhaseStatus,
)

READY_LINE = "AI_SERVICE_READY port={port}"


def create_app(
    settings: Settings,
    *,
    allowed_hosts: frozenset[str],
    probes: Probes | None = None,
    hardware: HardwareInfo | None = None,
    decider: Decider | None = None,
    helper: HelpSuggester | None = None,
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
    game_vram, footprints = load_footprints(settings.model_footprints_file)
    night_budget, night_kinds = load_night_plan(settings.night_plan_file)

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
            residency: Residency = app.state.residency  # exists once the hardware is known
            residency.register_driver(
                "ollama", OllamaDriver(str(settings.ollama_url), client, settings.keep_alive)
            )
            residency.register_driver("comfyui", ComfyUIDriver(str(settings.comfyui_url), client))
            if decider is None:
                app.state.decider = _build_decider(settings, ollama, app.state.queue)
            if helper is None:
                app.state.helper = _build_helper(settings, ollama, app.state.queue)
            if job_handlers is None and settings.image_backend == "comfyui":
                app.state.jobs.register("image", _comfy_images(settings, client, app))
            if job_handlers is None and settings.decider_backend == "ollama":
                app.state.jobs.register("question", _ollama_questions(settings, ollama, app))
                app.state.jobs.register("memory", _ollama_memory(settings, ollama, app))
                app.state.jobs.register("dream", _ollama_dreams(settings, ollama, app))
                app.state.jobs.register("invention", _ollama_inventions(settings, ollama, app))
            warm_task: asyncio.Task[None] | None = None
            if settings.warm_up and settings.decider_backend == "ollama":
                app.state.warm_up = "starting"
                warm_task = asyncio.create_task(_warm_up(app, ollama, settings.warm_up_retry_s))
            app.state.benchmark = _build_benchmark(settings, client, app)
            first_run: asyncio.Task[None] | None = None
            if (
                settings.benchmark_on_first_start
                and app.state.benchmark.state == "none"
                and app.state.benchmark.available
                and getattr(app.state, "tier", None) is not None
            ):
                first_run = asyncio.create_task(_first_benchmark(app, warm_task))
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
                if first_run is not None:
                    first_run.cancel()
                    with contextlib.suppress(asyncio.CancelledError):
                        await first_run
                await app.state.benchmark.close()
                await app.state.jobs.close()
                with contextlib.suppress(ResidencyError):
                    await app.state.residency.release_all()  # also image/sound models (R3)
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
    planner = Planner(
        night_budget,
        night_kinds,
        residency=lambda: getattr(app.state, "residency", None),
        tier=lambda: getattr(app.state, "tier", None),
    )
    app.state.planner = planner
    app.state.jobs = JobRunner(
        job_handlers if job_handlers is not None else _offline_handlers(settings),
        settings.job_capacity,
        gate=planner,
    )
    if job_handlers is None:
        app.state.jobs.register("sound", _sound_handler(settings))
        app.state.jobs.register(
            "model3d",
            Model3dJobs(
                load_model3d_settings(settings.content_dir),
                settings.model3d_image_dir,
                settings.model3d_output_dir,
                lambda: getattr(app.state, "tier", None),
                settings.model3d_backend,
            ),
        )
    # Without the model clients (tests, before the lifespan) the run has nothing to measure.
    app.state.benchmark = Benchmark(
        store=settings.benchmark_file,
        runner=app.state.jobs,
        residency=lambda: None,
        hardware=lambda: (None, "", 0),
        answer_tokens=settings.benchmark_answer_tokens,
        language=None,
        image=None,
        sound=None,
        on_result=planner.set_job_seconds,
        restore=_nothing,
    )
    if job_handlers is None and settings.image_backend == "mock":
        planner.release("image")
        app.state.jobs.register(
            "image",
            MockImages(
                load_image_settings(settings.content_dir),
                settings.prompts_dir,
                settings.image_output_dir,
                _image_tier(app),
                _text_guard(settings),
            ),
        )

    def _set_hardware(target: FastAPI, info: HardwareInfo) -> None:
        target.state.hardware = info
        target.state.tier = select_tier(tiers, info)
        # B7 S2: budget = largest GPU minus the game; drivers come with the HTTP client.
        target.state.residency = Residency(footprints, budget_mib(info, game_vram), {})

    if probes is not None:
        app.state.probes = probes
    if hardware is not None:
        _set_hardware(app, hardware)
    if decider is not None:
        app.state.decider = decider
    if helper is not None:
        app.state.helper = helper
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

    def _phase_status() -> PhaseStatus:
        runner: JobRunner = app.state.jobs
        counts = runner.counts()
        return PhaseStatus.model_validate(
            {
                "contract_version": CONTRACT_VERSION,
                "phase": planner.phase,
                "budget_left_s": planner.budget_left_s(),
                "queued": counts["queued"],
                "running": counts["running"],
                "deferred": counts["deferred"],
                "done_tonight": planner.done_tonight,
            }
        )

    @app.post("/phase")
    async def change_phase(request: Request) -> Response:
        """Day/night from the core (B7 R3): night kinds may run in the night budget."""
        body = await _read_body(request)
        if body is None:
            return error_response("bad_request", f"Request body above {MAX_BODY_BYTES} bytes.")
        try:
            data: object = json.loads(body)
        except (json.JSONDecodeError, UnicodeDecodeError):
            return error_response("bad_request", "Request body is not valid JSON.")
        problems = validator.errors("phase-change", data)
        if problems:
            return error_response("bad_request", "Invalid PhaseChange: " + "; ".join(problems[:3]))
        change = PhaseChange.model_validate(data)
        phase = str(getattr(change.phase, "value", change.phase))
        changed = planner.change(phase, change.tick)
        if changed and phase == "day":
            model = planner.model_of("question")
            residency = getattr(app.state, "residency", None)
            if model and residency is not None and residency.knows(model):
                # Morning: the language model comes back for the day (runs in the background).
                app.state.morning = asyncio.get_running_loop().create_task(
                    _quietly(residency.ensure(model))
                )
        app.state.jobs.reschedule()
        return JSONResponse(_phase_status().model_dump(mode="json"))

    @app.get("/phase")
    async def phase_status() -> Response:
        return JSONResponse(_phase_status().model_dump(mode="json"))

    @app.get("/benchmark")
    async def benchmark_status() -> Response:
        """B7 S7: the measurement run's state and last result."""
        return JSONResponse(app.state.benchmark.status().model_dump(mode="json"))

    @app.post("/benchmark")
    async def benchmark_start() -> Response:
        """Measure again (or for the first time); runs in the background, poll GET /benchmark."""
        run: Benchmark = app.state.benchmark
        if not run.available:
            return error_response(
                "model_unavailable", "No local model to measure (mock backends).", retryable=False
            )
        if app.state.stopping:
            return error_response("model_unavailable", "Service is stopping.")
        run.start()
        return JSONResponse(run.status().model_dump(mode="json"), status_code=202)

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
        if settings.decider_backend == "ollama" and planner.language_model_blocked():
            # Night (review GW1 P1): a model that must not share the card is resident (e.g. sound);
            # loading the language model now would overflow the VRAM. Routine mode for a moment.
            return error_response(
                "model_unavailable",
                "Computing night: the language model is unloaded for another model.",
                request_id=request_id,
                tick=decision_request.tick,
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

    @app.post("/help-suggestions")
    async def help_suggestions(request: Request) -> Response:
        """B6 R8/R10 (F5238): 1-3 calm suggestions for the player; the player waits (PLAYER)."""
        received = time.monotonic()
        body = await _read_body(request)
        if body is None:
            return error_response("bad_request", f"Request body above {MAX_BODY_BYTES} bytes.")
        try:
            data: object = json.loads(body)
        except (json.JSONDecodeError, UnicodeDecodeError):
            return error_response("bad_request", "Request body is not valid JSON.")
        problems = validator.errors("help-suggestions-request", data)
        if problems:
            rid, rtick = _ids_if_valid(data)
            return error_response(
                "bad_request",
                "Invalid HelpSuggestionsRequest: " + "; ".join(problems[:3]),
                request_id=rid,
                tick=rtick,
            )
        help_request = HelpSuggestionsRequest.model_validate(data)
        request_id = str(getattr(help_request.request_id, "root", help_request.request_id))
        deadline = received + help_request.deadline_ms / 1000
        tick = help_request.tick
        if app.state.stopping:
            return error_response(
                "model_unavailable", "Service is stopping.", request_id=request_id, tick=tick
            )
        if settings.decider_backend == "ollama" and (
            getattr(app.state, "tier", "unknown") is None or planner.language_model_blocked()
        ):
            return error_response(
                "model_unavailable",
                "The language model is not available now.",
                request_id=request_id,
                tick=tick,
            )
        current: HelpSuggester = app.state.helper
        try:
            answer = await current.suggest(help_request, deadline)
        except JobFailedError as error:
            return error_response(
                error.code, str(error), request_id=request_id, tick=tick, retryable=error.retryable
            )
        except Exception as error:
            return error_response(
                "internal", type(error).__name__, request_id=request_id, tick=tick
            )
        return JSONResponse(answer.model_dump(mode="json"))

    return app


MAX_BODY_BYTES = 256 * 1024  # largest request; real ones are a few KiB


async def _quietly(work: Awaitable[None]) -> None:
    """Background model load: a failure only means the next job loads it (logged by the driver)."""
    with contextlib.suppress(Exception):
        await work


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
            residency: Residency = app.state.residency
            if residency.knows(ollama.model):
                # Through the residency: it unloads never-together models (e.g. sound) first and
                # refuses a model above the budget. A refusal is a failed warm-up, never a
                # load past the budget (review GW1 N1).
                await residency.ensure(ollama.model)
            else:
                await ollama.warm_up(timeout=300)  # model without footprint: not tracked
            app.state.warm_up = "done"
            return
        except (OllamaUnavailableError, OllamaResponseError, httpx.HTTPError, ResidencyError):
            app.state.warm_up = "failed"
        await asyncio.sleep(retry_s)


def _sound_handler(settings: Settings) -> Handler:
    """Sound jobs (D471 D7): the MOSS worker if configured, otherwise the deterministic mock."""
    generation = load_generation_settings(settings.content_dir)
    if settings.sound_backend == "moss":
        if (
            settings.moss_python is None
            or settings.moss_model_dir is None
            or settings.moss_code_dir is None
        ):
            raise ValueError(
                "sound_backend moss needs moss_python, moss_model_dir and moss_code_dir"
            )
        worker = MossWorker(
            settings.moss_python,
            settings.moss_worker_script,
            settings.moss_model_dir,
            settings.moss_code_dir,
            settings.moss_start_timeout_s,
        )
        return MossSound(
            worker, settings.sound_output_dir, generation, settings.sound_render_timeout_s
        )
    return MockSound(settings.sound_output_dir, generation)


def _text_guard(settings: Settings) -> TextGuard:
    return TextGuard.from_file(settings.content_dir / "safety" / "rules.json")


async def _nothing() -> None:
    return None


async def _first_benchmark(app: FastAPI, warm_task: asyncio.Task[None] | None) -> None:
    """Measure once after the first start, when the warm-up is over (B7 R6)."""
    if warm_task is not None:
        with contextlib.suppress(Exception):
            await warm_task
    run: Benchmark = app.state.benchmark
    run.start()


def _bench_job(kind: str, payload: dict[str, object], seed: int) -> GenerationJob:
    return GenerationJob.model_validate(
        {
            "contract_version": CONTRACT_VERSION,
            "request_id": BENCH_IDS[kind],
            "tick": 0,
            "seed": seed,
            "display_locale": "en",
            "kind": kind,
            "priority": 0,
            "inhabitant": None,
            "object": None,
            "payload": {kind: payload},
        }
    )


BENCH_IDS = {
    "image": "00000000-0000-8000-8000-0000000b0001",
    "sound": "00000000-0000-8000-8000-0000000b0002",
}


def _build_benchmark(settings: Settings, client: httpx.AsyncClient, app: FastAPI) -> Benchmark:
    """The measurement run with probes for the models this PC really uses (B7 S7)."""
    runner: JobRunner = app.state.jobs
    planner: Planner = app.state.planner
    tier: str | None = getattr(app.state, "tier", None)
    language = (
        OllamaLanguageProbe(
            str(settings.ollama_url), client, settings.decider_model, settings.keep_alive
        )
        if settings.decider_backend == "ollama"
        else None
    )
    image: HandlerProbe | None = None
    image_handler = runner.handler("image")
    plans = load_image_settings(settings.content_dir).by_tier
    plan = plans.get(tier) if tier is not None else None
    if settings.image_backend == "comfyui" and image_handler is not None and plan is not None:
        width, height = load_image_settings(settings.content_dir).sizes["icon"]
        image_payload: dict[str, object] = {
            "subject": "a round clay bowl",
            "purpose": "icon",
            "style": "warm_comic",
            "width": width,
            "height": height,
            "object_type": None,
        }
        image = HandlerProbe(
            plan.model, image_handler, lambda seed: _bench_job("image", image_payload, seed)
        )
    sound: HandlerProbe | None = None
    sound_handler = runner.handler("sound")
    if settings.sound_backend == "moss" and sound_handler is not None:
        generation = json.loads(
            (settings.content_dir / "sound" / "generation.json").read_text(encoding="utf-8")
        )
        sound_payload: dict[str, object] = {
            "description": "wooden door creaks",
            "kind": "one_shot",
            "seconds": generation["seconds"]["one_shot"],
            "steps": generation["default_steps"],
            "object_type": None,
        }
        sound = HandlerProbe(
            "moss-sound", sound_handler, lambda seed: _bench_job("sound", sound_payload, seed)
        )

    def hardware() -> tuple[str | None, str, int]:
        info: HardwareInfo | None = getattr(app.state, "hardware", None)
        if info is None or not info.gpus:
            return tier, "", 0
        largest = max(info.gpus, key=lambda g: g.vram_mib)
        return tier, largest.name, largest.vram_mib

    async def restore() -> None:
        residency: Residency | None = getattr(app.state, "residency", None)
        model = settings.decider_model
        if planner.phase == "day" and residency is not None and residency.knows(model):
            await residency.ensure(model)  # the language model back for the day

    def measuring(on: bool) -> None:
        planner.measuring = on

    return Benchmark(
        store=settings.benchmark_file,
        runner=runner,
        residency=lambda: getattr(app.state, "residency", None),
        hardware=hardware,
        answer_tokens=settings.benchmark_answer_tokens,
        language=language,
        image=image,
        sound=sound,
        on_result=planner.set_job_seconds,
        restore=restore,
        measuring=measuring,
    )


def _image_tier(app: FastAPI) -> Callable[[], str | None]:
    return lambda: getattr(app.state, "tier", None)


def _comfy_images(settings: Settings, client: httpx.AsyncClient, app: FastAPI) -> ComfyImages:
    async def ensure(model_id: str) -> None:
        residency: Residency | None = getattr(app.state, "residency", None)
        if residency is not None and residency.knows(model_id):
            await residency.ensure(model_id)

    return ComfyImages(
        base_url=str(settings.comfyui_url),
        client=client,
        settings=load_image_settings(settings.content_dir),
        workflows_dir=settings.image_workflows_dir,
        prompts_dir=settings.prompts_dir,
        content_dir=settings.content_dir,
        output_dir=settings.image_output_dir,
        tier=_image_tier(app),
        ensure_model=ensure,
        poll_s=settings.image_poll_s,
        timeout_s=settings.image_timeout_s,
        guard=_text_guard(settings),
    )


def _offline_handlers(settings: Settings) -> dict[str, Handler]:
    """Handlers that need no model client: mock and replay."""
    vocab = Vocabulary.load(settings.content_dir / "vocabulary")
    if settings.decider_backend == "mock":
        return {
            "question": MockQuestions(vocab),
            "memory": MockMemory(),
            "dream": MockDreams(),
            "invention": MockInventions(vocab),
        }
    if settings.decider_backend == "replay":
        template = load_prompt(
            settings.prompts_dir, settings.question_prompt, settings.question_prompt_version
        )
        return {
            "question": ReplayQuestions(_question_log(settings), template, vocab),
            "memory": ReplayMemory(_memory_log(settings)),
            "dream": ReplayDreams(_dream_log(settings), _text_guard(settings)),
            "invention": ReplayInventions(
                _invention_log(settings),
                *_invention_prompts(settings),
                vocab,
                tech_levels(settings.content_dir),
                safety_categories(settings.content_dir),
            ),
        }
    return {}


def _invention_log(settings: Settings) -> CallLog:
    return CallLog(settings.log_dir / "inventions.jsonl")


def _invention_prompts(settings: Settings) -> tuple[PromptTemplate, PromptTemplate]:
    return (
        load_prompt(
            settings.prompts_dir, settings.invention_prompt, settings.invention_prompt_version
        ),
        load_prompt(settings.prompts_dir, settings.guard_prompt, settings.guard_prompt_version),
    )


def _ollama_inventions(settings: Settings, ollama: OllamaClient, app: FastAPI) -> OllamaInventions:
    inventor, guard = _invention_prompts(settings)
    return OllamaInventions(
        client=ollama,
        queue=app.state.queue,
        inventor=inventor,
        guard=guard,
        vocab=Vocabulary.load(settings.content_dir / "vocabulary"),
        levels=tech_levels(settings.content_dir),
        settings=_call_settings(settings),
        log=_invention_log(settings),
        safety=safety_categories(settings.content_dir),
    )


def _dream_log(settings: Settings) -> CallLog:
    return CallLog(settings.log_dir / "dreams.jsonl")


def _ollama_dreams(settings: Settings, ollama: OllamaClient, app: FastAPI) -> OllamaDreams:
    return OllamaDreams(
        client=ollama,
        queue=app.state.queue,
        template=load_prompt(
            settings.prompts_dir, settings.dream_prompt, settings.dream_prompt_version
        ),
        settings=_call_settings(settings),
        log=_dream_log(settings),
        guard=_text_guard(settings),
    )


def _memory_log(settings: Settings) -> CallLog:
    return CallLog(settings.log_dir / "memory.jsonl")


def _call_settings(settings: Settings) -> ModelCallSettings:
    return ModelCallSettings(
        temperature=settings.temperature,
        num_ctx=settings.num_ctx,
        max_retries=settings.max_retries,
        call_timeout_s=settings.question_call_timeout_s,
    )


def _ollama_memory(settings: Settings, ollama: OllamaClient, app: FastAPI) -> OllamaMemory:
    return OllamaMemory(
        client=ollama,
        queue=app.state.queue,
        template=load_prompt(
            settings.prompts_dir, settings.memory_prompt, settings.memory_prompt_version
        ),
        settings=_call_settings(settings),
        log=_memory_log(settings),
    )


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
        settings=_call_settings(settings),
        log=_question_log(settings),
    )


def _build_helper(settings: Settings, ollama: OllamaClient, queue: ModelQueue) -> HelpSuggester:
    guard = TextGuard.from_file(settings.content_dir / "safety" / "rules.json")
    log = CallLog(settings.log_dir / "help.jsonl")
    if settings.decider_backend == "mock":
        return MockHelp()
    if settings.decider_backend == "replay":
        return ReplayHelp(log, guard)
    return OllamaHelp(
        client=ollama,
        queue=queue,
        template=load_prompt(
            settings.prompts_dir, settings.help_prompt, settings.help_prompt_version
        ),
        settings=_call_settings(settings),
        log=log,
        guard=guard,
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
