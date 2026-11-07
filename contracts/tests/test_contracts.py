# jsonschema/referencing ship incomplete type information; keep strict mode elsewhere.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false
"""Contract tests: schemas are valid, examples match, vocabulary is consistent.

Example files are named <schema-name>.<case>.json, e.g. decision-request.hungry-berries.json.
"""

from __future__ import annotations

import fnmatch
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
    perceived = {o["id"] for o in request["perception"]["objects"]}
    for option in request["options"]:
        partners = option["partners"]
        assert partners == sorted(partners), "core sends partners sorted by id"
        assert set(partners) <= perceived, "partners are perceived objects"
        assert option["target"] not in partners
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


# Schemas for data files are covered by the files in content/, not by message examples.
# Data file schemas: common plus every schema named in contracts/content-map.json (same rule as the
# C# test). They are checked through their content files instead of examples.
DATA_FILE_SCHEMAS = {"common"} | {
    entry["schema"]
    for entry in json.loads((ROOT / "content-map.json").read_text(encoding="utf-8"))["entries"]
}


@pytest.mark.parametrize(
    "schema_name",
    sorted(
        p.name.removesuffix(".schema.json")
        for p in SCHEMA_FILES
        if p.name.removesuffix(".schema.json") not in DATA_FILE_SCHEMAS
    ),
)
def test_every_message_schema_has_valid_and_invalid_examples(schema_name: str) -> None:
    valid = [p for p in VALID_EXAMPLES if _schema_name(p) == schema_name]
    invalid = [p for p in INVALID_EXAMPLES if _schema_name(p) == schema_name]
    assert valid, f"no valid example for {schema_name}"
    assert invalid, f"no invalid example for {schema_name}"


# --- conservative schema subset (C# generation reads the schemas as draft-07) ---------------

REF_SIBLINGS_ALLOWED = {"$ref", "description", "title", "$comment"}
FORBIDDEN_KEYWORDS = {
    "$dynamicRef",
    "$dynamicAnchor",
    "unevaluatedProperties",
    "unevaluatedItems",
    "if",
    "then",
    "else",
    "dependentSchemas",
    "prefixItems",
}


def _walk(node: Any, path: str = "#") -> list[tuple[str, dict[str, Any]]]:
    found: list[tuple[str, dict[str, Any]]] = []
    if isinstance(node, dict):
        found.append((path, node))
        for key, value in node.items():
            found.extend(_walk(value, f"{path}/{key}"))
    elif isinstance(node, list):
        for index, value in enumerate(node):
            found.extend(_walk(value, f"{path}/{index}"))
    return found


@pytest.mark.parametrize("path", SCHEMA_FILES, ids=lambda p: p.name)
def test_schema_stays_in_the_conservative_subset(path: Path) -> None:
    for where, node in _walk(_load(path)):
        if "$ref" in node:
            extra = set(node) - REF_SIBLINGS_ALLOWED
            assert not extra, (
                f"{path.name} {where}: keywords next to $ref are ignored in C#: {extra}"
            )
        # "properties" maps names to schemas, so its keys are not keywords.
        if not where.endswith("/properties"):
            used = FORBIDDEN_KEYWORDS & set(node)
            assert not used, f"{path.name} {where}: outside the conservative subset: {used}"


# --- every data file in content/ is validated (ADR-0007) ------------------------------------
# contracts/content-map.json is shared with the C# tests. Patterns are relative to content/;
# "*" matches within one folder only (no "**").

CONTENT = ROOT.parent / "content"
CONTENT_MAP = ROOT / "content-map.json"
CONTENT_FILES = sorted(CONTENT.rglob("*.json"))


def _content_entries() -> list[tuple[str, str]]:
    data = _load(CONTENT_MAP)
    assert data["contract_version"] == "0.1"
    return [(entry["pattern"], entry["schema"]) for entry in data["entries"]]


def _matches(relative: str, pattern: str) -> bool:
    parts, pattern_parts = relative.split("/"), pattern.split("/")
    return len(parts) == len(pattern_parts) and all(
        fnmatch.fnmatchcase(part, pat) for part, pat in zip(parts, pattern_parts, strict=True)
    )


def _relative(path: Path) -> str:
    return path.relative_to(CONTENT).as_posix()


@pytest.mark.parametrize("path", CONTENT_FILES, ids=_relative)
def test_content_file_matches_exactly_one_schema_and_is_valid(path: Path) -> None:
    schemas = [
        schema for pattern, schema in _content_entries() if _matches(_relative(path), pattern)
    ]
    assert len(schemas) == 1, f"content/{_relative(path)}: matched by {schemas or 'no'} entry"
    _validator(schemas[0]).validate(_load(path))


def test_content_map_entries_are_used_and_known() -> None:
    names = {p.name.removesuffix(".schema.json") for p in SCHEMA_FILES}
    for pattern, schema in _content_entries():
        assert "**" not in pattern
        assert schema in names, f"{pattern}: unknown schema {schema}"
        assert any(_matches(_relative(p), pattern) for p in CONTENT_FILES), f"{pattern}: no file"


def test_matching_stays_within_one_folder() -> None:
    assert _matches("vocabulary/actions.json", "vocabulary/*.json")
    assert not _matches("vocabulary/sub/actions.json", "vocabulary/*.json")
    assert not _matches("hardware/tiers.json", "vocabulary/*.json")
