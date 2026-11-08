"""Day/night planner (B7 R3/R4, D471 D4): which job kinds may run now, and which model they need.

The core reports the world's phase (POST /phase). By day only kinds marked "any" run (questions,
inventions, build plans, processes) as soon as the language model is free. In the computing night
also the "night" kinds run (memory, dreams, chronicle, image, sound) within the night budget; what
does not finish becomes "deferred" and runs in the next night. On the comfort tier "night" kinds
may run any time (B7 R5). Before a job starts, the residency makes its model resident (it unloads
others first, never two big models above the budget). All values are data
(content/hardware/night.json, placeholders until Marcel decides).
"""

from __future__ import annotations

import json
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from glassworld_ai.residency import Residency, ResidencyError
from glassworld_contracts.models.night_plan_schema import NightPlan


@dataclass(frozen=True)
class KindPlan:
    when: str  # "any" | "night"
    model: str | None


def load_night_plan(path: Path) -> tuple[int, dict[str, KindPlan]]:
    data = NightPlan.model_validate(json.loads(path.read_text(encoding="utf-8")))
    kinds: dict[str, KindPlan] = {}
    for entry in data.kinds:
        kind = str(getattr(entry.kind, "value", entry.kind))
        when = str(getattr(entry.when, "value", entry.when))
        if kind in kinds:
            raise ValueError(f"{path.name}: kind {kind} listed twice")
        kinds[kind] = KindPlan(when, entry.model)
    return data.night_budget_s, kinds


class Planner:
    """Gate for the job runner: allowed(kind) and prepare(kind) (makes the model resident)."""

    def __init__(
        self,
        budget_s: int,
        kinds: dict[str, KindPlan],
        residency: Callable[[], Residency | None],
        tier: Callable[[], str | None],
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._budget_s = budget_s
        self._kinds = kinds
        self._residency = residency
        self._tier = tier
        self._clock = clock
        self.phase = "day"
        self._night_started: float | None = None
        self.done_tonight = 0

    def start_night(self) -> None:
        self.phase = "night"
        self._night_started = self._clock()
        self.done_tonight = 0

    def start_day(self) -> None:
        self.phase = "day"
        self._night_started = None

    def budget_left_s(self) -> int:
        if self.phase != "night" or self._night_started is None:
            return self._budget_s
        return max(0, int(self._budget_s - (self._clock() - self._night_started)))

    def night_only(self, kind: str) -> bool:
        plan = self._kinds.get(kind)
        return plan is not None and plan.when == "night" and self._tier() != "comfort"

    def allowed(self, kind: str) -> bool:
        """May a job of this kind start now?"""
        if not self.night_only(kind):
            return True
        return self.phase == "night" and self.budget_left_s() > 0

    async def prepare(self, kind: str) -> None:
        """Make the kind's model resident before the job runs (no-op without model or residency)."""
        plan = self._kinds.get(kind)
        residency = self._residency()
        if (
            plan is None
            or plan.model is None
            or residency is None
            or not residency.knows(plan.model)
        ):
            return
        await residency.ensure(plan.model)

    def finished(self) -> None:
        if self.phase == "night":
            self.done_tonight += 1

    def model_of(self, kind: str) -> str | None:
        plan = self._kinds.get(kind)
        return plan.model if plan else None


__all__ = ["KindPlan", "Planner", "ResidencyError", "load_night_plan"]
