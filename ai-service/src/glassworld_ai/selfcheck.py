"""Self-check of the AI parts at start (Marcel W1703 rule 2, Bogen 35 R11).

Per AI one state with a clear text and a fix hint, both as i18n keys (msg.selfcheck.<ai>.<reason>
and ...fix). Built only from the status the service already has (Ollama setup, model setup, warm-up,
hardware tier), so it answers at once and never waits for a model (rule 3).
"""

from __future__ import annotations

from typing import Any

from glassworld_contracts import CONTRACT_VERSION
from glassworld_contracts.models.hardware_tiers_schema import HardwareTiers

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


LEARN_BY_DAY_HINT = "msg.selfcheck.learn_by_day.hint"
LEARN_BY_DAY_REASONS = ("ok", "tier", "too_slow", "not_measured")


def learn_by_day(
    tiers: HardwareTiers, tier: str | None, language_seconds_per_job: float | None
) -> dict[str, Any]:
    """B2652 (A1568): whether the game offers the switch 'learn by day as well'. The rule is
    data (content/hardware/tiers.json, learn_by_day per tier); the measured seconds per
    language job come from the measurement run (B7 R6). By default everything new is learned
    in the computing night (B2650/B2652); this only says whether the switch may be shown."""
    entry = next((t for t in tiers.tiers if t.id == tier), None) if tier else None
    rule = entry.learn_by_day if entry is not None else None
    if rule is None or not rule.offered:
        reason = "tier"
    elif rule.max_language_seconds_per_job is None:
        reason = "ok"
    elif language_seconds_per_job is None:
        reason = "not_measured"
    elif language_seconds_per_job > float(rule.max_language_seconds_per_job):
        reason = "too_slow"
    else:
        reason = "ok"
    return {
        "offered": reason == "ok",
        "reason": reason,
        "message_key": f"msg.selfcheck.learn_by_day.{reason}",
        "hint_key": LEARN_BY_DAY_HINT,
    }


def self_check(
    ollama: dict[str, Any],
    models: dict[str, Any],
    tier: str | None,
    warm_up: str,
    learn: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """The SelfCheck contract message (statuses as their JSON dumps); `learn` from
    learn_by_day() when the service knows the tiers (A1568)."""
    check: dict[str, Any] = {
        "contract_version": CONTRACT_VERSION,
        "checks": [
            _language(ollama, models, tier, warm_up),
            _image(models),
            _check("sound", "not_planned", "not_planned"),  # W1703: no sound AI for players yet
        ],
    }
    if learn is not None:
        check["learn_by_day"] = learn
    return check


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
    keys += [f"msg.selfcheck.learn_by_day.{r}" for r in LEARN_BY_DAY_REASONS]
    keys.append(LEARN_BY_DAY_HINT)
    return keys
