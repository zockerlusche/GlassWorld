"""Packaged AI service (B10-S2): the packaged config keeps every repository setting."""

from __future__ import annotations

import importlib.util
import tomllib
from pathlib import Path

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
