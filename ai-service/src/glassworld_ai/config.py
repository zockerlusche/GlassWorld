"""Service configuration: config.toml, overridden by .env and GW_AI_* environment variables.

Endpoints and models have no defaults in code (B2460, R8); they come from configuration.
"""

from __future__ import annotations

from pathlib import Path

from pydantic import Field, HttpUrl, SecretStr
from pydantic_settings import (
    BaseSettings,
    PydanticBaseSettingsSource,
    SettingsConfigDict,
    TomlConfigSettingsSource,
)

DEFAULT_CONFIG_FILE = Path(__file__).resolve().parents[2] / "config.toml"


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_prefix="GW_AI_",
        env_file=".env",
        extra="forbid",
        frozen=True,
        toml_file=DEFAULT_CONFIG_FILE,
    )

    ollama_url: HttpUrl
    comfyui_url: HttpUrl
    decider_model: str = Field(min_length=1)
    decider_model_digest: str = Field(pattern=r"^[0-9a-f]{12,64}$")
    probe_timeout_s: float = Field(gt=0, le=30)
    token: SecretStr | None = Field(
        default=None,
        description="Per-session token set by the game via GW_AI_TOKEN; never stored in files.",
    )

    @classmethod
    def settings_customise_sources(
        cls,
        settings_cls: type[BaseSettings],
        init_settings: PydanticBaseSettingsSource,
        env_settings: PydanticBaseSettingsSource,
        dotenv_settings: PydanticBaseSettingsSource,
        file_secret_settings: PydanticBaseSettingsSource,
    ) -> tuple[PydanticBaseSettingsSource, ...]:
        # Priority: explicit arguments > environment > .env > config.toml
        return (
            init_settings,
            env_settings,
            dotenv_settings,
            TomlConfigSettingsSource(settings_cls),
        )


def load_settings(config_file: Path | None = None) -> Settings:
    """Load settings from config.toml (or another TOML file) plus overrides."""
    path = config_file or DEFAULT_CONFIG_FILE
    if not path.is_file():
        raise FileNotFoundError(f"Configuration file not found: {path}")

    class _Configured(Settings):
        model_config = SettingsConfigDict(toml_file=path)

    return _Configured()  # pyright: ignore[reportCallIssue]
