# jsonschema/referencing ship incomplete type information; keep strict mode elsewhere.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false
"""Contract tests: schemas are valid, examples match, vocabulary is consistent.

Example files are named <schema-name>.<case>.json, e.g. decision-request.hungry-berries.json.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[1]
SCHEMAS = ROOT / "schemas"
EXAMPLES = ROOT / "examples"
VOCABULARY = ROOT.parent / "content" / "vocabulary"


def _load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _registry() -> Registry[Any]:
    resources: list[tuple[str, Resource[Any]]] = []
    for path in SCHEMAS.glob("*.schema.json"):
        schema = _load(path)
        resource: Resource[Any] = Resource.from_contents(schema)
        resources.append((schema["$id"], resource))
    return Registry().with_resources(resources)


REGISTRY = _registry()
SCHEMA_FILES = sorted(SCHEMAS.glob("*.schema.json"))
VALID_EXAMPLES = sorted((EXAMPLES / "valid").glob("*.json"))
INVALID_EXAMPLES = sorted((EXAMPLES / "invalid").glob("*.json"))
VOCAB_FILES = sorted(VOCABULARY.glob("*.json"))


def _validator(schema_name: str) -> Draft202012Validator:
    schema = _load(SCHEMAS / f"{schema_name}.schema.json")
    return Draft202012Validator(schema, registry=REGISTRY)


def _schema_name(example: Path) -> str:
    return example.name.split(".", 1)[0]


def _vocab_ids(kind: str) -> set[str]:
    for path in VOCAB_FILES:
        data = _load(path)
        if data["kind"] == kind:
            return {entry["id"] for entry in data["entries"]}
    raise AssertionError(f"no vocabulary file of kind {kind!r}")


@pytest.mark.parametrize("path", SCHEMA_FILES, ids=lambda p: p.name)
def test_schema_is_valid_draft_2020_12(path: Path) -> None:
    Draft202012Validator.check_schema(_load(path))


@pytest.mark.parametrize("path", VALID_EXAMPLES, ids=lambda p: p.name)
def test_valid_example_matches_schema(path: Path) -> None:
    _validator(_schema_name(path)).validate(_load(path))


@pytest.mark.parametrize("path", INVALID_EXAMPLES, ids=lambda p: p.name)
def test_invalid_example_is_rejected(path: Path) -> None:
    errors = list(_validator(_schema_name(path)).iter_errors(_load(path)))
    assert errors, f"{path.name} should be rejected by its schema"


@pytest.mark.parametrize("path", VOCAB_FILES, ids=lambda p: p.name)
def test_vocabulary_file_is_valid_and_unique(path: Path) -> None:
    data = _load(path)
    _validator("vocabulary").validate(data)
    ids = [entry["id"] for entry in data["entries"]]
    assert len(ids) == len(set(ids)), f"duplicate ids in {path.name}"
    if data["kind"] == "action":
        assert all("target" in entry for entry in data["entries"])
    else:
        assert all("target" not in entry for entry in data["entries"])


@pytest.mark.parametrize(
    "path",
    [p for p in VALID_EXAMPLES if _schema_name(p) == "decision-request"],
    ids=lambda p: p.name,
)
def test_request_example_uses_known_vocabulary(path: Path) -> None:
    request = _load(path)
    inhabitant = request["inhabitant"]
    assert {n["id"] for n in inhabitant["needs"]} <= _vocab_ids("need")
    assert {m["id"] for m in inhabitant["mood"]} <= _vocab_ids("mood")
    assert {t["id"] for t in inhabitant["traits"]} <= _vocab_ids("trait")
    known = {p["property"] for o in request["perception"]["objects"] for p in o["known_properties"]}
    assert known <= _vocab_ids("property")
    assert {o["action"] for o in request["options"]} <= _vocab_ids("action")
    assert request["environment"]["time_of_day"] in _vocab_ids("time_of_day")
    weather = request["environment"]["weather"]
    assert set(weather) <= _vocab_ids("weather")
    assert weather == sorted(weather), "core sends weather sorted by id"
    option_ids = [o["option_id"] for o in request["options"]]
    assert len(option_ids) == len(set(option_ids)), "option_ids must be unique"


@pytest.mark.parametrize(
    "path",
    [p for p in VALID_EXAMPLES if _schema_name(p) == "decision-response"],
    ids=lambda p: p.name,
)
def test_response_example_answers_its_request(path: Path) -> None:
    case = path.name.split(".", 1)[1]
    request = _load(EXAMPLES / "valid" / f"decision-request.{case}")
    response = _load(path)
    assert response["request_id"] == request["request_id"]
    assert response["tick"] == request["tick"]
    assert response["option_id"] in {o["option_id"] for o in request["options"]}
    if response["expression"] is not None:
        assert response["expression"] in _vocab_ids("expression")
    if response["thought"] is not None:
        assert response["thought"]["locale"] == request["display_locale"]
