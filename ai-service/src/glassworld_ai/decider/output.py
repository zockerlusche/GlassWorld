# jsonschema/referencing ship incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Schema for the model output of one decision, built per request.

`option_id` is an enum of exactly the offered ids and `expression` an enum of the vocabulary, so
Ollama's structured output can only produce content-valid choices. The answer is still checked
again afterwards. `rationale` comes first so the model reasons before it chooses.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Any

from jsonschema import Draft202012Validator

THOUGHT_MAX = 140
RATIONALE_MAX = 280


@dataclass(frozen=True)
class ModelChoice:
    rationale: str
    option_id: str
    expression: str | None
    thought: str | None


class InvalidModelOutputError(Exception):
    """The model answer does not satisfy the output schema."""


def output_schema(option_ids: list[str], expression_ids: list[str]) -> dict[str, Any]:
    expression: dict[str, Any] = (
        {"anyOf": [{"type": "string", "enum": expression_ids}, {"type": "null"}]}
        if expression_ids
        else {"type": "null"}
    )
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["rationale", "option_id", "expression", "thought"],
        "properties": {
            "rationale": {"type": "string", "minLength": 1, "maxLength": RATIONALE_MAX},
            "option_id": {"type": "string", "enum": option_ids},
            "expression": expression,
            "thought": {
                "anyOf": [
                    {"type": "string", "minLength": 1, "maxLength": THOUGHT_MAX},
                    {"type": "null"},
                ]
            },
        },
    }


def parse_choice(raw: str, schema: dict[str, Any]) -> ModelChoice:
    """Parse and validate the raw model text against `schema`."""
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidModelOutputError(f"not JSON: {error.msg}") from error
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidModelOutputError(f"{where}: {first.message}")
    assert isinstance(data, dict)
    values: dict[str, Any] = dict(data)
    thought = values["thought"]
    return ModelChoice(
        rationale=str(values["rationale"]),
        option_id=str(values["option_id"]),
        expression=values["expression"],
        thought=thought.strip() if isinstance(thought, str) and thought.strip() else None,
    )
