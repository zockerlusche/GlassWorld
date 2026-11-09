"""The generic decision mock (Bogen 34 R26): shared vectors with the core, data only (A3)."""

from __future__ import annotations

import asyncio
import json
import time
from pathlib import Path
from typing import Any

import pytest

from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.decider.mock import MockWeights, pick
from glassworld_ai.words import plain_id
from glassworld_contracts import DecisionRequest

REPO = Path(__file__).resolve().parents[2]
VECTORS: list[dict[str, Any]] = json.loads(
    (REPO / "tests/mock/cases.json").read_text(encoding="utf-8")
)["cases"]


def _weights(data: dict[str, Any]) -> MockWeights:
    return MockWeights(
        default_permille=int(data["default_permille"]),
        permille={w["action"]: int(w["permille"]) for w in data["weights"]},
    )


@pytest.mark.parametrize("case", VECTORS, ids=[c["id"] for c in VECTORS])
def test_shared_vectors(case: dict[str, Any]) -> None:
    options = [(o["option_id"], o["action"]) for o in case["options"]]
    assert pick(options, case["seed"], _weights(case["weights"])) == case["expected"]


def test_the_data_file_loads_and_changing_only_data_changes_the_result() -> None:
    weights = MockWeights.load(REPO / "content/ai/mock-weights.json")
    request = DecisionRequest.model_validate(
        json.loads(
            (REPO / "contracts/examples/valid/decision-request.hungry-berries.json").read_text(
                "utf-8"
            )
        )
    )
    options = [(plain_id(o.option_id), plain_id(o.action)) for o in request.options]
    seeds = range(0, 20_000, 7)  # spans several totals of the weights
    before = {pick(options, s, weights) for s in seeds}
    eat = next(oid for oid, action in options if action == "eat")
    without_eat = MockWeights(weights.default_permille, {**weights.permille, "eat": 0})
    after = {pick(options, s, without_eat) for s in seeds}
    assert eat in before and eat not in after  # A3: only the data changed


def test_the_service_mock_follows_the_rule() -> None:
    data = json.loads(
        (REPO / "contracts/examples/valid/decision-request.hungry-berries.json").read_text("utf-8")
    )
    request = DecisionRequest.model_validate(data)
    weights = MockWeights.load(REPO / "content/ai/mock-weights.json")
    answer = asyncio.run(MockDecider(weights).decide(request, time.monotonic() + 5))
    options = [(plain_id(o.option_id), plain_id(o.action)) for o in request.options]
    assert plain_id(answer.option_id) == pick(options, int(request.seed), weights)
