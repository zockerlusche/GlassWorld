# jsonschema/referencing ship incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Validates incoming JSON against the contract schemas before pydantic sees it.

JSON Schema is the source of truth; the generated pydantic models miss some rules (e.g.
uniqueItems), so every request passes here first (see contracts/README).
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator
from referencing import Registry, Resource


class ContractValidator:
    def __init__(self, schemas_dir: Path) -> None:
        resources: list[tuple[str, Resource[Any]]] = []
        schemas: dict[str, dict[str, Any]] = {}
        for path in sorted(schemas_dir.glob("*.schema.json")):
            schema: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
            resources.append((str(schema["$id"]), Resource.from_contents(schema)))
            schemas[path.name.removesuffix(".schema.json")] = schema
        if not schemas:
            raise FileNotFoundError(f"no contract schemas in {schemas_dir}")
        registry: Registry[Any] = Registry().with_resources(resources)
        self._validators = {
            name: Draft202012Validator(schema, registry=registry)
            for name, schema in schemas.items()
        }

    def errors(self, schema_name: str, data: Any) -> list[str]:
        """Human-readable errors (empty = valid), most specific path first."""
        validator = self._validators[schema_name]
        found = sorted(validator.iter_errors(data), key=lambda e: list(e.path))
        return [f"{'/'.join(str(p) for p in e.path) or '(root)'}: {e.message}" for e in found]
