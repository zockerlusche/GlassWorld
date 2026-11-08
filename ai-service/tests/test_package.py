"""Packaged AI service (B10-S2): the packaged config keeps every repository setting."""

from __future__ import annotations

import importlib.util
import tomllib
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]


def _module():  # tools/ is not a package
    spec = importlib.util.spec_from_file_location(
        "package_ai_service", REPO / "tools/package_ai_service.py"
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_packaged_config_has_every_setting_with_packaged_paths() -> None:
    packaged = tomllib.loads(_module().packaged_config())
    repo = tomllib.loads((REPO / "ai-service/config.toml").read_text(encoding="utf-8"))
    assert set(repo) - {"token"} <= set(packaged)
    assert packaged["prompts_dir"] == "prompts" and packaged["content_dir"] == "../content"
    assert packaged["decider_model"] == repo["decider_model"]
    assert not any(str(v).startswith("../.local") for v in packaged.values())


def test_never_packages_into_the_repository() -> None:
    with pytest.raises(SystemExit, match="refusing"):
        _module().prepare_target(REPO)


def test_replaces_only_its_own_folder(tmp_path: Path) -> None:
    module = _module()
    foreign = tmp_path / "ai-service"
    foreign.mkdir()
    (foreign / "keep.txt").write_text("not ours", encoding="utf-8")
    with pytest.raises(SystemExit, match="not made by this script"):
        module.prepare_target(tmp_path)
    assert (foreign / "keep.txt").is_file()
    other = tmp_path / "fresh"
    first = module.prepare_target(other)
    (first / "old.txt").write_text("x", encoding="utf-8")
    second = module.prepare_target(other)  # its own folder: rebuilt
    assert second == first and not (second / "old.txt").exists()
