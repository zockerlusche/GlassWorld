from __future__ import annotations

from pathlib import Path

import pytest
from pydantic import ValidationError

from glassworld_ai.config import Settings, load_settings


def test_defaults_come_from_config_toml(settings: Settings) -> None:
    assert str(settings.ollama_url).startswith("http://127.0.0.1:")
    assert settings.decider_model == "qwen3.5:9b"
    assert settings.token is not None


def test_environment_overrides_config_file(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.chdir(tmp_path)
    monkeypatch.setenv("GW_AI_DECIDER_MODEL", "other-model:1b")
    assert load_settings().decider_model == "other-model:1b"


def test_missing_config_file_fails_clearly(tmp_path: Path) -> None:
    with pytest.raises(FileNotFoundError):
        load_settings(tmp_path / "missing.toml")


def test_incomplete_config_file_is_rejected(tmp_path: Path) -> None:
    config = tmp_path / "config.toml"
    config.write_text('ollama_url = "http://127.0.0.1:11434"\n', encoding="utf-8")
    with pytest.raises(ValidationError):
        load_settings(config)


def test_unknown_config_key_is_rejected(tmp_path: Path) -> None:
    source = Path(__file__).resolve().parents[1] / "config.toml"
    config = tmp_path / "config.toml"
    config.write_text(source.read_text(encoding="utf-8") + 'typo_key = "x"\n', encoding="utf-8")
    with pytest.raises(ValidationError):
        load_settings(config)
