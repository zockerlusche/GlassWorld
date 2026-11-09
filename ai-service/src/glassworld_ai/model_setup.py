"""AI models at first start (B10 R4).

content/ai/model-downloads.json lists the models per hardware tier with name, licence, size,
purpose and official source. GET /setup/models shows the detected tier's models and what is
already there; the game's dialog asks the player per model and only then calls
POST /setup/models/download with the agreed ids. One model at a time is loaded:

- kind `ollama`: pulled through the local Ollama from its registry (POST /api/pull, which
  continues partial layers by itself); afterwards the manifest digest must match the data.
- kind `files`: single files from an official source, resumable, SHA-256 checked, then moved
  into the model folder; a marker written last makes the model complete.

A cancel stops the running download; what is on disk stays and the next download continues.

Image models go into the models folder of the player's own ComfyUI (B2514, target `comfyui`),
once the game knows it; files already there with the same name and size count as present, other
files of the same name are never overwritten without consent. The status says whether ComfyUI
answers and which custom nodes the workflows need are missing (e.g. ComfyUI-GGUF).

If the player's own Ollama already holds a model of the same name in another version, it is
reported as `other_version` and only replaced when the player agreed (`replace`). The free-space
check looks at the configured Ollama model folder (or ~/.ollama/models); the folder of a player's
own Ollama with a custom OLLAMA_MODELS cannot be read over its API.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import os
import shutil
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Literal

import httpx

from glassworld_ai.downloads import TooLargeError, fetch_resumable, sha256_of, with_retries
from glassworld_contracts import CONTRACT_VERSION, ModelSetupStatus

State = Literal[
    "present",
    "missing",
    "other_version",
    "queued",
    "downloading",
    "verifying",
    "waiting_for_runtime",
    "failed",
]
MARKER = ".glassworld-model.json"
_log = logging.getLogger(__name__)


class ModelError(Exception):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True)
class ModelFile:
    url: str
    sha256: str
    size_bytes: int
    path: str


@dataclass(frozen=True)
class Model:
    id: str
    kind: Literal["ollama", "files"]
    role: str
    tiers: tuple[str, ...]
    licence: str
    size_bytes: int
    source_page: str
    purpose_key: str
    ollama_name: str | None = None
    ollama_digest: str | None = None
    files: tuple[ModelFile, ...] = ()
    target: Literal["store", "comfyui"] = "store"
    requires_nodes: tuple[str, ...] = ()


def load_catalog(path: Path) -> tuple[Model, ...]:
    """Read the data file and check what a schema cannot (sizes add up, safe paths)."""
    data: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
    models: list[Model] = []
    for raw in data["models"]:
        files = tuple(ModelFile(**f) for f in raw.get("files", []))
        ollama = raw.get("ollama")
        model = Model(
            id=raw["id"],
            kind=raw["kind"],
            role=raw["role"],
            tiers=tuple(raw["tiers"]),
            licence=raw["licence"],
            size_bytes=int(raw["size_bytes"]),
            source_page=raw["source_page"],
            purpose_key=raw["purpose_key"],
            ollama_name=ollama["name"] if ollama else None,
            ollama_digest=ollama["digest"] if ollama else None,
            files=files,
            target=raw.get("target", "store"),
            requires_nodes=tuple(raw.get("requires_nodes", [])),
        )
        if model.kind == "ollama" and (ollama is None or files):
            raise ValueError(f"{model.id}: kind ollama needs 'ollama' and no 'files'")
        if model.kind == "files":
            if not files or ollama is not None:
                raise ValueError(f"{model.id}: kind files needs 'files' and no 'ollama'")
            if sum(f.size_bytes for f in files) != model.size_bytes:
                raise ValueError(f"{model.id}: size_bytes is not the sum of its files")
            for f in files:
                if ".." in PurePosixPath(f.path).parts or f.path.startswith("/"):
                    raise ValueError(f"{model.id}: unsafe path {f.path}")
        models.append(model)
    if len({m.id for m in models}) != len(models):
        raise ValueError("model ids must be unique")
    return tuple(models)


class ModelSetup:
    def __init__(
        self,
        catalog: tuple[Model, ...],
        *,
        store_dir: Path,
        ollama_url: str,
        tier: Callable[[], str | None],
        transport: httpx.AsyncBaseTransport | None = None,
        ollama_models_dir: Path | None = None,
        free_bytes: Callable[[Path], int] | None = None,
        comfyui_models_dir: Path | None = None,
        comfyui_url: str | None = None,
    ) -> None:
        self._catalog = catalog
        # The player's own ComfyUI (B2514): its models folder, set by the game after the player
        # named it; None = not known yet (image models wait for it).
        self._comfyui_models = comfyui_models_dir
        self._comfyui = comfyui_url.rstrip("/") if comfyui_url else None
        self._ollama_models_dir = ollama_models_dir or _default_ollama_models(store_dir)
        self._free_bytes = free_bytes or _free_bytes
        self._store = store_dir
        self._ollama = ollama_url.rstrip("/")
        self._tier = tier
        self._transport = transport
        self._phase: dict[str, State] = {}
        self._error: dict[str, str] = {}
        self._done: dict[str, int] = {}
        self._queue: list[str] = []
        self._replace: set[str] = set()
        self._task: asyncio.Task[None] | None = None

    def _client(self) -> httpx.AsyncClient:
        return httpx.AsyncClient(transport=self._transport)

    def offered(self) -> tuple[Model, ...]:
        tier = self._tier()
        return tuple(m for m in self._catalog if tier is not None and tier in m.tiers)

    # --- what is there ------------------------------------------------------------------
    async def _ollama_models(self) -> dict[str, str] | None:
        """name -> digest of the local Ollama, None when Ollama does not answer."""
        try:
            async with self._client() as client:
                response = await client.get(self._ollama + "/api/tags", timeout=5)
            response.raise_for_status()
            listed = response.json().get("models", [])
        except (httpx.HTTPError, ValueError, AttributeError):
            return None
        return {str(m.get("name")): str(m.get("digest", "")) for m in listed}

    def _own(self, model: Model) -> Path:
        """Our folder for a model: its files (target store) or only its marker (target comfyui)."""
        return self._store / model.id.replace(":", "_")

    def _folder(self, model: Model) -> Path:
        if model.target == "comfyui":
            if self._comfyui_models is None:
                raise ModelError("runtime_missing", "the ComfyUI models folder is not known yet")
            return self._comfyui_models
        return self._own(model)

    def _files_present(self, model: Model) -> bool:
        if model.target == "comfyui" and self._comfyui_models is None:
            return False
        folder = self._folder(model)
        # In the player's ComfyUI the files may already be there from before (same name and size).
        if model.target == "store" and not (folder / MARKER).is_file():
            return False
        return all(
            (folder / f.path).is_file() and (folder / f.path).stat().st_size == f.size_bytes
            for f in model.files
        )

    def _files_on_disk(self, model: Model) -> int:
        if model.target == "comfyui" and self._comfyui_models is None:
            return 0
        folder, done = self._folder(model), 0
        for f in model.files:
            final, part = folder / f.path, folder / f"{f.path}.part"
            if final.is_file():
                done += final.stat().st_size
            elif part.is_file():
                done += part.stat().st_size
        return done

    async def status(self) -> ModelSetupStatus:
        offered = self.offered()
        local = await self._ollama_models() if any(m.kind == "ollama" for m in offered) else {}
        rows: list[dict[str, Any]] = []
        for model in offered:
            state: State
            done = self._done.get(model.id, 0)
            phase = self._phase.get(model.id)
            if phase in {"queued", "downloading", "verifying"}:
                state = phase
            elif model.kind == "ollama":
                if local is None:
                    state = "waiting_for_runtime"
                elif _has_digest(local, model):
                    state, done = "present", model.size_bytes
                elif phase == "failed":
                    state = "failed"
                elif _local_name(local, model) is not None:
                    state = "other_version"  # the player's own, another version (review #125)
                else:
                    state = "missing"
            elif model.target == "comfyui" and self._comfyui_models is None:
                state = "waiting_for_runtime"  # the game asks the player for the ComfyUI folder
            elif self._files_present(model):
                state, done = "present", model.size_bytes
            else:
                state = phase if phase == "failed" else "missing"
                done = self._files_on_disk(model)
            rows.append(
                {
                    "id": model.id,
                    "role": model.role,
                    "licence": model.licence,
                    "size_bytes": model.size_bytes,
                    "done_bytes": min(done, model.size_bytes),
                    "source_page": model.source_page,
                    "purpose_key": model.purpose_key,
                    "state": state,
                    "error": self._error.get(model.id) if state == "failed" else None,
                }
            )
        total = sum(m.size_bytes for m in offered)
        missing = sum(r["size_bytes"] - r["done_bytes"] for r in rows if r["state"] != "present")
        return ModelSetupStatus.model_validate(
            {
                "contract_version": CONTRACT_VERSION,
                "tier": self._tier(),
                "total_bytes": total,
                "missing_bytes": missing,
                "models": rows,
                "comfyui": await self._comfyui_status(offered),
            }
        )

    async def _comfyui_status(self, offered: tuple[Model, ...]) -> dict[str, Any] | None:
        """Image models in the player's ComfyUI: folder known, ComfyUI answers, nodes present."""
        needed = sorted({n for m in offered if m.target == "comfyui" for n in m.requires_nodes})
        if not any(m.target == "comfyui" for m in offered):
            return None
        missing: list[str] = []
        reachable = False
        if self._comfyui is not None:
            try:
                async with self._client() as client:
                    for node in needed:
                        response = await client.get(
                            f"{self._comfyui}/object_info/{node}", timeout=5
                        )
                        response.raise_for_status()
                        reachable = True
                        if node not in response.json():
                            missing.append(node)
            except (httpx.HTTPError, ValueError, AttributeError):
                reachable, missing = False, []
        return {
            "models_dir_known": self._comfyui_models is not None,
            "reachable": reachable,
            "missing_nodes": missing if reachable else needed,
        }

    # --- download -----------------------------------------------------------------------
    def busy(self) -> bool:
        return self._task is not None and not self._task.done()

    def start(self, ids: list[str], replace: list[str] | None = None) -> list[str]:
        """Queue the models the player agreed to; returns ids that are not offered (refused).

        `replace`: models of which the player's Ollama holds another version under the same name
        and the player agreed to replace it (state other_version, review GW1 #125).
        """
        offered = {m.id for m in self.offered()}
        unknown = [i for i in [*ids, *(replace or [])] if i not in offered]
        if unknown:
            return unknown
        self._replace = set(replace or [])
        for model_id in ids:
            if model_id not in self._queue and self._phase.get(model_id) not in {
                "downloading",
                "verifying",
            }:
                self._queue.append(model_id)
                self._phase[model_id] = "queued"
                self._error.pop(model_id, None)
        if not self.busy():
            self._task = asyncio.create_task(self._work())
        return []

    async def cancel(self) -> None:
        """Stop the running download and forget the queue; files on disk stay (resume)."""
        task = self._task
        queued, self._queue = self._queue, []
        if task is not None and not task.done():
            if not self._verifying():  # a checksum thread cannot be stopped: let it finish
                task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await asyncio.shield(task)
        for model_id in queued:
            self._phase.pop(model_id, None)
        for model_id, phase in list(self._phase.items()):
            if phase in {"queued", "downloading"}:
                self._phase.pop(model_id, None)

    def _verifying(self) -> bool:
        return any(p == "verifying" for p in self._phase.values())

    async def close(self) -> None:
        await self.cancel()

    async def _work(self) -> None:
        while self._queue:
            model_id = self._queue.pop(0)
            model = next(m for m in self._catalog if m.id == model_id)
            self._phase[model_id] = "downloading"
            try:
                self._check_space(model)
                if model.kind == "ollama":
                    await self._pull(model)
                else:
                    await self._fetch_files(model)
                self._phase.pop(model_id, None)
            except ModelError as error:
                self._phase[model_id], self._error[model_id] = "failed", error.code
            except httpx.HTTPError as error:
                _log.warning("model download %s: %s", model_id, error)
                self._phase[model_id], self._error[model_id] = "failed", "network"
            except OSError as error:
                _log.warning("model download %s: file error: %s", model_id, error)
                self._phase[model_id], self._error[model_id] = "failed", "disk"

    def _check_space(self, model: Model) -> None:
        """Refuse before writing when the disk cannot hold the rest (review GW1 #125)."""
        where = self._ollama_models_dir if model.kind == "ollama" else self._folder(model)
        need = model.size_bytes - (0 if model.kind == "ollama" else self._files_on_disk(model))
        if self._free_bytes(where) < need:
            raise ModelError("disk_full", f"{need} bytes needed in {where}")

    async def _pull(self, model: Model) -> None:
        local = await self._ollama_models()
        if local is None:
            raise ModelError("runtime_missing", "Ollama does not answer")
        if _has_digest(local, model):
            return
        existed = _local_name(local, model) is not None
        if existed and model.id not in self._replace:
            # Never overwrite the player's own model of the same name without asking.
            raise ModelError("other_version", f"{model.ollama_name} exists in another version")
        layers: dict[str, int] = {}
        timeout = httpx.Timeout(30.0, read=600.0)
        async with (
            self._client() as client,
            client.stream(
                "POST",
                self._ollama + "/api/pull",
                json={"model": model.ollama_name, "stream": True},
                timeout=timeout,
            ) as response,
        ):
            response.raise_for_status()
            async for line in response.aiter_lines():
                if not line.strip():
                    continue
                event: dict[str, Any] = json.loads(line)
                if "error" in event:
                    _log.warning("ollama pull %s: %s", model.id, event["error"])
                    raise ModelError("network", str(event["error"])[:200])
                digest = event.get("digest")
                if isinstance(digest, str) and isinstance(event.get("completed"), int):
                    layers[digest] = int(event["completed"])
                    self._done[model.id] = sum(layers.values())
                if str(event.get("status", "")).startswith("verifying"):
                    self._phase[model.id] = "verifying"
        local = await self._ollama_models()
        if local is None or not _has_digest(local, model):
            # The registry tag has moved (review GW1 #125): remove what was just pulled so it
            # neither takes space nor is used, and say so; the data needs a new pinned digest.
            if existed:  # it replaced the player's own, as agreed: keep it, only report
                raise ModelError("model_moved", f"{model.ollama_name} now has another digest")
            with contextlib.suppress(httpx.HTTPError):
                async with self._client() as client:
                    await client.request(
                        "DELETE", self._ollama + "/api/delete", json={"model": model.ollama_name}
                    )
            raise ModelError("model_moved", f"{model.ollama_name} now has another digest")

    async def _fetch_files(self, model: Model) -> None:
        folder = self._folder(model)
        finished = 0
        for item in model.files:
            final, part = folder / item.path, folder / f"{item.path}.part"
            if final.is_file() and final.stat().st_size == item.size_bytes:
                finished += item.size_bytes  # also a file the player's ComfyUI already had
                continue
            if model.target == "comfyui" and final.exists() and model.id not in self._replace:
                # Another file of the same name in the player's ComfyUI: never overwrite it
                # without asking (like the player's own Ollama model, review GW1 #125).
                raise ModelError("other_version", f"{item.path} exists with another size")

            def seen(done: int, base: int = finished) -> None:
                self._done[model.id] = base + done

            try:
                async with self._client() as client:
                    await fetch_resumable(client, item.url, part, item.size_bytes, seen)
            except TooLargeError as error:
                part.unlink(missing_ok=True)
                raise ModelError("checksum_mismatch", str(error)) from error
            self._phase[model.id] = "verifying"
            if await asyncio.to_thread(sha256_of, part) != item.sha256:
                part.unlink(missing_ok=True)  # a wrong file never continues
                raise ModelError("checksum_mismatch", f"{item.path}: wrong checksum")
            await asyncio.to_thread(with_retries, lambda p=part, f=final: os.replace(p, f))
            self._phase[model.id] = "downloading"
            finished += item.size_bytes
        marker = {"id": model.id, "files": {f.path: f.sha256 for f in model.files}}
        own = self._own(model)  # in the player's ComfyUI we write nothing but the model files
        own.mkdir(parents=True, exist_ok=True)
        (own / MARKER).write_text(json.dumps(marker), encoding="utf-8")


def _has_digest(local: dict[str, str], model: Model) -> bool:
    name = model.ollama_name or ""
    found = local.get(name) or local.get(f"{name}:latest")
    return (
        found is not None
        and model.ollama_digest is not None
        and (found == model.ollama_digest or model.ollama_digest.startswith(found))
    )


def _free_bytes(path: Path) -> int:
    """Free space of the drive that holds `path` (the folder may not exist yet)."""
    probe = path
    while not probe.exists() and probe.parent != probe:
        probe = probe.parent
    return shutil.disk_usage(probe).free


def _local_name(local: dict[str, str], model: Model) -> str | None:
    name = model.ollama_name or ""
    for candidate in (name, f"{name}:latest"):
        if candidate in local:
            return candidate
    return None


def _default_ollama_models(store_dir: Path) -> Path:
    """Where Ollama keeps models by default (~/.ollama/models). Without a home folder (service
    account, review GW1 #130) the free-space check looks at the drive of our store instead."""
    try:
        return Path.home() / ".ollama" / "models"
    except RuntimeError:
        return store_dir
