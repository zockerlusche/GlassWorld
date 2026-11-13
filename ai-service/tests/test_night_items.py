"""Night items for the dream scene (B7 R10, D637): what the queue does tonight, without wording."""

from __future__ import annotations

import asyncio
import copy
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
    other, _, _ = _runner()
    assert len(runner.session) == 16 and other.session != runner.session  # new at every start


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


def test_a_question_job_names_the_thing_it_is_about_and_a_memory_job_names_none() -> None:
    """GW3 #20970: the dream scene draws the thing an inhabitant wonders about; the type comes
    unchanged from the question's subject. A memory job is about no thing."""
    data: dict[str, Any] = json.loads(
        (EXAMPLES / "generation-job.question-berries.json").read_text("utf-8")
    )
    data["request_id"] = "00000000-0000-8000-8000-000000000003"
    data["payload"]["question"]["subject"] = {"kind": "type", "id": "red_berry"}
    question = GenerationJob.model_validate(data)
    person = copy.deepcopy(data)
    person["request_id"] = "00000000-0000-8000-8000-000000000004"
    person["payload"]["question"]["subject"] = {"kind": "inhabitant", "id": "inh-000002"}
    about_person = GenerationJob.model_validate(person)
    memory = _job("memory-condense", "00000000-0000-8000-8000-000000000005")

    async def handler(job: GenerationJob) -> JobOutcome:
        return JobOutcome({str(job.kind.value): {}}, "mock", "mock", None, 0, 1)

    runner = JobRunner({"question": handler, "memory": handler}, 8)

    async def scenario() -> None:
        runner.start_night(True)
        for job in (question, about_person, memory):
            runner.submit(job)
        await asyncio.sleep(0.05)

    asyncio.run(scenario())
    by_job = {str(i["job_id"]): i for i in runner.night_items(0)}
    assert by_job[str(question.request_id)]["object_type"] == "red_berry"
    assert by_job[str(question.request_id)]["inhabitant"] is not None
    assert by_job[str(about_person.request_id)]["object_type"] is None  # a person is no thing type
    assert by_job[str(memory.request_id)]["object_type"] is None


def test_a_done_question_shows_what_it_learned_as_ids_surest_first(settings: Settings) -> None:
    """A1618 (B2683): the dream scene shows what a question job gave the inhabitant - its guesses
    about the thing, ids only, surest first, at most 4 - and only once the job is done."""
    data: dict[str, Any] = json.loads(
        (EXAMPLES / "generation-job.question-berries.json").read_text("utf-8")
    )
    data["request_id"] = "00000000-0000-8000-8000-000000000006"
    data["payload"]["question"]["subject"] = {"kind": "type", "id": "pyrite"}
    question = GenerationJob.model_validate(data)
    guesses = [
        {"property": "sparks", "value": True, "confidence": 0.4},
        {"property": "hard", "value": True, "confidence": 0.9},
        {"property": "edible", "value": False, "confidence": 0.7},
        {"property": "heavy", "value": True, "confidence": 0.2},
        {"property": "shiny", "value": True, "confidence": 0.1},
    ]
    answer = {"understood": True, "guesses": guesses, "idea": None, "thought": None}

    async def handler(job: GenerationJob) -> JobOutcome:
        return JobOutcome({"question": {**answer, "rationale": "x"}}, "mock", "mock", None, 0, 1)

    runner = JobRunner({"question": handler}, 8)

    async def scenario() -> None:
        runner.start_night(True)
        runner.submit(question)
        await asyncio.sleep(0.05)

    asyncio.run(scenario())
    items = runner.night_items(0)
    assert all("learned" not in i for i in items if i["state"] != "done")
    done = next(i for i in items if i["state"] == "done")
    assert done["learned"] == [
        {"subject": "pyrite", "property": "hard", "value": True},
        {"subject": "pyrite", "property": "edible", "value": False},
        {"subject": "pyrite", "property": "sparks", "value": True},
        {"subject": "pyrite", "property": "heavy", "value": True},
    ]
    validator = ContractValidator(settings.schemas_dir)
    body = {"contract_version": "0.2", "phase": "night", "session": runner.session, "items": items}
    assert validator.errors("night-items", body) == []
