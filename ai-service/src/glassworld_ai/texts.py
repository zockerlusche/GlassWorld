"""Player-language texts from content/i18n (B2470), for what the AI service itself shows or hands to
the model in the player's language: the names of things (`vocab.object.<type>`) and the stand-in
thoughts (`msg.thought.<action>`, W1687) when the model's sentence does not pass the checks.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class TextCatalog:
    texts: dict[str, dict[str, str]]  # locale -> key -> text

    @classmethod
    def load(cls, i18n_dir: Path) -> TextCatalog:
        texts: dict[str, dict[str, str]] = {}
        for path in sorted(i18n_dir.glob("*.json")):
            data = json.loads(path.read_text(encoding="utf-8"))
            texts[str(data["locale"])] = {str(e["key"]): str(e["text"]) for e in data["entries"]}
        return cls(texts)

    def get(self, locale: str, key: str) -> str | None:
        return self.texts.get(locale, {}).get(key)

    def thing(self, locale: str, type_id: str) -> str | None:
        """The name of a kind of thing in the player's language, None if the data has none."""
        return self.get(locale, f"vocab.object.{type_id}")
