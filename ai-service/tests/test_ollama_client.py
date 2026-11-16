from __future__ import annotations

import asyncio
import json
from typing import Any

import httpx
import pytest

from glassworld_ai.llm.ollama import (
    ChatMessage,
    OllamaClient,
    OllamaRequestError,
    OllamaResponseError,
    OllamaUnavailableError,
)

SCHEMA: dict[str, Any] = {"type": "object", "properties": {"a": {"type": "string"}}}


def _client(handler: httpx.MockTransport) -> tuple[OllamaClient, httpx.AsyncClient]:
    http = httpx.AsyncClient(transport=handler)
    return OllamaClient("http://ollama.test", "qwen3.5:9b", http, keep_alive="10m"), http


def test_chat_sends_schema_seed_and_disables_thinking() -> None:
    seen: list[dict[str, Any]] = []

    def handle(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/api/chat"
        seen.append(json.loads(request.content))
        return httpx.Response(
            200,
            json={
                "message": {"role": "assistant", "content": '{"a":"x"}'},
                "prompt_eval_count": 120,
                "eval_count": 30,
            },
        )

    async def scenario() -> None:
        client, http = _client(httpx.MockTransport(handle))
        async with http:
            result = await client.chat(
                [ChatMessage("system", "s"), ChatMessage("user", "u")],
                schema=SCHEMA,
                seed=7,
                temperature=0.7,
                num_ctx=4096,
                timeout=5,
            )
        assert result.content == '{"a":"x"}'
        assert (result.prompt_tokens, result.output_tokens) == (120, 30)

    asyncio.run(scenario())
    body = seen[0]
    assert body["format"] == SCHEMA
    assert body["think"] is False
    assert body["stream"] is False
    assert body["keep_alive"] == "10m"
    assert body["options"] == {"seed": 7, "temperature": 0.7, "num_ctx": 4096}
    assert [m["role"] for m in body["messages"]] == ["system", "user"]


def test_connection_error_means_unavailable() -> None:
    def handle(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused", request=request)

    async def scenario() -> None:
        client, http = _client(httpx.MockTransport(handle))
        async with http:
            with pytest.raises(OllamaUnavailableError):
                await client.chat([], schema=SCHEMA, seed=1, temperature=0, num_ctx=1, timeout=1)

    asyncio.run(scenario())


@pytest.mark.parametrize(
    ("status", "error"),
    [(404, OllamaUnavailableError), (500, OllamaUnavailableError), (400, OllamaRequestError)],
)
def test_http_errors_are_mapped(status: int, error: type[Exception]) -> None:
    def handle(_: httpx.Request) -> httpx.Response:
        return httpx.Response(status, json={"error": "x"})

    async def scenario() -> None:
        client, http = _client(httpx.MockTransport(handle))
        async with http:
            with pytest.raises(error):
                await client.chat([], schema=SCHEMA, seed=1, temperature=0, num_ctx=1, timeout=1)

    asyncio.run(scenario())


def test_answer_without_content_is_an_error() -> None:
    def handle(_: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"done": True})

    async def scenario() -> None:
        client, http = _client(httpx.MockTransport(handle))
        async with http:
            with pytest.raises(OllamaResponseError):
                await client.chat([], schema=SCHEMA, seed=1, temperature=0, num_ctx=1, timeout=1)

    asyncio.run(scenario())


def test_unload_sets_keep_alive_zero_and_warm_up_loads() -> None:
    bodies: list[dict[str, Any]] = []

    def handle(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/api/generate"
        bodies.append(json.loads(request.content))
        return httpx.Response(200, json={"done": True})

    async def scenario() -> None:
        client, http = _client(httpx.MockTransport(handle))
        async with http:
            await client.warm_up(timeout=5)
            await client.unload(timeout=5)

    asyncio.run(scenario())
    assert bodies[0] == {"model": "qwen3.5:9b", "prompt": "", "keep_alive": "10m"}
    assert bodies[1] == {"model": "qwen3.5:9b", "keep_alive": 0}


def test_a_seed_offset_shifts_the_seed_sent_to_the_model() -> None:
    """R27 (D827 T1): N acceptance runs of one world sample differently; 0 changes nothing."""
    seen: list[int] = []

    def handle(request: httpx.Request) -> httpx.Response:
        seen.append(json.loads(request.content)["options"]["seed"])
        return httpx.Response(200, json={"message": {"role": "assistant", "content": "{}"}})

    async def scenario(offset: int) -> None:
        http = httpx.AsyncClient(transport=httpx.MockTransport(handle))
        client = OllamaClient("http://o.test", "m", http, keep_alive="1m", seed_offset=offset)
        async with http:
            await client.chat(
                [ChatMessage("user", "u")],
                schema=SCHEMA,
                seed=2_147_483_000,
                temperature=0.7,
                num_ctx=4096,
                timeout=5,
            )

    asyncio.run(scenario(0))
    asyncio.run(scenario(1_000))
    assert seen == [2_147_483_000, (2_147_483_000 + 1_000) % 2_147_483_648]
