"""Dreams (B7 S6, A1409): prompt snapshot, answer checks, model call, replay, night run.

Snapshot: GW_UPDATE_SNAPSHOTS=1 uv run pytest ai-service/tests/test_dream.py
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
from glassworld_ai.dream import (
    MockDreams,
    OllamaDreams,
    ReplayDreams,
    build_values,
    output_schema,
    parse_dream,
)
from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.llm.jobcall import InvalidOutputError, ModelCallSettings
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import PromptTemplate, load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.safety import TextGuard
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
SNAPSHOTS = Path(__file__).resolve().parent / "snapshots"
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.dream-berries.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)
GUARD = TextGuard.from_file(REPO / "content/safety/rules.json")
GOOD: dict[str, Any] = {
    "text": "Ich gehe durch einen Wald aus roten Beeren, und der Bach singt leise neben mir.",
    "tone": "joyful",
    "about": "t2",
}


def _template(settings: Settings) -> PromptTemplate:
    return load_prompt(settings.prompts_dir, "dream/tell", 1)


def _raw(**changes: Any) -> str:
    return json.dumps({**GOOD, **changes}, ensure_ascii=False)


def _status(outcome: JobOutcome) -> dict[str, Any]:
    return {
        "contract_version": JOB_DATA["contract_version"],
        "job_id": JOB_DATA["request_id"],
        "tick": JOB.tick,
        "kind": "dream",
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


def test_dream_prompt_snapshot(settings: Settings) -> None:
    template = _template(settings)
    rendered = template.render(build_values(JOB, template))
    text = f"=== SYSTEM ===\n{rendered.system}\n\n=== USER ===\n{rendered.user}\n"
    snapshot = SNAPSHOTS / "dream-tell.v1.berries.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


def test_values_name_things_in_plain_words(settings: Settings) -> None:
    values = build_values(JOB, _template(settings))
    assert values["language"] == "German"
    assert values["tones"] == "calm, joyful, curious, uneasy"
    assert "- found (thing 'red berries')" in values["events"]
    assert "- t2: thing 'red berries'" in values["things"]
    assert "red_berries" not in values["things"] + values["events"]
    assert values["memory"].startswith("> Woke on the beach")


def test_a_good_dream_parses() -> None:
    dream = parse_dream(_raw(), JOB, GUARD)
    assert (dream.tone, dream.about) == ("joyful", {"kind": "type", "id": "red_berries"})


@pytest.mark.parametrize(
    ("changes", "problem"),
    [
        ({"tone": "terrifying"}, "tone"),
        ({"about": "t9"}, "about"),
        ({"text": "x" * 221}, "text"),
        ({"text": "Ich sehe red_berries am Bach."}, "raw ids"),
        ({"text": "Then I kill the bird."}, "content guard"),
    ],
)
def test_bad_dreams_are_asked_again(changes: dict[str, Any], problem: str) -> None:
    with pytest.raises(InvalidOutputError, match=problem):
        parse_dream(_raw(**changes), JOB, GUARD)


def test_without_subjects_about_must_be_null() -> None:
    data = json.loads(json.dumps(JOB_DATA))
    for event in data["payload"]["dream"]["events"]:
        event["about"] = None
    job = GenerationJob.model_validate(data)
    assert output_schema(job)["properties"]["about"] == {"type": "null"}
    assert parse_dream(_raw(about=None), job, GUARD).about is None


def test_the_model_answer_becomes_a_result_and_replays(settings: Settings) -> None:
    answers = iter([_raw(tone="terrifying"), _raw()])
    seeds: list[int] = []

    def fake(request: httpx.Request) -> httpx.Response:
        seeds.append(json.loads(request.content)["options"]["seed"])
        return httpx.Response(
            200, json={"message": {"role": "assistant", "content": next(answers)}}
        )

    log = CallLog(settings.log_dir / "dreams.jsonl")
    handler = OllamaDreams(
        client=OllamaClient(
            "http://ollama.test",
            "qwen3.5:9b",
            httpx.AsyncClient(transport=httpx.MockTransport(fake)),
            keep_alive="5m",
        ),
        queue=ModelQueue(8),
        template=_template(settings),
        settings=ModelCallSettings(temperature=0.7, num_ctx=8192, max_retries=2, call_timeout_s=30),
        log=log,
        guard=GUARD,
    )
    outcome = asyncio.run(handler(JOB))
    assert seeds == [JOB.seed, JOB.seed + 1]
    assert outcome.attempts == 2 and outcome.prompt_version == "dream/tell.v1"
    validator = ContractValidator(settings.schemas_dir)
    assert validator.errors("job-status", _status(outcome)) == []

    replayed = asyncio.run(ReplayDreams(log, GUARD)(JOB))
    assert replayed.result == outcome.result
    other = GenerationJob.model_validate(
        {**JOB_DATA, "request_id": "00000000-0000-8000-8000-000000000009"}
    )
    with pytest.raises(JobFailedError):
        asyncio.run(ReplayDreams(log, GUARD)(other))


def test_mock_is_calm_and_valid(settings: Settings) -> None:
    outcome = asyncio.run(MockDreams()(JOB))
    assert outcome.result["dream"]["tone"] == "calm"  # type: ignore[index]
    assert ContractValidator(settings.schemas_dir).errors("job-status", _status(outcome)) == []


def test_dreams_wait_for_the_night(settings: Settings) -> None:
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
            accepted = await client.post("/jobs", json=JOB_DATA)
            assert accepted.json()["state"] == "deferred"
            night = {"contract_version": "0.2", "phase": "night", "tick": JOB.tick + 1}
            assert (await client.post("/phase", json=night)).status_code == 200
            status: dict[str, Any] = {}
            for _ in range(300):
                status = (await client.get(f"/jobs/{JOB_DATA['request_id']}")).json()
                if status["state"] in ("done", "failed"):
                    break
                await asyncio.sleep(0.01)
            return status

    status = asyncio.run(scenario())
    assert status["state"] == "done", status.get("error")
    assert status["result"]["dream"]["text"]["locale"] == "de"


def test_people_are_never_named_by_id(settings: Settings) -> None:
    data = json.loads(json.dumps(JOB_DATA))
    data["payload"]["dream"]["events"].append(
        {
            "tick": 1190,
            "kind": "met_inhabitant",
            "about": {"kind": "inhabitant", "id": "inh-0002"},
            "formative": True,
        }
    )
    values = build_values(GenerationJob.model_validate(data), _template(settings))
    assert "inh-0002" not in "".join(values.values())
    assert "- t3: another person" in values["things"]
