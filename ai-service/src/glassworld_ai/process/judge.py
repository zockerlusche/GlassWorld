# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Judge of unknown tries (B2537, D471 P2, A1450): job kind process.

An inhabitant applies a basic process to things that no rule covers. The model (world role: it
sees the true properties from the signature, never shown to players) decides feasible or not and,
if feasible, which input changes and how. The service builds the ProcessRule itself: id from the
signature hash, process, input conditions (the changing input first) and tool from the true
property sets, and checks everything the model chose (the guard in code, physics as data in the
prompt settings, validated at load). Tries that physics rules out are answered 'not feasible'
without a model call. Not feasible is a result too; the core stores it and never asks again.
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
GUARD_LISTS = ("heat_processes", "no_new", "never_remove")
GUARD_MAPS = ("needs_any", "never_if", "may_add", "may_remove")


class GuardError(ValueError):
    """The physics guard data is broken (fail fast at load, review GW1 #109)."""


def validate_guard(guard: dict[str, Any], vocab: Vocabulary) -> dict[str, Any]:
    """Keys, process ids and property ids of the guard must exist; returns it unchanged."""
    unknown = sorted(set(guard) - set(GUARD_LISTS) - set(GUARD_MAPS))
    if unknown:
        raise GuardError(f"unknown guard keys {unknown}")
    processes, properties = set(vocab.ids("process")), set(vocab.ids("property"))
    for key in GUARD_LISTS:
        ids = guard.get(key, [])
        allowed = processes if key == "heat_processes" else properties
        bad = sorted(set(ids) - allowed)
        if bad:
            raise GuardError(f"guard.{key}: unknown ids {bad}")
    for key in GUARD_MAPS:
        table = cast(dict[str, list[str]], guard.get(key, {}))
        bad_processes = sorted(set(table) - processes)
        bad_properties = sorted({p for ids in table.values() for p in ids} - properties)
        if bad_processes or bad_properties:
            raise GuardError(f"guard.{key}: unknown ids {bad_processes + bad_properties}")
    return guard


class JudgeContext:
    """What the judge needs from the data: properties, living properties, heat and tech order."""

    guard: dict[str, Any]

    def __init__(
        self, content_dir: Path, vocab: Vocabulary, guard: dict[str, Any] | None = None
    ) -> None:
        self.guard = validate_guard(guard or {}, vocab)
        settings = json.loads((content_dir / "invention/settings.json").read_text(encoding="utf-8"))
        self.living = set(cast(list[str], settings.get("living_properties", [])))
        self.properties = [p for p in vocab.ids("property") if p not in self.living]
        rules = json.loads((content_dir / "processes/rules.json").read_text(encoding="utf-8"))
        self.heat_levels = [str(h) for h in rules["heat_levels"]]  # lowest first (order matters)
        self.tech_levels = tech_levels(content_dir)


def guard_settings(template: PromptTemplate) -> dict[str, Any]:
    raw: object = template.settings.get("guard")
    return cast(dict[str, Any], raw) if isinstance(raw, dict) else {}


def _payload(job: GenerationJob) -> Any:
    payload = job.payload.process
    if payload is None:
        raise JobFailedError("bad_request", "process job without process payload", retryable=False)
    return payload


def _signature(payload: Any) -> tuple[str, list[list[str]], list[str] | None, str | None]:
    """Process, inputs in canonical order (as in the signature hash), tool, heat."""
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


def impossible(
    guard: dict[str, Any], process: str, inputs: list[list[str]], heat: str | None
) -> str | None:
    """Physics as data: why this try cannot work at all, or None (checked before any call)."""
    present = {p for props in inputs for p in props}
    if process in guard.get("heat_processes", []) and heat is None:
        return f"{process} works by heat and there is none"
    needs = cast(dict[str, list[str]], guard.get("needs_any", {})).get(process)
    if needs and not (set(needs) & present):
        return f"{process} needs an input with one of {needs}"
    never = sorted(
        set(cast(dict[str, list[str]], guard.get("never_if", {})).get(process, [])) & present
    )
    if never:
        return f"{process} does not work on {never}"
    may_add = cast(dict[str, list[str]], guard.get("may_add", {}))
    may_remove = cast(dict[str, list[str]], guard.get("may_remove", {}))
    if may_add.get(process) == [] and may_remove.get(process) == []:
        return f"{process} cannot change anything (yet)"
    return None


def precheck(job: GenerationJob, ctx: JudgeContext) -> dict[str, Any] | None:
    """The 'not feasible' result when physics rules the try out, else None (then ask the model)."""
    process, inputs, _, heat = _signature(_payload(job))
    reason = impossible(ctx.guard, process, inputs, heat)
    if reason is None:
        return None
    return {"feasible": False, "rule": None, "rationale": f"Physics: {reason}."[:280]}


def output_schema(job: GenerationJob, ctx: JudgeContext) -> dict[str, Any]:
    payload = _payload(job)
    process, inputs, _, heat = _signature(payload)
    by_heat = process in ctx.guard.get("heat_processes", [])
    heat_choices: list[Any] = _upto(ctx.heat_levels, heat) + ([] if by_heat else [None])
    may_add = cast(dict[str, list[str]], ctx.guard.get("may_add", {})).get(process)
    addable = [p for p in ctx.properties if may_add is None or p in may_add]
    protected = set(ctx.guard.get("never_remove", []))
    may_remove = cast(dict[str, list[str]], ctx.guard.get("may_remove", {})).get(process)
    removable = sorted(
        {p for props in inputs for p in props if may_remove is None or p in may_remove} - protected
    )
    add_items: dict[str, Any] = (
        {"type": "string", "enum": addable} if addable else {"type": "string", "enum": [""]}
    )
    remove_items: dict[str, Any] = (
        {"type": "string", "enum": removable} if removable else {"type": "string", "enum": [""]}
    )
    rule = {
        "type": "object",
        "additionalProperties": False,
        "required": [
            "changes",
            "add_properties",
            "remove_properties",
            "consumes",
            "heat_min",
            "tech_level",
            "duration_ticks",
        ],
        "properties": {
            "changes": {"type": "integer", "minimum": 1, "maximum": len(inputs)},
            "add_properties": {
                "type": "array",
                "maxItems": 4 if addable else 0,
                "uniqueItems": True,
                "items": add_items,
            },
            "remove_properties": {
                "type": "array",
                "maxItems": 4 if removable else 0,
                "uniqueItems": True,
                "items": remove_items,
            },
            "consumes": {
                "type": "array",
                "maxItems": max(0, len(inputs) - 1),
                "uniqueItems": True,
                "items": {"type": "integer", "minimum": 1, "maximum": len(inputs)},
            },
            "heat_min": {"enum": heat_choices or [None]},
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
    early = precheck(job, ctx)
    if early is not None:
        return early
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
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidOutputError(f"{where}: {first.message}")
    values = cast(dict[str, Any], data)
    rationale = " ".join(str(values["rationale"]).split())[:280]
    if not values["feasible"]:
        return {"feasible": False, "rule": None, "rationale": rationale}
    process, inputs, tool, heat = _signature(_payload(job))
    chosen = cast(dict[str, Any], values["rule"])
    target = int(chosen["changes"]) - 1
    if process in ctx.guard.get("heat_processes", []) and chosen["heat_min"] is None:
        raise InvalidOutputError(f"{process} works by heat: heat_min is needed")
    if target + 1 in chosen["consumes"]:
        raise InvalidOutputError("the changing input is never used up")
    removed = [p for p in chosen["remove_properties"] if p]
    if set(removed) - set(inputs[target]):
        raise InvalidOutputError("removed properties must belong to the changing input")
    present = {p for props in inputs for p in props}
    no_new = set(ctx.guard.get("no_new", [])) - present
    added = [p for p in chosen["add_properties"] if p and p not in no_new]  # never from nothing
    if set(added) & ctx.living:
        raise InvalidOutputError("things never become alive (B2531)")
    added = sorted(set(added) - set(inputs[target]))
    if not added and not removed:
        raise InvalidOutputError("a feasible try must change something")
    # Rule order: the changing input first (rule semantics), the others in canonical order.
    order = [target] + [i for i in range(len(inputs)) if i != target]
    rule = {
        "id": world_rule_id(process, inputs, tool, heat),
        "process": process,
        "inputs": [{"properties_all": inputs[i], "properties_none": []} for i in order],
        "tool": {"properties_all": tool, "properties_none": []} if tool else None,
        "heat_min": chosen["heat_min"],
        "duration_ticks": int(chosen["duration_ticks"]),
        "tech_level": chosen["tech_level"],
        "result": {
            "add_properties": added,
            "remove_properties": sorted(removed),
            "produces": None,
            "consumes": sorted(order.index(n - 1) for n in chosen["consumes"]),
        },
    }
    return {"feasible": True, "rule": rule, "rationale": rationale}


GUARD_SOURCE = ("local_model", "physics-guard")  # no model call: a data rule answered


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
        early = precheck(job, self._ctx)
        if early is not None:  # physics rules it out: no GPU call (review GW1 #109)
            return JobOutcome({"process": early}, *GUARD_SOURCE, None, 0, 1)
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
        early = precheck(job, self._ctx)
        if early is not None:
            return JobOutcome({"process": early}, *GUARD_SOURCE, None, 0, 1)
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:  # a replay never gets the answer later: not retryable (review GW1 #109)
            raise JobFailedError(
                "model_unavailable", f"no recorded answer for job {job_id}", retryable=False
            )
        try:
            result = parse_judgement(str(row["raw_output"]), job, self._ctx)
        except InvalidOutputError as error:
            raise JobFailedError(
                "model_unavailable", f"recorded answer does not fit job {job_id}", retryable=False
            ) from error
        attempts = row.get("attempt", 1)
        return JobOutcome(
            {"process": result},
            "replay",
            str(row.get("model", "replay"))[:128],
            str(row.get("prompt_version", "")) or None,
            0,
            attempts if isinstance(attempts, int) else 1,
        )
