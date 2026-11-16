"""Schnelldurchlauf (A1743): the world folder follows the game's world list."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import run


def test_the_folder_name_is_the_game_slug() -> None:
    """As WorldLibrary.Slug, so the menu finds meta.json under the id it expects."""
    assert run.slug("Schnell 123") == "schnell-123"
    assert run.slug("Grüße & Ö") == "gruesse-oe"
    assert run.slug("con") == "con-world"
    assert run.slug("***") == "world"
    assert len(run.slug("x" * 50)) == 32


def test_an_existing_world_is_never_overwritten(tmp_path: Path) -> None:
    (tmp_path / "probe").mkdir()
    (tmp_path / "probe-2").mkdir()
    assert run.free_folder(tmp_path, "Probe") == tmp_path / "probe-3"


def test_the_runner_is_started_directly_when_built(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """TL #27953: the built runner dll is found (newest target framework), so the quick run
    skips the ~15 s of 'dotnet run'; without a build there is none."""
    monkeypatch.setattr(run, "PROJECT", tmp_path)
    assert run.runner_dll() is None
    dll = tmp_path / "bin" / "Release" / "net10.0" / "GlassWorld.Simulation.Headless.dll"
    dll.parent.mkdir(parents=True)
    dll.write_bytes(b"")
    assert run.runner_dll() == dll


def test_the_worlds_go_where_the_game_keeps_its_data(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """A1787 (B2824): with GLASSWORLD_DATA (a package's start script names its daten/ folder)
    the world lands in that folder's worlds/, where the exported game looks."""
    monkeypatch.setenv("GLASSWORLD_DATA", str(tmp_path))
    assert run.worlds_folder() == tmp_path / "worlds"


def test_the_logs_go_into_the_log_folder_beside_daten(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    """A1787 part 2 (Marcel W2246): with a package's daten/ the logs go to log/ next to it."""
    monkeypatch.setenv("GLASSWORLD_DATA", str(tmp_path / "daten"))
    assert run.logs_folder() == tmp_path / "log"
    monkeypatch.setenv("GLASSWORLD_DATA", str(tmp_path / "other"))
    assert run.logs_folder() == tmp_path / "other" / "log"
