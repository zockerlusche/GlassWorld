"""Image to 3D (B7 S9, A1412): capability flag per tier, stand-in GLB, input check."""

from __future__ import annotations

import asyncio
import json
import struct
from pathlib import Path
from typing import Any

import pytest

from glassworld_ai.config import Settings
from glassworld_ai.jobs import JobFailedError, JobOutcome
from glassworld_ai.model3d import Model3dJobs, Model3dPlan, box_glb, load_model3d_settings
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import GenerationJob

REPO = Path(__file__).resolve().parents[2]
JOB_DATA: dict[str, Any] = json.loads(
    (REPO / "contracts/examples/valid/generation-job.model3d-vessel.json").read_text("utf-8")
)
JOB = GenerationJob.model_validate(JOB_DATA)


def _handler(tmp_path: Path, tier: str, plan: Model3dPlan | None = None) -> Model3dJobs:
    images = tmp_path / "images"
    images.mkdir(exist_ok=True)
    (images / JOB_DATA["payload"]["model3d"]["image_file"]).write_bytes(b"png")
    return Model3dJobs(
        plan or load_model3d_settings(REPO / "content"), images, tmp_path / "models", lambda: tier
    )


def _status(outcome: JobOutcome) -> dict[str, Any]:
    return {
        "contract_version": JOB_DATA["contract_version"],
        "job_id": JOB_DATA["request_id"],
        "tick": JOB.tick,
        "kind": "model3d",
        "state": "done",
        "result": outcome.result,
        "error": None,
        "provenance": {"source": outcome.source, "model": outcome.model, "prompt_version": None,
                       "seed": JOB.seed, "latency_ms": 0, "attempts": 1},
    }  # fmt: skip


def test_only_comfort_has_image_to_3d() -> None:
    plan = load_model3d_settings(REPO / "content")
    assert plan.by_tier == {"minimum": None, "standard": None, "comfort": "mock"}


def test_the_box_is_a_valid_glb() -> None:
    data, triangles = box_glb()
    magic, version, length = struct.unpack("<4sII", data[:12])
    assert (magic, version, length, triangles) == (b"glTF", 2, len(data), 12)
    json_length, kind = struct.unpack("<I4s", data[12:20])
    gltf = json.loads(data[20 : 20 + json_length])
    assert kind == b"JSON" and gltf["asset"]["version"] == "2.0"
    assert gltf["accessors"][1]["count"] == 36


def test_comfort_makes_the_stand_in(tmp_path: Path, settings: Settings) -> None:
    outcome = asyncio.run(_handler(tmp_path, "comfort")(JOB))
    assert ContractValidator(settings.schemas_dir).errors("job-status", _status(outcome)) == []
    result: Any = outcome.result["model3d"]
    assert (tmp_path / "models" / str(result["file"])).read_bytes()[:4] == b"glTF"


@pytest.mark.parametrize(
    ("tier", "plan", "code"),
    [
        ("standard", None, "model_unavailable"),
        ("minimum", None, "model_unavailable"),
        (
            "comfort",
            Model3dPlan({"minimum": None, "standard": None, "comfort": "trellis2"}, 4000),
            "model_unavailable",
        ),
        (
            "comfort",
            Model3dPlan({"minimum": None, "standard": None, "comfort": "mock"}, 6),
            "bad_request",
        ),
    ],
)
def test_no_retry_where_it_cannot_work(
    tmp_path: Path, tier: str, plan: Model3dPlan | None, code: str
) -> None:
    with pytest.raises(JobFailedError) as error:
        asyncio.run(_handler(tmp_path, tier, plan)(JOB))
    assert (error.value.code, error.value.retryable) == (code, False)


def test_a_missing_picture_is_a_bad_request(tmp_path: Path) -> None:
    handler = Model3dJobs(
        load_model3d_settings(REPO / "content"), tmp_path, tmp_path, lambda: "comfort"
    )
    with pytest.raises(JobFailedError) as error:
        asyncio.run(handler(JOB))
    assert error.value.code == "bad_request"
