"""Shared vocabulary from content/vocabulary (language-neutral ids with English descriptions)."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from glassworld_contracts import Vocabulary as VocabularyFile


@dataclass(frozen=True)
class Vocabulary:
    entries: dict[str, dict[str, str]]  # kind -> id -> description

    def ids(self, kind: str) -> list[str]:
        return sorted(self.entries.get(kind, {}))

    def describe(self, kind: str, entry_id: str) -> str | None:
        return self.entries.get(kind, {}).get(entry_id)

    @classmethod
    def load(cls, directory: Path) -> Vocabulary:
        entries: dict[str, dict[str, str]] = {}
        for path in sorted(directory.glob("*.json")):
            data = VocabularyFile.model_validate(json.loads(path.read_text(encoding="utf-8")))
            kind = str(data.kind.value if hasattr(data.kind, "value") else data.kind)
            target = entries.setdefault(kind, {})
            for entry in data.entries:
                if entry.id in target:
                    raise ValueError(f"{path.name}: duplicate vocabulary id {kind}.{entry.id}")
                target[entry.id] = entry.description
        return cls(entries=entries)
