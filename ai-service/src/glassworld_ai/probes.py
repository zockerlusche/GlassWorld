"""Detection of the external programs (Ollama, ComfyUI). The service never starts them."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Protocol

import httpx

from glassworld_ai.config import Settings


@dataclass(frozen=True)
class DependencyState:
    up: bool
    version: str | None


@dataclass(frozen=True)
class OllamaState:
    service: DependencyState
    model_available: bool
    model_loaded: bool


class Probes(Protocol):
    async def ollama(self) -> OllamaState: ...

    async def comfyui(self) -> DependencyState: ...


DOWN = DependencyState(up=False, version=None)


class HttpProbes:
    """Probes the real programs over HTTP on localhost."""

    def __init__(self, settings: Settings, client: httpx.AsyncClient) -> None:
        self._settings = settings
        self._client = client

    async def _get_json(self, base: str, path: str) -> Any:
        url = base.rstrip("/") + path
        response = await self._client.get(url, timeout=self._settings.probe_timeout_s)
        response.raise_for_status()
        return response.json()

    async def ollama(self) -> OllamaState:
        base = str(self._settings.ollama_url)
        try:
            version = await self._get_json(base, "/api/version")
            tags = await self._get_json(base, "/api/tags")
            running = await self._get_json(base, "/api/ps")
        except (httpx.HTTPError, ValueError):
            return OllamaState(service=DOWN, model_available=False, model_loaded=False)
        name = self._settings.decider_model
        digest = self._settings.decider_model_digest
        available = any(
            m.get("name") == name and str(m.get("digest", "")).startswith(digest)
            for m in tags.get("models", [])
        )
        loaded = any(m.get("name") == name for m in running.get("models", []))
        return OllamaState(
            service=DependencyState(up=True, version=_str_or_none(version.get("version"))),
            model_available=available,
            model_loaded=loaded,
        )

    async def comfyui(self) -> DependencyState:
        try:
            stats = await self._get_json(str(self._settings.comfyui_url), "/system_stats")
        except (httpx.HTTPError, ValueError):
            return DOWN
        system = stats.get("system", {})
        return DependencyState(up=True, version=_str_or_none(system.get("comfyui_version")))


def _str_or_none(value: object) -> str | None:
    return value if isinstance(value, str) else None
