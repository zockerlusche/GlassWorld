"""Day/night planner (B7 S4, D471 D4): kinds by phase, night budget, deferred, residency, /phase."""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any

import httpx

from conftest import TEST_HOST, TOKEN, FakeProbes
from glassworld_ai.app import create_app
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.jobs import JobOutcome, JobRunner
from glassworld_ai.planner import KindPlan, Planner, load_night_plan
from glassworld_ai.residency import Footprint, Residency
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
KINDS = {"question": KindPlan("any", "llm"), "image": KindPlan("night", "img")}


class Clock:
    def __init__(self) -> None:
        self.now = 0.0

    def __call__(self) -> float:
        return self.now


def test_the_data_file_loads() -> None:
    budget, kinds = load_night_plan(REPO / "content" / "hardware" / "night.json")
    assert budget > 0
    assert kinds["question"].when == "any"
    assert kinds["image"].when == "night"
    assert kinds["sound"].model == "moss-sound"


def test_night_kinds_only_at_night_within_the_budget() -> None:
    clock = Clock()
    planner = Planner(100, KINDS, lambda: None, lambda: "standard", clock)
    assert planner.allowed("question")
    assert not planner.allowed("image")
    planner.start_night()
    assert planner.allowed("image")
    clock.now = 99
    assert planner.budget_left_s() == 1
    clock.now = 101
    assert not planner.allowed("image")  # budget used up -> next night
    assert planner.allowed("question")


def test_comfort_runs_night_kinds_any_time() -> None:
    planner = Planner(100, KINDS, lambda: None, lambda: "comfort")
    assert planner.allowed("image")


def test_prepare_makes_the_model_resident() -> None:
    log: list[str] = []

    class Driver:
        async def load(self, model_id: str) -> None:
            log.append(f"load {model_id}")

        async def unload(self, model_id: str) -> None:
            log.append(f"unload {model_id}")

    residency = Residency(
        {
            "llm": Footprint("llm", "language", "d", 6000, 1.0, frozenset()),
            "img": Footprint("img", "image", "d", 7000, 1.0, frozenset()),
        },
        7680,
        {"d": Driver()},
    )
    planner = Planner(100, KINDS, lambda: residency, lambda: "standard")

    async def scenario() -> None:
        await planner.prepare("question")
        await planner.prepare("image")  # does not fit next to the language model

    asyncio.run(scenario())
    assert log == ["load llm", "unload llm", "load img"]


def test_runner_defers_by_day_and_runs_at_night() -> None:
    planner = Planner(
        100,
        {"question": KindPlan("any", None), "memory": KindPlan("night", None)},
        lambda: None,
        lambda: "standard",
    )
    ran: list[str] = []

    async def handler(job: GenerationJob) -> JobOutcome:
        ran.append(str(job.kind.value))
        return JobOutcome({}, "mock", "mock", None, 0, 1)

    runner = JobRunner({"question": handler, "memory": handler}, 8, gate=planner)

    def fake(kind: str, rid: str) -> Any:
        class FakeJob:
            pass

        job = FakeJob()
        job.request_id = rid  # type: ignore[attr-defined]
        job.kind = type("K", (), {"value": kind})()  # type: ignore[attr-defined]
        job.priority = 10  # type: ignore[attr-defined]
        job.model_dump = lambda mode="json": {"id": rid, "kind": kind}  # type: ignore[attr-defined]
        return job

    async def scenario() -> None:
        assert runner.submit(fake("memory", "m1"))[0] == "deferred"
        assert runner.submit(fake("question", "q1"))[0] == "queued"
        await asyncio.sleep(0.02)
        assert ran == ["question"]
        planner.start_night()
        runner.reschedule()
        await asyncio.sleep(0.02)
        assert ran == ["question", "memory"]
        assert planner.done_tonight == 1

    asyncio.run(scenario())


def test_phase_endpoints(settings: Settings) -> None:
    app = create_app(
        settings.model_copy(update={"decider_backend": "mock"}),
        allowed_hosts=frozenset({TEST_HOST}),
        probes=FakeProbes(),
        decider=MockDecider(),
    )
    validator = ContractValidator(settings.schemas_dir)

    async def scenario() -> None:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            status = (await client.get("/phase")).json()
            assert validator.errors("phase-status", status) == []
            assert status["phase"] == "day"
            night = await client.post(
                "/phase", json={"contract_version": "0.2", "phase": "night", "tick": 10}
            )
            assert night.json()["phase"] == "night"
            bad = await client.post(
                "/phase", json={"contract_version": "0.2", "phase": "dusk", "tick": 10}
            )
            assert (bad.status_code, bad.json()["code"]) == (422, "bad_request")

    asyncio.run(scenario())


def test_phase_change_is_idempotent() -> None:
    clock = Clock()
    planner = Planner(100, KINDS, lambda: None, lambda: "standard", clock)
    assert planner.change("night", 10)
    clock.now = 60
    assert not planner.change("night", 11)  # retry: same phase, budget keeps running
    assert planner.budget_left_s() == 40
    assert planner.change("day", 5)  # an older save was loaded: its phase counts
    assert planner.phase == "day"
    assert not planner.change("day", 20)


def test_decisions_wait_while_a_conflicting_night_model_is_resident() -> None:
    class Driver:
        async def load(self, model_id: str) -> None:
            return None

        async def unload(self, model_id: str) -> None:
            return None

    residency = Residency(
        {
            "llm": Footprint("llm", "language", "d", 6000, 1.0, frozenset()),
            "snd": Footprint("snd", "sound", "d", 5000, 1.0, frozenset({"llm"})),
        },
        7680,
        {"d": Driver()},
    )
    planner = Planner(
        100, {**KINDS, "sound": KindPlan("night", "snd")}, lambda: residency, lambda: "standard"
    )
    assert not planner.language_model_blocked()
    asyncio.run(residency.ensure("snd"))
    assert planner.language_model_blocked()  # /decide answers model_unavailable (routine mode)
    assert planner.affinity("sound") == 0 and planner.affinity("question") == 1


def test_a_model_larger_than_the_budget_fails_the_job_for_good() -> None:
    class Driver:
        async def load(self, model_id: str) -> None:
            return None

        async def unload(self, model_id: str) -> None:
            return None

    residency = Residency(
        {"snd": Footprint("snd", "sound", "d", 5734, 1.0, frozenset())}, 5120, {"d": Driver()}
    )
    planner = Planner(100, {"sound": KindPlan("any", "snd")}, lambda: residency, lambda: "minimum")

    async def handler(job: GenerationJob) -> JobOutcome:
        raise AssertionError("must not run")

    runner = JobRunner({"sound": handler}, 8, gate=planner)

    job = GenerationJob.model_validate(
        json.loads(
            (REPO / "contracts/examples/valid/generation-job.sound-door.json").read_text("utf-8")
        )
    )
    job_id = str(job.request_id)

    async def scenario() -> Any:
        runner.submit(job)
        for _ in range(500):  # wait for the state, not for a fixed time (review GW1 H1)
            status = runner.status(job_id)
            if status is not None and status.state.value == "failed":
                return status
            await asyncio.sleep(0.01)
        raise AssertionError("job did not fail")

    status = asyncio.run(scenario())
    assert status.error is not None
    assert (status.error.code.value, status.error.retryable) == ("model_unavailable", False)


def test_a_released_kind_changes_no_residency() -> None:
    planner = Planner(100, dict(KINDS), lambda: None, lambda: "standard")
    planner.release("image")
    assert planner.model_of("image") is None
    assert planner.night_only("image")  # still a night kind
