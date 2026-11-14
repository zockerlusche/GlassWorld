"""ProcessSignature: the Python side matches the shared test vectors (C# checks the same file)."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from glassworld_contracts.signature import canonical_signature, world_rule_id

VECTORS: list[dict[str, Any]] = json.loads(
    (Path(__file__).resolve().parents[1] / "testvectors" / "process-signature.json").read_text(
        encoding="utf-8"
    )
)["vectors"]


@pytest.mark.parametrize("vector", VECTORS, ids=[v["rule_id"] for v in VECTORS])
def test_signature_matches_the_test_vector(vector: dict[str, Any]) -> None:
    sig = vector["signature"]
    args = (sig["process"], sig["inputs"], sig["tool"], sig["heat_level"])
    assert canonical_signature(*args) == vector["canonical"]
    assert world_rule_id(*args) == vector["rule_id"]


def test_order_of_inputs_and_properties_does_not_matter() -> None:
    a = world_rule_id("bind", [["sharp", "hard"], ["fibrous"]], None, None)
    b = world_rule_id("bind", [["fibrous"], ["hard", "sharp"]], None, None)
    assert a == b
    assert a != world_rule_id("bind", [["fibrous"], ["hard", "sharp"]], None, "glowing")


def test_consumes_points_to_existing_inputs() -> None:
    """S4 (review GW1): ProcessOutcome.consumes indices must be below the number of inputs."""
    rules = json.loads(
        (Path(__file__).resolve().parents[2] / "content" / "processes" / "rules.json").read_text(
            encoding="utf-8"
        )
    )["rules"]
    for rule in rules:
        bad = [i for i in rule["result"]["consumes"] if i >= len(rule["inputs"])]
        assert bad == [], f"{rule['id']}: consumes {bad} but has {len(rule['inputs'])} inputs"
