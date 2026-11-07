# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Building plan (D471 stage 4, B4b R14, ADR-0004): kit parts for an approved invention.

The output schema per request allows only the kit's shapes and colours (data, content/build/kit.json
via the payload), the invention's materials and its step numbers, and sizes within its bounding
box. After the model the service checks what a schema cannot: every step has a part and every part
lies inside the bounding box. The core checks again (B4b R14) and has the last word.
"""

from __future__ import annotations

import json
from typing import Any, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
from glassworld_ai.jobs import JobOutcome
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
from glassworld_ai.words import ContextError, humanize, plain_id
from glassworld_contracts import GenerationJob

ROLE = "builder"
TOLERANCE = 1.05  # parts may stick out 5 % (rounding of the model)


def _payload(job: GenerationJob) -> Any:
    payload = job.payload.build_plan
    if payload is None:
        raise ContextError("job has no build_plan payload")
    return payload


def _box(payload: Any) -> tuple[float, float, float]:
    size = payload.invention.size_m
    return float(size.x), float(size.y), float(size.z)


def plan_schema(payload: Any) -> dict[str, Any]:
    sx, sy, sz = _box(payload)
    largest = max(sx, sy, sz)
    invention = payload.invention
    steps = len(invention.steps)

    def vec(minimum: float | None, maximum: float) -> dict[str, Any]:
        axis: dict[str, Any] = {"type": "number", "maximum": maximum}
        if minimum is not None:
            axis["minimum"] = minimum
        return {
            "type": "object",
            "additionalProperties": False,
            "required": ["x", "y", "z"],
            "properties": {"x": axis, "y": axis, "z": axis},
        }

    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["parts"],
        "properties": {
            "parts": {
                "type": "array",
                "minItems": max(2, steps),
                "maxItems": int(payload.max_parts),
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": [
                        "shape",
                        "size_m",
                        "position_m",
                        "rotation_y_deg",
                        "material",
                        "color",
                        "step",
                    ],
                    "properties": {
                        "shape": {"type": "string", "enum": [plain_id(s) for s in payload.shapes]},
                        "size_m": vec(0.005, largest * TOLERANCE),
                        "position_m": vec(-largest, largest),
                        "rotation_y_deg": {"type": "number", "minimum": -180, "maximum": 180},
                        "material": {
                            "type": "string",
                            "enum": [plain_id(m) for m in invention.materials],
                        },
                        "color": {"type": "string", "enum": [plain_id(c) for c in payload.colors]},
                        "step": {"type": "integer", "minimum": 1, "maximum": steps},
                    },
                },
            }
        },
    }


def parse_plan(raw: str, schema: dict[str, Any], payload: Any) -> dict[str, Any]:
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidOutputError(f"{where}: {first.message}")
    plan = cast(dict[str, Any], data)
    steps = len(payload.invention.steps)
    used = {int(p["step"]) for p in plan["parts"]}
    missing = sorted(set(range(1, steps + 1)) - used)
    if missing:
        raise InvalidOutputError(f"steps without a part: {missing}")
    sx, sy, sz = _box(payload)
    for i, part in enumerate(plan["parts"]):
        pos, size = part["position_m"], part["size_m"]
        inside = (
            abs(pos["x"]) + size["x"] / 2 <= sx / 2 * TOLERANCE
            and abs(pos["z"]) + size["z"] / 2 <= sz / 2 * TOLERANCE
            and pos["y"] - size["y"] / 2 >= -0.01 * sy
            and pos["y"] + size["y"] / 2 <= sy * TOLERANCE
        )
        if not inside:
            raise InvalidOutputError(f"part {i} lies outside the bounding box")
    return plan


def plan_values(job: GenerationJob, template: PromptTemplate) -> dict[str, str]:
    payload = _payload(job)
    invention = payload.invention
    sx, sy, sz = _box(payload)
    return {
        "invention": (
            f"{invention.name.en} - for {humanize(plain_id(invention.purpose))}; "
            f"materials: {', '.join(plain_id(m) for m in invention.materials)}"
        ),
        "size": f"width x {sx:g}, height y {sy:g}, depth z {sz:g}",
        "steps": "\n".join(
            f"{n}. {humanize(plain_id(s.process))} ({', '.join(plain_id(m) for m in s.materials)})"
            for n, s in enumerate(invention.steps, start=1)
        ),
        "shapes": ", ".join(plain_id(s) for s in payload.shapes),
        "colors": ", ".join(plain_id(c) for c in payload.colors),
        "max_parts": str(int(payload.max_parts)),
    }


class OllamaBuilder:
    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        settings: ModelCallSettings,
        log: CallLog,
    ) -> None:
        self._client, self._queue, self._template, self._settings, self._log = (
            client,
            queue,
            template,
            settings,
            log,
        )

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = _payload(job)
        schema = plan_schema(payload)
        rendered = self._template.render(plan_values(job, self._template))
        answer = await ask_model(
            role=ROLE,
            job=job,
            rendered=rendered,
            schema=schema,
            parse=lambda raw: parse_plan(raw, schema, payload),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
        )
        return JobOutcome(
            {"build_plan": answer.value},
            "ollama",
            self._client.model[:128],
            rendered.version,
            answer.latency_ms,
            answer.attempts,
        )


class ReplayBuilder:
    def __init__(self, log: CallLog) -> None:
        self._log = log
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for job {job_id}")
        payload = _payload(job)
        try:
            plan = parse_plan(str(row["raw_output"]), plan_schema(payload), payload)
        except InvalidOutputError as error:
            raise fail("model_unavailable", f"recorded answer does not fit job {job_id}") from error
        return JobOutcome(
            {"build_plan": plan},
            "replay",
            str(row.get("model", "replay"))[:128],
            str(row.get("prompt_version", "")) or None,
            0,
            1,
        )


class MockBuilder:
    """Deterministic stand-in: one stacked box per building step inside the bounding box."""

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = _payload(job)
        sx, sy, sz = _box(payload)
        steps = len(payload.invention.steps)
        height = sy / max(2, steps)
        parts = [
            {
                "shape": plain_id(payload.shapes[0]),
                "size_m": {"x": round(sx * 0.9, 4), "y": round(height, 4), "z": round(sz * 0.9, 4)},
                "position_m": {"x": 0, "y": round(height * (n - 0.5), 4), "z": 0},
                "rotation_y_deg": 0,
                "material": plain_id(payload.invention.materials[0]),
                "color": plain_id(payload.colors[0]),
                "step": min(n, steps),
            }
            for n in range(1, max(2, steps) + 1)
        ]
        return JobOutcome({"build_plan": {"parts": parts}}, "mock", "mock", None, 0, 1)
