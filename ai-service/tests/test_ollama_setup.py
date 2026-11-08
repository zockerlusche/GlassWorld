"""Ollama at first start (B10 R3, F5266 = A): detect, download, check, unpack, start."""

from __future__ import annotations

import asyncio
import hashlib
import io
import json
import zipfile
from pathlib import Path
from typing import Any

import httpx
import pytest
from fastapi import FastAPI

from conftest import TOKEN, get, make_app, post
from glassworld_ai.config import Settings
from glassworld_ai.ollama_setup import (
    MARKER,
    Asset,
    OllamaSetup,
    Runtime,
    SetupError,
    load_runtime,
    safe_extract,
)
from glassworld_ai.validation import ContractValidator

REPO = Path(__file__).resolve().parents[2]
URL = "https://github.com/ollama/ollama/releases/download/v0.40.14/ollama-windows-amd64.zip"
OLLAMA = "http://127.0.0.1:11999"


def _zip(entries: dict[str, bytes]) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as zf:
        for name, data in entries.items():
            zf.writestr(name, data)
    return buffer.getvalue()


ARCHIVE = _zip({"ollama.exe": b"MZ fake program", "lib/ollama/ggml.dll": b"x" * 5000})


def _runtime(archive: bytes = ARCHIVE, sha: str | None = None) -> Runtime:
    asset = Asset(
        platform="windows-x86_64",
        url=URL,
        sha256=sha or hashlib.sha256(archive).hexdigest(),
        size_bytes=len(archive),
        executable="ollama.exe",
    )
    return Runtime("0.40.14", "0.40.14", "MIT", "https://example.invalid/page", (asset,))


class FakeProcess:
    def __init__(self) -> None:
        self.stopped = False

    def poll(self) -> int | None:
        return 0 if self.stopped else None

    def terminate(self) -> None:
        self.stopped = True

    def kill(self) -> None:
        self.stopped = True

    def wait(self, timeout: float | None = None) -> int:
        return 0


class World:
    """A fake network: the release download (with Range) and the local Ollama's /api/version."""

    def __init__(self, archive: bytes = ARCHIVE, running: str | None = None) -> None:
        self.archive = archive
        self.running = running  # version string of the Ollama that answers, None = nothing
        self.ranges: list[str | None] = []
        self.launched: list[tuple[Path, dict[str, str]]] = []
        self.processes: list[FakeProcess] = []

    def handler(self, request: httpx.Request) -> httpx.Response:
        if request.url.path == "/api/version":
            if self.running is None:
                raise httpx.ConnectError("refused", request=request)
            return httpx.Response(200, json={"version": self.running})
        if str(request.url) == URL:
            header = request.headers.get("range")
            self.ranges.append(header)
            if header:
                start = int(header.removeprefix("bytes=").rstrip("-"))
                if start >= len(self.archive):
                    return httpx.Response(416)
                return httpx.Response(206, content=self.archive[start:])
            return httpx.Response(200, content=self.archive)
        return httpx.Response(404)

    def launcher(self, exe: Path, env: dict[str, str]) -> Any:
        self.launched.append((exe, env))
        self.running = "0.40.14"  # the started program answers
        process = FakeProcess()
        self.processes.append(process)
        return process


def _setup(
    tmp_path: Path,
    world: World,
    transport: httpx.AsyncBaseTransport,
    runtime: Runtime | None = None,
    candidates: list[Path] | None = None,
    platform: str = "windows-x86_64",
    url: str = OLLAMA,
    version_of: Any = None,
) -> OllamaSetup:
    return OllamaSetup(
        runtime or _runtime(world.archive),
        install_dir=tmp_path / "ollama",
        ollama_url=url,
        transport=transport,
        models_dir=tmp_path / "models",
        start_timeout_s=2,
        platform=platform,
        candidates=lambda: list(candidates or []),
        version_of=version_of or (lambda _p: None),
        launcher=world.launcher,
    )


def _run(world: World, body: Any) -> Any:
    return asyncio.run(body(httpx.MockTransport(world.handler)))


async def _finish(setup: OllamaSetup) -> None:
    setup.start_download()
    task = setup._task  # pyright: ignore[reportPrivateUsage]
    assert task is not None
    await task


def test_the_data_file_pins_the_adr_version_and_the_official_release() -> None:
    runtime = load_runtime(REPO / "content/ai/ollama-runtime.json")
    adr = (REPO / "docs/adr/0001-versions.md").read_text(encoding="utf-8")
    assert f"| Ollama | {runtime.version} |" in adr
    asset = runtime.asset_for("windows-x86_64")
    assert asset is not None
    assert asset.url.startswith(
        f"https://github.com/ollama/ollama/releases/download/v{runtime.version}/"
    )
    assert runtime.licence == "MIT"


def test_nothing_there_offers_the_download(tmp_path: Path) -> None:
    world = World()

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        return await _setup(tmp_path, world, client).status()

    status = _run(world, body)
    assert status.state == "missing" and status.source is None
    assert status.download.size_bytes == len(ARCHIVE) and status.download.licence == "MIT"
    assert status.download.done_bytes == 0 and status.error is None


@pytest.mark.parametrize(("version", "state"), [("0.40.14", "running"), ("0.41.2", "running"),
                                                 ("0.39.9", "outdated")])  # fmt: skip
def test_the_players_own_running_ollama_is_used(tmp_path: Path, version: str, state: str) -> None:
    world = World(running=version)

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client)
        started = await setup.ensure_running()
        return started, await setup.status()

    started, status = _run(world, body)
    assert status.state == state and status.source == "external" and status.version == version
    assert started is (state == "running")
    assert world.launched == []  # never a second one


def _newer(_path: Path) -> str:
    return "0.41.0"


def test_an_installed_but_stopped_ollama_of_the_player_is_started(tmp_path: Path) -> None:
    exe = tmp_path / "Programs/Ollama/ollama.exe"
    exe.parent.mkdir(parents=True)
    exe.write_bytes(b"MZ")
    world = World()

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client, candidates=[exe], version_of=_newer)
        before = await setup.status()
        started = await setup.ensure_running()
        return before, started

    before, started = _run(world, body)
    assert before.state == "installed" and before.source == "external"
    assert started and world.launched[0][0] == exe


def test_download_checks_unpacks_and_starts_on_localhost(tmp_path: Path) -> None:
    world = World()

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client)
        await _finish(setup)
        status = await setup.status()
        await setup.close()
        return status

    status = _run(world, body)
    installed = tmp_path / "ollama/0.40.14"
    assert (installed / "ollama.exe").read_bytes() == b"MZ fake program"
    assert (installed / "lib/ollama/ggml.dll").is_file()
    assert json.loads((installed / MARKER).read_text())["sha256"] == _runtime().assets[0].sha256
    assert not list((tmp_path / "ollama/download").glob("*.part"))  # 1.5 GB freed
    exe, env = world.launched[0]
    assert exe == installed / "ollama.exe"
    assert env["OLLAMA_HOST"] == "127.0.0.1:11999"
    assert env["OLLAMA_MODELS"] == str(tmp_path / "models")
    assert status.state == "running" and status.source == "managed"
    assert world.processes[0].stopped  # the service stops the Ollama it started


def test_a_cancelled_download_continues_where_it_stopped(tmp_path: Path) -> None:
    world = World()
    part = tmp_path / "ollama/download/ollama-windows-amd64.zip.part"
    part.parent.mkdir(parents=True)
    part.write_bytes(ARCHIVE[:1000])

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client)
        before = await setup.status()
        await _finish(setup)
        return before, await setup.status()

    before, after = _run(world, body)
    assert before.state == "missing" and before.download.done_bytes == 1000
    assert world.ranges == ["bytes=1000-"]
    assert after.state == "running"


def test_a_wrong_checksum_fails_and_starts_over_next_time(tmp_path: Path) -> None:
    world = World()

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client, runtime=_runtime(sha="0" * 64))
        await _finish(setup)
        return await setup.status()

    status = _run(world, body)
    assert status.state == "failed" and status.error == "checksum_mismatch"
    assert not (tmp_path / "ollama/0.40.14").exists()
    assert not list((tmp_path / "ollama/download").glob("*.part"))
    assert world.launched == []


@pytest.mark.parametrize("name", ["../evil.exe", "/abs.exe", "C:/x.exe", "a/../../b"])
def test_archive_entries_never_leave_the_target(tmp_path: Path, name: str) -> None:
    archive = tmp_path / "a.zip"
    archive.write_bytes(_zip({name: b"x", "ollama.exe": b"MZ"}))
    with pytest.raises(SetupError) as error:
        safe_extract(archive, tmp_path / "out")
    assert error.value.code == "bad_archive"
    assert not (tmp_path / "evil.exe").exists()


def test_an_archive_without_the_program_fails(tmp_path: Path) -> None:
    world = World(archive=_zip({"readme.txt": b"hi"}))

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client)
        await _finish(setup)
        return await setup.status()

    status = _run(world, body)
    assert status.state == "failed" and status.error == "bad_archive"


def test_other_systems_get_a_clear_error(tmp_path: Path) -> None:
    world = World()

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client, platform="linux-unsupported")
        await _finish(setup)
        return await setup.status()

    status = _run(world, body)
    assert status.state == "failed" and status.error == "unsupported_platform"


def test_a_network_address_is_never_served(tmp_path: Path) -> None:
    world = World()

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client, url="http://0.0.0.0:11434")
        await _finish(setup)
        return await setup.status()

    status = _run(world, body)
    assert status.state == "failed" and status.error == "start_failed"
    assert world.launched == []


def test_status_matches_the_contract(tmp_path: Path) -> None:
    world = World()
    validator = ContractValidator(REPO / "contracts/schemas")

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        return await _setup(tmp_path, world, client).status()

    data = _run(world, body).model_dump(mode="json")
    assert validator.errors("ollama-setup-status", data) == []


def _offline_app(settings: Settings, tmp_path: Path) -> FastAPI:
    patched = settings.model_copy(
        update={
            "ollama_url": "http://127.0.0.1:9",
            "ollama_install_dir": tmp_path / "ollama",
            "ollama_autostart": False,
        }
    )
    return make_app(patched)


def test_the_download_needs_the_players_consent(settings: Settings, tmp_path: Path) -> None:
    app = _offline_app(settings, tmp_path)
    for body in ({}, {"consent": False}, {"consent": "yes"}, b"not json"):
        response = post(app, "/setup/ollama/download", body)
        assert response.status_code == 422
        assert response.json()["code"] == "bad_request"
    assert not (tmp_path / "ollama").exists()


def test_get_setup_answers_with_the_contract(settings: Settings, tmp_path: Path) -> None:
    app = _offline_app(settings, tmp_path)
    response = get(app, "/setup/ollama", {"x-gw-token": TOKEN})
    assert response.status_code == 200
    validator = ContractValidator(REPO / "contracts/schemas")
    assert validator.errors("ollama-setup-status", response.json()) == []
    assert response.json()["required_version"] == "0.40.14"


def test_an_unfinished_unpack_is_replaced_and_only_the_marker_completes_it(tmp_path: Path) -> None:
    leftover = tmp_path / "ollama/0.40.14"
    leftover.mkdir(parents=True)
    (leftover / "ollama.exe").write_bytes(b"half")  # no marker: an interrupted unpack
    world = World()

    async def body(client: httpx.AsyncBaseTransport) -> Any:
        setup = _setup(tmp_path, world, client)
        before = setup.managed_executable()
        await _finish(setup)
        return before

    assert _run(world, body) is None
    assert (leftover / "ollama.exe").read_bytes() == b"MZ fake program"
    assert (leftover / MARKER).is_file()
