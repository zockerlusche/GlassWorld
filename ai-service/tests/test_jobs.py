"""Generation jobs (B7 S1): POST /jobs, GET /jobs/{id}, GET /jobs?state=."""

from __future__ import annotations

import asyncio
import copy
import json
from collections.abc import Awaitable, Callable, Mapping
from pathlib import Path
from typing import Any

import httpx
import pytest
from fastapi import FastAPI

from conftest import TEST_HOST, TOKEN, FakeProbes, post
from glassworld_ai.app import create_app
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.jobs import Handler, JobFailedError, JobOutcome
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

EXAMPLES = Path(__file__).resolve().parents[2] / "contracts" / "examples" / "valid"
JOB: dict[str, Any] = json.loads(
    (EXAMPLES / "generation-job.question-berries.json").read_text(encoding="utf-8")
)
RESULT: dict[str, object] = {
    "question": {
        "understood": True,
        "guesses": [{"property": "edible", "value": True, "confidence": 0.4}],
        "idea": "inspect",
        "thought": None,
        "rationale": "Unknown red berries; look closer first.",
    }
}


def job(n: int, priority: int = 40) -> dict[str, Any]:
    """Example job with its own request id (UUIDv8 layout) and priority."""
    copied = copy.deepcopy(JOB)
    copied["request_id"] = f"00000000-0000-8000-8000-{n:012d}"
    copied["priority"] = priority
    return copied


def outcome() -> JobOutcome:
    return JobOutcome(RESULT, "mock", "mock", "question.v1", 1, 1)


def make(settings: Settings, handlers: Mapping[str, Handler], capacity: int = 8) -> FastAPI:
    return create_app(
        settings.model_copy(update={"job_capacity": capacity}),
        allowed_hosts=frozenset({TEST_HOST}),
        probes=FakeProbes(),
        decider=MockDecider(),
        job_handlers=handlers,
    )


Scenario = Callable[[httpx.AsyncClient], Awaitable[None]]


def run(app: FastAPI, scenario: Scenario) -> None:
    """Run several requests in ONE event loop, so the job worker keeps running in between."""

    async def _call() -> None:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            await scenario(client)

    asyncio.run(_call())


async def wait_state(client: httpx.AsyncClient, job_id: str, state: str) -> dict[str, Any]:
    for _ in range(200):
        body: dict[str, Any] = (await client.get(f"/jobs/{job_id}")).json()
        if body.get("state") == state:
            return body
        await asyncio.sleep(0.005)
    raise AssertionError(f"job {job_id} never reached {state}")


def test_job_runs_and_status_matches_the_contract(settings: Settings) -> None:
    validator = ContractValidator(settings.schemas_dir)
    seen: list[GenerationJob] = []

    async def answer(received: GenerationJob) -> JobOutcome:
        seen.append(received)
        return outcome()

    async def scenario(client: httpx.AsyncClient) -> None:
        accepted = await client.post("/jobs", json=JOB)
        assert accepted.status_code == 202
        body = accepted.json()
        assert validator.errors("job-accepted", body) == []
        assert (body["job_id"], body["state"]) == (JOB["request_id"], "queued")
        status = await wait_state(client, JOB["request_id"], "done")
        assert validator.errors("job-status", status) == []
        assert status["result"] == RESULT
        assert status["provenance"]["seed"] == JOB["seed"]
        assert status["error"] is None
        listing = (await client.get("/jobs")).json()
        assert validator.errors("job-list", listing) == []
        assert [j["state"] for j in listing["jobs"]] == ["done"]

    run(make(settings, {"question": answer}), scenario)
    assert len(seen) == 1


def test_same_request_id_is_one_job(settings: Settings) -> None:
    calls: list[str] = []

    async def answer(received: GenerationJob) -> JobOutcome:
        calls.append(str(received.request_id))
        return outcome()

    async def scenario(client: httpx.AsyncClient) -> None:
        first = await client.post("/jobs", json=JOB)
        await wait_state(client, JOB["request_id"], "done")
        again = await client.post("/jobs", json=JOB)
        assert (first.status_code, again.status_code) == (202, 202)
        assert again.json()["state"] == "done"  # the core resubmits after a restart: harmless

    run(make(settings, {"question": answer}), scenario)
    assert len(calls) == 1


def test_higher_priority_runs_first_then_arrival_order(settings: Settings) -> None:
    order: list[int] = []
    gate = asyncio.Event()

    async def answer(received: GenerationJob) -> JobOutcome:
        await gate.wait()
        order.append(received.priority)
        return outcome()

    async def scenario(client: httpx.AsyncClient) -> None:
        await client.post("/jobs", json=job(1, priority=10))  # starts at once, waits on the gate
        for n, priority in [(2, 10), (3, 90), (4, 50), (5, 90)]:
            await client.post("/jobs", json=job(n, priority=priority))
        queued = (await client.get("/jobs", params={"state": "queued"})).json()["jobs"]
        assert [j["job_id"][-1] for j in queued] == ["3", "5", "4", "2"]
        gate.set()
        await wait_state(client, job(2)["request_id"], "done")

    run(make(settings, {"question": answer}), scenario)
    assert order == [10, 90, 90, 50, 10]


def test_handler_failure_and_crash_end_as_failed(settings: Settings) -> None:
    async def answer(received: GenerationJob) -> JobOutcome:
        if received.priority == 1:
            raise JobFailedError("invalid_model_output", "no JSON", retryable=True)
        raise RuntimeError("boom")

    async def scenario(client: httpx.AsyncClient) -> None:
        await client.post("/jobs", json=job(1, priority=1))
        await client.post("/jobs", json=job(2, priority=0))
        failed = await wait_state(client, job(1)["request_id"], "failed")
        assert failed["error"] == {
            "code": "invalid_model_output",
            "message": "no JSON",
            "retryable": True,
        }
        assert (failed["result"], failed["provenance"]) == (None, None)
        crashed = await wait_state(client, job(2)["request_id"], "failed")
        assert crashed["error"]["code"] == "internal"
        assert crashed["error"]["retryable"] is False

    run(make(settings, {"question": answer}), scenario)


def test_unsupported_kind_is_not_retryable(settings: Settings) -> None:
    response = post(make(settings, {}), "/jobs", JOB)
    body = response.json()
    assert (response.status_code, body["code"], body["retryable"]) == (
        503,
        "model_unavailable",
        False,
    )
    assert body["request_id"] == JOB["request_id"]


BAD_JOBS: dict[str, dict[str, Any]] = {
    "payload part does not match the kind": {"kind": "memory"},
    "no payload part": {"payload": {}},
    "priority above the schema maximum": {"priority": 101},
}


@pytest.mark.parametrize("reason", sorted(BAD_JOBS))
def test_bad_jobs_are_rejected(settings: Settings, reason: str) -> None:
    body = {**copy.deepcopy(JOB), **BAD_JOBS[reason]}
    response = post(make(settings, {"question": lambda _: _never()}), "/jobs", body)
    assert response.status_code == 422, reason
    assert response.json()["request_id"] == JOB["request_id"]


async def _never() -> JobOutcome:
    raise AssertionError("must not run")


def test_unknown_job_and_unknown_state(settings: Settings) -> None:
    app = make(settings, {})

    async def scenario(client: httpx.AsyncClient) -> None:
        missing = await client.get("/jobs/00000000-0000-8000-8000-000000000999")
        error = missing.json()
        assert (missing.status_code, error["code"]) == (404, "not_found")
        # retryable + id: after a service restart the core sends a new attempt, not "give up"
        assert error["retryable"] is True
        assert error["request_id"] == "00000000-0000-8000-8000-000000000999"
        bad = await client.get("/jobs", params={"state": "lost"})
        assert (bad.status_code, bad.json()["code"]) == (422, "bad_request")

    run(app, scenario)


def test_capacity_evicts_finished_jobs_but_never_open_ones(settings: Settings) -> None:
    gate = asyncio.Event()

    async def answer(received: GenerationJob) -> JobOutcome:
        if received.priority == 5:
            await gate.wait()
        return outcome()

    async def scenario(client: httpx.AsyncClient) -> None:
        await client.post("/jobs", json=job(1))
        await wait_state(client, job(1)["request_id"], "done")
        await client.post("/jobs", json=job(2, priority=5))  # running, held by the gate
        assert (await client.post("/jobs", json=job(3, priority=5))).status_code == 202  # evicts 1
        assert (await client.get(f"/jobs/{job(1)['request_id']}")).status_code == 404
        full = await client.post("/jobs", json=job(4))
        assert (full.status_code, full.json()["code"]) == (503, "overloaded")
        gate.set()
        await wait_state(client, job(3)["request_id"], "done")

    run(make(settings, {"question": answer}, capacity=2), scenario)


def test_shutdown_stops_jobs(settings: Settings) -> None:
    started = asyncio.Event()

    async def answer(_: GenerationJob) -> JobOutcome:
        started.set()
        await asyncio.sleep(10)
        return outcome()

    async def scenario(client: httpx.AsyncClient) -> None:
        await client.post("/jobs", json=job(1))
        await started.wait()
        assert (await client.post("/shutdown")).status_code == 202
        failed = (await client.get(f"/jobs/{job(1)['request_id']}")).json()
        assert (failed["state"], failed["error"]["retryable"]) == ("failed", True)
        refused = await client.post("/jobs", json=job(2))
        assert refused.json()["code"] == "model_unavailable"

    run(make(settings, {"question": answer}), scenario)


def test_same_id_with_other_content_is_refused(settings: Settings) -> None:
    async def scenario(client: httpx.AsyncClient) -> None:
        assert (await client.post("/jobs", json=job(1))).status_code == 202
        other = job(1)
        other["tick"] += 1  # e.g. after loading an older save the id comes back for other content
        refused = await client.post("/jobs", json=other)
        body = refused.json()
        assert (refused.status_code, body["code"], body["retryable"]) == (422, "bad_request", False)

    run(make(settings, {"question": lambda _: _wait_forever()}), scenario)


async def _wait_forever() -> JobOutcome:
    await asyncio.sleep(3600)
    raise AssertionError("unreachable")


def test_delete_forgets_queued_and_stops_running_jobs(settings: Settings) -> None:
    calls: list[int] = []

    async def answer(received: GenerationJob) -> JobOutcome:
        calls.append(received.priority)
        if received.priority == 50:
            await asyncio.sleep(3600)  # stopped by DELETE
        return outcome()

    async def scenario(client: httpx.AsyncClient) -> None:
        await client.post("/jobs", json=job(1, priority=50))  # runs at once
        await client.post("/jobs", json=job(2, priority=40))
        await client.post("/jobs", json=job(3, priority=30))
        await asyncio.sleep(0.01)
        assert (await client.delete(f"/jobs/{job(3)['request_id']}")).status_code == 204
        assert (await client.delete(f"/jobs/{job(1)['request_id']}")).status_code == 204
        assert (await client.delete(f"/jobs/{job(1)['request_id']}")).status_code == 204  # again
        await wait_state(client, job(2)["request_id"], "done")
        gone = await client.get(f"/jobs/{job(1)['request_id']}")
        assert gone.status_code == 404

    run(make(settings, {"question": answer}), scenario)
    assert calls == [50, 40]  # job 3 never ran


def test_unread_finished_jobs_are_evicted_last(settings: Settings) -> None:
    async def answer(_: GenerationJob) -> JobOutcome:
        return outcome()

    async def scenario(client: httpx.AsyncClient) -> None:
        await client.post("/jobs", json=job(1))
        await client.post("/jobs", json=job(2))
        await asyncio.sleep(0.05)  # both done; only job 2 is read by the core
        assert (await client.get(f"/jobs/{job(2)['request_id']}")).json()["state"] == "done"
        assert (await client.post("/jobs", json=job(3))).status_code == 202
        assert (await client.get(f"/jobs/{job(1)['request_id']}")).status_code == 200
        assert (await client.get(f"/jobs/{job(2)['request_id']}")).status_code == 404

    run(make(settings, {"question": answer}, capacity=2), scenario)


def test_oversized_body_is_refused(settings: Settings) -> None:
    response = post(make(settings, {}), "/jobs", b"{" + b" " * (300 * 1024) + b"}")
    assert (response.status_code, response.json()["code"]) == (422, "bad_request")


def test_status_fields_follow_the_state(settings: Settings) -> None:
    async def answer(received: GenerationJob) -> JobOutcome:
        if received.priority == 1:
            raise JobFailedError("timeout", "slow", retryable=True)
        return outcome()

    async def scenario(client: httpx.AsyncClient) -> None:
        await client.post("/jobs", json=job(1, priority=1))
        await client.post("/jobs", json=job(2, priority=0))
        failed = await wait_state(client, job(1)["request_id"], "failed")
        done = await wait_state(client, job(2)["request_id"], "done")
        assert failed["error"] is not None
        assert (failed["result"], failed["provenance"]) == (None, None)
        assert done["error"] is None
        assert done["result"] is not None
        assert done["provenance"] is not None

    run(make(settings, {"question": answer}), scenario)
