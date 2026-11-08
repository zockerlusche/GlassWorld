"""Ollama at first start (B10 R3, F5266 = A).

The game does not ship Ollama. At first start the service reports whether a usable Ollama runs
or is installed (the player's own, `external`). If not, the game's download dialog (R4) shows
size, licence and source; only after the player agreed does the game call POST
/setup/ollama/download. The service then loads the official release named in
content/ai/ollama-runtime.json, checks its SHA-256, unpacks it into the user data folder and
starts `ollama serve` on the configured localhost address (`managed`). A cancelled or broken
download continues where it stopped.

The service only ever stops an Ollama it started itself, never the player's own.
"""

from __future__ import annotations

import asyncio
import contextlib
import hashlib
import json
import logging
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Literal
from urllib.parse import urlsplit

import httpx

from glassworld_contracts import CONTRACT_VERSION, OllamaSetupStatus

State = Literal[
    "running",
    "outdated",
    "installed",
    "starting",
    "missing",
    "downloading",
    "verifying",
    "installing",
    "failed",
]
MARKER = ".glassworld-ollama.json"
_log = logging.getLogger(__name__)
CHUNK = 1 << 20
LOCAL_HOSTS = frozenset({"127.0.0.1", "localhost", "::1"})
_VERSION = re.compile(r"(\d+)\.(\d+)\.(\d+)")


class SetupError(Exception):
    """A step of the setup failed; `code` is the stable error code of the status contract."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True)
class Asset:
    platform: str
    url: str
    sha256: str
    size_bytes: int
    executable: str

    @property
    def file_name(self) -> str:
        return PurePosixPath(urlsplit(self.url).path).name


@dataclass(frozen=True)
class Runtime:
    version: str
    accept_from: str
    licence: str
    source_page: str
    assets: tuple[Asset, ...]

    def asset_for(self, platform: str) -> Asset | None:
        return next((a for a in self.assets if a.platform == platform), None)


def load_runtime(path: Path) -> Runtime:
    data: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
    return Runtime(
        version=data["version"],
        accept_from=data["accept_from"],
        licence=data["licence"],
        source_page=data["source_page"],
        assets=tuple(Asset(**a) for a in data["assets"]),
    )


def parse_version(text: str | None) -> tuple[int, int, int] | None:
    match = _VERSION.search(text or "")
    return (int(match[1]), int(match[2]), int(match[3])) if match else None


def current_platform() -> str:
    machine = (os.environ.get("PROCESSOR_ARCHITECTURE") or "").lower()
    if sys.platform == "win32" and machine in {"amd64", "x86_64", ""}:
        return "windows-x86_64"
    return f"{sys.platform}-unsupported"


def safe_extract(archive: Path, target: Path) -> None:
    """Unpack without letting any entry leave `target` (absolute paths, '..', drive letters)."""
    root = target.resolve()
    with zipfile.ZipFile(archive) as zf:
        for info in zf.infolist():
            name = info.filename.replace("\\", "/")
            parts = PurePosixPath(name).parts
            if name.startswith("/") or ".." in parts or (parts and ":" in parts[0]):
                raise SetupError("bad_archive", f"unsafe path in archive: {info.filename}")
            dest = (root / name).resolve()
            if not dest.is_relative_to(root):
                raise SetupError("bad_archive", f"unsafe path in archive: {info.filename}")
            if info.is_dir():
                dest.mkdir(parents=True, exist_ok=True)
                continue
            dest.parent.mkdir(parents=True, exist_ok=True)
            with zf.open(info) as src, dest.open("wb") as out:
                shutil.copyfileobj(src, out, CHUNK)


def _with_retries(step: Callable[[], object], attempts: int = 10, wait_s: float = 1.0) -> None:
    """File steps that a virus scanner may briefly block on Windows (PermissionError)."""
    for attempt in range(attempts):
        try:
            step()
            return
        except PermissionError:
            if attempt == attempts - 1:
                raise
            time.sleep(wait_s)


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        while block := f.read(CHUNK):
            digest.update(block)
    return digest.hexdigest()


VersionReader = Callable[[Path], str | None]
Launcher = Callable[[Path, dict[str, str]], "subprocess.Popen[bytes]"]


def read_program_version(executable: Path) -> str | None:
    """`ollama --version` of an installed program (prints the client version without a server)."""
    try:
        done = subprocess.run(
            [str(executable), "--version"],
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return None
    found = parse_version(done.stdout + done.stderr)
    return ".".join(map(str, found)) if found else None


def launch_serve(executable: Path, env: dict[str, str]) -> subprocess.Popen[bytes]:
    return subprocess.Popen(
        [str(executable), "serve"],
        env=env,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )


def installed_candidates() -> list[Path]:
    """Where the player's own Ollama usually is (PATH, the official installer's folder)."""
    found: list[Path] = []
    on_path = shutil.which("ollama")
    if on_path:
        found.append(Path(on_path))
    local = os.environ.get("LOCALAPPDATA")
    if local:
        found.append(Path(local) / "Programs" / "Ollama" / "ollama.exe")
    return found


class OllamaSetup:
    def __init__(
        self,
        runtime: Runtime,
        *,
        install_dir: Path,
        ollama_url: str,
        transport: httpx.AsyncBaseTransport | None = None,
        models_dir: Path | None = None,
        start_timeout_s: float = 60.0,
        platform: str | None = None,
        candidates: Callable[[], list[Path]] = installed_candidates,
        version_of: VersionReader = read_program_version,
        launcher: Launcher = launch_serve,
    ) -> None:
        self._runtime = runtime
        self._dir = install_dir
        self._url = ollama_url.rstrip("/")
        self._transport = transport
        self._models_dir = models_dir
        self._start_timeout_s = start_timeout_s
        self._asset = runtime.asset_for(platform or current_platform())
        self._candidates = candidates
        self._version_of = version_of
        self._launcher = launcher
        self._phase: State | None = None  # set while a step of ours runs, or after a failure
        self._error: str | None = None
        self._done_bytes = 0
        self._task: asyncio.Task[None] | None = None
        self._process: subprocess.Popen[bytes] | None = None
        self._lock = asyncio.Lock()

    def _new_client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(transport=self._transport)

    # --- where things are -------------------------------------------------------------
    @property
    def _version_dir(self) -> Path:
        return self._dir / self._runtime.version

    @property
    def _part(self) -> Path:
        name = self._asset.file_name if self._asset else "ollama.zip"
        return self._dir / "download" / f"{name}.part"

    def managed_executable(self) -> Path | None:
        if self._asset is None:
            return None
        exe = self._version_dir / self._asset.executable
        return exe if exe.is_file() and (self._version_dir / MARKER).is_file() else None

    def _fits(self, version: str | None) -> bool:
        have, need = parse_version(version), parse_version(self._runtime.accept_from)
        return have is not None and need is not None and have >= need

    def _external_executable(self) -> tuple[Path, str] | None:
        for path in self._candidates():
            if path.is_file():
                version = self._version_of(path)
                if self._fits(version):
                    return path, str(version)
        return None

    async def _running_version(self) -> str | None:
        try:
            async with self._new_client() as client:
                response = await client.get(self._url + "/api/version", timeout=3)
            response.raise_for_status()
            version = response.json().get("version")
        except (httpx.HTTPError, ValueError, AttributeError):
            return None
        return version if isinstance(version, str) else ""

    # --- status -------------------------------------------------------------------------
    async def status(self) -> OllamaSetupStatus:
        state: State
        source: Literal["external", "managed"] | None = None
        version: str | None = None
        if self._phase in {"downloading", "verifying", "installing", "starting"}:
            state = self._phase  # type: ignore[assignment]
        else:
            running = await self._running_version()
            ours = self._process is not None and self._process.poll() is None
            if running is not None:
                version = running or None
                source = "managed" if ours else "external"
                state = "running" if self._fits(running) else "outdated"
            elif self._phase == "failed":
                state = "failed"
            elif self.managed_executable() is not None:
                state, source, version = "installed", "managed", self._runtime.version
            elif (found := await asyncio.to_thread(self._external_executable)) is not None:
                state, source, version = "installed", "external", found[1]
            else:
                state = "missing"
        if state not in {"downloading", "verifying", "installing"}:
            self._done_bytes = self._part.stat().st_size if self._part.is_file() else 0
        asset = self._asset
        return OllamaSetupStatus.model_validate(
            {
                "contract_version": CONTRACT_VERSION,
                "state": state,
                "source": source,
                "version": version,
                "required_version": self._runtime.version,
                "download": {
                    "url": asset.url if asset else "",
                    "size_bytes": asset.size_bytes if asset else 0,
                    "done_bytes": self._done_bytes,
                    "licence": self._runtime.licence,
                    "source_page": self._runtime.source_page,
                },
                "error": self._error if state == "failed" else None,
            }
        )

    # --- download + install -------------------------------------------------------------
    def busy(self) -> bool:
        return self._task is not None and not self._task.done()

    def start_download(self) -> None:
        """Only after the player agreed in the game's dialog (the endpoint demands consent)."""
        if self.busy():
            return
        self._phase, self._error = "downloading", None
        self._task = asyncio.create_task(self._install())

    async def cancel(self) -> None:
        """Stop a running download; what is on disk stays, the next start continues."""
        task = self._task
        if task is not None and not task.done():
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await task
        if self._phase in {"downloading", "verifying", "installing"}:
            self._phase = None

    async def _install(self) -> None:
        try:
            if self._asset is None:
                raise SetupError("unsupported_platform", "no Ollama build for this system")
            if self.managed_executable() is None:
                await self._download(self._asset)
                self._phase = "verifying"
                digest = await asyncio.to_thread(sha256_of, self._part)
                if digest != self._asset.sha256:
                    # A wrong file never continues: start over next time.
                    self._part.unlink(missing_ok=True)
                    self._done_bytes = 0
                    raise SetupError("checksum_mismatch", "the download's checksum is wrong")
                self._phase = "installing"
                await asyncio.to_thread(self._unpack, self._asset)
            self._phase = None
            await self.ensure_running()
        except SetupError as error:
            self._phase, self._error = "failed", error.code
        except httpx.HTTPError:
            self._phase, self._error = "failed", "network"
        except OSError as error:
            _log.warning("Ollama setup: file error: %s", error)
            self._phase, self._error = "failed", "disk"

    async def _download(self, asset: Asset) -> None:
        self._part.parent.mkdir(parents=True, exist_ok=True)
        have = self._part.stat().st_size if self._part.is_file() else 0
        if have > asset.size_bytes:
            self._part.unlink()
            have = 0
        self._done_bytes = have
        if have == asset.size_bytes:
            return
        headers = {"Range": f"bytes={have}-"} if have else {}
        timeout = httpx.Timeout(30.0, read=120.0)
        async with (
            self._new_client() as client,
            client.stream(
                "GET", asset.url, headers=headers, timeout=timeout, follow_redirects=True
            ) as response,
        ):
            if response.status_code == 416:  # nothing left to fetch: the size check decides
                return
            response.raise_for_status()
            if have and response.status_code != 206:
                have = 0  # the server sent the whole file again
            with self._part.open("ab" if have else "wb") as out:
                self._done_bytes = have
                async for chunk in response.aiter_bytes(CHUNK):
                    await asyncio.to_thread(out.write, chunk)
                    self._done_bytes += len(chunk)
                    if self._done_bytes > asset.size_bytes:
                        raise SetupError("checksum_mismatch", "the download is too large")

    def _unpack(self, asset: Asset) -> None:
        """Unpack straight into the version folder; the marker, written last, makes it complete.

        No folder rename: right after unpacking, virus scanners hold the new programs open and
        Windows refuses to rename their folder (seen in the acceptance run of A1423).
        """
        target = self._version_dir
        for leftover in (target, self._dir / f"{self._runtime.version}.partial"):
            if leftover.exists():  # an unmarked, unfinished unpack of ours
                _with_retries(lambda path=leftover: shutil.rmtree(path))
        target.mkdir(parents=True)
        safe_extract(self._part, target)
        if not (target / asset.executable).is_file():
            raise SetupError("bad_archive", f"{asset.executable} missing in the archive")
        marker = {"version": self._runtime.version, "sha256": asset.sha256, "url": asset.url}
        (target / MARKER).write_text(json.dumps(marker), encoding="utf-8")
        _with_retries(lambda: self._part.unlink(missing_ok=True))  # 1.5 GB not needed any more
        self._done_bytes = 0

    # --- start / stop -------------------------------------------------------------------
    async def ensure_running(self) -> bool:
        """Start an installed Ollama when none answers. True when a usable one answers."""
        async with self._lock:
            running = await self._running_version()
            if running is not None:
                return self._fits(running)
            exe = self.managed_executable()
            if exe is None:
                found = await asyncio.to_thread(self._external_executable)
                exe = found[0] if found else None
            if exe is None:
                return False
            if urlsplit(self._url).hostname not in LOCAL_HOSTS:
                # Never open Ollama to the network (B2459): only a localhost address is served.
                self._phase, self._error = "failed", "start_failed"
                return False
            self._phase = "starting"
            try:
                self._process = self._launcher(exe, self._serve_env())
            except OSError:
                self._phase, self._error = "failed", "start_failed"
                return False
            loop = asyncio.get_running_loop()
            deadline = loop.time() + self._start_timeout_s
            while loop.time() < deadline:
                if self._process.poll() is not None:
                    break
                if await self._running_version() is not None:
                    self._phase = None
                    return True
                await asyncio.sleep(0.5)
            await asyncio.to_thread(self._stop_own)
            self._phase, self._error = "failed", "start_failed"
            return False

    def _serve_env(self) -> dict[str, str]:
        env = dict(os.environ)
        host = urlsplit(self._url)
        env["OLLAMA_HOST"] = f"{host.hostname}:{host.port or 11434}"  # localhost only (config)
        if self._models_dir is not None:
            env["OLLAMA_MODELS"] = str(self._models_dir)
        return env

    def _stop_own(self) -> None:
        process, self._process = self._process, None
        if process is None or process.poll() is not None:
            return
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)

    async def close(self) -> None:
        """Service end: stop a download and the Ollama we started (never the player's own)."""
        await self.cancel()
        await asyncio.to_thread(self._stop_own)
