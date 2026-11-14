"""B9 R26 (GW3 #25889): a reserved word is in the vocabulary, but no prompt lists it."""

from __future__ import annotations

from pathlib import Path

from glassworld_ai.vocabulary import Vocabulary

REPO = Path(__file__).resolve().parents[2]


def test_reserved_family_words_are_never_listed_in_a_prompt() -> None:
    vocab = Vocabulary.load(REPO / "content/vocabulary")
    general = vocab.general_ids("action")
    for word in ("bury", "quarrel", "sail", "wish_child", "court"):
        assert word in vocab.ids("action")
        assert word not in general
    assert "eat" in general
