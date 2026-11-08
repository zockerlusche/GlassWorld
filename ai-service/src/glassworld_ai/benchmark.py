"""Measurement run at first start (B7 R6, S7, A1410): what this PC needs per model.

Measures, one model after the other and through the residency (never two models above the
budget): the language model's load time and tokens per second, the image model's load time and
seconds per picture, the sound model's load time and seconds per sound. The job runner holds
while it runs. The result is saved (repeatable at any time with POST /benchmark) and feeds the
planner: a night job starts only if its expected time still fits into the night budget.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol, cast

import httpx

from glassworld_ai.residency import Residency
from glassworld_contracts import CONTRACT_VERSION, BenchmarkStatus

IDLE_POLL_S = 0.5


class StillBusyError(RuntimeError):
    """A job did not end in time before the measurement."""


@dataclass(frozen=True)
class Generated:
    """What the language model reports for one answer (Ollama eval statistics)."""

    tokens: int
    seconds: float


class LanguageProbe(Protocol):
    model: str

    async def generate(self, tokens: int) -> Generated: ...


class JobProbe(Protocol):
    """Image or sound: make one fixed test job with the real handler."""

    model: str

    async def make(self) -> None: ...


class Runner(Protocol):
    def hold(self) -> None: ...
    def resume(self) -> None: ...
    def counts(self) -> dict[str, int]: ...


class Benchmark:
    def __init__(
        self,
        *,
        store: Path,
        runner: Runner,
        residency: Callable[[], Residency | None],
        hardware: Callable[[], tuple[str | None, str, int]],
        answer_tokens: int,
        language: LanguageProbe | None,
        image: JobProbe | None,
        sound: JobProbe | None,
        on_result: Callable[[dict[str, float]], None],
        restore: Callable[[], Awaitable[None]],
        measuring: Callable[[bool], None] = lambda _: None,
        idle_timeout_s: float = 600,
        overall_timeout_s: float = 1800,
        clock: Callable[[], float] = time.monotonic,
        wall: Callable[[], float] = time.time,
    ) -> None:
        self._store = store
        self._runner = runner
        self._residency = residency
        self._hardware = hardware
        self._answer_tokens = answer_tokens
        self._language = language
        self._image = image
        self._sound = sound
        self._on_result = on_result
        self._restore = restore
        self._measuring = measuring
        self._idle_timeout_s = idle_timeout_s
        self._overall_timeout_s = overall_timeout_s
        self._clock = clock
        self._wall = wall
        self.state = "none"
        self.result: dict[str, Any] | None = None
        self.error: str | None = None
        self._task: asyncio.Task[None] | None = None
        self._load_saved()

    @property
    def available(self) -> bool:
        return self._language is not None or self._image is not None or self._sound is not None

    def status(self) -> BenchmarkStatus:
        return BenchmarkStatus.model_validate(
            {
                "contract_version": CONTRACT_VERSION,
                "state": self.state,
                "result": self.result,
                "error": self.error,
            }
        )

    def start(self) -> None:
        """Start a run in the background (a running one is kept)."""
        if self.state == "running":
            return
        self.state = "running"
        self.error = None
        self._task = asyncio.get_running_loop().create_task(self.run())

    async def close(self) -> None:
        if self._task is not None and not self._task.done():
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task

    async def run(self) -> None:
        self.state = "running"
        self._runner.hold()
        self._measuring(True)
        try:
            async with asyncio.timeout(self._overall_timeout_s):  # review GW1 #97
                await self._measure_all()
            self.state = "done"
        except asyncio.CancelledError:
            self.state = "failed"
            self.error = "cancelled"
            raise
        except TimeoutError:
            self.state = "failed"
            self.error = f"measurement took longer than {self._overall_timeout_s:.0f} s"
        except Exception as error:
            self.state = "failed"
            self.error = f"{type(error).__name__}: {error}"[:300]
        finally:
            self._measuring(False)
            with contextlib.suppress(Exception):
                await self._restore()  # e.g. the language model back for the day
            self._runner.resume()

    async def _measure_all(self) -> None:
        await self._wait_idle()
        residency = self._residency()
        if residency is not None:
            await residency.release_all()  # every model measured from unloaded
        language = await self._section(self._measure_language)
        image = await self._section(lambda: self._measure_job(self._image))
        sound = await self._section(lambda: self._measure_job(self._sound))
        if language is None and image is None and sound is None:
            raise RuntimeError(self.error or "no model could be measured")
        tier, gpu, vram = self._hardware()
        self.result = {
            "measured_at": int(self._wall()),
            "tier": tier,
            "gpu": gpu[:128],
            "vram_mib": vram,
            "answer_tokens": self._answer_tokens,
            "language": language,
            "image": image,
            "sound": sound,
        }
        self.status()  # validates the result against the contract
        self._save()
        self._apply()

    # --- internals ------------------------------------------------------------------------

    async def _wait_idle(self) -> None:
        until = self._clock() + self._idle_timeout_s
        while self._runner.counts()["running"] > 0:
            if self._clock() >= until:
                raise StillBusyError("a job kept running; measure again later")
            await asyncio.sleep(IDLE_POLL_S)

    async def _section(
        self, measure: Callable[[], Awaitable[dict[str, Any] | None]]
    ) -> dict[str, Any] | None:
        """One model; a failure leaves it unmeasured (null) and is reported, the rest goes on."""
        try:
            return await measure()
        except Exception as error:
            self.error = f"{type(error).__name__}: {error}"[:300]
            return None

    async def _measure_language(self) -> dict[str, Any] | None:
        probe = self._language
        if probe is None:
            return None
        load_s = await self._load(probe.model)
        answer = await probe.generate(self._answer_tokens)
        rate = answer.tokens / answer.seconds if answer.seconds > 0 else 0.0
        seconds = self._answer_tokens / rate if rate > 0 else answer.seconds
        await self._unload(probe.model)
        return {
            "model": probe.model,
            "load_s": round(load_s, 2),
            "seconds_per_job": round(seconds, 2),
            "tokens_per_s": round(rate, 1),
        }

    async def _measure_job(self, probe: JobProbe | None) -> dict[str, Any] | None:
        if probe is None:
            return None
        residency = self._residency()
        tracked = residency is not None and residency.knows(probe.model)
        started = self._clock()
        if tracked:
            await self._load(probe.model)
        await probe.make()  # cold: loads the model if the driver loads with the first job
        cold = self._clock() - started
        started = self._clock()
        await probe.make()  # warm: only the job
        warm = self._clock() - started
        await self._unload(probe.model)
        return {
            "model": probe.model,
            "load_s": round(max(0.0, cold - warm), 2),
            "seconds_per_job": round(warm, 2),
            "tokens_per_s": None,
        }

    async def _load(self, model: str) -> float:
        residency = self._residency()
        started = self._clock()
        if residency is not None and residency.knows(model):
            await residency.ensure(model)
        return self._clock() - started

    async def _unload(self, model: str) -> None:
        residency = self._residency()
        if residency is not None and residency.knows(model):
            await residency.release(model)

    def _apply(self) -> None:
        if self.result is None:
            return
        seconds: dict[str, float] = {}
        for key in ("language", "image", "sound"):
            measured: object = self.result.get(key)
            if isinstance(measured, dict):
                entry = cast(dict[str, Any], measured)
                seconds[str(entry["model"])] = float(entry["seconds_per_job"])
        self._on_result(seconds)

    def _save(self) -> None:
        self._store.parent.mkdir(parents=True, exist_ok=True)
        partial = self._store.with_suffix(".part")
        partial.write_text(json.dumps(self.result, indent=2) + "\n", encoding="utf-8")
        partial.replace(self._store)

    def _load_saved(self) -> None:
        try:
            data = json.loads(self._store.read_text(encoding="utf-8"))
            BenchmarkStatus.model_validate(
                {
                    "contract_version": CONTRACT_VERSION,
                    "state": "done",
                    "result": data,
                    "error": None,
                }
            )
        except (OSError, ValueError):
            return  # never measured, or an unreadable file: measure again
        _, gpu, vram = self._hardware()
        if gpu and (data.get("gpu") != gpu[:128] or data.get("vram_mib") != vram):
            return  # another graphics card: the old values do not hold (review GW1 #97)
        self.result = data
        self.state = "done"
        self._apply()


# --- probes for the real service -------------------------------------------------------------

BENCH_PROMPT = (
    "Describe a quiet morning on a small island in plain sentences: the beach, the stream, "
    "the trees and the light. Keep writing until you are stopped."
)


class OllamaLanguageProbe:
    """One fixed answer of `tokens` tokens; Ollama reports eval_count and eval_duration."""

    def __init__(
        self, base_url: str, client: httpx.AsyncClient, model: str, keep_alive: str
    ) -> None:
        self.model = model
        self._base = base_url.rstrip("/")
        self._client = client
        self._keep_alive = keep_alive

    async def generate(self, tokens: int) -> Generated:
        response = await self._client.post(
            f"{self._base}/api/generate",
            json={
                "model": self.model,
                "prompt": BENCH_PROMPT,
                "stream": False,
                "keep_alive": self._keep_alive,
                "options": {"num_predict": tokens, "seed": 1, "temperature": 0},
            },
            timeout=600,
        )
        response.raise_for_status()
        data: dict[str, Any] = response.json()
        return Generated(int(data.get("eval_count", 0)), float(data.get("eval_duration", 0)) / 1e9)


class HandlerProbe:
    """Runs a test job through the real job handler (image or sound). Every run gets its own
    seed: ComfyUI answers an identical workflow from its cache (live run L713: 1 s)."""

    def __init__(
        self, model: str, handler: Callable[[Any], Awaitable[Any]], job: Callable[[int], Any]
    ) -> None:
        self.model = model
        self._handler = handler
        self._job = job
        self._runs = 0

    async def make(self) -> None:
        self._runs += 1
        await self._handler(self._job(int(time.time()) % 1_000_000 * 10 + self._runs))
