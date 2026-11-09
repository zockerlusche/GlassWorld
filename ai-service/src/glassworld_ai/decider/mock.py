"""The generic decision mock's rule (Bogen 34 R26), the same in the AI service and in the core.

Weights per action come from content/ai/mock-weights.json (permille, integer). The options are
sorted by option id; with total = sum of their weights, pick = seed mod total, and the first option
whose running sum exceeds pick is chosen. With total 0 the option with the action idle is chosen,
else the first by id. There is no branch for any single option: only the data change the result
(A3). Option ids sort as text (ordinal). Shared test vectors: tests/mock/cases.json (pytest here, a
C# test in the core).
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path


@dataclass(frozen=True)
class MockWeights:
    default_permille: int = 1000
    permille: dict[str, int] = field(default_factory=dict[str, int])

    def of(self, action: str) -> int:
        return self.permille.get(action, self.default_permille)

    @classmethod
    def load(cls, path: Path) -> MockWeights:
        """Reads the data file (validated against mock-weights.schema.json by the content check)."""
        data = json.loads(path.read_text(encoding="utf-8"))
        return cls(
            default_permille=int(data["default_permille"]),
            permille={str(w["action"]): int(w["permille"]) for w in data["weights"]},
        )


def pick(options: list[tuple[str, str]], seed: int, weights: MockWeights) -> str:
    """The chosen option id among (option_id, action) pairs; seed is a non-negative integer."""
    if not options:
        raise ValueError("no options to choose from")
    ordered = sorted(options)
    total = sum(weights.of(action) for _, action in ordered)
    if total == 0:
        return next((oid for oid, action in ordered if action == "idle"), ordered[0][0])
    target = seed % total
    running = 0
    for option_id, action in ordered:
        running += weights.of(action)
        if running > target:
            return option_id
    raise AssertionError("unreachable: the running sum reaches the total")
