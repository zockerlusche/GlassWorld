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
    night_plan_file: Path = Field(
        description="When job kinds run and which model they need (content/hardware/night.json).",
    )
    model_footprints_file: Path = Field(
        description="VRAM/load time per model (content/hardware/models.json, B7 S2).",
    )

    # Ollama at first start (B10 R3, F5266 = A)
    ollama_runtime_file: Path = Field(
        description="Version, source and checksum of Ollama (content/ai/ollama-runtime.json)."
    )
    ollama_install_dir: Path = Field(
        description="Where a downloaded Ollama is kept (the game sets the user data folder)."
    )
    ollama_models_dir: Path | None = Field(
        default=None, description="OLLAMA_MODELS for the Ollama the service starts (optional)."
    )
    ollama_autostart: bool = Field(
        default=True, description="Start an installed Ollama when none answers at service start."
    )
    ollama_start_timeout_s: float = Field(default=60, gt=0, le=600)

    # AI models at first start (B10 R4)
    model_downloads_file: Path = Field(
        description="Models per tier: licence, size, source (content/ai/model-downloads.json)."
    )
    model_store_dir: Path = Field(
        description="Where downloaded model files go (the game sets the user data folder)."
    )
    comfyui_models_dir: Path | None = Field(
        default=None,
        description="Models folder of the player's own ComfyUI (B2514); the game sets it once the "
        "player named it. Image models are downloaded there.",
    )

    # Measurement run (B7 S7)
    benchmark_file: Path = Field(description="Saved result of the measurement run.")
    benchmark_on_first_start: bool = Field(
        default=False, description="Measure once after the first start (the game turns it on)."
    )
    benchmark_timeout_s: float = Field(
        default=1800, gt=0, le=7200, description="Upper limit for one measurement run."
    )
    benchmark_answer_tokens: int = Field(
        default=128, ge=16, le=2048, description="Tokens of the language model's test answer."
    )

    # Image jobs (B7 S5)
    image_backend: Literal["mock", "comfyui"] = Field(
        default="mock", description="comfyui = the local image model, mock = a plain picture."
    )
    image_output_dir: Path = Field(description="Made pictures; the game copies them.")
    image_workflows_dir: Path = Field(description="ComfyUI workflow templates (API format).")
    image_poll_s: float = Field(default=1.0, gt=0, le=60, description="ComfyUI history poll.")
    image_timeout_s: float = Field(
        default=900, gt=0, le=7200, description="Per picture (comfort edit takes ~5 min)."
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
    process_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    process_prompt_version: int = Field(ge=1)
    dream_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    dream_prompt_version: int = Field(ge=1)
    review_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    review_prompt_version: int = Field(ge=1)
    help_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    help_prompt_version: int = Field(ge=1)
    conversation_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    conversation_prompt_version: int = Field(ge=1)
    builder_prompt: str = Field(pattern=r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")
    builder_prompt_version: int = Field(ge=1)
    question_call_timeout_s: float = Field(
        gt=0, le=3600, description="Per model call; questions themselves have no deadline."
    )
    job_capacity: int = Field(
        ge=1, le=1024, description="Jobs held in memory (B7 S1); finished ones make room first."
    )
    keep_alive: str = Field(min_length=1, max_length=16)
    warm_up: bool = Field(description="Load the decider model right after start.")
    warm_up_retry_s: float = Field(gt=0, le=3600, description="Retry a failed warm-up after.")

    # Sound jobs (B2530, D471 D7): mock without the model; moss = worker in the MOSS environment.
    sound_backend: Literal["mock", "moss"] = Field(default="mock")
    sound_render_timeout_s: float = Field(default=600, gt=0, le=7200, description="Per sound.")
    moss_start_timeout_s: float = Field(default=300, gt=0, le=3600, description="Model load.")
    moss_python: Path | None = Field(default=None, description="Python of the MOSS environment.")
    moss_model_dir: Path | None = Field(default=None, description="MOSS weights (local).")
    moss_code_dir: Path | None = Field(default=None, description="MOSS code at a pinned commit.")

    # Folders; relative paths are relative to the config file.
    prompts_dir: Path
    content_dir: Path
    schemas_dir: Path
    log_dir: Path
    lock_dir: Path
    sound_output_dir: Path = Field(description="Made sounds; the game copies them into the world.")
    model3d_image_dir: Path = Field(description="Pictures of image jobs (input of 3D jobs).")
    model3d_output_dir: Path = Field(description="Made 3D models; the game copies them.")
    model3d_backend: Literal["mock", "trellis2"] = Field(
        default="mock",
        description="mock = stand-in box; trellis2 = the real model (not wired yet).",
    )
    moss_worker_script: Path = Field(description="tools/moss/worker.py")
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
        "sound_output_dir",
        "moss_worker_script",
        "model3d_image_dir",
        "model3d_output_dir",
        "hardware_tiers_file",
        "model_footprints_file",
        "night_plan_file",
        "image_output_dir",
        "benchmark_file",
        "image_workflows_dir",
        "ollama_runtime_file",
        "ollama_install_dir",
        "model_downloads_file",
        "model_store_dir",
    ):
        value: Path = getattr(settings, name)
        if not value.is_absolute():
            resolved[name] = (path.parent / value).resolve()
    return settings.model_copy(update=resolved) if resolved else settings
