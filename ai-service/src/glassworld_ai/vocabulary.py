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

    def ids(self, kind: str) -> list[str]:
        return sorted(self.entries.get(kind, {}))

    def general_ids(self, kind: str) -> list[str]:
        """The ids without an occasion: what a prompt may offer in general (review GW2 #295 -
        'give up' is no idea for an unknown berry)."""
        bound = self.occasions.get(kind, {})
        return [i for i in self.ids(kind) if i not in bound]

    def describe(self, kind: str, entry_id: str) -> str | None:
        return self.entries.get(kind, {}).get(entry_id)

    @classmethod
    def load(cls, directory: Path) -> Vocabulary:
        entries: dict[str, dict[str, str]] = {}
        occasions: dict[str, dict[str, str]] = {}
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
        return cls(entries=entries, occasions=occasions)
