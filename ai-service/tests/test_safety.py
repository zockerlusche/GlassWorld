"""Content guard: the shared edge-case set (tests/safety/cases.json) on the rules in content/."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from glassworld_ai.safety import TextGuard

REPO = Path(__file__).resolve().parents[2]
GUARD = TextGuard.from_file(REPO / "content" / "safety" / "rules.json")
CASES: list[dict[str, Any]] = json.loads(
    (REPO / "tests" / "safety" / "cases.json").read_text(encoding="utf-8")
)["cases"]


def test_set_is_large_enough() -> None:
    for locale in ("de", "en"):
        rows = [c for c in CASES if c["locale"] == locale]
        assert len(rows) >= 60, locale
        assert any(c["expect"] == "allow" for c in rows)
        assert {c["category"] for c in rows if c["expect"] == "block"} >= {
            "sexual",
            "violence",
            "self_harm",
            "hate",
            "personal_data",
            "drugs",
            "injection",
        }


@pytest.mark.parametrize(
    "case", [c for c in CASES if c["stage"] == "prefilter"], ids=lambda c: c["id"]
)
def test_prefilter_case(case: dict[str, Any]) -> None:
    verdict = GUARD.check(case["text"])
    if case["expect"] == "allow":
        assert verdict.allowed, f"{case['text']!r} blocked: {verdict}"
    else:
        assert not verdict.allowed, f"{case['text']!r} allowed"
        assert verdict.category == case["category"], verdict


@pytest.mark.parametrize("case", [c for c in CASES if c["stage"] == "model"], ids=lambda c: c["id"])
def test_model_stage_case_passes_the_prefilter(case: dict[str, Any]) -> None:
    # These need the AI rule guard; the prefilter must not claim them (no false certainty).
    assert GUARD.check(case["text"]).allowed


def test_too_long_is_blocked() -> None:
    verdict = GUARD.check("a" * 281)
    assert not verdict.allowed
    assert verdict.reason == "too_long"


def test_control_characters_do_not_hide_words() -> None:
    assert not GUARD.check("se" + chr(0x200B) + "x").allowed  # zero-width space
    assert not GUARD.check("s\u0000e\u0000x").allowed


def test_full_width_letters_are_normalised() -> None:
    assert not GUARD.check(chr(0xFF53) + chr(0xFF45) + chr(0xFF58)).allowed  # full-width "sex"


def test_decomposed_umlaut_is_composed() -> None:
    assert not GUARD.check("to" + chr(0x0308) + "ten").allowed  # "töten" with a combining mark
