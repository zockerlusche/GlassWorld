"""Release notes from the changelog (B10 R8)."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from types import ModuleType

import pytest

ROOT = Path(__file__).resolve().parents[2]


def _module() -> ModuleType:
    spec = importlib.util.spec_from_file_location(
        "release_notes", Path(__file__).with_name("release_notes.py")
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


NOTES = _module()

CHANGELOG = """# Changelog

Intro.

## [Unreleased]

### Added

- Something new.

## [0.2.0] - 2026-11-21

### Added

- The showdown build.

### Fixed

- A bug.

## [0.1.0] - 2026-11-15

- First.
"""


def test_a_version_gets_exactly_its_section_and_the_footer() -> None:
    text = NOTES.notes(CHANGELOG, "0.2.0")
    assert text.startswith("### Added\n\n- The showdown build.")
    assert "- A bug." in text
    assert "Something new" not in text and "First." not in text
    assert "SHA256SUMS.txt" in text and "LICENSES.md" in text


def test_the_last_section_runs_to_the_end() -> None:
    assert NOTES.notes(CHANGELOG, "0.1.0").startswith("- First.\n")


def test_probe_builds_use_unreleased() -> None:
    assert NOTES.notes(CHANGELOG, None).startswith("### Added\n\n- Something new.")


@pytest.mark.parametrize(
    ("version", "reason"),
    [("0.3.0", "no section"), ("v0.2.0", "not a semantic version"), ("1.0", "semantic")],
)
def test_missing_or_bad_versions_fail(version: str, reason: str) -> None:
    with pytest.raises(NOTES.NotesError, match=reason):
        NOTES.notes(CHANGELOG, version)


def test_an_empty_section_fails() -> None:
    with pytest.raises(NOTES.NotesError, match="empty"):
        NOTES.notes("## [0.1.0]\n\n## [0.0.1]\n- x\n", "0.1.0")


def test_the_repository_changelog_has_notes_for_a_probe_build() -> None:
    text = NOTES.notes((ROOT / "CHANGELOG.md").read_text(encoding="utf-8"), None)
    assert len(text) > 200


def test_cli_fails_loudly(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    log = tmp_path / "CHANGELOG.md"
    log.write_text(CHANGELOG, encoding="utf-8")
    assert NOTES.main(["9.9.9", "--changelog", str(log)]) == 1
    assert "RELEASE_NOTES_FAIL" in capsys.readouterr().err
    assert NOTES.main(["0.1.0", "--changelog", str(log)]) == 0


def _pack_module() -> ModuleType:
    spec = importlib.util.spec_from_file_location("pack", Path(__file__).with_name("pack.py"))
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


PACK = _pack_module()


def test_pack_writes_zip_and_checksum(tmp_path: Path) -> None:
    import hashlib
    import zipfile

    stage = tmp_path / "GlassWorld-0.1.0-windows-x64"
    (stage / "content").mkdir(parents=True)
    (stage / "GlassWorld.exe").write_bytes(b"MZ")
    (stage / "content" / "a.json").write_text("{}", encoding="utf-8")
    archive = PACK.pack(stage, tmp_path / "out", ["F:/Build/GlassWorld"])
    with zipfile.ZipFile(archive) as zf:
        assert sorted(zf.namelist()) == [
            "GlassWorld-0.1.0-windows-x64/GlassWorld.exe",
            "GlassWorld-0.1.0-windows-x64/content/a.json",
        ]
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    sums = (tmp_path / "out" / "SHA256SUMS.txt").read_text(encoding="utf-8")
    assert sums == f"{digest}  GlassWorld-0.1.0-windows-x64.zip\n"


def test_pack_refuses_build_machine_paths(tmp_path: Path) -> None:
    stage = tmp_path / "GlassWorld-probe-windows-x64"
    stage.mkdir()
    (stage / "Start-mit-KI.cmd").write_text(
        "set GLASSWORLD_AI_ROOT=f:\\build\\GlassWorld\r\n", encoding="utf-8"
    )
    with pytest.raises(PACK.PackError, match=r"Start-mit-KI.cmd"):
        PACK.pack(stage, tmp_path / "out", ["F:/Build/GlassWorld"])
    assert not (tmp_path / "out").exists()


def test_notes_file_is_utf8(tmp_path: Path) -> None:
    log = tmp_path / "CHANGELOG.md"
    log.write_text("## [0.1.0]\n\n- arrow \u2192 and umlaut \u00e4\n", encoding="utf-8")
    out = tmp_path / "notes.md"
    assert NOTES.main(["0.1.0", "--changelog", str(log), "--out", str(out)]) == 0
    assert "\u2192" in out.read_text(encoding="utf-8")


def test_pack_finds_paths_in_binary_files(tmp_path: Path) -> None:
    """Review GW1 #130: byte code and launchers carried the build machine's user folder."""
    stage = tmp_path / "GlassWorld-probe-windows-x64"
    (stage / "python" / "Scripts").mkdir(parents=True)
    (stage / "python" / "mod.cpython-312.pyc").write_bytes(
        b"\x00\x01C:/Users/Someone/AppData/uv/Lib/mod.py\x00"
    )
    wide = r"C:\Users\Someone\stage\python.exe".encode("utf-16-le")
    (stage / "python" / "Scripts" / "tool.exe").write_bytes(b"MZ\x00" + wide)
    (stage / "clean.bin").write_bytes(b"MZ nothing here")
    leaks = PACK.leaked_paths(stage, [r"c:\users\someone"])
    assert sorted(leaks) == ["python/Scripts/tool.exe", "python/mod.cpython-312.pyc"]
