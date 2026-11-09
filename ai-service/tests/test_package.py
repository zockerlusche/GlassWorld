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


def test_every_library_of_the_embedded_python_needs_a_licence_row(tmp_path: Path) -> None:
    """Review GW1 #51: named one by one, so a new module in a newer build stops the package."""
    package = _module()
    python_dir = tmp_path / "python"
    (python_dir / "DLLs").mkdir(parents=True)
    for name in ("python312.dll", "vcruntime140.dll"):
        (python_dir / name).write_bytes(b"MZ")
    for name in ("_ssl.pyd", "_socket.pyd", "unicodedata.pyd"):
        (python_dir / "DLLs" / name).write_bytes(b"MZ")
    package.check_inventory(python_dir)  # all named: passes
    (python_dir / "DLLs" / "_brand_new.pyd").write_bytes(b"MZ")
    with pytest.raises(SystemExit, match=r"_brand_new.pyd"):
        package.check_inventory(python_dir)


def test_build_machine_paths_are_stripped_from_installed_metadata(tmp_path: Path) -> None:
    """B10 R8 privacy: pip's direct_url.json of local packages names the build folder."""
    package = _module()
    site = tmp_path / "python" / "Lib" / "site-packages"
    local = site / "glassworld_ai_service-0.1.0.dist-info"
    index = site / "httpx-0.28.1.dist-info"
    for folder in (local, index):
        folder.mkdir(parents=True)
    (local / "direct_url.json").write_text('{"url": "file:///F:/build/ai-service"}', "utf-8")
    (local / "RECORD").write_text(
        "glassworld_ai/__init__.py,sha256=x,1\n"
        "glassworld_ai_service-0.1.0.dist-info/direct_url.json,,\n",
        encoding="utf-8",
    )
    (index / "direct_url.json").write_text('{"url": "https://example.invalid/x.whl"}', "utf-8")
    package.strip_build_paths(tmp_path / "python")
    assert not (local / "direct_url.json").exists()
    assert "direct_url" not in (local / "RECORD").read_text(encoding="utf-8")
    assert (index / "direct_url.json").exists()  # no build path in it: untouched


def test_finish_python_drops_launchers_tests_and_old_byte_code(tmp_path: Path) -> None:
    """Review GW1 #130: launchers and byte code named the build machine."""
    package = _module()
    python_dir = tmp_path / "python"
    (python_dir / "Scripts").mkdir(parents=True)
    (python_dir / "Scripts" / "uvicorn.exe").write_bytes(b"MZ")
    tests = python_dir / "Lib" / "site-packages" / "jsonschema" / "tests"
    tests.mkdir(parents=True)
    (tests / "test_x.py").write_text("x = 1\n", encoding="utf-8")
    cache = python_dir / "Lib" / "asyncio" / "__pycache__"
    cache.mkdir(parents=True)
    (cache / "base.cpython-312.pyc").write_bytes(b"old")
    package.finish_python(python_dir)  # no python.exe here: no recompilation
    assert not (python_dir / "Scripts").exists()
    assert not tests.exists()
    assert not cache.exists()
