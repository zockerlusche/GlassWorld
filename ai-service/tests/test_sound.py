"""Sound jobs (B2530, D471 D7): mock, check, MOSS handler with a fake worker, /jobs integration."""

from __future__ import annotations

import asyncio
import copy
import json
import math
import struct
import wave
from pathlib import Path
from typing import Any

import httpx
import pytest

from conftest import TEST_HOST, TOKEN, FakeProbes
from glassworld_ai.app import create_app
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.sound import (
    GenerationSettings,
    MockSound,
    MossSound,
    load_generation_settings,
    measure_wav,
)
from glassworld_ai.validation import ContractValidator
from glassworld_contracts.models.generation_job_schema import GenerationJob

REPO = Path(__file__).resolve().parents[2]
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.sound-door.json").read_text(encoding="utf-8")
)
SETTINGS = GenerationSettings(
    min_steps=25, default_steps=50, audible_peak_min=0.05, audible_rms_min=0.001, seed_retries=2
)


def _write(path: Path, seconds: float, amplitude: float) -> None:
    rate = 48000
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(
            b"".join(
                struct.pack("<h", int(amplitude * math.sin(2 * math.pi * 440 * i / rate) * 32767))
                for i in range(int(seconds * rate))
            )
        )


class FakeWorker:
    """Answers like the MOSS worker; silent for the first `silent` seeds."""

    def __init__(self, silent: int) -> None:
        self.silent = silent
        self.requests: list[dict[str, Any]] = []

    async def render(self, request: dict[str, Any], timeout_s: float) -> dict[str, Any]:
        self.requests.append(request)
        quiet = len(self.requests) <= self.silent
        _write(Path(request["out"]), float(request["seconds"]), 0.0 if quiet else 0.4)
        return {"ok": True}


def _job() -> GenerationJob:
    return GenerationJob.model_validate(copy.deepcopy(JOB_DATA))


def test_generation_data_is_read_from_content() -> None:
    data = load_generation_settings(REPO / "content")
    assert data.default_steps >= data.min_steps


def test_measure_finds_level_and_silence(tmp_path: Path) -> None:
    _write(tmp_path / "loud.wav", 1.0, 0.5)
    _write(tmp_path / "quiet.wav", 1.0, 0.0)
    loud = measure_wav(tmp_path / "loud.wav")
    assert abs(loud.seconds - 1.0) < 1e-6
    assert abs(loud.peak - 0.5) < 0.01
    assert abs(loud.loudness_dbfs + 9.03) < 0.1  # sine at half scale
    assert measure_wav(tmp_path / "quiet.wav").loudness_dbfs == -120.0


def test_mock_writes_a_deterministic_checked_file(tmp_path: Path) -> None:
    first = asyncio.run(MockSound(tmp_path, SETTINGS)(_job()))
    data = (tmp_path / first.result["sound"]["file"]).read_bytes()  # type: ignore[index]
    second = asyncio.run(MockSound(tmp_path, SETTINGS)(_job()))
    assert first.result == second.result
    assert (tmp_path / second.result["sound"]["file"]).read_bytes() == data  # type: ignore[index]
    assert first.source == "mock"


def test_moss_retries_with_the_next_seed_and_raises_the_steps(tmp_path: Path) -> None:
    worker = FakeWorker(silent=1)
    job = _job()
    job.payload.sound.steps = 10  # type: ignore[union-attr]
    outcome = asyncio.run(MossSound(worker, tmp_path, SETTINGS, 60)(job))  # type: ignore[arg-type]
    sound: dict[str, Any] = outcome.result["sound"]  # type: ignore[assignment]
    assert sound["attempts"] == 2
    assert sound["seed_used"] == int(job.seed) + 1
    assert [r["steps"] for r in worker.requests] == [25, 25]  # below min_steps: silence (D440)
    assert outcome.source == "local_model"


def test_moss_gives_up_after_the_retries_and_leaves_no_file(tmp_path: Path) -> None:
    worker = FakeWorker(silent=99)
    with pytest.raises(JobFailedError) as failed:
        asyncio.run(MossSound(worker, tmp_path, SETTINGS, 60)(_job()))  # type: ignore[arg-type]
    assert failed.value.code == "invalid_model_output"
    assert len(worker.requests) == SETTINGS.seed_retries + 1
    assert list(tmp_path.glob("*.wav")) == []


def test_sound_jobs_run_over_jobs_with_the_mock(settings: Settings, tmp_path: Path) -> None:
    app = create_app(
        settings.model_copy(update={"decider_backend": "mock", "sound_output_dir": tmp_path}),
        allowed_hosts=frozenset({TEST_HOST}),
        probes=FakeProbes(),
        decider=MockDecider(),
    )

    async def scenario() -> dict[str, Any]:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            assert (await client.post("/jobs", json=JOB_DATA)).status_code == 202
            # Upper bound for a slow CI runner; a passing run takes well under a second.
            loop = asyncio.get_running_loop()
            until = loop.time() + 30
            while loop.time() < until:
                body: dict[str, Any] = (await client.get(f"/jobs/{JOB_DATA['request_id']}")).json()
                if body["state"] in ("done", "failed"):
                    return body
                await asyncio.sleep(0.02)
        raise AssertionError("sound job never finished")

    status = asyncio.run(scenario())
    assert status["state"] == "done"
    assert ContractValidator(settings.schemas_dir).errors("job-status", status) == []
    assert (tmp_path / status["result"]["sound"]["file"]).is_file()


def test_the_worker_never_loads_by_itself(tmp_path: Path) -> None:
    """Only the residency loads models (GW2 review #79): render on an unloaded worker fails."""
    from glassworld_ai.sound import MossWorker
    from glassworld_ai.sound.worker import WorkerError

    worker = MossWorker(tmp_path / "python", tmp_path / "worker.py", tmp_path, tmp_path, 1)
    with pytest.raises(WorkerError):
        asyncio.run(worker.render({}, 1))
    assert not worker.running
