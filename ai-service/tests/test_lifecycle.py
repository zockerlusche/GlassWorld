"""Lifecycle (ADR-0006): parent watch, lock file, warm-up state, shutdown."""

from __future__ import annotations

import asyncio
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

import httpx

from conftest import REFERENCE_PC, TOKEN, FakeProbes, make_app
from glassworld_ai.app import create_app
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.lifecycle import LockFile, process_alive, watch_parent

SERVICE_DIR = Path(__file__).resolve().parents[1]
REPO = SERVICE_DIR.parent


def test_start_marker_identifies_this_process() -> None:
    from glassworld_ai.lifecycle import process_start_marker

    marker = process_start_marker(os.getpid())
    assert marker is not None and marker == process_start_marker(os.getpid())
    assert process_start_marker(-1) is None


def test_process_alive_for_self_and_ended_process() -> None:
    assert process_alive(os.getpid())
    child = subprocess.Popen([sys.executable, "-c", "pass"])
    child.wait(timeout=10)
    assert not process_alive(child.pid)
    assert not process_alive(-1)


def test_watch_parent_calls_back_once_when_gone() -> None:
    states = iter([True, True, False])
    calls: list[str] = []
    asyncio.run(watch_parent(4242, lambda: calls.append("gone"), 0.0, lambda _: next(states)))
    assert calls == ["gone"]


def test_lock_file_reports_only_living_foreign_services(tmp_path: Path) -> None:
    lock = LockFile(tmp_path / "ai-service.lock")
    assert lock.leftover() is None
    info = lock.write(port=50123)
    assert info.pid == os.getpid()
    assert lock.leftover() is None  # our own lock is no leftover
    (tmp_path / "ai-service.lock").write_text(
        json.dumps({"pid": 999_999, "port": 1, "started_utc": "x"}), encoding="utf-8"
    )
    # foreign lock without start marker: never reported (could be a reused pid)
    assert lock.leftover(alive=lambda _: True) is None
    (tmp_path / "ai-service.lock").write_text(
        json.dumps({"pid": 999_999, "port": 1, "started_utc": "x", "start_marker": 42}),
        encoding="utf-8",
    )
    assert lock.leftover(alive=lambda _: True, marker=lambda _: 42) is not None
    assert lock.leftover(alive=lambda _: True, marker=lambda _: 43) is None  # pid reused
    assert lock.leftover(alive=lambda _: False, marker=lambda _: 42) is None
    lock.remove()  # a foreign lock is never removed
    assert (tmp_path / "ai-service.lock").exists()


def test_health_says_starting_while_the_model_loads(settings: Settings) -> None:
    app = make_app(settings)
    app.state.warm_up = "starting"
    body = _get_health(app)
    assert body["status"] == "starting"
    assert body["detail"] == "Decider model is loading."
    app.state.warm_up = "failed"
    assert _get_health(app)["status"] == "degraded"


def test_lifespan_warms_up_and_always_unloads_and_shutdown_blocks_new_calls(
    settings: Settings,
) -> None:
    bodies: list[dict[str, object]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        bodies.append(json.loads(request.content))
        return httpx.Response(200, json={"done": True})

    exits: list[str] = []
    app = create_app(
        settings,
        allowed_hosts=frozenset({"testserver"}),
        probes=FakeProbes(),
        hardware=REFERENCE_PC,  # CI runners have no GPU: budget 0 would refuse the warm-up
        decider=MockDecider(),
        request_exit=lambda: exits.append("exit"),
        transport=httpx.MockTransport(handler),
    )
    request = json.loads(
        (REPO / "contracts/examples/valid/decision-request.hungry-berries.json").read_text("utf-8")
    )

    async def scenario() -> None:
        async with app.router.lifespan_context(app):
            await asyncio.sleep(0.05)  # warm-up task runs
            assert app.state.warm_up == "done"
            transport = httpx.ASGITransport(app=app)
            async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as c:
                forbidden = await c.post("/shutdown")
                assert forbidden.status_code == 403  # token required
                stop = await c.post("/shutdown", headers={"x-gw-token": TOKEN})
                assert stop.status_code == 202
                late = await c.post("/decide", json=request, headers={"x-gw-token": TOKEN})
                assert late.status_code == 503  # no new model call while stopping
                assert late.json()["retryable"] is True
        # lifespan ended (shutdown, parent gone or signal): the model is unloaded

    asyncio.run(scenario())
    assert bodies[0]["keep_alive"] == "30m" and bodies[0]["prompt"] == ""  # warm-up
    assert bodies[-1] == {"model": "qwen3.5:9b", "keep_alive": 0}  # unload at the end
    assert exits == ["exit"]


def test_service_exits_when_its_parent_ends_and_cleans_its_lock(tmp_path: Path) -> None:
    parent = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(3)"])
    env = {
        **os.environ,
        "GW_AI_OLLAMA_URL": "http://127.0.0.1:9",
        "GW_AI_COMFYUI_URL": "http://127.0.0.1:9",
        "GW_AI_PROBE_TIMEOUT_S": "1",
        "GW_AI_TOKEN": "t",
        "GW_AI_DECIDER_BACKEND": "mock",
        "GW_AI_WARM_UP": "false",
        "GW_AI_LOCK_DIR": str(tmp_path),
        "GW_AI_LOG_DIR": str(tmp_path / "logs"),
    }
    service = subprocess.Popen(
        [sys.executable, "-m", "glassworld_ai", "--port", "0", "--parent-pid", str(parent.pid)],
        cwd=tmp_path,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    try:
        assert service.stdout is not None
        line = service.stdout.readline().strip()
        assert re.fullmatch(r"AI_SERVICE_READY port=\d+", line), line
        lock = json.loads((tmp_path / "ai-service.lock").read_text(encoding="utf-8"))
        assert lock["pid"] == service.pid or process_alive(lock["pid"])
        parent.wait(timeout=10)
        started = time.monotonic()
        service.wait(timeout=15)
        assert time.monotonic() - started < 15
        assert not (tmp_path / "ai-service.lock").exists()
    finally:
        if service.poll() is None:
            service.terminate()
            service.wait(timeout=10)
        if parent.poll() is None:
            parent.terminate()


def _get_health(app: object) -> dict[str, object]:
    from conftest import get

    return get(app, "/health").json()  # type: ignore[arg-type]
