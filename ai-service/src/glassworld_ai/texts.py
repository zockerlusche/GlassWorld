"""Player-language texts from content/i18n (B2470), for what the AI service itself shows or hands to
the model in the player's language, e.g. the names of things (`vocab.object.<type>`). The former
stand-in thoughts (`msg.thought.<action>`) are archived in docs/archive (B2644).
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path


@dataclass(frozen=True)
class NounList:
    """A1818 (TL F5347): the nouns of a language whose nouns are not capitalised (English), from
    an open word list (content/language/nouns.<locale>.json): base forms, and irregular plurals
    mapped to their base form. Regular plurals (-s, -es, -ies) are folded by rule."""

    nouns: frozenset[str]
    irregular: dict[str, str]

    def base(self, word: str) -> str:
        """The base form of a word: its irregular singular, else the regular plural taken off."""
        low = word.lower()
        if low in self.irregular:
            return self.irregular[low]
        for plural, singular in (("ies", "y"), ("ches", "ch"), ("shes", "sh"), ("xes", "x")):
            if low.endswith(plural) and len(low) > len(plural) + 1:
                candidate = low[: -len(plural)] + singular
                if candidate in self.nouns:
                    return candidate
        if low.endswith("es") and low[:-2] in self.nouns:
            return low[:-2]
        if low.endswith("s") and not low.endswith("ss") and low[:-1] in self.nouns:
            return low[:-1]
        return low

    def is_noun(self, word: str) -> bool:
        return self.base(word) in self.nouns


@dataclass(frozen=True)
class TextCatalog:
    texts: dict[str, dict[str, str]]  # locale -> key -> text
    noun_lists: dict[str, NounList] = field(default_factory=lambda: {})

    @classmethod
    def load(cls, i18n_dir: Path) -> TextCatalog:
        texts: dict[str, dict[str, str]] = {}
        for path in sorted(i18n_dir.glob("*.json")):
            data = json.loads(path.read_text(encoding="utf-8"))
            texts[str(data["locale"])] = {str(e["key"]): str(e["text"]) for e in data["entries"]}
        lists: dict[str, NounList] = {}
        for path in sorted((i18n_dir.parent / "language").glob("nouns.*.json")):
            data = json.loads(path.read_text(encoding="utf-8"))
            lists[str(data["locale"])] = NounList(
                frozenset(str(n) for n in data["nouns"]),
                {str(k): str(v) for k, v in dict(data.get("irregular_plurals", {})).items()},
            )
        return cls(texts, lists)

    def nouns(self, locale: str) -> NounList | None:
        """The noun list of a language without capitalised nouns, None for German (capitals)."""
        return self.noun_lists.get(locale)

    def get(self, locale: str, key: str) -> str | None:
        return self.texts.get(locale, {}).get(key)

    def thing(self, locale: str, type_id: str) -> str | None:
        """The name of a kind of thing in the player's language, None if the data has none."""
        return self.get(locale, f"vocab.object.{type_id}")
