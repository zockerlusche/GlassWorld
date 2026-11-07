"""Ollama client: chat with structured output (JSON Schema), thinking off, fixed seed.

Only HTTP on localhost. The service detects Ollama but never starts it (ADR-0006).
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any, cast

import httpx


class OllamaUnavailableError(Exception):
    """Ollama is not reachable or the model is missing (ServiceError 'model_unavailable')."""


class OllamaRequestError(Exception):
    """Ollama rejected our request (4xx): a bug on our side, retrying does not help."""


class OllamaResponseError(Exception):
    """Ollama answered, but not with a usable result."""


@dataclass(frozen=True)
class ChatMessage:
    role: str  # "system" | "user" | "assistant"
    content: str


@dataclass(frozen=True)
class ChatResult:
    content: str
    duration_ms: int
    prompt_tokens: int | None
    output_tokens: int | None


class OllamaClient:
    def __init__(
        self, base_url: str, model: str, client: httpx.AsyncClient, keep_alive: str
    ) -> None:
        self._base = base_url.rstrip("/")
        self._model = model
        self._client = client
        self._keep_alive = keep_alive

    @property
    def model(self) -> str:
        return self._model

    async def _post(self, path: str, body: dict[str, Any], timeout: float) -> dict[str, Any]:
        try:
            response = await self._client.post(self._base + path, json=body, timeout=timeout)
        except httpx.TimeoutException:
            raise
        except httpx.HTTPError as error:
            raise OllamaUnavailableError(f"Ollama not reachable: {type(error).__name__}") from error
        if response.status_code == 404:
            raise OllamaUnavailableError(f"model {self._model} not found")
        if response.status_code >= 500:
            raise OllamaUnavailableError(f"Ollama error {response.status_code}")
        if response.status_code >= 400:
            raise OllamaRequestError(f"Ollama rejected the request ({response.status_code})")
        try:
            data = response.json()
        except ValueError as error:
            raise OllamaResponseError("Ollama answer is not JSON") from error
        if not isinstance(data, dict):
            raise OllamaResponseError("Ollama answer is not an object")
        return data  # pyright: ignore[reportUnknownVariableType]

    async def chat(
        self,
        messages: list[ChatMessage],
        *,
        schema: dict[str, Any],
        seed: int,
        temperature: float,
        num_ctx: int,
        timeout: float,
    ) -> ChatResult:
        """One non-streaming chat call whose output must follow `schema`."""
        body: dict[str, Any] = {
            "model": self._model,
            "messages": [{"role": m.role, "content": m.content} for m in messages],
            "format": schema,
            "stream": False,
            "think": False,
            "keep_alive": self._keep_alive,
            "options": {"seed": seed, "temperature": temperature, "num_ctx": num_ctx},
        }
        started = time.perf_counter()
        data = await self._post("/api/chat", body, timeout)
        message: object = data.get("message")
        content: object = (
            cast(dict[str, object], message).get("content") if isinstance(message, dict) else None
        )
        if not isinstance(content, str):
            raise OllamaResponseError("Ollama answer has no message content")
        return ChatResult(
            content=content,
            duration_ms=int((time.perf_counter() - started) * 1000),
            prompt_tokens=_int_or_none(data.get("prompt_eval_count")),
            output_tokens=_int_or_none(data.get("eval_count")),
        )

    async def warm_up(self, timeout: float) -> None:
        """Load the model into VRAM without generating text."""
        await self._post(
            "/api/generate",
            {"model": self._model, "prompt": "", "keep_alive": self._keep_alive},
            timeout,
        )

    async def unload(self, timeout: float) -> None:
        """Unload the model from VRAM (keep_alive 0), e.g. on shutdown."""
        await self._post("/api/generate", {"model": self._model, "keep_alive": 0}, timeout)


def _int_or_none(value: object) -> int | None:
    return value if isinstance(value, int) else None
