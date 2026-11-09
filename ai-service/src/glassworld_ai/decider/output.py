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

from glassworld_ai.words import leaked_ids

THOUGHT_MAX = 140
RATIONALE_MAX = 280


@dataclass(frozen=True)
class ModelChoice:
    rationale: str
    option_id: str
    expression: str | None
    thought: str | None
    name_text: str | None = None  # B9 R11, prompt v5: only with the option name_person
    gossip: dict[str, Any] | None = None  # B9 R6, prompt v5: only with the option gossip


class InvalidModelOutputError(Exception):
    """The model answer does not satisfy the output schema."""


def output_schema(
    option_ids: list[str], expression_ids: list[str], b9: bool = False
) -> dict[str, Any]:
    expression: dict[str, Any] = (
        {"anyOf": [{"type": "string", "enum": expression_ids}, {"type": "null"}]}
        if expression_ids
        else {"type": "null"}
    )
    schema: dict[str, Any] = {
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
    if b9:
        # Prompt v5 (B9): name for name_person, a belief about a third person for gossip.
        schema["required"] += ["name_text", "gossip"]
        schema["properties"]["name_text"] = {
            "anyOf": [{"type": "string", "minLength": 2, "maxLength": 16}, {"type": "null"}]
        }
        schema["properties"]["gossip"] = {
            "anyOf": [
                {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["about", "property", "value"],
                    "properties": {
                        "about": {"type": "string", "maxLength": 64},
                        "property": {"type": "string", "maxLength": 64},
                        "value": {"type": "boolean"},
                    },
                },
                {"type": "null"},
            ]
        }
    return schema


def parse_choice(
    raw: str, schema: dict[str, Any], hidden_ids: frozenset[str] = frozenset()
) -> ModelChoice:
    """Parse and validate the raw model text against `schema`; the player-visible thought must
    not show internal entity ids (`hidden_ids`)."""
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
    if isinstance(thought, str):
        leaked = leaked_ids(thought, hidden_ids)
        if leaked:
            raise InvalidModelOutputError(f"thought shows internal ids {leaked}")
    return ModelChoice(
        rationale=str(values["rationale"]),
        option_id=str(values["option_id"]),
        expression=values["expression"],
        thought=thought.strip() if isinstance(thought, str) and thought.strip() else None,
        name_text=values.get("name_text") if isinstance(values.get("name_text"), str) else None,
        gossip=values.get("gossip") if isinstance(values.get("gossip"), dict) else None,
    )


def readable_name(text: str | None) -> str | None:
    """B9 R11: one word in Latin letters, first letter upper case ("tavi" -> "Tavi"), or None."""
    if not text:
        return None
    word = text.strip()
    if not word.isascii() or not word.isalpha() or not 2 <= len(word) <= 16:
        return None
    return word[0].upper() + word[1:].lower()
