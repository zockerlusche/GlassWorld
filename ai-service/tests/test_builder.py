"""Builder (D471 D3): building plan from kit parts, schema per request, checks, handlers, /jobs."""

from __future__ import annotations

import asyncio
import copy
import json
import os
from pathlib import Path
from typing import Any

import httpx
import pytest

from conftest import TEST_HOST, TOKEN, FakeProbes
from glassworld_ai.app import create_app
from glassworld_ai.builder.plan import (
    MockBuilder,
    OllamaBuilder,
    ReplayBuilder,
    parse_plan,
    plan_schema,
    plan_values,
)
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.llm.jobcall import InvalidOutputError, ModelCallSettings
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.build-plan-bowl.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)
PAYLOAD = JOB.payload.build_plan
GOOD: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/job-status.build-plan-bowl.json").read_text("utf-8")
)["result"]["build_plan"]


def _handler(settings: Settings, answers: list[str]) -> tuple[OllamaBuilder, CallLog]:
    queue = iter(answers)

    def fake(_: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"message": {"role": "assistant", "content": next(queue)}})

    log = CallLog(settings.log_dir / "builder.jsonl")
    client = OllamaClient(
        "http://ollama.test",
        "qwen3.5:9b",
        httpx.AsyncClient(transport=httpx.MockTransport(fake)),
        keep_alive="5m",
    )
    handler = OllamaBuilder(
        client=client,
        queue=ModelQueue(8),
        template=load_prompt(settings.prompts_dir, "builder/plan", 1),
        settings=ModelCallSettings(temperature=0.7, num_ctx=8192, max_retries=1, call_timeout_s=30),
        log=log,
    )
    return handler, log


def _status(plan: dict[str, Any]) -> dict[str, object]:
    return {
        "contract_version": JOB_DATA["contract_version"],
        "job_id": JOB_DATA["request_id"],
        "tick": JOB.tick,
        "kind": "build_plan",
        "state": "done",
        "result": {"build_plan": plan},
        "error": None,
        "provenance": {
            "source": "ollama",
            "model": "x",
            "prompt_version": "p",
            "seed": JOB.seed,
            "latency_ms": 0,
            "attempts": 1,
        },
    }


def test_good_plan_passes_and_matches_the_contract(settings: Settings) -> None:
    handler, log = _handler(settings, [json.dumps(GOOD)])
    outcome = asyncio.run(handler(JOB))
    assert outcome.result == {"build_plan": GOOD}
    assert outcome.prompt_version == "builder/plan.v1"
    assert ContractValidator(settings.schemas_dir).errors("job-status", _status(GOOD)) == []
    assert [r["outcome"] for r in log.read()] == ["ok"]


def _part(**changes: object) -> dict[str, Any]:
    part = copy.deepcopy(GOOD["parts"][0])
    part.update(changes)
    return part


@pytest.mark.parametrize(
    ("parts", "problem"),
    [
        ([_part(shape="pyramid"), _part(step=2)], "shape"),
        ([_part(color="neon_pink"), _part(step=2)], "color"),
        ([_part(material="iron_bar"), _part(step=2)], "material"),
        ([_part(), _part()], "steps without a part"),
        ([_part(position_m={"x": 0.2, "y": 0.05, "z": 0}), _part(step=2)], "outside"),
        ([_part(step=3), _part(step=2)], "step"),
    ],
)
def test_plans_outside_the_kit_or_the_invention_are_invalid(
    parts: list[dict[str, Any]], problem: str
) -> None:
    schema = plan_schema(PAYLOAD)
    with pytest.raises(InvalidOutputError, match=problem):
        parse_plan(json.dumps({"parts": parts}), schema, PAYLOAD)


def test_invalid_plan_is_retried_then_fails(settings: Settings) -> None:
    bad = json.dumps({"parts": [_part(), _part()]})
    handler, _ = _handler(settings, [bad, json.dumps(GOOD)])
    assert asyncio.run(handler(JOB)).attempts == 2
    handler, _ = _handler(settings, [bad, bad])
    with pytest.raises(JobFailedError) as failure:
        asyncio.run(handler(JOB))
    assert failure.value.code == "invalid_model_output"


def test_replay_and_mock(settings: Settings) -> None:
    handler, log = _handler(settings, [json.dumps(GOOD)])
    original = asyncio.run(handler(JOB))
    assert asyncio.run(ReplayBuilder(log)(JOB)).result == original.result
    mock = asyncio.run(MockBuilder()(JOB))
    plan = mock.result["build_plan"]
    assert isinstance(plan, dict)
    parse_plan(json.dumps(plan), plan_schema(PAYLOAD), PAYLOAD)  # the mock plan passes the checks
    assert ContractValidator(settings.schemas_dir).errors("job-status", _status(plan)) == []  # type: ignore[arg-type]


def test_prompt_snapshot(settings: Settings) -> None:
    template = load_prompt(settings.prompts_dir, "builder/plan", 1)
    rendered = template.render(plan_values(JOB, template))
    text = f"=== SYSTEM ===\n{rendered.system}\n\n=== USER ===\n{rendered.user}\n"
    snapshot = Path(__file__).resolve().parent / "snapshots" / "builder-plan.v1.bowl.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


def test_build_plan_job_over_jobs_with_the_mock_backend(settings: Settings) -> None:
    app = create_app(
        settings.model_copy(update={"decider_backend": "mock"}),
        allowed_hosts=frozenset({TEST_HOST}),
        probes=FakeProbes(),
        decider=MockDecider(),
    )

    async def scenario() -> dict[str, Any]:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            assert (await client.post("/jobs", json=JOB_DATA)).status_code == 202
            for _ in range(200):
                body: dict[str, Any] = (await client.get(f"/jobs/{JOB_DATA['request_id']}")).json()
                if body["state"] == "done":
                    return body
                await asyncio.sleep(0.005)
        raise AssertionError("build plan job never finished")

    status = asyncio.run(scenario())
    assert ContractValidator(settings.schemas_dir).errors("job-status", status) == []
    assert len(status["result"]["build_plan"]["parts"]) >= 2
