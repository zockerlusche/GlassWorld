"""Builds the HealthStatus contract message from the probe results."""

from __future__ import annotations

import asyncio

from glassworld_ai import SERVICE_VERSION
from glassworld_ai.probes import DependencyState, Probes
from glassworld_contracts import CONTRACT_VERSION, HealthStatus


def _dependency(name: str, state: DependencyState) -> dict[str, object]:
    return {"name": name, "status": "up" if state.up else "down", "version": state.version}


async def health_status(probes: Probes, decider_model: str) -> HealthStatus:
    ollama, comfyui = await asyncio.gather(probes.ollama(), probes.comfyui())
    ready = ollama.service.up and ollama.model_available
    return HealthStatus.model_validate(
        {
            "contract_version": CONTRACT_VERSION,
            "status": "ready" if ready else "degraded",
            "service_version": SERVICE_VERSION,
            # Sorted by name for a stable answer.
            "dependencies": [
                _dependency("comfyui", comfyui),
                _dependency("ollama", ollama.service),
            ],
            "decider_model": {
                "name": decider_model,
                "available": ollama.model_available,
                "loaded": ollama.model_loaded,
            },
        }
    )
