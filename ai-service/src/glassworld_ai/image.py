"""Image jobs (B7 R5/R7, A1408): a picture for an invented thing, made by the local image model.

Tier standard: z-image-turbo through ComfyUI with the style in the prompt; tier comfort:
qwen-image-edit with the style reference D355 (TL #18681). Which workflow a tier uses is data
(content/image/generation.json); a tier without one has no image AI (capability flag, B7 R5) and
the thing keeps the colours of the construction kit. The workflow templates live in
tools/comfyui/workflows. Every result is checked (PNG, size) before the job is done.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import struct
import time
import zlib
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any, cast

import httpx

from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.prompts import PromptTemplate, load_prompt
from glassworld_ai.words import plain_id
from glassworld_contracts import GenerationJob
from glassworld_contracts.models.image_generation_schema import ImageGeneration

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
CLIENT_ID = "glassworld-ai"


@dataclass(frozen=True)
class TierPlan:
    workflow: str
    model: str
    prompt: str
    prompt_version: int
    reference_image: str | None


@dataclass(frozen=True)
class ImageSettings:
    """content/image/generation.json (placeholders until Marcel decides)."""

    by_tier: dict[str, TierPlan | None]
    sizes: dict[str, tuple[int, int]]


def load_image_settings(content_dir: Path) -> ImageSettings:
    data = ImageGeneration.model_validate(
        json.loads((content_dir / "image" / "generation.json").read_text(encoding="utf-8"))
    )
    tiers: dict[str, TierPlan | None] = {}
    for tier in ("minimum", "standard", "comfort"):
        plan = getattr(data.by_tier, tier)
        tiers[tier] = (
            None
            if plan is None
            else TierPlan(
                plan.workflow, plan.model, plan.prompt, plan.prompt_version, plan.reference_image
            )
        )
    sizes = {
        "icon": (data.sizes.icon.width, data.sizes.icon.height),
        "texture": (data.sizes.texture.width, data.sizes.texture.height),
    }
    return ImageSettings(tiers, sizes)


def file_name(job_id: str) -> str:
    """Name by pattern in the output folder (no path)."""
    return f"img-{job_id.lower()}.png"


def png_size(data: bytes) -> tuple[int, int]:
    """Width and height from the PNG header; raises ValueError if it is no PNG."""
    if len(data) < 24 or not data.startswith(PNG_SIGNATURE) or data[12:16] != b"IHDR":
        raise ValueError("not a PNG file")
    width, height = struct.unpack(">II", data[16:24])
    return int(width), int(height)


def fill_workflow(template: dict[str, Any], values: dict[str, object]) -> dict[str, Any]:
    """Replace the placeholders ("{prompt}", "{seed}", ...) of an API workflow by their values.

    A placeholder is a whole string value; numbers stay numbers. A placeholder without a value is
    an error (a broken template must not reach the model).
    """

    def walk(node: object) -> object:
        if isinstance(node, dict):
            return {k: walk(v) for k, v in cast(dict[str, object], node).items()}
        if isinstance(node, list):
            return [walk(v) for v in cast(list[object], node)]
        if isinstance(node, str) and node.startswith("{") and node.endswith("}"):
            key = node[1:-1]
            if key not in values:
                raise ValueError(f"workflow placeholder {node} has no value")
            return values[key]
        return node

    return cast(dict[str, Any], walk(template))


def _fail(code: str, message: str, *, retryable: bool) -> JobFailedError:
    return JobFailedError(code, message, retryable=retryable)


def _payload(job: GenerationJob) -> Any:
    image = job.payload.image
    if image is None:
        raise _fail("bad_request", "image job without image payload", retryable=False)
    return image


def _settings_table(template: PromptTemplate, key: str) -> dict[str, str]:
    raw: object = template.settings.get(key)
    if not isinstance(raw, dict):
        raise _fail("internal", f"{template.version_id}: settings.{key} missing", retryable=False)
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def render_prompt(template: PromptTemplate, image: Any) -> str:
    purpose = plain_id(image.purpose)
    style = plain_id(image.style)
    styles = _settings_table(template, "styles")
    if style not in styles:
        raise _fail("bad_request", f"unknown image style {style}", retryable=False)
    framing = _settings_table(template, "framing")[purpose]
    rendered = template.render(
        {"subject": str(image.subject), "framing": framing, "style": styles[style]}
    )
    return " ".join(rendered.user.split())


class ImageJobs:
    """Common part of the ComfyUI handler and the mock: tier plan, prompt, file, result."""

    def __init__(
        self,
        settings: ImageSettings,
        prompts_dir: Path,
        output_dir: Path,
        tier: Callable[[], str | None],
    ) -> None:
        self._settings = settings
        self._prompts_dir = prompts_dir
        self._output_dir = output_dir
        self._tier = tier
        self._templates: dict[tuple[str, int], PromptTemplate] = {}

    def plan(self) -> TierPlan:
        tier = self._tier()
        plan = self._settings.by_tier.get(tier) if tier is not None else None
        if plan is None:
            # B7 R5: this hardware has no image AI; the thing keeps the construction-kit look.
            raise _fail(
                "model_unavailable",
                f"no image AI on tier {tier or 'below minimum'}",
                retryable=False,
            )
        return plan

    def template(self, plan: TierPlan) -> PromptTemplate:
        key = (plan.prompt, plan.prompt_version)
        if key not in self._templates:
            self._templates[key] = load_prompt(self._prompts_dir, *key)
        return self._templates[key]

    def store(self, job: GenerationJob, data: bytes, width: int, height: int) -> str:
        try:
            size = png_size(data)
        except ValueError as error:
            raise _fail("invalid_model_output", str(error), retryable=True) from error
        if size != (width, height):
            raise _fail(
                "invalid_model_output",
                f"picture is {size[0]}x{size[1]} instead of {width}x{height}",
                retryable=True,
            )
        self._output_dir.mkdir(parents=True, exist_ok=True)
        name = file_name(plain_id(job.request_id))
        partial = self._output_dir / (name + ".part")
        partial.write_bytes(data)
        partial.replace(self._output_dir / name)
        return name

    @staticmethod
    def result(name: str, image: Any, seed: int, workflow: str) -> dict[str, object]:
        return {
            "image": {
                "file": name,
                "width": int(image.width),
                "height": int(image.height),
                "seed_used": seed,
                "workflow": workflow,
                "style": plain_id(image.style),
            }
        }


class ComfyImages(ImageJobs):
    """Runs the tier's workflow in ComfyUI and fetches the picture."""

    def __init__(
        self,
        *,
        base_url: str,
        client: httpx.AsyncClient,
        settings: ImageSettings,
        workflows_dir: Path,
        prompts_dir: Path,
        content_dir: Path,
        output_dir: Path,
        tier: Callable[[], str | None],
        ensure_model: Callable[[str], Awaitable[None]],
        poll_s: float,
        timeout_s: float,
    ) -> None:
        super().__init__(settings, prompts_dir, output_dir, tier)
        self._base = base_url.rstrip("/")
        self._client = client
        self._workflows_dir = workflows_dir
        self._content_dir = content_dir
        self._ensure_model = ensure_model
        self._poll_s = poll_s
        self._timeout_s = timeout_s

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        started = time.monotonic()
        image = _payload(job)
        plan = self.plan()
        template = self.template(plan)
        text = render_prompt(template, image)
        seed = int(job.seed)
        # The planner made the kind's model resident; a tier with another model (comfort) makes
        # its own resident here, so the residency's bookkeeping stays right (B7 S2).
        await self._ensure_model(plan.model)
        try:
            async with asyncio.timeout(self._timeout_s):
                values: dict[str, object] = {
                    "prompt": text,
                    "seed": seed,
                    "breite": int(image.width),
                    "hoehe": int(image.height),
                }
                if plan.reference_image is not None:
                    values["bild"] = await self._upload(self._content_dir / plan.reference_image)
                workflow = fill_workflow(self._workflow(plan.workflow), values)
                _name_outputs(workflow, plain_id(job.request_id))
                data = await self._run(workflow)
        except TimeoutError as error:
            raise _fail("timeout", "image model did not finish in time", retryable=True) from error
        except httpx.HTTPError as error:
            raise _fail(
                "model_unavailable", f"ComfyUI: {type(error).__name__}", retryable=True
            ) from error
        name = self.store(job, data, int(image.width), int(image.height))
        return JobOutcome(
            self.result(name, image, seed, plan.workflow),
            "comfyui",
            plan.model,
            template.version_id,
            int((time.monotonic() - started) * 1000),
            1,
        )

    def _workflow(self, name: str) -> dict[str, Any]:
        path = self._workflows_dir / f"{name}.api.json"
        try:
            return cast(dict[str, Any], json.loads(path.read_text(encoding="utf-8")))
        except (OSError, json.JSONDecodeError) as error:
            raise _fail("internal", f"workflow {name}: {error}", retryable=False) from error

    async def _upload(self, path: Path) -> str:
        """Put the style reference into ComfyUI's input folder (same name, overwritten)."""
        response = await self._client.post(
            f"{self._base}/upload/image",
            files={"image": (path.name, path.read_bytes(), "image/png")},
            data={"overwrite": "true", "type": "input"},
            timeout=30,
        )
        if response.status_code >= 400:
            raise _fail(
                "model_unavailable", f"ComfyUI upload {response.status_code}", retryable=True
            )
        return str(response.json()["name"])

    async def _run(self, workflow: dict[str, Any]) -> bytes:
        response = await self._client.post(
            f"{self._base}/prompt", json={"prompt": workflow, "client_id": CLIENT_ID}, timeout=30
        )
        if response.status_code >= 400:
            # A broken workflow or a missing model file: no retry helps (log the node errors).
            raise _fail(
                "model_unavailable",
                f"ComfyUI refused the workflow: {response.text[:300]}",
                retryable=False,
            )
        prompt_id = str(response.json()["prompt_id"])
        try:
            entry = await self._wait(prompt_id)
        except asyncio.CancelledError:
            await self._forget(prompt_id)
            raise
        except TimeoutError:
            await self._forget(prompt_id)
            raise
        return await self._fetch(entry)

    async def _wait(self, prompt_id: str) -> dict[str, Any]:
        while True:
            response = await self._client.get(f"{self._base}/history/{prompt_id}", timeout=30)
            response.raise_for_status()
            history = cast(dict[str, Any], response.json())
            entry = history.get(prompt_id)
            if entry is not None:
                status = cast(dict[str, Any], entry.get("status") or {})
                if status.get("status_str") == "error":
                    raise _fail("model_unavailable", "ComfyUI run failed", retryable=True)
                if status.get("completed", True):
                    return cast(dict[str, Any], entry)
            await asyncio.sleep(self._poll_s)

    async def _fetch(self, entry: dict[str, Any]) -> bytes:
        outputs = cast(dict[str, Any], entry.get("outputs") or {})
        for output in outputs.values():
            images = cast(list[dict[str, Any]], cast(dict[str, Any], output).get("images") or [])
            if images:
                first = images[0]
                response = await self._client.get(
                    f"{self._base}/view",
                    params={
                        "filename": str(first.get("filename", "")),
                        "subfolder": str(first.get("subfolder", "")),
                        "type": str(first.get("type", "output")),
                    },
                    timeout=60,
                )
                response.raise_for_status()
                return response.content
        raise _fail("invalid_model_output", "ComfyUI run gave no picture", retryable=True)

    async def _forget(self, prompt_id: str) -> None:
        """Take our run out of ComfyUI's queue (best effort). Never /interrupt: that would stop
        whatever runs now, maybe someone else's run."""
        try:
            await self._client.post(f"{self._base}/queue", json={"delete": [prompt_id]}, timeout=10)
        except httpx.HTTPError:
            return


def _name_outputs(workflow: dict[str, Any], job_id: str) -> None:
    """ComfyUI saves into its own output folder; give our runs their own recognisable prefix."""
    for node in workflow.values():
        if isinstance(node, dict) and cast(dict[str, Any], node).get("class_type") == "SaveImage":
            inputs = cast(dict[str, Any], cast(dict[str, Any], node).get("inputs", {}))
            inputs["filename_prefix"] = f"glassworld/job-{job_id}"


class MockImages(ImageJobs):
    """Deterministic stand-in without a model: a plain picture in a colour from the seed."""

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        image = _payload(job)
        plan = self.plan()  # the capability flag holds for the mock as well
        render_prompt(self.template(plan), image)  # unknown styles fail like with the model
        seed = int(job.seed)
        colour = hashlib.sha256(f"{seed}/{image.subject}".encode()).digest()[:3]
        data = solid_png(int(image.width), int(image.height), colour)
        name = self.store(job, data, int(image.width), int(image.height))
        return JobOutcome(self.result(name, image, seed, plan.workflow), "mock", "mock", None, 0, 1)


def solid_png(width: int, height: int, rgb: bytes) -> bytes:
    """A minimal RGB PNG of one colour (standard library only)."""

    def chunk(kind: bytes, body: bytes) -> bytes:
        return (
            struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
        )

    row = b"\x00" + rgb * width
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return (
        PNG_SIGNATURE
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(row * height, 9))
        + chunk(b"IEND", b"")
    )
