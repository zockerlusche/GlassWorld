"""Self-check and diagnostics report (Marcel W1703 rules 2 and 4, Bogen 35 R11 / A3)."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from fastapi import FastAPI

from conftest import TOKEN, get, make_app, post
from glassworld_ai.config import Settings
from glassworld_ai.diagnostics import scrub
from glassworld_ai.selfcheck import all_keys, self_check
from glassworld_ai.validation import ContractValidator

REPO = Path(__file__).resolve().parents[2]
VALIDATOR = ContractValidator(REPO / "contracts" / "schemas")
RUNNING = {"state": "running"}
LANGUAGE_OK = {"id": "qwen3.5:9b", "role": "language", "state": "present"}
IMAGE = {"id": "z-image-turbo", "role": "image", "state": "present"}
COMFY_OK: dict[str, Any] = {"models_dir_known": True, "reachable": True, "missing_nodes": []}


def _models(*models: dict[str, Any], comfyui: dict[str, Any] | None = None) -> dict[str, Any]:
    return {"models": list(models), "comfyui": comfyui}


S = "standard"
OK: dict[str, Any] = {"models": [LANGUAGE_OK], "comfyui": None}


def _lang(state: str) -> dict[str, Any]:
    return _models({**LANGUAGE_OK, "state": state})


def _by_ai(check: dict[str, Any]) -> dict[str, dict[str, Any]]:
    assert VALIDATOR.errors("self-check", check) == []
    return {c["ai"]: c for c in check["checks"]}


@pytest.mark.parametrize(
    ("ollama", "models", "tier", "warm", "state", "reason"),
    [
        (RUNNING, OK, None, "done", "problem", "tier_below"),
        ({"state": "missing"}, OK, S, "done", "problem", "runtime_missing"),
        ({"state": "installed"}, OK, S, "done", "problem", "runtime_stopped"),
        ({"state": "downloading"}, OK, S, "done", "preparing", "runtime_preparing"),
        (RUNNING, _lang("missing"), S, "done", "problem", "model_missing"),
        (RUNNING, _lang("other_version"), S, "done", "problem", "model_other_version"),
        (RUNNING, OK, S, "starting", "preparing", "model_loading"),
        (RUNNING, OK, S, "done", "ok", "ready"),
    ],
)  # fmt: skip
def test_language_states(
    ollama: dict[str, Any],
    models: dict[str, Any],
    tier: str | None,
    warm: str,
    state: str,
    reason: str,
) -> None:
    language = _by_ai(self_check(ollama, models, tier, warm))["language"]
    assert (language["state"], language["reason"]) == (state, reason)
    assert (language["fix_key"] is not None) == (state == "problem")  # a problem says how to fix it


@pytest.mark.parametrize(
    ("models", "state", "reason", "detail"),
    [
        (_models(LANGUAGE_OK), "not_offered", "not_offered", []),
        (_models(LANGUAGE_OK, IMAGE, comfyui={**COMFY_OK, "models_dir_known": False}),
         "problem", "folder_unknown", []),
        (_models(LANGUAGE_OK, IMAGE, comfyui={**COMFY_OK, "reachable": False}),
         "problem", "comfyui_down", []),
        (_models(LANGUAGE_OK, IMAGE, comfyui={**COMFY_OK, "missing_nodes": ["UnetLoaderGGUF"]}),
         "problem", "nodes_missing", ["UnetLoaderGGUF"]),
        (_models(LANGUAGE_OK, {**IMAGE, "state": "missing"}, comfyui=COMFY_OK),
         "problem", "models_missing", []),
        (_models(LANGUAGE_OK, IMAGE, comfyui=COMFY_OK), "ok", "ready", []),
    ],
)  # fmt: skip
def test_image_states(models: dict[str, Any], state: str, reason: str, detail: list[str]) -> None:
    image = _by_ai(self_check(RUNNING, models, "standard", "done"))["image"]
    assert (image["state"], image["reason"], image["detail"]) == (state, reason, detail)


def test_sound_is_not_planned_for_players() -> None:
    """W1703: no sound AI for players in the first release, only the basic library."""
    sound = _by_ai(self_check(RUNNING, _models(LANGUAGE_OK), "standard", "done"))["sound"]
    assert sound["state"] == "not_planned" and sound["fix_key"] is None


def test_every_self_check_text_exists_in_german_and_english() -> None:
    for locale in ("de", "en"):
        data = json.loads((REPO / f"content/i18n/{locale}.json").read_text(encoding="utf-8"))
        keys = {e["key"] for e in data["entries"]}
        missing = [k for k in all_keys() if k not in keys]
        assert missing == [], (locale, missing)


def test_scrub_removes_user_paths_and_secrets() -> None:
    known = [Path("D:/Games/GlassWorld/ai")]
    text = scrub(
        "C:\\Users\\marcel\\AppData\\x.log /home/marcel/a /Users/marcel/b "
        "D:/Games/GlassWorld/ai/logs hf_abcdefghijklmnopqrstuv token=geheim123",
        known,
    )
    for leaked in ("marcel", "hf_abcdefghijklmnopqrstuv", "geheim123", "D:/Games/GlassWorld/ai"):
        assert leaked not in text, leaked
    assert "<user>" in text and "<dir>" in text


def _offline(settings: Settings, tmp_path: Path) -> FastAPI:
    patched = settings.model_copy(
        update={
            "ollama_url": "http://127.0.0.1:9",
            "ollama_install_dir": tmp_path / "ollama",
            "model_store_dir": tmp_path / "store",
            "ollama_autostart": False,
        }
    )
    return make_app(patched)


def test_setup_check_answers_with_the_contract(settings: Settings, tmp_path: Path) -> None:
    response = get(_offline(settings, tmp_path), "/setup/check", {"x-gw-token": TOKEN})
    assert response.status_code == 200
    checks = _by_ai(response.json())
    assert set(checks) == {"language", "image", "sound"}
    assert checks["language"]["state"] in {"problem", "preparing"}  # no Ollama in the test


def test_the_diagnostics_report_hides_tokens_and_user_paths(
    settings: Settings, tmp_path: Path
) -> None:
    """A3: the report contains no tokens and no user paths (automatic test)."""
    settings.log_dir.mkdir(parents=True, exist_ok=True)
    home = str(Path.home())
    row = {
        "time_utc": "2026-11-10T10:00:00Z",
        "outcome": "model_error",
        "detail": f"failed reading {home}/x with hf_SECRETSECRETSECRET1 and {settings.log_dir}",
    }
    (settings.log_dir / "decider.jsonl").write_text(json.dumps(row) + "\n", encoding="utf-8")
    response = post(_offline(settings, tmp_path), "/diagnostics", {})
    assert response.status_code == 200
    body = response.json()
    assert VALIDATOR.errors("diagnostics-report", body) == []
    text = body["text"]
    assert "model_error" in text  # the last error is in the report
    for leaked in (home, home.replace("\\", "/"), "hf_SECRETSECRETSECRET1", str(settings.log_dir)):
        assert leaked not in text, leaked
    assert (settings.log_dir / body["file_name"]).read_text(encoding="utf-8") == text


def test_scrub_removes_the_account_name_outside_known_folders() -> None:
    """Review GW1 #135: a traceback path outside the known folders keeps no user name."""
    text = scrub(
        "File D:/Spiele/Kim42/GlassWorld/x.py and E:/Kim42-backup but Kimono stays",
        [],
        user_names=["Kim42"],
    )
    assert "Kim42" not in text
    assert "D:/Spiele/<user>/GlassWorld" in text and "E:/<user>-backup" in text
    assert "Kimono" in text  # only the whole name


def test_account_names_come_from_the_system(monkeypatch: pytest.MonkeyPatch) -> None:
    from glassworld_ai.diagnostics import account_names

    monkeypatch.setenv("USERNAME", "Kim42")
    monkeypatch.setenv("USER", "ab")  # too short to be replaced safely
    names = account_names()
    assert "Kim42" in names and "ab" not in names
    assert scrub("C:/Games/Kim42/log.txt", []) == "C:/Games/<user>/log.txt"
