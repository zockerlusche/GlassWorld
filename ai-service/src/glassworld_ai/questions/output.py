# jsonschema/referencing ship incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Schema for the model output of one question, built per request.

`property` is an enum of the property vocabulary and `idea` an enum of the action vocabulary, so
Ollama's structured output can only name known things (B4a S2). Checked again afterwards:
duplicate properties and guesses with understood=false are rejected.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from typing import Any

from jsonschema import Draft202012Validator

THOUGHT_MAX = 140
RATIONALE_MAX = 280
GUESSES_MAX = 8  # contract maximum (QuestionResult.guesses)


class InvalidQuestionOutputError(Exception):
    """The model answer does not satisfy the output schema."""


@dataclass(frozen=True)
class ModelGuess:
    property: str
    value: bool
    confidence: float


@dataclass(frozen=True)
class ModelAnswer:
    rationale: str
    understood: bool
    guesses: tuple[ModelGuess, ...]
    idea: str | None
    thought: str | None


def output_schema(
    property_ids: list[str], action_ids: list[str], max_guesses: int
) -> dict[str, Any]:
    idea: dict[str, Any] = (
        {"anyOf": [{"type": "string", "enum": action_ids}, {"type": "null"}]}
        if action_ids
        else {"type": "null"}
    )
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["rationale", "understood", "guesses", "idea", "thought"],
        "properties": {
            "rationale": {"type": "string", "minLength": 1, "maxLength": RATIONALE_MAX},
            "understood": {"type": "boolean"},
            "guesses": {
                "type": "array",
                "maxItems": min(max_guesses, GUESSES_MAX),
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["property", "value", "confidence"],
                    "properties": {
                        "property": {"type": "string", "enum": property_ids},
                        "value": {"type": "boolean"},
                        "confidence": {"type": "number", "minimum": 0, "maximum": 1},
                    },
                },
            },
            "idea": idea,
            "thought": {
                "anyOf": [
                    {"type": "string", "minLength": 1, "maxLength": THOUGHT_MAX},
                    {"type": "null"},
                ]
            },
        },
    }


def parse_answer(raw: str, schema: dict[str, Any]) -> ModelAnswer:
    """Parse and validate the raw model text against `schema` plus the rules a schema can't say."""
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidQuestionOutputError(f"not JSON: {error.msg}") from error
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        where = "/".join(str(p) for p in first.path) or "(root)"
        raise InvalidQuestionOutputError(f"{where}: {first.message}")
    assert isinstance(data, dict)
    values: dict[str, Any] = dict(data)
    guesses = tuple(
        ModelGuess(str(g["property"]), bool(g["value"]), float(g["confidence"]))
        for g in values["guesses"]
    )
    names = [g.property for g in guesses]
    if len(set(names)) != len(names):
        raise InvalidQuestionOutputError("guesses: a property is guessed twice")
    if not values["understood"] and guesses:
        raise InvalidQuestionOutputError("guesses must be empty when understood is false")
    thought = values["thought"]
    return ModelAnswer(
        rationale=str(values["rationale"]),
        understood=bool(values["understood"]),
        guesses=guesses,
        idea=values["idea"],
        thought=thought.strip() if isinstance(thought, str) and thought.strip() else None,
    )
