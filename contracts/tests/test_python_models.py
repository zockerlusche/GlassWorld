"""Contract tests for the generated pydantic models (Python side of the contracts)."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from typing import Any

import pytest
from pydantic import BaseModel, ValidationError

from glassworld_contracts import (
    CONTRACT_VERSION,
    DecisionRequest,
    DecisionResponse,
    HealthStatus,
    ServiceError,
    Vocabulary,
)

CONTRACTS = Path(__file__).resolve().parents[1]
EXAMPLES = CONTRACTS / "examples"
VOCABULARY = CONTRACTS.parent / "content" / "vocabulary"

MODELS: dict[str, type[BaseModel]] = {
    "decision-request": DecisionRequest,
    "decision-response": DecisionResponse,
    "service-error": ServiceError,
    "health-status": HealthStatus,
}

# Schema rules the generated pydantic models do not enforce. JSON Schema validation
# (jsonschema in the AI service, JsonSchema.Net in C#) is the authority for these.
KNOWN_GENERATOR_GAPS = {
    "decision-request.duplicate-weather.json": "uniqueItems is not enforced by pydantic",
    "decision-request.partners-duplicate.json": "uniqueItems is not enforced by pydantic",
}


def _load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _model(path: Path) -> type[BaseModel]:
    return MODELS[path.name.split(".", 1)[0]]


@pytest.mark.parametrize("path", sorted((EXAMPLES / "valid").glob("*.json")), ids=lambda p: p.name)
def test_valid_example_round_trips(path: Path) -> None:
    raw = _load(path)
    parsed = _model(path).model_validate(raw)
    assert parsed.model_dump(mode="json") == raw


@pytest.mark.parametrize(
    "path", sorted((EXAMPLES / "invalid").glob("*.json")), ids=lambda p: p.name
)
def test_invalid_example_is_rejected_by_pydantic(path: Path) -> None:
    if path.name in KNOWN_GENERATOR_GAPS:
        pytest.xfail(KNOWN_GENERATOR_GAPS[path.name])
    with pytest.raises(ValidationError):
        _model(path).model_validate(_load(path))


def test_known_gaps_refer_to_existing_examples() -> None:
    names = {p.name for p in (EXAMPLES / "invalid").glob("*.json")}
    assert set(KNOWN_GENERATOR_GAPS) <= names


@pytest.mark.parametrize("path", sorted(VOCABULARY.glob("*.json")), ids=lambda p: p.name)
def test_vocabulary_file_parses(path: Path) -> None:
    Vocabulary.model_validate(_load(path))


def test_contract_version_constant_matches_schema() -> None:
    schema = _load(CONTRACTS / "schemas" / "common.schema.json")
    assert schema["$defs"]["ContractVersion"]["const"] == CONTRACT_VERSION


def test_generated_models_are_up_to_date() -> None:
    result = subprocess.run(
        [sys.executable, str(CONTRACTS / "generate_python.py"), "--check"],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stderr
