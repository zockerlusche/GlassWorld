"""Self-check of the AI parts at start (Marcel W1703 rule 2, Bogen 35 R11).

Per AI one state with a clear text and a fix hint, both as i18n keys (msg.selfcheck.<ai>.<reason>
and ...fix). Built only from the status the service already has (Ollama setup, model setup, warm-up,
hardware tier), so it answers at once and never waits for a model (rule 3).
"""

from __future__ import annotations

from typing import Any

from glassworld_contracts import CONTRACT_VERSION

PREPARING = {"downloading", "verifying", "installing", "starting", "queued"}


def _check(ai: str, state: str, reason: str, detail: list[str] | None = None) -> dict[str, Any]:
    key = f"msg.selfcheck.{ai}.{reason}"
    return {
        "ai": ai,
        "state": state,
        "reason": reason,
        "message_key": key,
        "fix_key": f"{key}.fix" if state == "problem" else None,
        "detail": detail or [],
    }


def _language(
    ollama: dict[str, Any], models: dict[str, Any], tier: str | None, warm_up: str
) -> dict[str, Any]:
    if tier is None:
        return _check("language", "problem", "tier_below")
    runtime = str(ollama.get("state"))
    if runtime == "missing":
        return _check("language", "problem", "runtime_missing")
    if runtime == "outdated":
        return _check("language", "problem", "runtime_outdated")
    if runtime == "failed":
        return _check("language", "problem", "runtime_failed")
    if runtime == "installed":
        return _check("language", "problem", "runtime_stopped")
    if runtime in PREPARING:
        return _check("language", "preparing", "runtime_preparing")
    states = [str(m["state"]) for m in models.get("models", []) if m.get("role") == "language"]
    if any(s in PREPARING for s in states):
        return _check("language", "preparing", "model_preparing")
    if "failed" in states:
        return _check("language", "problem", "model_failed")
    if "other_version" in states:
        return _check("language", "problem", "model_other_version")
    if any(s != "present" for s in states):
        return _check("language", "problem", "model_missing")
    if warm_up == "starting":
        return _check("language", "preparing", "model_loading")
    if warm_up == "failed":
        return _check("language", "problem", "model_not_loaded")
    return _check("language", "ok", "ready")


def _image(models: dict[str, Any]) -> dict[str, Any]:
    comfyui: dict[str, Any] | None = models.get("comfyui")
    images = [m for m in models.get("models", []) if m.get("role") in {"image", "image_edit"}]
    if comfyui is None or not images:
        return _check("image", "not_offered", "not_offered")
    if not comfyui.get("models_dir_known"):
        return _check("image", "problem", "folder_unknown")
    if not comfyui.get("reachable"):
        return _check("image", "problem", "comfyui_down")
    nodes = [str(n) for n in comfyui.get("missing_nodes", [])]
    if nodes:
        return _check("image", "problem", "nodes_missing", nodes)
    states = [str(m["state"]) for m in images]
    if any(s in PREPARING for s in states):
        return _check("image", "preparing", "models_preparing")
    if "failed" in states:
        return _check("image", "problem", "models_failed")
    if any(s != "present" for s in states):
        return _check("image", "problem", "models_missing")
    return _check("image", "ok", "ready")


def self_check(
    ollama: dict[str, Any], models: dict[str, Any], tier: str | None, warm_up: str
) -> dict[str, Any]:
    """The SelfCheck contract message (statuses as their JSON dumps)."""
    return {
        "contract_version": CONTRACT_VERSION,
        "checks": [
            _language(ollama, models, tier, warm_up),
            _image(models),
            _check("sound", "not_planned", "not_planned"),  # W1703: no sound AI for players yet
        ],
    }


def all_keys() -> list[str]:
    """Every key self_check can return, so a test can demand DE and EN texts for each."""
    keys: list[str] = []
    problems = {
        "language": [
            "tier_below", "runtime_missing", "runtime_outdated", "runtime_failed",
            "runtime_stopped", "model_failed", "model_other_version", "model_missing",
            "model_not_loaded",
        ],
        "image": [
            "folder_unknown", "comfyui_down", "nodes_missing", "models_failed", "models_missing",
        ],
    }  # fmt: skip
    others = {
        "language": ["runtime_preparing", "model_preparing", "model_loading", "ready"],
        "image": ["not_offered", "models_preparing", "ready"],
        "sound": ["not_planned"],
    }
    for ai, reasons in problems.items():
        keys += [
            k for r in reasons for k in (f"msg.selfcheck.{ai}.{r}", f"msg.selfcheck.{ai}.{r}.fix")
        ]
    for ai, reasons in others.items():
        keys += [f"msg.selfcheck.{ai}.{r}" for r in reasons]
    return keys
