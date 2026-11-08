"""Resumable downloads from official sources (B10 R3/R4): HTTP Range, size limit, SHA-256.

Shared by the Ollama setup and the model download. A cancelled download keeps its partial file
so the next start continues; a file that turns out wrong is removed by the caller.
"""

from __future__ import annotations

import asyncio
import hashlib
import time
from collections.abc import Callable
from pathlib import Path

import httpx

CHUNK = 1 << 20
TIMEOUT = httpx.Timeout(30.0, read=120.0)


class TooLargeError(Exception):
    """The source sent more bytes than the data file says: never keep such a file."""


async def fetch_resumable(
    client: httpx.AsyncClient,
    url: str,
    part: Path,
    size_bytes: int,
    progress: Callable[[int], None],
) -> None:
    """Fetch `url` into `part`, continuing what is already there. `progress` gets bytes on disk."""
    part.parent.mkdir(parents=True, exist_ok=True)
    have = part.stat().st_size if part.is_file() else 0
    if have > size_bytes:
        part.unlink()
        have = 0
    progress(have)
    if have == size_bytes:
        return
    headers = {"Range": f"bytes={have}-"} if have else {}
    async with client.stream(
        "GET", url, headers=headers, timeout=TIMEOUT, follow_redirects=True
    ) as response:
        if response.status_code == 416:  # nothing left to fetch: the size check decides
            return
        response.raise_for_status()
        if have and response.status_code != 206:
            have = 0  # the server sent the whole file again
        with part.open("ab" if have else "wb") as out:
            done = have
            progress(done)
            async for chunk in response.aiter_bytes(CHUNK):
                await asyncio.to_thread(out.write, chunk)
                done += len(chunk)
                progress(done)
                if done > size_bytes:
                    raise TooLargeError(f"{url} is larger than {size_bytes} bytes")


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        while block := f.read(CHUNK):
            digest.update(block)
    return digest.hexdigest()


def with_retries(step: Callable[[], object], attempts: int = 10, wait_s: float = 1.0) -> None:
    """File steps that a virus scanner may briefly block on Windows (PermissionError)."""
    for attempt in range(attempts):
        try:
            step()
            return
        except PermissionError:
            if attempt == attempts - 1:
                raise
            time.sleep(wait_s)
