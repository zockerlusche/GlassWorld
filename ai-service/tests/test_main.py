"""End-to-end start: real process, OS-assigned port, ready line, /health over HTTP.

Ollama and ComfyUI are pointed at a closed port, so the test needs no GPU and no network.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path

import httpx

SERVICE_DIR = Path(__file__).resolve().parents[1]
CLOSED_PORT_URL = "http://127.0.0.1:9"


def test_service_announces_port_and_answers_health(tmp_path: Path) -> None:
    env = {
        **os.environ,
        "GW_AI_OLLAMA_URL": CLOSED_PORT_URL,
        "GW_AI_COMFYUI_URL": CLOSED_PORT_URL,
        "GW_AI_PROBE_TIMEOUT_S": "1",
        "GW_AI_TOKEN": "e2e-token",
    }
    process = subprocess.Popen(
        [sys.executable, "-m", "glassworld_ai", "--port", "0"],
        cwd=tmp_path,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    try:
        assert process.stdout is not None
        line = process.stdout.readline().strip()
        match = re.fullmatch(r"AI_SERVICE_READY port=(\d+)", line)
        assert match, f"unexpected first line: {line!r}"
        port = int(match.group(1))
        assert port > 0

        response = httpx.get(f"http://127.0.0.1:{port}/health", timeout=10)
        assert response.status_code == 200
        assert response.json()["status"] == "degraded"

        forbidden = httpx.get(f"http://127.0.0.1:{port}/decide", timeout=10)
        assert forbidden.status_code == 403
    finally:
        process.terminate()
        process.wait(timeout=10)
