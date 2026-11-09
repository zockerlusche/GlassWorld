"""AI models at first start (B10 R4): offer per tier, consent per model, digest/SHA-256, resume."""

from __future__ import annotations

import asyncio
import hashlib
import json
from pathlib import Path
from typing import Any

import httpx
import pytest
from fastapi import FastAPI

from conftest import TOKEN, get, make_app, post
from glassworld_ai.config import Settings
from glassworld_ai.model_setup import MARKER, Model, ModelFile, ModelSetup, load_catalog
from glassworld_ai.validation import ContractValidator

REPO = Path(__file__).resolve().parents[2]
OLLAMA = "http://127.0.0.1:11999"
DIGEST = "ab" * 32
FILE_A = b"A" * 3000
FILE_B = b"B" * 2000
URL_A = "https://huggingface.co/org/repo/resolve/0123abc/a.gguf"
URL_B = "https://huggingface.co/org/repo/resolve/0123abc/vae/b.safetensors"


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


LANGUAGE = Model(
    id="lm:9b",
    kind="ollama",
    role="language",
    tiers=("minimum", "standard", "comfort"),
    licence="Apache-2.0",
    size_bytes=900,
    source_page="https://ollama.com/library/lm:9b",
    purpose_key="ui.setup.model_language",
    ollama_name="lm:9b",
    ollama_digest=DIGEST,
)
IMAGE = Model(
    id="picture",
    kind="files",
    role="image",
    tiers=("standard", "comfort"),
    licence="Apache-2.0",
    size_bytes=len(FILE_A) + len(FILE_B),
    source_page="https://huggingface.co/org/repo",
    purpose_key="ui.setup.model_image",
    files=(
        ModelFile(URL_A, _sha(FILE_A), len(FILE_A), "a.gguf"),
        ModelFile(URL_B, _sha(FILE_B), len(FILE_B), "vae/b.safetensors"),
    ),
)


class FakeNet:
    """Fake Ollama (/api/tags, /api/pull stream) and a fake file host with Range."""

    def __init__(self, ollama_up: bool = True, pulled_digest: str = DIGEST) -> None:
        self.ollama_up = ollama_up
        self.models: dict[str, str] = {}
        self.pulled_digest = pulled_digest
        self.pull_error: str | None = None
        self.files = {URL_A: FILE_A, URL_B: FILE_B}
        self.ranges: list[tuple[str, str | None]] = []
        self.pulls = 0
        self.deleted: list[str] = []

    def handler(self, request: httpx.Request) -> httpx.Response:
        url = str(request.url)
        if url.startswith(OLLAMA):
            if not self.ollama_up:
                raise httpx.ConnectError("refused", request=request)
            if request.url.path == "/api/tags":
                listed = [{"name": n, "digest": d} for n, d in self.models.items()]
                return httpx.Response(200, json={"models": listed})
            if request.url.path == "/api/delete":
                name = json.loads(request.content)["model"]
                self.deleted.append(name)
                self.models.pop(name, None)
                return httpx.Response(200)
            if request.url.path == "/api/pull":
                self.pulls += 1
                name = json.loads(request.content)["model"]
                if self.pull_error:
                    lines = [{"status": "pulling manifest"}, {"error": self.pull_error}]
                else:
                    lines = [
                        {"status": "pulling manifest"},
                        {
                            "status": "pulling x",
                            "digest": "sha256:1",
                            "total": 600,
                            "completed": 300,
                        },
                        {
                            "status": "pulling x",
                            "digest": "sha256:1",
                            "total": 600,
                            "completed": 600,
                        },
                        {
                            "status": "pulling y",
                            "digest": "sha256:2",
                            "total": 300,
                            "completed": 300,
                        },
                        {"status": "verifying sha256 digest"},
                        {"status": "success"},
                    ]
                    self.models[name] = self.pulled_digest
                body = "\n".join(json.dumps(line) for line in lines) + "\n"
                return httpx.Response(200, content=body.encode())
        if url in self.files:
            data = self.files[url]
            header = request.headers.get("range")
            self.ranges.append((url, header))
            if header:
                start = int(header.removeprefix("bytes=").rstrip("-"))
                return httpx.Response(206, content=data[start:])
            return httpx.Response(200, content=data)
        return httpx.Response(404)


def _plenty(_path: Path) -> int:
    return 10**12


def _setup(
    tmp_path: Path, net: FakeNet, tier: str | None = "standard", free: Any = _plenty
) -> ModelSetup:
    return ModelSetup(
        (LANGUAGE, IMAGE),
        store_dir=tmp_path / "store",
        ollama_url=OLLAMA,
        tier=lambda: tier,
        transport=httpx.MockTransport(net.handler),
        ollama_models_dir=tmp_path / "ollama-models",
        free_bytes=free,
    )


async def _all(setup: ModelSetup, ids: list[str]) -> Any:
    assert setup.start(ids) == []
    task = setup._task  # pyright: ignore[reportPrivateUsage]
    assert task is not None
    await task
    return await setup.status()


def _by_id(status: Any) -> dict[str, Any]:
    return {m.id: m for m in status.models}


def test_the_data_file_offers_the_configured_language_model_to_every_tier(
    settings: Settings,
) -> None:
    catalog = load_catalog(REPO / "content/ai/model-downloads.json")
    language = [m for m in catalog if m.role == "language"]
    assert len(language) == 1
    model = language[0]
    assert model.ollama_name == settings.decider_model
    assert (model.ollama_digest or "").startswith(settings.decider_model_digest)
    assert set(model.tiers) == {"minimum", "standard", "comfort"}
    i18n = {
        loc: {
            e["key"]
            for e in json.loads((REPO / f"content/i18n/{loc}.json").read_text("utf-8"))["entries"]
        }
        for loc in ("de", "en")
    }
    for m in catalog:
        assert m.purpose_key in i18n["de"] and m.purpose_key in i18n["en"]


def test_the_offer_follows_the_tier(tmp_path: Path) -> None:
    net = FakeNet()

    async def body() -> Any:
        return [
            await _setup(tmp_path, net, tier).status() for tier in ("minimum", "standard", None)
        ]

    minimum, standard, none = asyncio.run(body())
    assert [m.id for m in minimum.models] == ["lm:9b"]  # minimum: only the language model
    assert [m.id for m in standard.models] == ["lm:9b", "picture"]
    assert none.models == [] and none.total_bytes == 0
    assert standard.total_bytes == standard.missing_bytes == 900 + 5000
    assert {m.state for m in standard.models} == {"missing"}


def test_only_offered_models_can_be_started(tmp_path: Path) -> None:
    net = FakeNet()

    async def body() -> list[str]:
        return _setup(tmp_path, net, "minimum").start(["lm:9b", "picture"])

    assert asyncio.run(body()) == ["picture"]


def test_ollama_model_is_pulled_and_its_digest_checked(tmp_path: Path) -> None:
    net = FakeNet()

    async def body() -> Any:
        return await _all(_setup(tmp_path, net), ["lm:9b"])

    model = _by_id(asyncio.run(body()))["lm:9b"]
    assert model.state == "present" and model.done_bytes == 900
    assert net.pulls == 1


def test_a_present_model_is_not_pulled_again(tmp_path: Path) -> None:
    net = FakeNet()
    net.models["lm:9b"] = DIGEST

    async def body() -> Any:
        return await _all(_setup(tmp_path, net), ["lm:9b"])

    assert _by_id(asyncio.run(body()))["lm:9b"].state == "present"
    assert net.pulls == 0


def test_a_moved_tag_is_removed_again_and_reported(tmp_path: Path) -> None:
    """Review GW1 #125: a pull with another digest must not stay installed."""
    net = FakeNet(pulled_digest="cd" * 32)

    async def body() -> Any:
        return await _all(_setup(tmp_path, net), ["lm:9b"])

    model = _by_id(asyncio.run(body()))["lm:9b"]
    assert model.state == "failed" and model.error == "model_moved"
    assert net.deleted == ["lm:9b"] and "lm:9b" not in net.models


def test_a_pull_error_is_reported(tmp_path: Path) -> None:
    net = FakeNet()
    net.pull_error = "connection reset"

    async def body() -> Any:
        return await _all(_setup(tmp_path, net), ["lm:9b"])

    model = _by_id(asyncio.run(body()))["lm:9b"]
    assert model.state == "failed" and model.error == "network"


def test_without_ollama_the_language_model_waits_for_it(tmp_path: Path) -> None:
    net = FakeNet(ollama_up=False)

    async def body() -> Any:
        setup = _setup(tmp_path, net)
        before = await setup.status()
        after = await _all(setup, ["lm:9b"])
        return before, after

    before, after = asyncio.run(body())
    assert _by_id(before)["lm:9b"].state == "waiting_for_runtime"
    assert _by_id(after)["lm:9b"].state == "waiting_for_runtime"  # failed, but Ollama is missing


def test_files_are_checked_moved_and_marked(tmp_path: Path) -> None:
    net = FakeNet()

    async def body() -> Any:
        return await _all(_setup(tmp_path, net), ["picture"])

    model = _by_id(asyncio.run(body()))["picture"]
    folder = tmp_path / "store/picture"
    assert model.state == "present" and model.done_bytes == 5000
    assert (folder / "a.gguf").read_bytes() == FILE_A
    assert (folder / "vae/b.safetensors").read_bytes() == FILE_B
    assert (folder / MARKER).is_file()
    assert not list(folder.rglob("*.part"))


def test_files_continue_where_they_stopped(tmp_path: Path) -> None:
    net = FakeNet()
    part = tmp_path / "store/picture/a.gguf.part"
    part.parent.mkdir(parents=True)
    part.write_bytes(FILE_A[:1200])

    async def body() -> Any:
        setup = _setup(tmp_path, net)
        before = await setup.status()
        return before, await _all(setup, ["picture"])

    before, after = asyncio.run(body())
    assert _by_id(before)["picture"].done_bytes == 1200
    assert (URL_A, "bytes=1200-") in net.ranges
    assert _by_id(after)["picture"].state == "present"


def test_a_wrong_file_is_removed_and_reported(tmp_path: Path) -> None:
    net = FakeNet()
    net.files[URL_B] = b"X" * len(FILE_B)  # same size, other content

    async def body() -> Any:
        return await _all(_setup(tmp_path, net), ["picture"])

    model = _by_id(asyncio.run(body()))["picture"]
    assert model.state == "failed" and model.error == "checksum_mismatch"
    assert not (tmp_path / "store/picture/vae/b.safetensors.part").exists()
    assert not (tmp_path / "store/picture" / MARKER).exists()
    assert (tmp_path / "store/picture/a.gguf").is_file()  # the good file stays


def test_cancel_keeps_what_is_on_disk(tmp_path: Path) -> None:
    net = FakeNet()
    part = tmp_path / "store/picture/a.gguf.part"
    part.parent.mkdir(parents=True)
    part.write_bytes(FILE_A[:500])

    async def body() -> Any:
        setup = _setup(tmp_path, net)
        assert setup.start(["picture", "lm:9b"]) == []
        await setup.cancel()
        return setup.busy(), await setup.status()

    busy, status = asyncio.run(body())
    assert busy is False
    assert {m.state for m in status.models} == {"missing"}
    assert part.stat().st_size >= 500  # the next download continues from here


def _wrong_size(d: dict[str, Any]) -> None:
    d["models"][0]["size_bytes"] = 1


def _unsafe_path(d: dict[str, Any]) -> None:
    d["models"][0]["files"][0]["path"] = "../x"


def _no_files(d: dict[str, Any]) -> None:
    del d["models"][0]["files"]


@pytest.mark.parametrize(
    ("change", "message"),
    [(_wrong_size, "size_bytes"), (_unsafe_path, "unsafe"), (_no_files, "needs 'files'")],
)
def test_the_catalog_loader_refuses_broken_data(tmp_path: Path, change: Any, message: str) -> None:
    data: dict[str, Any] = {
        "contract_version": "0.2",
        "models": [
            {
                "id": "picture",
                "kind": "files",
                "role": "image",
                "tiers": ["standard"],
                "licence": "Apache-2.0",
                "size_bytes": 5000,
                "source_page": "https://huggingface.co/org/repo",
                "purpose_key": "ui.setup.model_image",
                "files": [
                    {"url": URL_A, "sha256": _sha(FILE_A), "size_bytes": 3000, "path": "a.gguf"},
                    {"url": URL_B, "sha256": _sha(FILE_B), "size_bytes": 2000, "path": "b.st"},
                ],
            }
        ],
    }
    path = tmp_path / "m.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    load_catalog(path)  # the base is fine
    change(data)
    path.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ValueError, match=message):
        load_catalog(path)


def test_status_matches_the_contract(tmp_path: Path) -> None:
    net = FakeNet()
    validator = ContractValidator(REPO / "contracts/schemas")

    async def body() -> Any:
        return await _setup(tmp_path, net).status()

    assert validator.errors("model-setup-status", asyncio.run(body()).model_dump(mode="json")) == []


def _offline_app(settings: Settings, tmp_path: Path) -> FastAPI:
    patched = settings.model_copy(
        update={
            "ollama_url": "http://127.0.0.1:9",
            "ollama_install_dir": tmp_path / "ollama",
            "model_store_dir": tmp_path / "store",
            "ollama_autostart": False,
        }
    )
    return make_app(patched)


def test_the_download_needs_consent_and_named_offered_models(
    settings: Settings, tmp_path: Path
) -> None:
    app = _offline_app(settings, tmp_path)
    bodies: list[dict[str, object]] = [
        {"models": ["qwen3.5:9b"]},
        {"consent": True},
        {"consent": True, "models": []},
        {"consent": True, "models": ["not-offered"]},
    ]
    for body in bodies:
        response = post(app, "/setup/models/download", body)
        assert response.status_code == 422, body
        assert response.json()["code"] == "bad_request"


def test_get_models_answers_with_the_contract(settings: Settings, tmp_path: Path) -> None:
    app = _offline_app(settings, tmp_path)
    response = get(app, "/setup/models", {"x-gw-token": TOKEN})
    assert response.status_code == 200
    validator = ContractValidator(REPO / "contracts/schemas")
    assert validator.errors("model-setup-status", response.json()) == []
    body = response.json()
    # Reference PC = tier standard: the language model and the image AI (in the player's ComfyUI).
    assert [m["id"] for m in body["models"]] == ["qwen3.5:9b", "z-image-turbo"]
    image = body["models"][1]
    assert image["state"] == "waiting_for_runtime"  # the ComfyUI folder is not known yet
    assert body["comfyui"]["models_dir_known"] is False


def test_a_full_disk_is_found_before_writing(tmp_path: Path) -> None:
    """Review GW1 #125: refuse before a multi-GB download, not after filling the disk."""
    net = FakeNet()

    def little(_path: Path) -> int:
        return 100  # less than either model

    async def body() -> Any:
        return await _all(_setup(tmp_path, net, free=little), ["lm:9b", "picture"])

    models = _by_id(asyncio.run(body()))
    assert models["lm:9b"].error == "disk_full" and models["picture"].error == "disk_full"
    assert net.pulls == 0 and not net.ranges


def test_free_space_counts_files_already_on_disk(tmp_path: Path) -> None:
    net = FakeNet()
    part = tmp_path / "store/picture/a.gguf.part"
    part.parent.mkdir(parents=True)
    part.write_bytes(FILE_A[:3000])  # 3000 of 5000 there

    def just_enough(_path: Path) -> int:
        return 2000

    async def body() -> Any:
        return await _all(_setup(tmp_path, net, free=just_enough), ["picture"])

    assert _by_id(asyncio.run(body()))["picture"].state == "present"


def test_the_players_other_version_is_never_replaced_without_asking(tmp_path: Path) -> None:
    """Review GW1 #125: the player's own model under the same name stays unless agreed."""
    net = FakeNet()
    net.models["lm:9b"] = "ee" * 32  # the player's own, another version

    async def body() -> Any:
        setup = _setup(tmp_path, net)
        before = await setup.status()
        after = await _all(setup, ["lm:9b"])
        return before, after

    before, after = asyncio.run(body())
    assert _by_id(before)["lm:9b"].state == "other_version"
    assert _by_id(after)["lm:9b"].error == "other_version"
    assert net.pulls == 0 and net.models["lm:9b"] == "ee" * 32


def test_replacing_the_players_version_needs_consent_and_is_never_deleted(
    tmp_path: Path,
) -> None:
    net = FakeNet()
    net.models["lm:9b"] = "ee" * 32

    async def agreed() -> Any:
        setup = _setup(tmp_path, net)
        assert setup.start(["lm:9b"], replace=["lm:9b"]) == []
        assert setup._task is not None  # pyright: ignore[reportPrivateUsage]
        await setup._task  # pyright: ignore[reportPrivateUsage]
        return await setup.status()

    assert _by_id(asyncio.run(agreed()))["lm:9b"].state == "present"
    moved = FakeNet(pulled_digest="cd" * 32)
    moved.models["lm:9b"] = "ee" * 32

    async def tag_moved() -> Any:
        setup = _setup(tmp_path, moved)
        assert setup.start(["lm:9b"], replace=["lm:9b"]) == []
        assert setup._task is not None  # pyright: ignore[reportPrivateUsage]
        await setup._task  # pyright: ignore[reportPrivateUsage]
        return await setup.status()

    assert _by_id(asyncio.run(tag_moved()))["lm:9b"].error == "model_moved"
    assert moved.deleted == []  # it replaced the player's own as agreed: never deleted


COMFY = "http://127.0.0.1:18188"
PICTURE = Model(
    id="picture-c",
    kind="files",
    role="image",
    tiers=("standard",),
    licence="Apache-2.0",
    size_bytes=len(FILE_A) + len(FILE_B),
    source_page="https://huggingface.co/org/repo",
    purpose_key="ui.setup.model_image",
    files=(
        ModelFile(URL_A, _sha(FILE_A), len(FILE_A), "unet/a.gguf"),
        ModelFile(URL_B, _sha(FILE_B), len(FILE_B), "vae/b.safetensors"),
    ),
    target="comfyui",
    requires_nodes=("UnetLoaderGGUF",),
)


def _comfy_setup(
    tmp_path: Path, net: FakeNet, nodes: set[str], models_dir: Path | None
) -> ModelSetup:
    def handler(request: httpx.Request) -> httpx.Response:
        if str(request.url).startswith(COMFY + "/object_info/"):
            node = request.url.path.rsplit("/", 1)[-1]
            return httpx.Response(200, json={node: {}} if node in nodes else {})
        return net.handler(request)

    return ModelSetup(
        (PICTURE,),
        store_dir=tmp_path / "store",
        ollama_url=OLLAMA,
        tier=lambda: "standard",
        transport=httpx.MockTransport(handler),
        free_bytes=_plenty,
        comfyui_models_dir=models_dir,
        comfyui_url=COMFY,
    )


def test_image_models_wait_for_the_players_comfyui_folder(tmp_path: Path) -> None:
    async def body() -> Any:
        setup = _comfy_setup(tmp_path, FakeNet(), {"UnetLoaderGGUF"}, None)
        return await _all(setup, ["picture-c"])

    status = asyncio.run(body())
    assert status.models[0].state == "waiting_for_runtime"
    assert status.comfyui is not None and status.comfyui.models_dir_known is False


def test_image_models_go_into_the_players_comfyui_and_nothing_else(tmp_path: Path) -> None:
    comfy = tmp_path / "ComfyUI" / "models"
    comfy.mkdir(parents=True)

    async def body() -> Any:
        return await _all(
            _comfy_setup(tmp_path, FakeNet(), {"UnetLoaderGGUF"}, comfy), ["picture-c"]
        )

    status = asyncio.run(body())
    assert status.models[0].state == "present"
    assert (comfy / "unet/a.gguf").read_bytes() == FILE_A
    assert (comfy / "vae/b.safetensors").read_bytes() == FILE_B
    assert sorted(p.name for p in comfy.rglob("*") if p.is_file()) == ["a.gguf", "b.safetensors"]
    assert (tmp_path / "store/picture-c" / MARKER).is_file()  # our marker stays with us
    assert status.comfyui is not None and status.comfyui.missing_nodes == []


def test_files_the_player_already_has_count_and_others_are_never_overwritten(
    tmp_path: Path,
) -> None:
    comfy = tmp_path / "ComfyUI" / "models"
    (comfy / "unet").mkdir(parents=True)
    (comfy / "vae").mkdir(parents=True)
    (comfy / "unet/a.gguf").write_bytes(FILE_A)  # already there, same size
    (comfy / "vae/b.safetensors").write_bytes(b"the player's own other file")
    net = FakeNet()

    async def body() -> Any:
        return await _all(_comfy_setup(tmp_path, net, {"UnetLoaderGGUF"}, comfy), ["picture-c"])

    status = asyncio.run(body())
    assert status.models[0].error == "other_version"
    assert (comfy / "vae/b.safetensors").read_bytes() == b"the player's own other file"
    assert not net.ranges  # nothing downloaded: a.gguf was there, b needs consent


def test_missing_custom_nodes_are_named(tmp_path: Path) -> None:
    comfy = tmp_path / "ComfyUI" / "models"
    comfy.mkdir(parents=True)

    async def body() -> Any:
        return await _comfy_setup(tmp_path, FakeNet(), set(), comfy).status()

    status = asyncio.run(body())
    assert status.comfyui is not None
    assert status.comfyui.reachable is True
    assert status.comfyui.missing_nodes == ["UnetLoaderGGUF"]


def test_the_data_offers_image_ai_for_standard_and_editing_for_comfort() -> None:
    catalog = {m.id: m for m in load_catalog(REPO / "content/ai/model-downloads.json")}
    assert catalog["z-image-turbo"].target == "comfyui"
    assert set(catalog["z-image-turbo"].tiers) == {"standard", "comfort"}
    assert catalog["qwen-image-edit-2511"].tiers == ("comfort",)
    workflows = {
        p.name: p.read_text(encoding="utf-8")
        for p in (REPO / "tools/comfyui/workflows").glob("*.json")
    }
    for model in (catalog["z-image-turbo"], catalog["qwen-image-edit-2511"]):
        for item in model.files:
            # every file the data downloads is one a workflow loads, under the same name
            assert any(item.path.rsplit("/", 1)[-1] in text for text in workflows.values()), item
        assert all(f.url.startswith("https://huggingface.co/") and "/resolve/" in f.url
                   for f in model.files)  # fmt: skip
