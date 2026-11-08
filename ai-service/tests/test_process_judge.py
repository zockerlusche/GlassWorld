"""Judge of unknown tries (B2537, P2/A1450): prompt, guard checks, model call, replay, endpoint.

Snapshot: GW_UPDATE_SNAPSHOTS=1 uv run pytest ai-service/tests/test_process_judge.py
"""

from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path
from typing import Any

import httpx
import pytest

from conftest import TEST_HOST, TOKEN, FakeProbes
from glassworld_ai.app import create_app
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.llm.jobcall import InvalidOutputError, ModelCallSettings
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.process.judge import (
    GuardError,
    JudgeContext,
    MockProcesses,
    OllamaProcesses,
    ReplayProcesses,
    build_values,
    guard_settings,
    parse_judgement,
)
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.validation import ContractValidator
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import GenerationJob
from glassworld_contracts.signature import world_rule_id

REPO = Path(__file__).resolve().parents[2]
SNAPSHOTS = Path(__file__).resolve().parent / "snapshots"
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.process-char-wood.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)  # char a dry branch at embers (snapshot)
DRY_DATA: dict[str, Any] = json.loads(json.dumps(JOB_DATA))
DRY_DATA["payload"]["process"].update(process="dry", heat_level=None)
DRY_DATA["payload"]["process"]["signature"] = {
    "process": "dry",
    "inputs": [["braidable", "fibrous", "long_lasting"]],
    "tool": None,
    "heat_level": None,
}
DRY = GenerationJob.model_validate(DRY_DATA)
CTX = JudgeContext(
    REPO / "content",
    Vocabulary.load(REPO / "content/vocabulary"),
    guard_settings(load_prompt(REPO / "prompts", "process/judge", 1)),
)
GOOD: dict[str, Any] = {
    "feasible": True,
    "rationale": "Wood heated without much air turns into charcoal.",
    "rule": {
        "changes": 1,
        "add_properties": ["burnable"],
        "remove_properties": [],
        "consumes": [],
        "heat_min": None,
        "tech_level": "tool",
        "duration_ticks": 300,
    },
}


def _raw(**rule_changes: Any) -> str:
    data = json.loads(json.dumps(GOOD))
    data["rule"].update(rule_changes)
    return json.dumps(data)


def _status(outcome: JobOutcome) -> dict[str, Any]:
    return {
        "contract_version": JOB_DATA["contract_version"],
        "job_id": JOB_DATA["request_id"],
        "tick": JOB.tick,
        "kind": "process",
        "state": "done",
        "result": outcome.result,
        "error": None,
        "provenance": {
            "source": outcome.source,
            "model": outcome.model,
            "prompt_version": outcome.prompt_version,
            "seed": JOB.seed,
            "latency_ms": outcome.latency_ms,
            "attempts": outcome.attempts,
        },
    }


def test_prompt_snapshot(settings: Settings) -> None:
    template = load_prompt(settings.prompts_dir, "process/judge", 1)
    rendered = template.render(build_values(JOB, CTX))
    text = f"=== SYSTEM ===\n{rendered.system}\n\n=== USER ===\n{rendered.user}\n"
    snapshot = SNAPSHOTS / "process-judge.v1.char-wood.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


def test_the_service_builds_the_rule_from_the_signature() -> None:
    result = parse_judgement(_raw(), DRY, CTX)
    rule = result["rule"]
    props = ["braidable", "fibrous", "long_lasting"]
    assert rule["id"] == world_rule_id("dry", [props], None, None)
    assert rule["process"] == "dry"
    assert rule["inputs"] == [{"properties_all": props, "properties_none": []}]
    assert rule["tool"] is None
    assert rule["result"] == {
        "add_properties": ["burnable"],
        "remove_properties": [],
        "produces": None,
        "consumes": [],
    }


def test_not_feasible_is_a_result() -> None:
    result = parse_judgement(
        json.dumps({**GOOD, "feasible": False, "rationale": "Nothing happens."}), DRY, CTX
    )
    assert result == {"feasible": False, "rule": None, "rationale": "Nothing happens."}


@pytest.mark.parametrize(
    ("changes", "problem"),
    [
        ({"heat_min": "warm"}, "heat_min"),  # no heat available
        ({"tech_level": "metal"}, "tech_level"),  # above the inhabitant's tech level
        ({"remove_properties": ["sharp"]}, "remove_properties"),  # no input has 'sharp'
        ({"consumes": [1]}, "consumes"),  # only one input: nothing else can be used up
        ({"add_properties": ["long_lasting"]}, "change something"),  # no real change
        ({"add_properties": [], "remove_properties": []}, "change something"),
    ],
)
def test_the_guard_asks_again(changes: dict[str, Any], problem: str) -> None:
    with pytest.raises(InvalidOutputError, match=problem):
        parse_judgement(_raw(**changes), DRY, CTX)


def test_things_never_become_alive() -> None:
    ctx = JudgeContext(REPO / "content", Vocabulary.load(REPO / "content/vocabulary"))
    ctx.living = {"burnable"}  # pretend: a living property (B2531 list is data)
    with pytest.raises(InvalidOutputError, match="alive"):
        parse_judgement(_raw(), DRY, ctx)


def test_model_call_result_and_replay(settings: Settings) -> None:
    answers = iter([_raw(heat_min="warm"), _raw()])
    seeds: list[int] = []

    def fake(request: httpx.Request) -> httpx.Response:
        seeds.append(json.loads(request.content)["options"]["seed"])
        return httpx.Response(
            200, json={"message": {"role": "assistant", "content": next(answers)}}
        )

    log = CallLog(settings.log_dir / "processes.jsonl")
    handler = OllamaProcesses(
        client=OllamaClient(
            "http://ollama.test",
            "qwen3.5:9b",
            httpx.AsyncClient(transport=httpx.MockTransport(fake)),
            keep_alive="5m",
        ),
        queue=ModelQueue(8),
        template=load_prompt(settings.prompts_dir, "process/judge", 1),
        context=CTX,
        settings=ModelCallSettings(temperature=0.7, num_ctx=8192, max_retries=2, call_timeout_s=30),
        log=log,
    )
    outcome = asyncio.run(handler(DRY))
    assert seeds == [DRY.seed, DRY.seed + 1] and outcome.attempts == 2
    assert ContractValidator(settings.schemas_dir).errors("job-status", _status(outcome)) == []
    assert asyncio.run(ReplayProcesses(log, CTX)(DRY)).result == outcome.result
    other = GenerationJob.model_validate(
        {**DRY_DATA, "request_id": "00000000-0000-8000-8000-000000000077"}
    )
    with pytest.raises(JobFailedError):
        asyncio.run(ReplayProcesses(log, CTX)(other))


def test_mock_through_the_endpoint(settings: Settings) -> None:
    app = create_app(
        settings.model_copy(update={"decider_backend": "mock"}),
        allowed_hosts=frozenset({TEST_HOST}),
        probes=FakeProbes(),
        decider=MockDecider(),
    )
    validator = ContractValidator(settings.schemas_dir)

    async def scenario() -> dict[str, Any]:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            assert (await client.post("/jobs", json=JOB_DATA)).json()["state"] == "queued"
            status: dict[str, Any] = {}
            for _ in range(300):
                status = (await client.get(f"/jobs/{JOB_DATA['request_id']}")).json()
                if status["state"] in ("done", "failed"):
                    break
                await asyncio.sleep(0.01)
            return status

    status = asyncio.run(scenario())
    assert status["state"] == "done" and validator.errors("job-status", status) == []
    assert status["result"]["process"]["feasible"] is False
    assert asyncio.run(MockProcesses()(JOB)).source == "mock"


def test_physics_says_no_without_asking_again() -> None:
    data = json.loads(json.dumps(JOB_DATA))
    data["payload"]["process"]["process"] = "braid"
    data["payload"]["process"]["signature"]["process"] = "braid"
    data["payload"]["process"]["signature"]["inputs"] = [["hard", "portable"]]
    job = GenerationJob.model_validate(data)
    result = parse_judgement(_raw(heat_min=None), job, CTX)  # the model said feasible
    assert result["feasible"] is False and result["rationale"].startswith("Physics: braid needs")


@pytest.mark.parametrize(
    ("changes", "problem"),
    [({"heat_min": None, "add_properties": ["burnable"]}, "heat_min")],
)
def test_heat_processes_need_heat(changes: dict[str, Any], problem: str) -> None:
    """char works by heat: the schema has no null heat_min for it."""
    with pytest.raises(InvalidOutputError, match=problem):
        parse_judgement(_raw(**changes), JOB, CTX)


def test_invented_food_is_dropped_and_doubles_removed() -> None:
    result = parse_judgement(_raw(add_properties=["burnable", "burnable"]), DRY, CTX)
    assert result["rule"]["result"]["add_properties"] == ["burnable"]
    ctx = JudgeContext(
        REPO / "content",
        Vocabulary.load(REPO / "content/vocabulary"),
        {**CTX.guard, "may_add": {"dry": ["burnable", "edible"]}},
    )
    result = parse_judgement(_raw(add_properties=["burnable", "edible"]), DRY, ctx)
    assert result["rule"]["result"]["add_properties"] == ["burnable"]  # food never from nothing


def _two_inputs() -> GenerationJob:
    """Soak a stone in water: canonical order puts the water first (drinkable < hard)."""
    data = json.loads(json.dumps(DRY_DATA))
    data["payload"]["process"].update(process="soak")
    data["payload"]["process"]["signature"] = {
        "process": "soak",
        "inputs": [["hard", "malleable"], ["cooling", "drinkable"]],
        "tool": None,
        "heat_level": None,
    }
    return GenerationJob.model_validate(data)


def test_the_changing_input_comes_first_in_the_rule() -> None:
    raw = json.dumps(
        {
            **GOOD,
            "rule": {
                **GOOD["rule"],
                "changes": 2,
                "add_properties": ["malleable"],
                "remove_properties": ["hard"],
                "consumes": [1],
            },
        }
    )
    rule = parse_judgement(raw, _two_inputs(), CTX)["rule"]
    assert rule["inputs"][0]["properties_all"] == ["hard", "malleable"]  # the stone changes
    assert rule["result"]["remove_properties"] == ["hard"]
    assert rule["result"]["consumes"] == [1]  # the water (canonical 1) is now rule input 2


def test_protected_properties_are_never_removed() -> None:
    raw = json.dumps(
        {
            **GOOD,
            "rule": {
                **GOOD["rule"],
                "changes": 1,
                "remove_properties": ["drinkable"],
                "add_properties": [],
            },
        }
    )
    with pytest.raises(InvalidOutputError, match="remove_properties"):
        parse_judgement(raw, _two_inputs(), CTX)


def test_impossible_tries_never_call_the_model(settings: Settings) -> None:
    calls: list[int] = []

    def fake(request: httpx.Request) -> httpx.Response:
        calls.append(1)
        return httpx.Response(500)

    data = json.loads(json.dumps(JOB_DATA))
    data["payload"]["process"]["signature"]["heat_level"] = None  # char without heat
    job = GenerationJob.model_validate(data)
    handler = OllamaProcesses(
        client=OllamaClient(
            "http://ollama.test",
            "qwen3.5:9b",
            httpx.AsyncClient(transport=httpx.MockTransport(fake)),
            keep_alive="5m",
        ),
        queue=ModelQueue(8),
        template=load_prompt(settings.prompts_dir, "process/judge", 1),
        context=CTX,
        settings=ModelCallSettings(temperature=0.7, num_ctx=8192, max_retries=2, call_timeout_s=30),
        log=CallLog(settings.log_dir / "processes.jsonl"),
    )
    outcome = asyncio.run(handler(job))
    assert calls == [] and outcome.result["process"]["feasible"] is False  # type: ignore[index]
    assert outcome.source == "local_model" and outcome.model == "physics-guard"


@pytest.mark.parametrize(
    "guard",
    [
        {"never_iff": {"melt": ["burnable"]}},
        {"may_add": {"dry": ["unicorn_dust"]}},
        {"heat_processes": ["levitate"]},
    ],
)
def test_a_broken_guard_fails_at_load(guard: dict[str, Any]) -> None:
    with pytest.raises(GuardError):
        JudgeContext(REPO / "content", Vocabulary.load(REPO / "content/vocabulary"), guard)


def test_replay_without_a_recording_is_not_retryable(settings: Settings) -> None:
    with pytest.raises(JobFailedError) as error:
        asyncio.run(ReplayProcesses(CallLog(settings.log_dir / "empty.jsonl"), CTX)(DRY))
    assert error.value.retryable is False


def test_a_process_takes_away_only_what_it_may() -> None:
    """Drying never removes fibres (live run 5: grass lost 'fibrous'); may_remove is data."""
    with pytest.raises(InvalidOutputError, match="remove_properties"):
        parse_judgement(_raw(remove_properties=["fibrous"]), DRY, CTX)


def test_a_process_that_can_change_nothing_is_not_feasible() -> None:
    data = json.loads(json.dumps(DRY_DATA))
    data["payload"]["process"]["process"] = "grind"
    data["payload"]["process"]["signature"].update(
        process="grind", inputs=[["edible", "long_lasting"]]
    )
    ctx = JudgeContext(
        REPO / "content",
        Vocabulary.load(REPO / "content/vocabulary"),
        {**CTX.guard, "may_add": {"grind": []}, "may_remove": {"grind": []}},
    )
    result = parse_judgement(_raw(), GenerationJob.model_validate(data), ctx)
    assert result["feasible"] is False and "cannot change anything" in result["rationale"]
