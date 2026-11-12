"""Tests of the night test content (A1558)."""

import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent))
from night_test_content import ROOT, TRIGGER, make


def _trigger(content: Path) -> dict[str, object]:
    data = json.loads((content / "decider" / "settings.json").read_text(encoding="utf-8"))
    return next(t for t in data["triggers"] if t["kind"] == TRIGGER)


def test_the_copy_asks_about_things_at_night_and_the_repository_stays(tmp_path: Path) -> None:
    before = (ROOT / "content" / "decider" / "settings.json").read_bytes()
    out = make(tmp_path / "content")
    assert _trigger(out)["ask"] == "night"
    assert (ROOT / "content" / "decider" / "settings.json").read_bytes() == before
    assert _trigger(ROOT / "content").get("ask", "immediate") == "immediate"
    # Everything else is the same data.
    assert (out / "vocabulary").is_dir()
    knowledge = ROOT / "content" / "knowledge" / "settings.json"
    assert (out / "knowledge" / "settings.json").read_bytes() == knowledge.read_bytes()


def test_an_existing_folder_is_never_overwritten(tmp_path: Path) -> None:
    (tmp_path / "content").mkdir()
    with pytest.raises(SystemExit, match="exists already"):
        make(tmp_path / "content")
