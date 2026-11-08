"""Image to 3D jobs (B7 R9, ADR-0004, A1412): interface and stand-in only.

TRELLIS.2 needs 16-24 GB VRAM and Linux, so it is an optional comfort-tier backend that is not
wired yet. Which tier may make 3D models is data (content/model3d/settings.json, capability
flag); on every other tier the job fails as not retryable and the core keeps the
construction-kit builder. The stand-in ("mock") writes a small valid binary glTF (a box) so the
game side can be built and tested.
"""

from __future__ import annotations

import json
import struct
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.words import plain_id
from glassworld_contracts import GenerationJob
from glassworld_contracts.models.model3d_settings_schema import Model3dSettings

GLB_MAGIC = b"glTF"


@dataclass(frozen=True)
class Model3dPlan:
    by_tier: dict[str, str | None]
    max_triangles: int


def load_model3d_settings(content_dir: Path) -> Model3dPlan:
    data = Model3dSettings.model_validate(
        json.loads((content_dir / "model3d" / "settings.json").read_text(encoding="utf-8"))
    )
    tiers: dict[str, str | None] = {}
    for tier in ("minimum", "standard", "comfort"):
        value = getattr(data.by_tier, tier)
        tiers[tier] = None if value is None else str(getattr(value, "value", value))
    return Model3dPlan(tiers, data.max_triangles)


def file_name(job_id: str) -> str:
    return f"mdl-{job_id.lower()}.glb"


def box_glb() -> tuple[bytes, int]:
    """A unit box as binary glTF 2.0 (positions + indices); returns (bytes, triangles)."""
    corners = [(x, y, z) for x in (-0.5, 0.5) for y in (0.0, 1.0) for z in (-0.5, 0.5)]
    faces = [
        (0, 1, 3), (0, 3, 2), (4, 6, 7), (4, 7, 5), (0, 4, 5), (0, 5, 1),
        (2, 3, 7), (2, 7, 6), (0, 2, 6), (0, 6, 4), (1, 5, 7), (1, 7, 3),
    ]  # fmt: skip
    positions = b"".join(struct.pack("<3f", *c) for c in corners)
    indices = b"".join(struct.pack("<3H", *f) for f in faces)
    indices += b"\x00" * (-len(indices) % 4)
    binary = positions + indices
    gltf = {
        "asset": {"version": "2.0", "generator": "glassworld-ai model3d stand-in"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0}, "indices": 1}]}],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": len(positions), "target": 34962},
            {"buffer": 0, "byteOffset": len(positions), "byteLength": len(faces) * 6,
             "target": 34963},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 8, "type": "VEC3",
             "min": [-0.5, 0.0, -0.5], "max": [0.5, 1.0, 0.5]},
            {"bufferView": 1, "componentType": 5123, "count": len(faces) * 3, "type": "SCALAR"},
        ],
    }  # fmt: skip
    text = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    text += b" " * (-len(text) % 4)
    chunks = (
        struct.pack("<I4s", len(text), b"JSON")
        + text
        + struct.pack("<I4s", len(binary), b"BIN\x00")
        + binary
    )
    return GLB_MAGIC + struct.pack("<II", 2, 12 + len(chunks)) + chunks, len(faces)


def _payload(job: GenerationJob) -> Any:
    payload = job.payload.model3d
    if payload is None:
        raise JobFailedError("bad_request", "model3d job without model3d payload", retryable=False)
    return payload


class Model3dJobs:
    """Capability flag per tier, input check, stand-in output."""

    def __init__(
        self,
        plan: Model3dPlan,
        image_dir: Path,
        output_dir: Path,
        tier: Callable[[], str | None],
    ) -> None:
        self._plan = plan
        self._image_dir = image_dir
        self._output_dir = output_dir
        self._tier = tier

    async def __call__(self, job: GenerationJob) -> JobOutcome:
        payload = _payload(job)
        tier = self._tier()
        backend = self._plan.by_tier.get(tier) if tier is not None else None
        if backend is None:
            raise JobFailedError(
                "model_unavailable",
                f"no image-to-3D on tier {tier or 'below minimum'} (construction kit, ADR-0004)",
                retryable=False,
            )
        if backend != "mock":
            raise JobFailedError(
                "model_unavailable",
                f"3D backend {backend} is not wired yet (B7 S9)",
                retryable=False,
            )
        if not (self._image_dir / str(payload.image_file)).is_file():
            raise JobFailedError(
                "bad_request", f"picture {payload.image_file} not found", retryable=False
            )
        data, triangles = box_glb()
        limit = min(int(payload.max_triangles), self._plan.max_triangles)
        if triangles > limit:
            raise JobFailedError(
                "bad_request", f"{triangles} triangles above {limit}", retryable=False
            )
        self._output_dir.mkdir(parents=True, exist_ok=True)
        name = file_name(plain_id(job.request_id))
        partial = self._output_dir / (name + ".part")
        partial.write_bytes(data)
        partial.replace(self._output_dir / name)
        result: dict[str, object] = {
            "model3d": {"file": name, "triangles": triangles, "seed_used": int(job.seed)}
        }
        return JobOutcome(result, "mock", "mock", None, 0, 1)
