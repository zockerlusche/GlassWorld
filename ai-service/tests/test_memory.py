"""Memory condensation (B4a S4): prompt, budget, formative events, handlers, /jobs wiring.

Snapshot: GW_UPDATE_SNAPSHOTS=1 uv run pytest ai-service/tests/test_memory.py
"""

from __future__ import annotations

import asyncio
import copy
import json
import os
from pathlib import Path
from typing import Any, cast

import httpx
import pytest

from conftest import TEST_HOST, TOKEN, FakeProbes
from glassworld_ai.app import create_app
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.llm.jobcall import InvalidOutputError, ModelCallSettings
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.memory.condense import (
    MockMemory,
    OllamaMemory,
    ReplayMemory,
    build_values,
    output_schema,
    parse_condensed,
)
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
SNAPSHOTS = Path(__file__).resolve().parent / "snapshots"
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.memory-condense.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)
FORMATIVE = [1100, 1180]
GOOD = json.dumps(
    {
        "summary": "Woke up alone on a beach, found fresh water. Met another person (inh-0002). "
        "Red berries caused a stomach ache.",
        "kept": [1180, 1100],
    }
)


def _template(settings: Settings) -> Any:
    return load_prompt(settings.prompts_dir, "memory/condense", 1)


def _ollama(content: str) -> httpx.Response:
    return httpx.Response(200, json={"message": {"role": "assistant", "content": content}})


def _handler(settings: Settings, answers: list[str]) -> tuple[OllamaMemory, CallLog, list[int]]:
    seeds: list[int] = []
    queue = iter(answers)

    def fake(request: httpx.Request) -> httpx.Response:
        seeds.append(json.loads(request.content)["options"]["seed"])
        return _ollama(next(queue))

    log = CallLog(settings.log_dir / "memory.jsonl")
    client = OllamaClient(
        "http://ollama.test",
        "qwen3.5:9b",
        httpx.AsyncClient(transport=httpx.MockTransport(fake)),
        keep_alive="5m",
    )
    handler = OllamaMemory(
        client=client,
        queue=ModelQueue(8),
        template=_template(settings),
        settings=ModelCallSettings(temperature=0.7, num_ctx=8192, max_retries=2, call_timeout_s=30),
        log=log,
    )
    return handler, log, seeds


def _status(outcome_result: dict[str, object], source: str) -> dict[str, object]:
    return {
        "contract_version": JOB_DATA["contract_version"],
        "job_id": JOB_DATA["request_id"],
        "tick": JOB.tick,
        "kind": "memory",
        "state": "done",
        "result": outcome_result,
        "error": None,
        "provenance": {
            "source": source,
            "model": "x",
            "prompt_version": None,
            "seed": JOB.seed,
            "latency_ms": 0,
            "attempts": 1,
        },
    }


def test_memory_prompt_snapshot(settings: Settings) -> None:
    template = _template(settings)
    rendered = template.render(build_values(JOB, template))
    text = f"=== SYSTEM ===\n{rendered.system}\n\n=== USER ===\n{rendered.user}\n"
    snapshot = SNAPSHOTS / "memory-condense.v1.example.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


def test_formative_events_are_marked_and_the_old_summary_quoted(settings: Settings) -> None:
    values = build_values(JOB, _template(settings))
    assert values["summary"].startswith("> Woke up alone")
    assert "[tick 1100] met inhabitant (person 'inh-0002') - FORMATIVE, keep it" in values["events"]
    assert "[tick 1150] drank (thing 'spring water')\n" in values["events"] + "\n"
    assert values["max_chars"] == "600"


@pytest.mark.parametrize(
    ("raw", "problem"),
    [
        ("no json", "not JSON"),
        (json.dumps({"summary": "x" * 601, "kept": FORMATIVE}), "summary"),
        (json.dumps({"summary": "Met someone.", "kept": [1100]}), "kept"),
        (json.dumps({"summary": "Met someone.", "kept": [1100, 1150]}), "kept"),
        (json.dumps({"summary": "   ", "kept": FORMATIVE}), "empty"),
    ],
)
def test_budget_and_formative_events_are_enforced(raw: str, problem: str) -> None:
    schema = output_schema(600, FORMATIVE)
    with pytest.raises(InvalidOutputError, match=problem):
        parse_condensed(raw, schema, FORMATIVE)


def test_a_tick_named_once_per_event_of_that_tick_is_the_same_answer() -> None:
    """Jobs from the real core carry several events of one tick (review of #227, run L838):
    the model named the tick twice, and the job failed for good."""
    schema = output_schema(600, FORMATIVE)
    twice = json.dumps({"summary": "Met someone. Berries hurt.", "kept": [1100, 1180, 1180]})
    assert parse_condensed(twice, schema, FORMATIVE).kept == (1100, 1180)
    missing = json.dumps({"summary": "Met someone.", "kept": [1180, 1180]})
    with pytest.raises(InvalidOutputError):
        parse_condensed(missing, schema, FORMATIVE)


def test_ollama_condenses_and_retries_with_a_new_seed(settings: Settings) -> None:
    too_long = json.dumps({"summary": "x" * 700, "kept": FORMATIVE})
    handler, log, seeds = _handler(settings, [too_long, GOOD])
    outcome = asyncio.run(handler(JOB))
    assert outcome.attempts == 2
    assert seeds == [JOB.seed, JOB.seed + 1]
    assert outcome.prompt_version == "memory/condense.v1"
    assert outcome.result == {"memory": {"summary": json.loads(GOOD)["summary"], "kept": FORMATIVE}}
    assert [r["outcome"] for r in log.read()] == ["invalid", "ok"]
    validator = ContractValidator(settings.schemas_dir)
    assert validator.errors("job-status", _status(outcome.result, "ollama")) == []


def test_no_valid_condensation_fails_without_cutting_text(settings: Settings) -> None:
    bad = json.dumps({"summary": "Nothing.", "kept": []})
    handler, _, _ = _handler(settings, [bad, bad, bad])
    with pytest.raises(JobFailedError) as failure:
        asyncio.run(handler(JOB))
    assert (failure.value.code, failure.value.retryable) == ("invalid_model_output", True)


def test_replay_and_mock(settings: Settings) -> None:
    handler, log, _ = _handler(settings, [GOOD])
    original = asyncio.run(handler(JOB))
    replayed = asyncio.run(ReplayMemory(log)(JOB))
    assert replayed.result == original.result
    assert replayed.source == "replay"

    mock = asyncio.run(MockMemory()(JOB))
    memory = cast(dict[str, object], mock.result["memory"])
    assert memory["kept"] == FORMATIVE
    assert len(str(memory["summary"])) <= 600
    assert asyncio.run(MockMemory()(JOB)) == mock  # deterministic


def test_mock_keeps_the_budget_with_many_events() -> None:
    data = copy.deepcopy(JOB_DATA)
    events = [
        {"tick": 2000 + i, "kind": "walked_around", "about": None, "formative": False}
        for i in range(30)
    ]
    data["payload"]["memory"]["events"] = events
    data["payload"]["memory"]["max_chars"] = 100
    outcome = asyncio.run(MockMemory()(GenerationJob.model_validate(data)))
    memory = cast(dict[str, object], outcome.result["memory"])
    assert len(str(memory["summary"])) <= 100


def test_memory_jobs_run_over_jobs_with_the_mock_backend(settings: Settings) -> None:
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
            assert accepted.json()["state"] == "deferred"  # memory runs in the computing night
            night = {
                "contract_version": JOB_DATA["contract_version"],
                "phase": "night",
                "tick": 1300,
            }
            assert (await client.post("/phase", json=night)).status_code == 200
            for _ in range(200):
                body: dict[str, Any] = (await client.get(f"/jobs/{JOB_DATA['request_id']}")).json()
                if body["state"] == "done":
                    return body
                await asyncio.sleep(0.005)
        raise AssertionError("memory job never finished")

    status = asyncio.run(scenario())
    assert ContractValidator(settings.schemas_dir).errors("job-status", status) == []
    assert "question" not in status["result"]
    assert status["result"]["memory"]["kept"] == FORMATIVE


def test_impossible_budget_fails_without_asking_the_model(settings: Settings) -> None:
    data = copy.deepcopy(JOB_DATA)
    data["payload"]["memory"]["events"] = [
        {"tick": 3000 + i, "kind": "felt_pain", "about": None, "formative": True} for i in range(10)
    ]
    data["payload"]["memory"]["max_chars"] = 100  # 10 formative events cannot fit
    handler, log, seeds = _handler(settings, [])
    with pytest.raises(JobFailedError) as failure:
        asyncio.run(handler(GenerationJob.model_validate(data)))
    assert (failure.value.code, failure.value.retryable) == ("bad_request", False)
    assert seeds == []
    assert log.read() == []


def test_v2_gives_a_tick_one_line_and_lists_the_formative_ticks(settings: Settings) -> None:
    """Review of #227 (runs L838/L839): two lines with one tick made the model name the tick
    twice and leave another formative tick out, three attempts in a row."""
    template = load_prompt(settings.prompts_dir, "memory/condense", 2)
    data = JOB.model_dump(mode="json")
    events = data["payload"]["memory"]["events"]
    events.append(
        {
            "tick": 1180,
            "kind": "ate",
            "about": {"kind": "type", "id": "red_berry_bush"},
            "formative": False,
        }
    )
    job = GenerationJob.model_validate(data)
    rendered = template.render(build_values(job, template))
    lines = [line for line in rendered.user.splitlines() if line.startswith("- [tick 1180]")]
    assert len(lines) == 1 and "; " in lines[0] and lines[0].endswith("- FORMATIVE, keep it")
    assert "## Formative ticks (2)\n1100, 1180\n" in rendered.user
    v1 = _template(settings)
    old = v1.render(build_values(job, v1)).user
    assert sum(line.startswith("- [tick 1180]") for line in old.splitlines()) == 2  # v1 as it was
    text = f"=== SYSTEM ===\n{rendered.system}\n\n=== USER ===\n{rendered.user}\n"
    snapshot = SNAPSHOTS / "memory-condense.v2.same-tick.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert text == snapshot.read_text(encoding="utf-8")


def test_v3_tells_attempts_as_attempts_counts_repeats_and_uses_learned_names(
    settings: Settings,
) -> None:
    """Review of #227: the core recorded 'drink flint' for an attempt, and the memory said
    'Drank flint'; 127 of 175 events were repeated drinking and eating; ids stood for names."""
    template = load_prompt(settings.prompts_dir, "memory/condense", 3)
    data = JOB.model_dump(mode="json")
    data["payload"]["memory"]["events"] = [
        {
            "tick": 87,
            "kind": "name_learned",
            "about": {"kind": "inhabitant", "id": "inh-000002"},
            "formative": True,
            "label": {"de": "Rena", "en": "Rena"},
        },
        {
            "tick": 227,
            "kind": "drink",
            "about": {"kind": "type", "id": "flint"},
            "formative": True,
            "outcome": "found_out",
        },
        {
            "tick": 300,
            "kind": "eat",
            "about": {"kind": "type", "id": "dry_wood"},
            "formative": False,
            "outcome": "nothing",
        },
        {
            "tick": 900,
            "kind": "drink",
            "about": {"kind": "type", "id": "spring_water"},
            "formative": False,
            "outcome": "done",
            "times": 6,
        },
        {
            "tick": 950,
            "kind": "talk_to",
            "about": {"kind": "inhabitant", "id": "inh-000003"},
            "formative": False,
            "outcome": "done",
        },
    ]
    job = GenerationJob.model_validate(data)
    rendered = template.render(build_values(job, template))
    user = rendered.user
    assert "- [tick 87] name learned (person 'Rena') - FORMATIVE, keep it" in user
    assert (
        "- [tick 227] tried to drink (thing 'flint') - it did not work, but something was "
        "learned from it - FORMATIVE, keep it"
    ) in user
    assert "- [tick 300] tried to eat (thing 'dry wood') - nothing happened" in user
    assert "- [tick 900] drink (thing 'spring water'), 6 times in all" in user
    assert "- [tick 950] talk to (another person)" in user
    assert "inh-" not in user
    v2 = load_prompt(settings.prompts_dir, "memory/condense", 2)
    old = v2.render(build_values(job, v2)).user
    assert "- [tick 227] drink (thing 'flint') - FORMATIVE, keep it" in old  # v2 as it was
    text = f"=== SYSTEM ===\n{rendered.system}\n\n=== USER ===\n{user}\n"
    snapshot = SNAPSHOTS / "memory-condense.v3.outcomes.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert text == snapshot.read_text(encoding="utf-8")
