"""Night items for the dream scene (B7 R10, D637): what the queue does tonight, without wording."""

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
from glassworld_ai.planner import KindPlan, Planner
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

EXAMPLES = Path(__file__).resolve().parents[2] / "contracts" / "examples" / "valid"


def _job(name: str, request_id: str) -> GenerationJob:
    data: dict[str, Any] = json.loads((EXAMPLES / f"generation-job.{name}.json").read_text("utf-8"))
    data["request_id"] = request_id
    return GenerationJob.model_validate(data)


IMAGE = _job("image-vessel", "00000000-0000-8000-8000-000000000001")
DREAM = _job("dream-berries", "00000000-0000-8000-8000-000000000002")
PICTURE: dict[str, object] = {
    "image": {
        "file": "vessel-7.png",
        "width": 512,
        "height": 512,
        "seed_used": 7,
        "workflow": "z-image-turbo-t2i",
        "style": "warm_comic",
    }
}


def _runner() -> tuple[JobRunner, Planner, list[str]]:
    planner = Planner(
        100,
        {"image": KindPlan("night", None), "dream": KindPlan("night", None)},
        lambda: None,
        lambda: "standard",
    )
    ran: list[str] = []

    async def handler(job: GenerationJob) -> JobOutcome:
        kind = str(job.kind.value)
        ran.append(kind)
        result: dict[str, object] = PICTURE if kind == "image" else {"dream": {}}
        return JobOutcome(result, "mock", "mock", None, 0, 1)

    return JobRunner({"image": handler, "dream": handler}, 8, gate=planner), planner, ran


def test_nothing_is_noted_by_day_and_a_night_starts_with_the_open_jobs() -> None:
    runner, planner, ran = _runner()

    async def scenario() -> None:
        assert runner.submit(IMAGE)[0] == "deferred"
        assert runner.submit(DREAM)[0] == "deferred"
        assert runner.night_items(0) == []  # by day nothing is noted
        planner.start_night()
        runner.start_night(True)
        runner.reschedule()
        await asyncio.sleep(0.05)

    asyncio.run(scenario())
    assert sorted(ran) == ["dream", "image"]
    items = runner.night_items(0)
    seqs = [int(str(i["seq"])) for i in items]
    assert seqs == list(range(1, len(items) + 1))
    image = [i for i in items if i["job_id"] == str(IMAGE.request_id)]
    assert [i["state"] for i in image] == ["deferred", "queued", "running", "done"]
    assert image[-1]["image"] == "vessel-7.png"
    assert all(i["image"] is None for i in image[:-1])  # the picture only once it is done
    # References come unchanged from the job; no wording is passed on.
    assert image[0]["object_type"] == "rt_vessel_clay"
    assert set(items[0]) == {"seq", "job_id", "kind", "state", "inhabitant", "object_type", "image"}
    assert runner.night_items(seqs[-2]) == items[-1:]


def test_a_since_beyond_the_newest_number_gives_everything() -> None:
    runner, planner, _ = _runner()

    async def scenario() -> None:
        runner.submit(DREAM)
        planner.start_night()
        runner.start_night(True)

    asyncio.run(scenario())
    assert runner.night_items(999) == runner.night_items(0) != []


def test_the_endpoint_answers_by_the_contract(settings: Settings) -> None:
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
            day = (await client.get("/night/items")).json()
            assert validator.errors("night-items", day) == []
            assert (day["phase"], day["items"]) == ("day", [])
            await client.post(
                "/phase", json={"contract_version": "0.2", "phase": "night", "tick": 9}
            )
            night = (await client.get("/night/items", params={"since": 0})).json()
            assert validator.errors("night-items", night) == []
            assert night["phase"] == "night"
            bad = await client.get("/night/items", params={"since": -1})
            assert bad.status_code == 422

    asyncio.run(scenario())
