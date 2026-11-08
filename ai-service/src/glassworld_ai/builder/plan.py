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
import math
from pathlib import Path
from typing import Any, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
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
from glassworld_ai.words import ContextError, humanize, plain_id
from glassworld_contracts import GenerationJob

ROLE = "builder"
SIZE_SLACK = 1.05  # schema bound for a single part size (rounding of the model)


def bounds_tolerance(content_dir: Path) -> float:
    """The core's tolerance for the plan's overall extent (content/build/settings.json)."""
    data = json.loads((content_dir / "build/settings.json").read_text(encoding="utf-8"))
    return float(data["bounds_tolerance"])


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
                        "size_m": vec(0.005, largest * SIZE_SLACK),
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


def parse_plan(raw: str, schema: dict[str, Any], payload: Any, tolerance: float) -> dict[str, Any]:
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
    _check_extent(plan["parts"], _box(payload), tolerance)
    return plan


def _check_extent(
    parts: list[dict[str, Any]], box: tuple[float, float, float], tolerance: float
) -> None:
    """The same rule as the core's BuildPlanCheck (review GW1 #70): the box around all parts,
    a part turned about y reaching half its horizontal diagonal, within size x tolerance."""
    lo = [float("inf")] * 3
    hi = [float("-inf")] * 3
    for part in parts:
        pos, size = part["position_m"], part["size_m"]
        if float(part["rotation_y_deg"]) % 180 == 0:
            reach_x, reach_z = size["x"] / 2, size["z"] / 2
        else:
            reach_x = reach_z = math.hypot(size["x"], size["z"]) / 2
        for axis, (centre, reach) in enumerate(
            [(pos["x"], reach_x), (pos["y"], size["y"] / 2), (pos["z"], reach_z)]
        ):
            lo[axis] = min(lo[axis], centre - reach)
            hi[axis] = max(hi[axis], centre + reach)
    for axis, name in enumerate("xyz"):
        if hi[axis] - lo[axis] > box[axis] * tolerance:
            raise InvalidOutputError(f"the parts reach {name} beyond the invention's size")


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
        tolerance: float,
    ) -> None:
        self._client, self._queue, self._template, self._settings, self._log = (
            client,
            queue,
            template,
            settings,
            log,
        )
        self._tolerance = tolerance

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = _payload(job)
        if len(payload.invention.steps) > int(payload.max_parts):
            # No plan can satisfy this (every step needs a part): fail at once (review GW1 #70).
            raise JobFailedError(
                "bad_request",
                f"{len(payload.invention.steps)} steps but at most {payload.max_parts} parts",
                retryable=False,
            )
        schema = plan_schema(payload)
        rendered = self._template.render(plan_values(job, self._template))
        answer = await ask_model(
            role=ROLE,
            job=job,
            rendered=rendered,
            schema=schema,
            parse=lambda raw: parse_plan(raw, schema, payload, self._tolerance),
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
    def __init__(self, log: CallLog, tolerance: float) -> None:
        self._log = log
        self._tolerance = tolerance
        self._answers: dict[str, dict[str, object]] | None = None

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        job_id = plain_id(job.request_id)
        if self._answers is None:
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(job_id)
        if row is None:
            # A replay never gets the answer later: not retryable (review GW1 #70).
            raise JobFailedError(
                "model_unavailable", f"no recorded answer for job {job_id}", retryable=False
            )
        payload = _payload(job)
        try:
            plan = parse_plan(
                str(row["raw_output"]), plan_schema(payload), payload, self._tolerance
            )
        except InvalidOutputError as error:
            raise JobFailedError(
                "model_unavailable", f"recorded answer does not fit job {job_id}", retryable=False
            ) from error
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
