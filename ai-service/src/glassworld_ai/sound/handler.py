"""Sound job handlers: the MOSS worker or a deterministic mock (B2530, D471 D7)."""

from __future__ import annotations

import array
import hashlib
import json
import math
import time
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.sound.check import SoundStats, measure_wav
from glassworld_ai.sound.worker import MossWorker, WorkerError
from glassworld_contracts.models.generation_job_schema import GenerationJob

SEED_LIMIT = 2**31


@dataclass(frozen=True)
class GenerationSettings:
    """content/sound/generation.json (placeholders from measurement report D440)."""

    min_steps: int
    default_steps: int
    audible_peak_min: float
    audible_rms_min: float
    seed_retries: int


def load_generation_settings(content_dir: Path) -> GenerationSettings:
    data: dict[str, Any] = json.loads(
        (content_dir / "sound" / "generation.json").read_text(encoding="utf-8")
    )
    return GenerationSettings(
        int(data["min_steps"]),
        int(data["default_steps"]),
        float(data["audible_peak_min"]),
        float(data["audible_rms_min"]),
        int(data["seed_retries"]),
    )


def file_name(job_id: str) -> str:
    """Name by pattern in the output folder (no path, GW3 #18815)."""
    return f"snd-{job_id.lower()}.wav"


def problems(stats: SoundStats, seconds: float, settings: GenerationSettings) -> list[str]:
    """Why a result is not usable (empty = usable)."""
    found: list[str] = []
    if abs(stats.seconds - seconds) > 0.1:
        found.append(f"length {stats.seconds:.2f} s instead of {seconds:.2f} s")
    if stats.peak < settings.audible_peak_min or stats.rms < settings.audible_rms_min:
        found.append("silent")
    if stats.peak >= 0.999:
        found.append("clipped")
    return found


def _payload(job: GenerationJob) -> Any:
    sound = job.payload.sound
    if sound is None:
        raise JobFailedError("bad_request", "sound job without sound payload", retryable=False)
    return sound


def _result(name: str, stats: SoundStats, seed: int, attempts: int) -> dict[str, object]:
    return {
        "sound": {
            "file": name,
            "seconds": round(stats.seconds, 3),
            "seed_used": seed,
            "attempts": attempts,
            "peak_dbfs": round(stats.peak_dbfs, 2),
            "loudness_dbfs": round(stats.loudness_dbfs, 2),
        }
    }


class MossSound:
    """Renders with the MOSS worker; retries with the next seed until a result passes the check."""

    def __init__(
        self,
        worker: MossWorker,
        output_dir: Path,
        settings: GenerationSettings,
        render_timeout_s: float,
    ) -> None:
        self._worker = worker
        self._output_dir = output_dir
        self._settings = settings
        self._timeout_s = render_timeout_s

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        sound = _payload(job)
        steps = max(int(sound.steps), self._settings.min_steps)  # below: silence (D440)
        self._output_dir.mkdir(parents=True, exist_ok=True)
        name = file_name(str(job.request_id))
        target = self._output_dir / name
        started = time.perf_counter()
        last: list[str] = []
        for attempt in range(self._settings.seed_retries + 1):
            seed = (int(job.seed) + attempt) % SEED_LIMIT
            try:
                answer = await self._worker.render(
                    {
                        "prompt": sound.description,
                        "seconds": float(sound.seconds),
                        "loop": str(
                            sound.kind.value if hasattr(sound.kind, "value") else sound.kind
                        )
                        == "loop",
                        "steps": steps,
                        "seed": seed,
                        "out": str(target),
                    },
                    self._timeout_s,
                )
            except WorkerError as error:
                raise JobFailedError("model_unavailable", str(error), retryable=True) from error
            if not answer.get("ok"):
                last = [str(answer.get("error", "worker failed"))]
                continue
            stats = measure_wav(target)
            last = problems(stats, float(sound.seconds), self._settings)
            if not last:
                return JobOutcome(
                    _result(name, stats, seed, attempt + 1),
                    "local_model",
                    "moss-soundeffect-v2.0",
                    "sound/moss.v1",
                    int((time.perf_counter() - started) * 1000),
                    attempt + 1,
                )
        target.unlink(missing_ok=True)
        raise JobFailedError(
            "invalid_model_output", f"no usable sound: {'; '.join(last)}", retryable=False
        )


class MockSound:
    """Deterministic stand-in: a soft tone from the job seed (no model, tests and CI)."""

    def __init__(self, output_dir: Path, settings: GenerationSettings) -> None:
        self._output_dir = output_dir
        self._settings = settings

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        sound = _payload(job)
        self._output_dir.mkdir(parents=True, exist_ok=True)
        name = file_name(str(job.request_id))
        target = self._output_dir / name
        seed = int(job.seed) % SEED_LIMIT
        digest = hashlib.sha256(f"{seed}/{sound.description}".encode()).digest()
        frequency = 220 + digest[0] * 2
        rate = 48000
        count = int(float(sound.seconds) * rate)
        with wave.open(str(target), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(rate)
            ramp = 0.02 * rate
            step = 2 * math.pi * frequency / rate
            samples = array.array(
                "h",
                (
                    int(9830 * min(1.0, i / ramp, (count - i) / ramp) * math.sin(step * i))
                    for i in range(count)
                ),
            )
            wav.writeframes(samples.tobytes())
        stats = measure_wav(target)
        return JobOutcome(_result(name, stats, seed, 1), "mock", "mock", None, 0, 1)
