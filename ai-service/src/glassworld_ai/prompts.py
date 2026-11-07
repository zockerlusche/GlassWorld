"""Prompts as code: versioned TOML files under prompts/<role>/<name>.v<N>.toml.

A prompt file holds plain text templates (`string.Template`, `$name` placeholders); nothing in
it is executed. Rendering is strict: a missing placeholder value is an error, so a prompt can
never silently lose context.
"""

from __future__ import annotations

import re
import tomllib
from dataclasses import dataclass, field
from pathlib import Path
from string import Template
from typing import Any

_NAME = re.compile(r"^[a-z][a-z0-9_]*/[a-z][a-z0-9_-]*$")


class PromptError(Exception):
    """The prompt file is missing, malformed or a placeholder has no value."""


@dataclass(frozen=True)
class RenderedPrompt:
    version: str  # e.g. "decider/decide.v1"
    system: str
    user: str


@dataclass(frozen=True)
class PromptTemplate:
    name: str  # "<role>/<name>", e.g. "decider/decide"
    version: int
    system: str
    user: str
    settings: dict[str, Any] = field(default_factory=dict[str, Any])

    @property
    def version_id(self) -> str:
        return f"{self.name}.v{self.version}"

    def render(self, values: dict[str, str]) -> RenderedPrompt:
        try:
            system = Template(self.system).substitute(values)
            user = Template(self.user).substitute(values)
        except (KeyError, ValueError) as error:
            raise PromptError(f"{self.version_id}: placeholder without value: {error}") from error
        return RenderedPrompt(version=self.version_id, system=system.strip(), user=user.strip())


def load_prompt(prompts_dir: Path, name: str, version: int) -> PromptTemplate:
    """Load prompts/<role>/<name>.v<version>.toml. `name` is "<role>/<name>"."""
    if not _NAME.fullmatch(name):
        raise PromptError(f"invalid prompt name {name!r}")
    path = prompts_dir / f"{name}.v{version}.toml"
    try:
        data = tomllib.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise PromptError(f"prompt file not found: {path}") from error
    except tomllib.TOMLDecodeError as error:
        raise PromptError(f"{path}: {error}") from error
    if data.get("name") != name or data.get("version") != version:
        raise PromptError(f"{path}: name/version inside the file do not match the file name")
    system, user = data.get("system"), data.get("user")
    if not isinstance(system, str) or not isinstance(user, str):
        raise PromptError(f"{path}: 'system' and 'user' must be strings")
    settings = data.get("settings", {})
    if not isinstance(settings, dict):
        raise PromptError(f"{path}: 'settings' must be a table")
    return PromptTemplate(
        name=name,
        version=version,
        system=system,
        user=user,
        settings=settings,  # pyright: ignore[reportUnknownArgumentType]
    )
