"""Service configuration: config.toml, overridden by .env and GW_AI_* environment variables.

Endpoints and models have no defaults in code (B2460, R8); they come from configuration.
"""

from __future__ import annotations

from pathlib import Path
from typing import Literal

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
    hardware_tiers_file: Path = Field(
        description="Tier thresholds (content/hardware/tiers.json); relative to the config file.",
    )
    model_footprints_file: Path = Field(
        description="VRAM/load time per model (content/hardware/models.json, B7 S2).",
    )

    # Decider (B3)
    decider_backend: Literal["ollama", "mock", "replay"]
    decider_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    decider_prompt_version: int = Field(ge=1)
    temperature: float = Field(ge=0, le=2)
    num_ctx: int = Field(ge=512, le=131072)
    prompt_char_budget: int = Field(ge=500, le=100000)
    max_retries: int = Field(ge=0, le=2, description="Handbook: at most two retries.")
    queue_size: int = Field(ge=1, le=10000)
    question_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    question_prompt_version: int = Field(ge=1)
    memory_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    memory_prompt_version: int = Field(ge=1)
    invention_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    invention_prompt_version: int = Field(ge=1)
    guard_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    guard_prompt_version: int = Field(ge=1)
    question_call_timeout_s: float = Field(
        gt=0, le=3600, description="Per model call; questions themselves have no deadline."
    )
    job_capacity: int = Field(
        ge=1, le=1024, description="Jobs held in memory (B7 S1); finished ones make room first."
    )
    keep_alive: str = Field(min_length=1, max_length=16)
    warm_up: bool = Field(description="Load the decider model right after start.")
    warm_up_retry_s: float = Field(gt=0, le=3600, description="Retry a failed warm-up after.")

    # Folders; relative paths are relative to the config file.
    prompts_dir: Path
    content_dir: Path
    schemas_dir: Path
    log_dir: Path
    lock_dir: Path
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

    settings = _Configured()  # pyright: ignore[reportCallIssue]
    resolved: dict[str, Path] = {}
    for name in (
        "prompts_dir",
        "content_dir",
        "schemas_dir",
        "log_dir",
        "lock_dir",
        "hardware_tiers_file",
        "model_footprints_file",
    ):
        value: Path = getattr(settings, name)
        if not value.is_absolute():
            resolved[name] = (path.parent / value).resolve()
    return settings.model_copy(update=resolved) if resolved else settings
