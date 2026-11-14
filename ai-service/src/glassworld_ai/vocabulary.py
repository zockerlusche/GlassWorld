"""Shared vocabulary from content/vocabulary (language-neutral ids with English descriptions)."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

from glassworld_contracts import Vocabulary as VocabularyFile


@dataclass(frozen=True)
class Vocabulary:
    entries: dict[str, dict[str, str]]  # kind -> id -> description
    # kind -> id -> occasion: an entry offered only on that occasion (only_when, e.g. the
    # actions of a stuck building, A1373 step 2)
    occasions: dict[str, dict[str, str]] = field(default_factory=dict[str, dict[str, str]])
    # kind -> ids kept for a later step (reserved, B9 R26): never listed in a prompt (GW3 #25889).
    reserved: dict[str, set[str]] = field(default_factory=dict[str, set[str]])
    # R26 (B9-1b, GW1 #439), the second guard: the actions the core can carry out (with timing in
    # content/effects); None = not known (a vocabulary loaded without the effects), every action counts
    timed_actions: frozenset[str] | None = None

    def ids(self, kind: str) -> list[str]:
        return sorted(self.entries.get(kind, {}))

    def general_ids(self, kind: str) -> list[str]:
        """The ids without an occasion: what a prompt may offer in general (review GW2 #295 -
        'give up' is no idea for an unknown berry). Never a reserved word, and for actions only
        those the core can carry out (R26): a word without timing is not offered yet."""
        bound = self.occasions.get(kind, {})
        kept = self.reserved.get(kind, set())
        timed = self.timed_actions if kind == "action" else None
        return [
            i
            for i in self.ids(kind)
            if i not in bound and i not in kept and (timed is None or i in timed)
        ]

    def describe(self, kind: str, entry_id: str) -> str | None:
        return self.entries.get(kind, {}).get(entry_id)

    @classmethod
    def load(cls, directory: Path) -> Vocabulary:
        entries: dict[str, dict[str, str]] = {}
        occasions: dict[str, dict[str, str]] = {}
        reserved: dict[str, set[str]] = {}
        for path in sorted(directory.glob("*.json")):
            data = VocabularyFile.model_validate(json.loads(path.read_text(encoding="utf-8")))
            kind = str(data.kind.value if hasattr(data.kind, "value") else data.kind)
            target = entries.setdefault(kind, {})
            for entry in data.entries:
                if entry.id in target:
                    raise ValueError(f"{path.name}: duplicate vocabulary id {kind}.{entry.id}")
                target[entry.id] = entry.description
                occasion: object = getattr(entry, "only_when", None)
                if occasion is not None:
                    occasions.setdefault(kind, {})[entry.id] = str(occasion)
                if getattr(entry, "reserved", None):
                    reserved.setdefault(kind, set()).add(entry.id)
        return cls(
            entries=entries,
            occasions=occasions,
            reserved=reserved,
            timed_actions=_timed_actions(directory.parent / "effects"),
        )


def _timed_actions(directory: Path) -> frozenset[str] | None:
    """The action ids with timing in content/effects/*.json, or None without that folder."""
    if not directory.is_dir():
        return None
    timed: set[str] = set()
    for path in sorted(directory.glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        timed.update(str(a["action"]) for a in data.get("actions", []))
    return frozenset(timed)
