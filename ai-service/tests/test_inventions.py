"""Inventions (D471 D2): inventor + rule guard flow, schemas per request, handlers, /jobs."""

from __future__ import annotations

import asyncio
import copy
import json
from pathlib import Path
from typing import Any, cast

import httpx
import pytest

from conftest import TEST_HOST, TOKEN, FakeProbes
from glassworld_ai.app import create_app
from glassworld_ai.calllog import CallLog
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.invention.flow import (
    MockInventions,
    OllamaInventions,
    ReplayInventions,
    invention_schema,
    inventor_schema,
    parse_inventor,
    safety_categories,
    tech_levels,
)
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.llm.jobcall import InvalidOutputError, ModelCallSettings
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.validation import ContractValidator
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.invention-water-vessel.json").read_text(
        "utf-8"
    )
)
JOB = GenerationJob.model_validate(JOB_DATA)
BOWL: dict[str, Any] = {
    "name": {"de": "Lehmschale", "en": "Clay bowl"},
    "purpose": "drink",
    "category": "object",
    "materials": ["clay_lump"],
    "size_m": {"x": 0.25, "y": 0.12, "z": 0.25},
    "steps": [
        {"process": "shape", "materials": ["clay_lump"]},
        {"process": "dry", "materials": ["clay_lump"]},
    ],
    "introduces": ["container"],
    "tech_level": "hut",
}


def proposal(invention: dict[str, Any] | None = None, thought: str | None = "Eine Schale!") -> str:
    return json.dumps({"invention": invention or BOWL, "thought": thought})


def verdict(
    decision: str,
    invention: dict[str, Any] | None = None,
    sound: dict[str, Any] | None = None,
    reasons: list[str] | None = None,
) -> str:
    return json.dumps(
        {
            "note": f"Guard: {decision}.",
            "decision": decision,
            "reasons": reasons or [],
            "invention": invention,
            "sound": sound,
        }
    )


def _vocab(settings: Settings) -> Vocabulary:
    return Vocabulary.load(settings.content_dir / "vocabulary")


def _handler(
    settings: Settings, answers: list[str]
) -> tuple[OllamaInventions, CallLog, list[dict[str, Any]]]:
    sent: list[dict[str, Any]] = []
    queue = iter(answers)

    def fake(request: httpx.Request) -> httpx.Response:
        sent.append(json.loads(request.content))
        return httpx.Response(200, json={"message": {"role": "assistant", "content": next(queue)}})

    log = CallLog(settings.log_dir / "inventions.jsonl")
    client = OllamaClient(
        "http://ollama.test",
        "qwen3.5:9b",
        httpx.AsyncClient(transport=httpx.MockTransport(fake)),
        keep_alive="5m",
    )
    handler = OllamaInventions(
        client=client,
        queue=ModelQueue(8),
        inventor=load_prompt(settings.prompts_dir, "invention/invent", 1),
        guard=load_prompt(settings.prompts_dir, "guard/invention", 1),
        vocab=_vocab(settings),
        levels=tech_levels(settings.content_dir),
        settings=ModelCallSettings(temperature=0.7, num_ctx=8192, max_retries=1, call_timeout_s=30),
        log=log,
        safety=safety_categories(settings.content_dir),
    )
    return handler, log, sent


def _status(result: dict[str, object]) -> dict[str, object]:
    return {
        "contract_version": JOB_DATA["contract_version"],
        "job_id": JOB_DATA["request_id"],
        "tick": JOB.tick,
        "kind": "invention",
        "state": "done",
        "result": result,
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


def _result(outcome: Any) -> dict[str, Any]:
    return cast(dict[str, Any], outcome.result["invention"])


def test_approved_invention_with_sound(settings: Settings) -> None:
    sound = {
        "needed": True,
        "description": "clay bowl set on stone, dull knock",
        "kind": "one_shot",
        "part": None,
    }
    handler, log, sent = _handler(settings, [proposal(), verdict("approve", sound=sound)])
    outcome = asyncio.run(handler(JOB))
    result = _result(outcome)
    assert (result["decision"], result["invention"], result["sound"]) == ("approve", BOWL, sound)
    assert result["thought"] == {"locale": "de", "text": "Eine Schale!"}
    assert [r["decision"] for r in result["rounds"]] == ["approve"]
    assert (
        ContractValidator(settings.schemas_dir).errors("job-status", _status(outcome.result)) == []
    )
    assert [r["role"] for r in log.read()] == ["invention", "guard"]
    # structured output: materials only from the perception, tech level up to the allowed one
    material_enum = sent[0]["format"]["properties"]["invention"]["properties"]["materials"][
        "items"
    ]["enum"]
    assert material_enum == ["clay_lump", "dry_branch"]
    levels = sent[0]["format"]["properties"]["invention"]["properties"]["tech_level"]["enum"]
    assert levels == ["wilderness", "tool", "hut"]


def test_downsize_uses_the_guards_invention(settings: Settings) -> None:
    smaller = {**BOWL, "size_m": {"x": 0.15, "y": 0.08, "z": 0.15}}
    handler, _, _ = _handler(
        settings, [proposal(), verdict("downsize", invention=smaller, reasons=["too_large"])]
    )
    result = _result(asyncio.run(handler(JOB)))
    assert (result["decision"], result["invention"]["size_m"]) == ("downsize", smaller["size_m"])


def test_revise_goes_back_to_the_inventor_once(settings: Settings) -> None:
    handler, _, sent = _handler(
        settings,
        [proposal(), verdict("revise", reasons=["material"]), proposal(), verdict("approve")],
    )
    result = _result(asyncio.run(handler(JOB)))
    assert [r["decision"] for r in result["rounds"]] == ["revise", "approve"]
    assert "The rule guard says" in sent[2]["messages"][1]["content"]


def test_revise_in_the_last_round_becomes_reject(settings: Settings) -> None:
    handler, _, _ = _handler(
        settings, [proposal(), verdict("revise"), proposal(), verdict("revise")]
    )
    result = _result(asyncio.run(handler(JOB)))
    assert (result["decision"], result["invention"], result["sound"]) == ("reject", None, None)
    assert [r["decision"] for r in result["rounds"]] == ["revise", "reject"]


def test_guard_rejects_an_invented_animal(settings: Settings) -> None:
    handler, _, _ = _handler(settings, [proposal(), verdict("reject", reasons=["living_being"])])
    result = _result(asyncio.run(handler(JOB)))
    assert (result["decision"], result["rounds"][0]["reasons"]) == ("reject", ["living_being"])


def test_unperceived_material_or_step_material_is_invalid(settings: Settings) -> None:
    shape = invention_schema(_vocab(settings), ["clay_lump"], ["wilderness", "tool", "hut"], 1.0)
    schema = inventor_schema(shape)
    with pytest.raises(InvalidOutputError):
        parse_inventor(proposal({**BOWL, "materials": ["iron_bar"]}), schema)
    steps = [
        {"process": "shape", "materials": ["clay_lump"]},
        {"process": "dry", "materials": ["dry_branch"]},
    ]
    with pytest.raises(InvalidOutputError):  # schema: dry_branch is not offered at all here
        parse_inventor(proposal({**BOWL, "steps": steps}), schema)


def test_no_valid_answer_fails_retryable(settings: Settings) -> None:
    handler, _, _ = _handler(settings, ["nonsense", "nonsense"])
    with pytest.raises(JobFailedError) as failure:
        asyncio.run(handler(JOB))
    assert (failure.value.code, failure.value.retryable) == ("invalid_model_output", True)


def test_replay_reproduces_the_rounds(settings: Settings) -> None:
    answers = [proposal(), verdict("revise"), proposal(), verdict("approve")]
    handler, log, _ = _handler(settings, answers)
    original = asyncio.run(handler(JOB))
    replay = ReplayInventions(
        log,
        load_prompt(settings.prompts_dir, "invention/invent", 1),
        load_prompt(settings.prompts_dir, "guard/invention", 1),
        _vocab(settings),
        tech_levels(settings.content_dir),
        safety_categories(settings.content_dir),
    )
    assert asyncio.run(replay(JOB)).result == original.result


def test_mock_is_valid_and_deterministic(settings: Settings) -> None:
    mock = MockInventions(_vocab(settings))
    first, second = asyncio.run(mock(JOB)), asyncio.run(mock(JOB))
    assert first == second
    assert ContractValidator(settings.schemas_dir).errors("job-status", _status(first.result)) == []


def test_invention_job_over_jobs_with_the_mock_backend(settings: Settings) -> None:
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
            assert (await client.post("/jobs", json=copy.deepcopy(JOB_DATA))).status_code == 202
            for _ in range(200):
                body: dict[str, Any] = (await client.get(f"/jobs/{JOB_DATA['request_id']}")).json()
                if body["state"] == "done":
                    return body
                await asyncio.sleep(0.005)
        raise AssertionError("invention job never finished")

    status = asyncio.run(scenario())
    assert ContractValidator(settings.schemas_dir).errors("job-status", status) == []
    assert status["result"]["invention"]["decision"] == "approve"


def test_prompt_snapshots(settings: Settings) -> None:
    import os

    from glassworld_ai.invention.flow import guard_values, inventor_values

    inventor = load_prompt(settings.prompts_dir, "invention/invent", 1)
    guard = load_prompt(settings.prompts_dir, "guard/invention", 1)
    a = inventor.render(inventor_values(JOB, inventor, _vocab(settings), ""))
    b = guard.render(
        guard_values(JOB, BOWL, 1, _vocab(settings), safety_categories(settings.content_dir))
    )
    text = (
        f"=== INVENTOR SYSTEM ===\n{a.system}\n\n=== INVENTOR USER ===\n{a.user}\n\n"
        f"=== GUARD SYSTEM ===\n{b.system}\n\n=== GUARD USER ===\n{b.user}\n"
    )
    snapshot = Path(__file__).resolve().parent / "snapshots" / "invention-invent.v1+guard.v1.txt"
    if os.environ.get("GW_UPDATE_SNAPSHOTS") == "1":
        snapshot.write_text(text, encoding="utf-8", newline="\n")
    assert snapshot.exists(), f"missing snapshot {snapshot.name}; run with GW_UPDATE_SNAPSHOTS=1"
    assert text == snapshot.read_text(encoding="utf-8")


def test_reason_codes_come_from_the_data(settings: Settings) -> None:
    handler, _, _ = _handler(
        settings,
        [
            proposal(),
            verdict("reject", reasons=["looks_odd"]),
            verdict("reject", reasons=["harmful"]),
        ],
    )
    result = _result(asyncio.run(handler(JOB)))  # the unknown code is invalid, the retry is used
    assert result["rounds"][0]["reasons"] == ["harmful"]


def test_repeated_ids_are_normalised_not_rejected(settings: Settings) -> None:
    repeated = {
        **BOWL,
        "materials": ["clay_lump", "clay_lump"],
        "steps": [
            {"process": "shape", "materials": ["clay_lump", "clay_lump"]},
            {"process": "dry", "materials": ["clay_lump"]},
        ],
    }
    handler, _, _ = _handler(settings, [proposal(repeated), verdict("approve")])
    result = _result(asyncio.run(handler(JOB)))
    assert result["invention"]["materials"] == ["clay_lump"]
    assert result["invention"]["steps"][0]["materials"] == ["clay_lump"]


def test_v4_and_guard_v3_check_that_the_idea_answers_the_want(settings: Settings) -> None:
    """B2668 (run L1172: a want for company got a cooking oven): the inventor hears the need with
    what it is about and must answer exactly that; the guard sees the want and may send an idea
    for something else back (off_want). Nothing names a solution (PR2)."""
    from glassworld_ai.invention.flow import guard_values, inventor_values

    vocab = _vocab(settings)
    data = copy.deepcopy(JOB_DATA)
    data["payload"]["invention"]["trigger"] = "need"
    data["payload"]["invention"]["want"] = "company"
    job = GenerationJob.model_validate(data)
    about = (vocab.describe("need", "company") or "").rstrip(".")
    v4 = load_prompt(settings.prompts_dir, "invention/invent", 4)
    rendered = v4.render(inventor_values(job, v4, vocab, ""))
    assert f"You need something: company ({about})." in rendered.user
    assert "An idea for something else is wrong" in rendered.system
    v3 = load_prompt(settings.prompts_dir, "invention/invent", 3)
    assert f"({about})" not in v3.render(inventor_values(job, v3, vocab, "")).user  # v3 as it was

    safety = safety_categories(settings.content_dir)
    g3 = load_prompt(settings.prompts_dir, "guard/invention", 3)
    guard = g3.render(guard_values(job, BOWL, 1, vocab, safety, g3.settings))
    assert f"## What the inhabitant wanted\ncompany ({about})" in guard.user
    assert "- off_want:" in guard.system and "off_want" in guard.system.split("Decide:")[0]
    g2 = load_prompt(settings.prompts_dir, "guard/invention", 2)
    old = g2.render(guard_values(job, BOWL, 1, vocab, safety, g2.settings))
    assert "off_want" not in old.system and "What the inhabitant wanted" not in old.user


def test_v5_and_guard_v4_take_a_ripe_idea_as_the_want(settings: Settings) -> None:
    """B2687 (R28): a ripe idea of its own is the reason - the inventor hears it, the guard checks
    the thing against it; older prompts render as before."""
    from glassworld_ai.invention.flow import guard_values, inventor_values

    vocab = _vocab(settings)
    data = copy.deepcopy(JOB_DATA)
    data["payload"]["invention"]["trigger"] = "idea"
    data["payload"]["invention"]["want"] = "purpose"
    data["payload"]["invention"]["idea"] = {
        "id": "idea-0001",
        "text": "The sharp stone might open a nut.",
        "roots": [{"kind": "type", "id": "flint"}],
    }
    job = GenerationJob.model_validate(data)
    v5 = load_prompt(settings.prompts_dir, "invention/invent", 5)
    rendered = v5.render(inventor_values(job, v5, vocab, ""))
    assert "You had an idea of your own: The sharp stone might open a nut." in rendered.user
    safety = safety_categories(settings.content_dir)
    g4 = load_prompt(settings.prompts_dir, "guard/invention", 4)
    guard = g4.render(guard_values(job, BOWL, 1, vocab, safety, g4.settings))
    assert "its own idea: The sharp stone might open a nut." in guard.user
    # Review GW1 #333: with trigger idea the want (lowest need, for the record) is not said,
    # so a good idea is never sent back for not helping that need.
    assert "Purpose" not in guard.user
    assert "Purpose" not in rendered.user
    g3 = load_prompt(settings.prompts_dir, "guard/invention", 3)
    old = g3.render(guard_values(job, BOWL, 1, vocab, safety, g3.settings))
    assert "its own idea" not in old.user


def test_v6_says_what_the_inhabitant_knows_about_its_materials(settings: Settings) -> None:
    """A1582 (R8): its own beliefs with their source, also a 'not'; never the truth (GW1 #372)."""
    from glassworld_ai.invention.flow import inventor_values

    vocab = _vocab(settings)
    data = copy.deepcopy(JOB_DATA)
    data["payload"]["invention"]["inhabitant"]["knowledge"] = [
        {
            "subject": {"kind": "type", "id": "clay_lump"},
            "property": "edible",
            "value": False,
            "confidence": 0.9,
            "source": "experienced",
            "told_by": None,
            "tick": 10,
        }
    ]
    job = GenerationJob.model_validate(data)
    v6 = load_prompt(settings.prompts_dir, "invention/invent", 6)
    user = v6.render(inventor_values(job, v6, vocab, "")).user
    assert "## What you know\n- clay_lump is not edible (you tried it yourself)" in user
    v5 = load_prompt(settings.prompts_dir, "invention/invent", 5)
    assert "## What you know" not in v5.render(inventor_values(job, v5, vocab, "")).user
    # An invention by the inhabitant's name for it, never its runtime id alone (review GW1 #374).
    data["payload"]["invention"]["inhabitant"]["knowledge"][0] |= {
        "subject": {"kind": "type", "id": "inv_a1b2c3d4e5f6"},
        "label": {"de": "Lehmschale", "en": "Clay bowl"},
    }
    named = GenerationJob.model_validate(data)
    user = v6.render(inventor_values(named, v6, vocab, "")).user
    assert 'inv_a1b2c3d4e5f6 (you call it "Lehmschale") is not edible' in user
