# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Invention job (D471 stage 2): the inventor proposes, the rule guard checks (own instruction).

Flow: invent -> guard. "approve"/"downsize" end it; "revise" sends the guard's note back to the
inventor (next round); "reject" ends it. At most `max_rounds` guard rounds (data, B4b: 2); a
revise in the last round becomes a reject. The guard also decides on sound (B2530) and rejects
living beings (B2531) and harmful things (B4b R16). Everything is a PROPOSAL: the core checks
deterministically afterwards and has the last word (ADR-0005).

Output schemas are built per request: materials only from what the inhabitant perceives,
purpose/processes/properties only from the vocabulary, tech level only up to the allowed one.
"""

from __future__ import annotations

import json
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any, ClassVar, Protocol, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
from glassworld_ai.jobs import JobOutcome
from glassworld_ai.llm.jobcall import (
    InvalidOutputError,
    ModelCallSettings,
    ask_model,
    fail,
)
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import PromptTemplate, RenderedPrompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.vocabulary import Vocabulary
from glassworld_ai.words import (
    RUNTIME_TYPE,
    ContextError,
    bands,
    humanize,
    own_names,
    plain_id,
    vocab_line,
    word,
)
from glassworld_contracts import GenerationJob

INVENT_ROLE = "invention"
GUARD_ROLE = "guard"
THOUGHT_MAX = 140

Parse = Callable[[str], Any]


class Asker(Protocol):
    """Asks the model (or a replay) once for a structured answer; returns (value, attempts)."""

    def __call__(
        self, role: str, rendered: RenderedPrompt, schema: dict[str, Any], parse: Parse
    ) -> Awaitable[tuple[Any, int]]: ...


def tech_levels(content_dir: Path) -> list[str]:
    """Tech levels in their order (vocabulary file order, lowest first)."""
    data = json.loads((content_dir / "vocabulary" / "tech_levels.json").read_text("utf-8"))
    return [str(entry["id"]) for entry in data["entries"]]


def table(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def _payload(job: GenerationJob) -> Any:
    payload = job.payload.invention
    if payload is None:
        raise ContextError("job has no invention payload")
    return payload


# --- schemas ---------------------------------------------------------------------------------


def _strings(values: list[str]) -> dict[str, Any]:
    return {"type": "string", "enum": values} if values else {"type": "string", "maxLength": 0}


def invention_schema(
    vocab: Vocabulary, materials: list[str], levels: list[str], max_size: float
) -> dict[str, Any]:
    material = _strings(materials)
    axis = {"type": "number", "minimum": 0.01, "maximum": max_size}
    return {
        "type": "object",
        "additionalProperties": False,
        "required": [
            "name",
            "purpose",
            "category",
            "materials",
            "size_m",
            "steps",
            "introduces",
            "tech_level",
        ],
        "properties": {
            "name": {
                "type": "object",
                "additionalProperties": False,
                "required": ["de", "en"],
                "properties": {
                    "de": {"type": "string", "minLength": 1, "maxLength": 40},
                    "en": {"type": "string", "minLength": 1, "maxLength": 40},
                },
            },
            "purpose": _strings(vocab.general_ids("action")),
            "category": {"type": "string", "enum": ["object", "building"]},
            "materials": {
                "type": "array",
                "minItems": 1,
                "maxItems": 6,
                "items": material,
            },
            "size_m": {
                "type": "object",
                "additionalProperties": False,
                "required": ["x", "y", "z"],
                "properties": {"x": axis, "y": axis, "z": axis},
            },
            "steps": {
                "type": "array",
                "minItems": 2,
                "maxItems": 8,
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["process", "materials"],
                    "properties": {
                        "process": _strings(vocab.ids("process")),
                        "materials": {
                            "type": "array",
                            "minItems": 1,
                            "maxItems": 6,
                            "items": material,
                        },
                    },
                },
            },
            "introduces": {
                "type": "array",
                "maxItems": 6,
                "items": _strings(vocab.ids("property")),
            },
            "tech_level": _strings(levels),
        },
    }


def inventor_schema(invention: dict[str, Any]) -> dict[str, Any]:
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["invention", "thought"],
        "properties": {
            "invention": invention,
            "thought": {
                "anyOf": [
                    {"type": "string", "minLength": 1, "maxLength": THOUGHT_MAX},
                    {"type": "null"},
                ]
            },
        },
    }


def guard_schema(invention: dict[str, Any], reasons: list[str]) -> dict[str, Any]:
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["note", "decision", "reasons", "invention", "sound"],
        "properties": {
            "note": {"type": "string", "minLength": 1, "maxLength": 280},
            "decision": {"type": "string", "enum": ["approve", "downsize", "revise", "reject"]},
            "reasons": {
                "type": "array",
                "maxItems": 6,
                "items": _strings(reasons),
            },
            "invention": {"anyOf": [invention, {"type": "null"}]},
            "sound": {
                "anyOf": [
                    {
                        "type": "object",
                        "additionalProperties": False,
                        "required": ["needed", "description", "kind", "part"],
                        "properties": {
                            "needed": {"type": "boolean"},
                            "description": {"type": "string", "maxLength": 120},
                            "kind": {"type": "string", "enum": ["one_shot", "loop"]},
                            "part": {
                                "anyOf": [
                                    {
                                        "type": "string",
                                        "pattern": "^[a-z][a-z0-9_.-]*$",
                                        "maxLength": 64,
                                    },
                                    {"type": "null"},
                                ]
                            },
                        },
                    },
                    {"type": "null"},
                ]
            },
        },
    }


def _validated(raw: str, schema: dict[str, Any]) -> dict[str, Any]:
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidOutputError(f"{where}: {first.message}")
    return cast(dict[str, Any], data)


def _dedupe(values: list[str]) -> list[str]:
    return list(dict.fromkeys(values))


def _check_invention(invention: dict[str, Any]) -> None:
    """Rules a schema cannot say: step materials must be among the invention's materials.

    Repeated entries are removed first: Ollama's structured output does not enforce uniqueItems
    (live run L701), and a repeated id adds nothing (normalisation, nothing invented).
    """
    invention["materials"] = _dedupe(invention["materials"])
    invention["introduces"] = _dedupe(invention["introduces"])
    for step in invention["steps"]:
        step["materials"] = _dedupe(step["materials"])
    used = set(invention["materials"])
    for step in invention["steps"]:
        extra = sorted(set(step["materials"]) - used)
        if extra:
            raise InvalidOutputError(f"step uses materials not in the invention: {extra}")


def parse_inventor(raw: str, schema: dict[str, Any]) -> dict[str, Any]:
    data = _validated(raw, schema)
    _check_invention(data["invention"])
    # The prompt shows materials by id (the answer names them so). The id must not come back in
    # what the player will read: the thought and the new thing's names (review GW1 #256).
    shown = [data.get("thought"), *dict(data["invention"].get("name") or {}).values()]
    if any(isinstance(text, str) and RUNTIME_TYPE.search(text) for text in shown):
        raise InvalidOutputError("a runtime type id in the thought or the name")
    return data


def parse_guard(raw: str, schema: dict[str, Any]) -> dict[str, Any]:
    data = _validated(raw, schema)
    data["reasons"] = _dedupe(data["reasons"])  # the contract wants unique codes
    if data["decision"] == "downsize":
        if data["invention"] is None:
            raise InvalidOutputError("downsize needs the changed invention")
        _check_invention(data["invention"])
    if data["decision"] in ("revise", "reject"):
        data["sound"] = None  # sound only for things that are made
    return data


# --- prompt values ---------------------------------------------------------------------------


def _materials_text(payload: Any, names: dict[str, str] | None = None) -> str:
    """The materials by their ids (the answer must name them so); from v3 with the name the
    inhabitant has for a material that is itself an invention."""
    names = names or {}
    return "\n".join(
        f"- {plain_id(m.type)}"
        + (f' (you call it "{names[plain_id(m.type)]}")' if plain_id(m.type) in names else "")
        + (
            f" (you notice: {', '.join(humanize(plain_id(p)) for p in m.observed)})"
            if m.observed
            else ""
        )
        for m in payload.materials
    )


def _material_names(job: GenerationJob, template: PromptTemplate) -> dict[str, str]:
    """v3 (#251): the inhabitant's own name of a material that is itself an invention."""
    if template.settings.get("material_names") is not True:
        return {}
    materials: list[Any] = list(_payload(job).materials)
    return own_names(
        ((m.type, getattr(m, "label", None)) for m in materials), plain_id(job.display_locale)
    )


def inventor_values(
    job: GenerationJob, template: PromptTemplate, vocab: Vocabulary, feedback: str
) -> dict[str, str]:
    payload = _payload(job)
    settings = template.settings
    trigger = plain_id(payload.trigger)
    want = plain_id(payload.want)
    # v4 (B2668): a need is said with what it is about, so the idea can answer it.
    about = vocab.describe("need", want) if settings.get("want_about") is True else None
    why = (
        table(settings, "trigger_words")
        .get(trigger, "$want")
        .replace("$want", humanize(want) + (f" ({about.rstrip('.')})" if about else ""))
    )
    language = table(settings, "language_names").get(plain_id(job.display_locale))
    if language is None:
        raise ContextError(f"no language name for {plain_id(job.display_locale)}")
    inhabitant = payload.inhabitant
    traits = sorted(inhabitant.traits, key=lambda t: plain_id(t.id))
    trait_words = bands(settings, "trait_words") if "trait_words" in settings else [(1.01, "")]
    character = (
        "\n".join(
            f"- {humanize(plain_id(t.id))}: {word(trait_words, t.value)} ({t.value:.2f})"
            for t in traits
        )
        or "- (nothing special)"
    )
    return {
        "why": why,
        "character": character,
        "materials": _materials_text(payload, _material_names(job, template)),
        "tech_level": humanize(plain_id(payload.tech_level)),
        "processes": "\n".join(
            f"- {vocab_line(vocab, 'process', p)}" for p in vocab.ids("process")
        ),
        "properties": ", ".join(vocab.ids("property")),
        "actions": ", ".join(vocab.general_ids("action")),
        "max_size": f"{payload.limits.max_size_m:g}",
        "thought_language": language,
        "feedback": feedback,
    }


def safety_categories(content_dir: Path, examples: int = 6) -> str:
    """Youth-protection categories of content/safety/rules.json (same file as the core, B4b R9)."""
    data = json.loads((content_dir / "safety" / "rules.json").read_text("utf-8"))
    lines: list[str] = []
    for category in data["categories"]:
        terms = ", ".join(category.get("terms_en", [])[:examples])
        lines.append(f"- {category['id']}" + (f" (e.g. {terms})" if terms else ""))
    return "\n".join(lines)


def guard_values(
    job: GenerationJob,
    invention: dict[str, Any],
    round_no: int,
    vocab: Vocabulary,
    safety: str,
    settings: dict[str, Any] | None = None,
) -> dict[str, str]:
    payload = _payload(job)
    data = json.dumps(invention, ensure_ascii=False, indent=1)
    # v3 (B2668): reasons bound to an occasion are listed only by a prompt that names it.
    occasions = set((settings or {}).get("reason_occasions", []))
    bound = vocab.occasions.get("guard_reason", {})
    reasons = [r for r in vocab.ids("guard_reason") if r not in bound or bound[r] in occasions]
    want = plain_id(payload.want)
    about = vocab.describe("need", want)
    return {
        "want": humanize(want) + (f" ({about.rstrip('.')})" if about else ""),
        "reasons": "\n".join(f"- {r}: {vocab.describe('guard_reason', r)}" for r in reasons),
        "safety": safety,
        "invention": "\n".join("> " + line for line in data.splitlines()),
        "materials": _materials_text(payload),
        "tech_level": humanize(plain_id(payload.tech_level)),
        "max_size": f"{payload.limits.max_size_m:g}",
        "round": str(round_no),
        "max_rounds": str(payload.limits.max_rounds),
    }


# --- the flow --------------------------------------------------------------------------------


@dataclass(frozen=True)
class FlowResult:
    """attempts = number of guard rounds (1..max_rounds); every single model call with its retries
    is in the call log (review GW1 R4)."""

    result: dict[str, object]
    attempts: int
    prompt_version: str


async def run_flow(
    job: GenerationJob,
    inventor: PromptTemplate,
    guard: PromptTemplate,
    vocab: Vocabulary,
    levels: list[str],
    ask: Asker,
    safety: str,
) -> FlowResult:
    payload = _payload(job)
    allowed = (
        levels[: levels.index(plain_id(payload.tech_level)) + 1]
        if plain_id(payload.tech_level) in levels
        else levels
    )
    materials = [plain_id(m.type) for m in payload.materials]
    shape = invention_schema(vocab, materials, allowed, float(payload.limits.max_size_m))
    i_schema, g_schema = inventor_schema(shape), guard_schema(shape, vocab.ids("guard_reason"))
    max_rounds = int(payload.limits.max_rounds)
    locale = plain_id(job.display_locale)
    rounds: list[dict[str, object]] = []
    feedback = ""
    thought: str | None = None
    for round_no in range(1, max_rounds + 1):
        rendered = inventor.render(inventor_values(job, inventor, vocab, feedback))
        proposal, _ = await ask(
            INVENT_ROLE, rendered, i_schema, lambda raw: parse_inventor(raw, i_schema)
        )
        invention = proposal["invention"]
        thought = proposal["thought"] or thought
        g_rendered = guard.render(
            guard_values(job, invention, round_no, vocab, safety, guard.settings)
        )
        verdict, _ = await ask(
            GUARD_ROLE, g_rendered, g_schema, lambda raw: parse_guard(raw, g_schema)
        )
        decision = verdict["decision"]
        if decision == "revise" and round_no == max_rounds:
            decision = "reject"  # no round left (B4b: at most max_rounds)
        rounds.append(
            {
                "round": round_no,
                "decision": decision,
                "reasons": verdict["reasons"],
                "note": verdict["note"],
            }
        )
        if decision in ("approve", "downsize"):
            final = verdict["invention"] if decision == "downsize" else invention
            return FlowResult(
                {
                    "invention": {
                        "decision": decision,
                        "invention": final,
                        "rounds": rounds,
                        "sound": verdict["sound"],
                        "thought": {"locale": locale, "text": thought} if thought else None,
                    }
                },
                len(rounds),
                f"{inventor.version_id}+{guard.version_id}",
            )
        if decision == "reject":
            break
        reasons = ", ".join(verdict["reasons"]) or "none"
        feedback = (
            f"## The rule guard says\n> {verdict['note']}\n(reasons: {reasons}). "
            "Change the idea accordingly.\n"
        )
    return FlowResult(
        {
            "invention": {
                "decision": "reject",
                "invention": None,
                "rounds": rounds,
                "sound": None,
                "thought": None,
            }
        },
        max(1, len(rounds)),
        f"{inventor.version_id}+{guard.version_id}",
    )


# --- handlers --------------------------------------------------------------------------------


class OllamaInventions:
    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        inventor: PromptTemplate,
        guard: PromptTemplate,
        vocab: Vocabulary,
        levels: list[str],
        settings: ModelCallSettings,
        log: CallLog,
        safety: str,
    ) -> None:
        self._client, self._queue, self._log = client, queue, log
        self._inventor, self._guard, self._vocab, self._levels = inventor, guard, vocab, levels
        self._settings = settings
        self._safety = safety

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        async def ask(
            role: str, rendered: RenderedPrompt, schema: dict[str, Any], parse: Parse
        ) -> tuple[Any, int]:
            answer = await ask_model(
                role=role,
                job=job,
                rendered=rendered,
                schema=schema,
                parse=parse,
                client=self._client,
                queue=self._queue,
                settings=self._settings,
                log=self._log,
            )
            return answer.value, answer.attempts

        import time

        started = time.monotonic()
        flow = await run_flow(
            job, self._inventor, self._guard, self._vocab, self._levels, ask, self._safety
        )
        return JobOutcome(
            flow.result,
            "ollama",
            self._client.model[:128],
            flow.prompt_version,
            int((time.monotonic() - started) * 1000),
            flow.attempts,
        )


class ReplayInventions:
    """Replays the recorded inventor/guard answers of a job in their order (from the call log)."""

    def __init__(
        self,
        log: CallLog,
        inventor: PromptTemplate,
        guard: PromptTemplate,
        vocab: Vocabulary,
        levels: list[str],
        safety: str,
    ) -> None:
        self._safety = safety
        self._log, self._inventor, self._guard, self._vocab, self._levels = (
            log,
            inventor,
            guard,
            vocab,
            levels,
        )

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        queues: dict[str, list[str]] = {INVENT_ROLE: [], GUARD_ROLE: []}
        model = "replay"
        for row in self._log.read():
            role = row.get("role")
            if row.get("request_id") == job_id and row.get("outcome") == "ok" and role in queues:
                queues[str(role)].append(str(row["raw_output"]))
                model = str(row.get("model", model))
        if not queues[INVENT_ROLE]:
            raise fail("model_unavailable", f"no recorded answer for job {job_id}")

        async def ask(
            role: str, rendered: RenderedPrompt, schema: dict[str, Any], parse: Parse
        ) -> tuple[Any, int]:
            if not queues[role]:
                raise fail("model_unavailable", f"recording of job {job_id} ends early ({role})")
            try:
                return parse(queues[role].pop(0)), 1
            except InvalidOutputError as error:
                raise fail(
                    "model_unavailable", f"recorded answer does not fit job {job_id}"
                ) from error

        flow = await run_flow(
            job, self._inventor, self._guard, self._vocab, self._levels, ask, self._safety
        )
        return JobOutcome(
            flow.result,
            "replay",
            model[:128],
            flow.prompt_version,
            0,
            flow.attempts,
        )


class MockInventions:
    """Deterministic stand-in for CI and core tests: a small object from the first material,
    approved in round 1, no sound. Not a real idea; only for wiring."""

    STEPS: ClassVar[list[str]] = ["shape", "dry"]

    def __init__(self, vocab: Vocabulary) -> None:
        self._vocab = vocab

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = _payload(job)
        first = plain_id(payload.materials[0].type)
        processes = [p for p in self.STEPS if p in self._vocab.ids("process")] or self._vocab.ids(
            "process"
        )[:2]
        size = min(0.3, float(payload.limits.max_size_m))
        invention = {
            "name": {"de": "Ding aus " + humanize(first), "en": "Thing of " + humanize(first)},
            "purpose": plain_id(payload.want)
            if plain_id(payload.want) in self._vocab.ids("action")
            else self._vocab.ids("action")[0],
            "category": "object",
            "materials": [first],
            "size_m": {"x": size, "y": size, "z": size},
            "steps": [{"process": p, "materials": [first]} for p in (processes * 2)[:2]],
            "introduces": [],
            "tech_level": plain_id(payload.tech_level),
        }
        result: dict[str, object] = {
            "invention": {
                "decision": "approve",
                "invention": invention,
                "rounds": [
                    {"round": 1, "decision": "approve", "reasons": [], "note": "Mock: approved."}
                ],
                "sound": None,
                "thought": None,
            }
        }
        return JobOutcome(result, "mock", "mock", None, 0, 1)
