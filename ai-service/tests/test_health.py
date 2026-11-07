# jsonschema/referencing ship incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownVariableType=false
# pyright: reportUnknownArgumentType=false
from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

from conftest import OLLAMA_DOWN, FakeProbes, get, make_app
from glassworld_ai.config import Settings
from glassworld_ai.hardware import HardwareInfo
from glassworld_ai.probes import DOWN, DependencyState, OllamaState
from glassworld_contracts import HealthStatus

SCHEMAS = Path(__file__).resolve().parents[2] / "contracts" / "schemas"


def _validate_against_schema(body: object) -> None:
    resources: list[tuple[str, Resource[Any]]] = []
    for path in SCHEMAS.glob("*.schema.json"):
        schema = json.loads(path.read_text(encoding="utf-8"))
        resources.append((schema["$id"], Resource.from_contents(schema)))
    registry: Registry[Any] = Registry().with_resources(resources)
    schema = json.loads((SCHEMAS / "health-status.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator(schema, registry=registry).validate(body)


def test_health_ready_when_ollama_and_model_are_there(settings: Settings) -> None:
    response = get(make_app(settings), "/health")
    assert response.status_code == 200
    body = response.json()
    _validate_against_schema(body)
    health = HealthStatus.model_validate(body)
    assert health.status == "ready"
    assert health.decider_model.name == settings.decider_model
    assert [d.name for d in health.dependencies] == ["comfyui", "ollama"]
    assert health.hardware.tier == "standard"
    assert [(g.name, g.vram_mib) for g in health.hardware.gpus] == [
        ("NVIDIA GeForce RTX 3080", 10240)
    ]


def test_health_reports_no_tier_without_gpu(settings: Settings) -> None:
    no_gpu = HardwareInfo(gpus=(), cpu_name=None, logical_cpus=4, ram_mib=8192)
    body = get(make_app(settings, hardware=no_gpu), "/health").json()
    _validate_against_schema(body)
    assert body["hardware"]["tier"] is None
    assert body["hardware"]["gpus"] == []


def test_health_degraded_when_ollama_is_down(settings: Settings) -> None:
    probes = FakeProbes(ollama_state=OLLAMA_DOWN, comfyui_state=DOWN)
    body = get(make_app(settings, probes), "/health").json()
    _validate_against_schema(body)
    assert body["status"] == "degraded"
    assert all(d["status"] == "down" and d["version"] is None for d in body["dependencies"])


def test_health_degraded_when_model_is_missing(settings: Settings) -> None:
    probes = FakeProbes(
        ollama_state=OllamaState(
            service=DependencyState(up=True, version="0.40.14"),
            model_available=False,
            model_loaded=False,
        )
    )
    body = get(make_app(settings, probes), "/health").json()
    assert body["status"] == "degraded"
    assert body["decider_model"]["available"] is False


def test_health_needs_no_token(settings: Settings) -> None:
    assert get(make_app(settings), "/health").status_code == 200
