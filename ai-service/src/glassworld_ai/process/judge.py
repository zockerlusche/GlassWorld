# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Judge of unknown tries (B2537, D471 P2, A1450): job kind process.

An inhabitant applies a basic process to things that no rule covers. The model (world role: it
sees the true properties from the signature, never shown to players) decides feasible or not and,
if feasible, what changes. The service builds the ProcessRule itself: id from the signature hash,
process, input conditions and tool from the true property sets, and checks everything the model
chose (the guard in code): vocabulary, no living properties (B2531), removed properties belong to
input 1, heat and tech level not above what is available, consumed inputs exist. Otherwise it asks
again (seed + n). Not feasible is a result too; the core stores it and never asks again.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
from glassworld_ai.invention.flow import tech_levels
from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.llm.jobcall import (
    InvalidOutputError,
    ModelCallSettings,
    ask_model,
    fail,
    recorded_answers,
)
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import PromptTemplate
from glassworld_ai.queue import ModelQueue
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import humanize, plain_id
from glassworld_contracts import GenerationJob
from glassworld_contracts.signature import world_rule_id

ROLE = "process"
MIN_TICKS, MAX_TICKS = 5, 2000  # technical bounds for the model; the core scales by data


class JudgeContext:
    """What the judge needs from the data: properties, living properties, heat and tech order."""

    guard: dict[str, Any]

    def __init__(
        self, content_dir: Path, vocab: Vocabulary, guard: dict[str, Any] | None = None
    ) -> None:
        self.guard = guard or {}
        settings = json.loads((content_dir / "invention/settings.json").read_text(encoding="utf-8"))
        self.living = set(cast(list[str], settings.get("living_properties", [])))
        self.properties = [p for p in vocab.ids("property") if p not in self.living]
        rules = json.loads((content_dir / "processes/rules.json").read_text(encoding="utf-8"))
        self.heat_levels = [str(h) for h in rules["heat_levels"]]  # lowest first (order matters)
        self.tech_levels = tech_levels(content_dir)


def _payload(job: GenerationJob) -> Any:
    payload = job.payload.process
    if payload is None:
        raise JobFailedError("bad_request", "process job without process payload", retryable=False)
    return payload


def _signature(payload: Any) -> tuple[str, list[list[str]], list[str] | None, str | None]:
    sig = payload.signature
    inputs = sorted(
        (sorted({plain_id(p) for p in props}) for props in sig.inputs), key=lambda ps: ",".join(ps)
    )
    tool = sorted({plain_id(p) for p in sig.tool}) if sig.tool else None
    heat = plain_id(sig.heat_level) if sig.heat_level is not None else None
    return plain_id(sig.process), inputs, tool, heat


def _upto(levels: list[str], level: str | None) -> list[str]:
    if level is None or level not in levels:
        return []
    return levels[: levels.index(level) + 1]


def output_schema(job: GenerationJob, ctx: JudgeContext) -> dict[str, Any]:
    payload = _payload(job)
    process, inputs, _, heat = _signature(payload)
    by_heat = process in ctx.guard.get("heat_processes", [])
    heat_choices: list[Any] = _upto(ctx.heat_levels, heat) + ([] if by_heat else [None])
    may_add = cast(dict[str, list[str]], ctx.guard.get("may_add", {})).get(process)
    addable = [p for p in ctx.properties if may_add is None or p in may_add] or ctx.properties[:1]
    first = inputs[0]
    rule = {
        "type": "object",
        "additionalProperties": False,
        "required": [
            "add_properties",
            "remove_properties",
            "consumes",
            "heat_min",
            "tech_level",
            "duration_ticks",
        ],
        "properties": {
            "add_properties": {
                "type": "array",
                "maxItems": 4,
                "uniqueItems": True,
                "items": {"type": "string", "enum": addable},
            },
            "remove_properties": {
                "type": "array",
                "maxItems": 4,
                "uniqueItems": True,
                "items": {"type": "string", "enum": first},
            },
            "consumes": {
                "type": "array",
                "maxItems": 3,
                "uniqueItems": True,
                "items": {"type": "integer", "minimum": 2, "maximum": max(2, len(inputs))},
            },
            "heat_min": {"enum": heat_choices},
            "tech_level": {
                "type": "string",
                "enum": _upto(ctx.tech_levels, plain_id(payload.tech_level)) or ctx.tech_levels[:1],
            },
            "duration_ticks": {"type": "integer", "minimum": MIN_TICKS, "maximum": MAX_TICKS},
        },
    }
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["feasible", "rationale", "rule"],
        "properties": {
            "feasible": {"type": "boolean"},
            "rationale": {"type": "string", "minLength": 1, "maxLength": 280},
            "rule": rule,  # always filled (Ollama ignores conditions); unused when not feasible
        },
    }


def build_values(job: GenerationJob, ctx: JudgeContext) -> dict[str, str]:
    payload = _payload(job)
    process, inputs, tool, heat = _signature(payload)

    def props(values: list[str]) -> str:
        return ", ".join(humanize(v) for v in values) or "(nothing special)"

    return {
        "process": humanize(process),
        "inputs": "\n".join(f"{n}. {props(p)}" for n, p in enumerate(inputs, start=1)),
        "tool": props(tool) if tool else "(none)",
        "heat": humanize(heat) if heat else "(no heat)",
        "seen": ", ".join(humanize(plain_id(t.type)) for t in payload.inputs) or "(nothing)",
        "tech_level": humanize(plain_id(payload.tech_level)),
    }


def parse_judgement(raw: str, job: GenerationJob, ctx: JudgeContext) -> dict[str, Any]:
    """The guard in code: schema, then the checks the schema cannot express; returns the result."""
    process, inputs, tool, heat = _signature(_payload(job))
    reason = impossible(ctx.guard, process, inputs, heat)
    if reason is not None:  # physics says no, whatever the model thinks (not a model error)
        return {"feasible": False, "rule": None, "rationale": f"Physics: {reason}."[:280]}
    schema = output_schema(job, ctx)
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    if isinstance(data, dict) and isinstance(cast(dict[str, Any], data).get("rule"), dict):
        chosen_raw = cast(dict[str, Any], cast(dict[str, Any], data)["rule"])
        for key in ("add_properties", "remove_properties", "consumes"):  # Ollama: no uniqueItems
            if isinstance(chosen_raw.get(key), list):
                chosen_raw[key] = list(dict.fromkeys(cast(list[Any], chosen_raw[key])))
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        raise InvalidOutputError(
            f"{'/'.join(str(p) for p in first.path) or '(root)'}: {first.message}"
        )
    values = cast(dict[str, Any], data)
    rationale = " ".join(str(values["rationale"]).split())[:280]
    if not values["feasible"]:
        return {"feasible": False, "rule": None, "rationale": rationale}
    chosen = values["rule"]
    present = {p for props in inputs for p in props}
    # Food or drink never comes from nothing (guard no_new): drop it instead of asking again.
    chosen["add_properties"] = [
        p for p in chosen["add_properties"] if p not in set(ctx.guard.get("no_new", [])) - present
    ]
    if not chosen["add_properties"] and not chosen["remove_properties"]:
        raise InvalidOutputError("a feasible try must change something")
    if set(chosen["add_properties"]) & ctx.living:
        raise InvalidOutputError("things never become alive (B2531)")
    _physics(ctx.guard, process, inputs, chosen)
    if set(chosen["add_properties"]) <= set(inputs[0]) and not chosen["remove_properties"]:
        raise InvalidOutputError("input 1 already has every added property")
    rule = {
        "id": world_rule_id(process, inputs, tool, heat),
        "process": process,
        "inputs": [{"properties_all": p, "properties_none": []} for p in inputs],
        "tool": {"properties_all": tool, "properties_none": []} if tool else None,
        "heat_min": chosen["heat_min"],
        "duration_ticks": int(chosen["duration_ticks"]),
        "tech_level": chosen["tech_level"],
        "result": {
            "add_properties": sorted(set(chosen["add_properties"]) - set(inputs[0])),
            "remove_properties": sorted(chosen["remove_properties"]),
            "produces": None,
            "consumes": sorted(n - 1 for n in chosen["consumes"] if n - 1 < len(inputs)),
        },
    }
    return {"feasible": True, "rule": rule, "rationale": rationale}


def guard_settings(template: PromptTemplate) -> dict[str, Any]:
    raw: object = template.settings.get("guard")
    return cast(dict[str, Any], raw) if isinstance(raw, dict) else {}


def impossible(
    guard: dict[str, Any], process: str, inputs: list[list[str]], heat: str | None
) -> str | None:
    """Physics as data (prompt settings.guard): why this try cannot work at all, or None."""
    present = {p for props in inputs for p in props}
    needs = cast(dict[str, list[str]], guard.get("needs_any", {})).get(process)
    if needs and not (set(needs) & present):
        return f"{process} needs an input with one of {needs}"
    if process in guard.get("heat_processes", []) and heat is None:
        return f"{process} works by heat and there is none"
    never = sorted(
        set(cast(dict[str, list[str]], guard.get("never_if", {})).get(process, [])) & present
    )
    if never:
        return f"{process} does not work on {never}"
    return None


def _physics(
    guard: dict[str, Any], process: str, inputs: list[list[str]], chosen: dict[str, Any]
) -> None:
    """Model mistakes (asked again): heat processes need heat."""
    if process in guard.get("heat_processes", []) and chosen["heat_min"] is None:
        raise InvalidOutputError(f"{process} works by heat: heat_min is needed")


class OllamaProcesses:
    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        context: JudgeContext,
        settings: ModelCallSettings,
        log: CallLog,
    ) -> None:
        self._client, self._queue, self._template = client, queue, template
        self._ctx, self._settings, self._log = context, settings, log

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        rendered = self._template.render(build_values(job, self._ctx))
        answer = await ask_model(
            role=ROLE,
            job=job,
            rendered=rendered,
            schema=output_schema(job, self._ctx),
            parse=lambda raw: parse_judgement(raw, job, self._ctx),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
        )
        return JobOutcome(
            {"process": answer.value},
            "ollama",
            self._client.model[:128],
            rendered.version,
            answer.latency_ms,
            answer.attempts,
        )


class MockProcesses:
    """Deterministic stand-in: nothing new happens (not feasible), so core tests stay calm."""

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        _payload(job)
        result = {"feasible": False, "rule": None, "rationale": "Mock: no new rule."}
        return JobOutcome({"process": result}, "mock", "mock", None, 0, 1)


class ReplayProcesses:
    def __init__(self, log: CallLog, context: JudgeContext) -> None:
        self._log, self._ctx = log, context
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for job {job_id}")
        try:
            result = parse_judgement(str(row["raw_output"]), job, self._ctx)
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            {"process": result},
            "replay",
            str(row.get("model", "replay"))[:128],
            str(row.get("prompt_version", "")) or None,
            0,
            attempts if isinstance(attempts, int) else 1,
        )
