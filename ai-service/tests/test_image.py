"""Image jobs (B7 S5, A1408): tier plan, workflow filling, fake ComfyUI, mock, night run."""

from __future__ import annotations

import asyncio
import json
import re
from pathlib import Path
from typing import Any

import httpx
import pytest

from conftest import TEST_HOST, TOKEN, make_app
from glassworld_ai.config import Settings
from glassworld_ai.image import (
    ComfyImages,
    MockImages,
    fill_workflow,
    load_image_settings,
    png_size,
    render_prompt,
    solid_png,
)
from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.prompts import load_prompt
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.image-vessel.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)
SETTINGS = load_image_settings(REPO / "content")


def _status(outcome: JobOutcome) -> dict[str, Any]:
    return {
        "contract_version": JOB_DATA["contract_version"],
        "job_id": JOB_DATA["request_id"],
        "tick": JOB.tick,
        "kind": "image",
        "state": "done",
        "result": outcome.result,
        "error": None,
        "provenance": {
            "source": outcome.source,
            "model": outcome.model,
            "prompt_version": outcome.prompt_version,
            "seed": JOB.seed,
            "latency_ms": outcome.latency_ms,
            "attempts": outcome.attempts,
        },
    }


def test_the_data_file_has_no_image_ai_on_minimum() -> None:
    assert SETTINGS.by_tier["minimum"] is None
    standard = SETTINGS.by_tier["standard"]
    comfort = SETTINGS.by_tier["comfort"]
    assert standard is not None and standard.reference_image is None
    assert comfort is not None and comfort.reference_image is not None
    assert (REPO / "content" / comfort.reference_image).is_file()
    for plan in (standard, comfort):
        assert (REPO / "tools/comfyui/workflows" / f"{plan.workflow}.api.json").is_file()


def test_fill_workflow_keeps_numbers_and_refuses_open_placeholders() -> None:
    template = {
        "1": {"inputs": {"seed": "{seed}", "text": "{prompt}", "fixed": 8, "ref": ["2", 0]}}
    }
    filled = fill_workflow(template, {"seed": 7, "prompt": "a bowl"})
    assert filled == {"1": {"inputs": {"seed": 7, "text": "a bowl", "fixed": 8, "ref": ["2", 0]}}}
    with pytest.raises(ValueError, match="seed"):
        fill_workflow(template, {"prompt": "a bowl"})


def test_both_workflow_templates_fill_completely() -> None:
    for name, extra in (("z-image-turbo-t2i", {}), ("qwen-image-edit-style", {"bild": "ref.png"})):
        template = json.loads(
            (REPO / f"tools/comfyui/workflows/{name}.api.json").read_text("utf-8")
        )
        values: dict[str, object] = {"prompt": "x", "seed": 1, "breite": 512, "hoehe": 512, **extra}
        assert not re.search(r'"\{[a-z_]+\}"', json.dumps(fill_workflow(template, values)))


def test_prompt_has_subject_and_style(settings: Settings) -> None:
    template = load_prompt(settings.prompts_dir, "image/object", 1)
    text = render_prompt(template, JOB.payload.image)
    assert text.startswith("a round clay water vessel with a short handle.")
    assert "comic" in text and "$" not in text
    other = JOB.payload.image.model_copy(update={"style": "pixel_art"})  # type: ignore[union-attr]
    with pytest.raises(JobFailedError) as error:
        render_prompt(template, other)
    assert (error.value.code, error.value.retryable) == ("bad_request", False)


class FakeComfy:
    """Answers like ComfyUI: /prompt, /history (first still running), /view, /upload, /queue."""

    def __init__(self, picture: bytes, refuse: bool = False, never_done: bool = False) -> None:
        self.picture = picture
        self.refuse = refuse
        self.never_done = never_done
        self.calls: list[str] = []
        self.workflow: dict[str, Any] = {}
        self.polls = 0

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.calls.append(f"{request.method} {request.url.path}")
        path = request.url.path
        if path == "/prompt":
            if self.refuse:
                return httpx.Response(400, json={"error": "missing model", "node_errors": {}})
            self.workflow = json.loads(request.content)["prompt"]
            return httpx.Response(200, json={"prompt_id": "p1", "number": 1})
        if path == "/history/p1":
            self.polls += 1
            if self.polls < 2 or self.never_done:
                return httpx.Response(200, json={})
            outputs = {
                "13": {
                    "images": [{"filename": "x.png", "subfolder": "glassworld", "type": "output"}]
                }
            }
            return httpx.Response(
                200,
                json={
                    "p1": {
                        "status": {"status_str": "success", "completed": True},
                        "outputs": outputs,
                    }
                },
            )
        if path == "/view":
            return httpx.Response(200, content=self.picture)
        if path == "/upload/image":
            return httpx.Response(
                200,
                json={"name": "style-reference-warm-comic.png", "subfolder": "", "type": "input"},
            )
        if path == "/queue":
            return httpx.Response(200, json={})
        return httpx.Response(404)


def _handler(
    settings: Settings, fake: FakeComfy, tier: str, ensured: list[str], timeout_s: float = 30
) -> ComfyImages:
    async def ensure(model_id: str) -> None:
        ensured.append(model_id)

    return ComfyImages(
        base_url="http://comfy.test",
        client=httpx.AsyncClient(transport=httpx.MockTransport(fake)),
        settings=SETTINGS,
        workflows_dir=REPO / "tools/comfyui/workflows",
        prompts_dir=settings.prompts_dir,
        content_dir=REPO / "content",
        output_dir=settings.log_dir.parent / "images",
        tier=lambda: tier,
        ensure_model=ensure,
        poll_s=0.001,
        timeout_s=timeout_s,
    )


def test_standard_runs_z_image_and_stores_the_png(settings: Settings) -> None:
    fake = FakeComfy(solid_png(512, 512, b"\x80\x60\x40"))
    ensured: list[str] = []
    handler = _handler(settings, fake, "standard", ensured)
    outcome = asyncio.run(handler(JOB))
    assert ensured == ["z-image-turbo"]
    assert "POST /upload/image" not in fake.calls
    sampler = next(n for n in fake.workflow.values() if n["class_type"] == "KSampler")
    assert sampler["inputs"]["seed"] == JOB.seed
    latent = next(n for n in fake.workflow.values() if n["class_type"] == "EmptySD3LatentImage")
    assert (latent["inputs"]["width"], latent["inputs"]["height"]) == (512, 512)
    save = next(n for n in fake.workflow.values() if n["class_type"] == "SaveImage")
    assert save["inputs"]["filename_prefix"] == f"glassworld/job-{JOB_DATA['request_id']}"
    validator = ContractValidator(settings.schemas_dir)
    assert validator.errors("job-status", _status(outcome)) == []
    result: Any = outcome.result["image"]
    stored = settings.log_dir.parent / "images" / str(result["file"])
    assert png_size(stored.read_bytes()) == (512, 512)
    assert outcome.prompt_version == "image/object.v1"


def test_comfort_uploads_the_style_reference_and_makes_its_model_resident(
    settings: Settings,
) -> None:
    fake = FakeComfy(solid_png(512, 512, b"\x80\x60\x40"))
    ensured: list[str] = []
    outcome = asyncio.run(_handler(settings, fake, "comfort", ensured)(JOB))
    assert ensured == ["qwen-image-edit-2511"]
    assert "POST /upload/image" in fake.calls
    loader = next(n for n in fake.workflow.values() if n["class_type"] == "LoadImage")
    assert loader["inputs"]["image"] == "style-reference-warm-comic.png"
    assert outcome.result["image"]["workflow"] == "qwen-image-edit-style"  # type: ignore[index]


@pytest.mark.parametrize(
    ("tier", "fake", "code", "retryable"),
    [
        ("minimum", FakeComfy(b""), "model_unavailable", False),
        ("standard", FakeComfy(solid_png(256, 256, b"\x00\x00\x00")), "invalid_model_output", True),
        ("standard", FakeComfy(b"not a png"), "invalid_model_output", True),
        ("standard", FakeComfy(b"", refuse=True), "model_unavailable", False),
    ],
)
def test_failures(
    settings: Settings, tier: str, fake: FakeComfy, code: str, retryable: bool
) -> None:
    with pytest.raises(JobFailedError) as error:
        asyncio.run(_handler(settings, fake, tier, [])(JOB))
    assert (error.value.code, error.value.retryable) == (code, retryable)


def test_a_timeout_takes_the_run_out_of_the_queue_but_never_interrupts(settings: Settings) -> None:
    fake = FakeComfy(b"", never_done=True)
    with pytest.raises(JobFailedError) as error:
        asyncio.run(_handler(settings, fake, "standard", [], timeout_s=0.05)(JOB))
    assert error.value.code == "timeout"
    assert "POST /queue" in fake.calls
    assert "POST /interrupt" not in fake.calls


def test_mock_honours_the_tier(settings: Settings) -> None:
    out = settings.log_dir.parent / "images"
    outcome = asyncio.run(MockImages(SETTINGS, settings.prompts_dir, out, lambda: "standard")(JOB))
    assert outcome.source == "mock"
    with pytest.raises(JobFailedError):
        asyncio.run(MockImages(SETTINGS, settings.prompts_dir, out, lambda: "minimum")(JOB))


def test_image_jobs_wait_for_the_night_and_then_run(settings: Settings) -> None:
    app = make_app(settings.model_copy(update={"decider_backend": "mock", "image_backend": "mock"}))
    validator = ContractValidator(settings.schemas_dir)

    async def scenario() -> None:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            accepted = await client.post("/jobs", json=JOB_DATA)
            assert accepted.json()["state"] == "deferred"
            night = {"contract_version": "0.2", "phase": "night", "tick": JOB.tick + 1}
            assert (await client.post("/phase", json=night)).status_code == 200
            status: dict[str, Any] = {}
            for _ in range(200):
                status = (await client.get(f"/jobs/{JOB_DATA['request_id']}")).json()
                if status["state"] == "done":
                    break
                await asyncio.sleep(0.01)
            assert status["state"] == "done", status["error"]
            assert validator.errors("job-status", status) == []

    asyncio.run(scenario())
