"""Model residency (B7 S2): budget, never-together rules, LRU unloading, drivers, data file."""

from __future__ import annotations

import asyncio
import json
from pathlib import Path

import httpx
import pytest

from conftest import REFERENCE_PC, make_app
from glassworld_ai.config import Settings
from glassworld_ai.hardware import Gpu, HardwareInfo
from glassworld_ai.residency import (
    ComfyUIDriver,
    Footprint,
    HookDriver,
    OllamaDriver,
    Residency,
    ResidencyError,
    budget_mib,
    load_footprints,
)

REPO = Path(__file__).resolve().parents[2]
MODELS_FILE = REPO / "content" / "hardware" / "models.json"


class FakeDriver:
    def __init__(self, log: list[str]) -> None:
        self.log = log

    async def load(self, model_id: str) -> None:
        self.log.append(f"load {model_id}")

    async def unload(self, model_id: str) -> None:
        self.log.append(f"unload {model_id}")


def fp(model_id: str, vram: int, never_with: tuple[str, ...] = ()) -> Footprint:
    return Footprint(model_id, "language", "fake", vram, 1.0, frozenset(never_with))


def make(budget: int, *footprints: Footprint) -> tuple[Residency, list[str], list[float]]:
    log: list[str] = []
    now = [0.0]

    def clock() -> float:
        now[0] += 1
        return now[0]

    residency = Residency(
        {f.model_id: f for f in footprints}, budget, {"fake": FakeDriver(log)}, clock
    )
    return residency, log, now


def test_the_data_file_loads_and_the_game_is_subtracted() -> None:
    game, models = load_footprints(MODELS_FILE)
    assert game == 2560
    assert {"qwen3.5:9b", "moss-sound"} <= set(models)
    rtx3080 = HardwareInfo(
        gpus=(Gpu("RTX 3080", 10240),), cpu_name=None, logical_cpus=16, ram_mib=None
    )
    assert budget_mib(rtx3080, game) == 7680
    assert budget_mib(HardwareInfo((), None, None, None), game) == 0
    # On the reference PC the language model fits, and sound never runs next to it.
    assert models["qwen3.5:9b"].vram_mib <= 7680
    assert "qwen3.5:9b" in models["moss-sound"].never_with


def test_fits_without_unloading() -> None:
    residency, log, _ = make(10000, fp("a", 4000), fp("b", 5000))
    asyncio.run(residency.ensure("a"))
    asyncio.run(residency.ensure("b"))
    assert log == ["load a", "load b"]
    assert residency.loaded() == ["a", "b"]
    assert residency.used_mib() == 9000


def test_least_recently_used_makes_room() -> None:
    residency, log, _ = make(10000, fp("a", 4000), fp("b", 4000), fp("c", 4000))

    async def scenario() -> None:
        await residency.ensure("a")
        await residency.ensure("b")
        await residency.ensure("a")  # a used again -> b is the oldest
        await residency.ensure("c")

    asyncio.run(scenario())
    assert log == ["load a", "load b", "unload b", "load c"]
    assert residency.loaded() == ["a", "c"]


def test_never_together_unloads_even_with_room() -> None:
    residency, log, _ = make(20000, fp("llm", 6758), fp("sound", 5734, ("llm",)))

    async def scenario() -> None:
        await residency.ensure("llm")
        await residency.ensure("sound")
        await residency.ensure("llm")  # rule works both ways

    asyncio.run(scenario())
    assert log == ["load llm", "unload llm", "load sound", "unload sound", "load llm"]


def test_too_big_or_unknown_models_are_refused() -> None:
    residency, log, _ = make(7680, fp("edit", 9933))
    with pytest.raises(ResidencyError, match="budget"):
        asyncio.run(residency.ensure("edit"))
    with pytest.raises(ResidencyError, match="no footprint"):
        asyncio.run(residency.ensure("unknown"))
    assert log == []


def test_release_and_mark_loaded() -> None:
    residency, log, _ = make(10000, fp("llm", 6000), fp("img", 3000))
    residency.mark_loaded("llm")  # decider warmed up by the service start
    asyncio.run(residency.ensure("img"))
    asyncio.run(residency.release("llm"))
    asyncio.run(residency.release_all())
    assert log == ["load img", "unload llm", "unload img"]
    assert residency.loaded() == []


def test_changes_happen_one_at_a_time() -> None:
    log: list[str] = []

    class SlowDriver(FakeDriver):
        async def load(self, model_id: str) -> None:
            log.append(f"start {model_id}")
            await asyncio.sleep(0.01)
            log.append(f"done {model_id}")

    residency = Residency({"a": fp("a", 6000), "b": fp("b", 6000)}, 8000, {"fake": SlowDriver(log)})

    async def scenario() -> None:
        await asyncio.gather(residency.ensure("a"), residency.ensure("b"))

    asyncio.run(scenario())
    assert log[:2] == ["start a", "done a"]  # b waits until a is loaded, then unloads it
    assert residency.loaded() == ["b"]


def test_hook_driver_for_the_sound_model() -> None:
    calls: list[str] = []

    async def on_load(model_id: str) -> None:
        calls.append(f"hook load {model_id}")

    async def on_unload(model_id: str) -> None:
        calls.append(f"hook unload {model_id}")

    sound = Footprint("moss-sound", "sound", "hook", 5734, 50.0, frozenset())
    residency = Residency({"moss-sound": sound}, 7680, {})
    with pytest.raises(ResidencyError, match="no driver"):
        asyncio.run(residency.ensure("moss-sound"))
    residency.register_driver("hook", HookDriver(on_load, on_unload))
    asyncio.run(residency.ensure("moss-sound"))
    asyncio.run(residency.release("moss-sound"))
    assert calls == ["hook load moss-sound", "hook unload moss-sound"]


def test_ollama_and_comfyui_drivers_call_the_right_endpoints() -> None:
    seen: list[tuple[str, dict[str, object]]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append((request.url.path, json.loads(request.content)))
        return httpx.Response(200, json={})

    async def scenario() -> None:
        async with httpx.AsyncClient(transport=httpx.MockTransport(handler)) as client:
            ollama = OllamaDriver("http://ollama.test", client, "30m")
            comfy = ComfyUIDriver("http://comfy.test", client)
            await ollama.load("qwen3.5:9b")
            await ollama.unload("qwen3.5:9b")
            await comfy.load("z-image-turbo")
            await comfy.unload("z-image-turbo")

    asyncio.run(scenario())
    assert seen == [
        ("/api/generate", {"model": "qwen3.5:9b", "prompt": "", "keep_alive": "30m"}),
        ("/api/generate", {"model": "qwen3.5:9b", "keep_alive": 0}),
        ("/free", {"unload_models": True, "free_memory": True}),
    ]


def test_driver_errors_become_residency_errors() -> None:
    def down(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused", request=request)

    async def scenario() -> None:
        async with httpx.AsyncClient(transport=httpx.MockTransport(down)) as client:
            await ComfyUIDriver("http://comfy.test", client).unload("x")

    with pytest.raises(ResidencyError, match="ComfyUI"):
        asyncio.run(scenario())


def test_the_app_builds_the_residency_from_the_hardware(settings: Settings) -> None:
    app = make_app(settings)  # reference PC hardware
    residency: Residency = app.state.residency
    largest = max(g.vram_mib for g in REFERENCE_PC.gpus)
    assert residency.budget_mib == largest - 2560
    assert residency.loaded() == []


def test_mark_loaded_respects_budget_and_never_together() -> None:
    residency, _, _ = make(10000, fp("llm", 6758), fp("sound", 5734, ("llm",)), fp("small", 1000))
    asyncio.run(residency.ensure("sound"))
    with pytest.raises(ResidencyError, match="cannot be marked"):
        residency.mark_loaded("llm")  # never together with sound (review GW1 R1)
    residency.mark_loaded("small")
    assert residency.loaded() == ["small", "sound"]


def test_never_with_must_name_known_models(tmp_path: Path) -> None:
    data = json.loads(MODELS_FILE.read_text(encoding="utf-8"))
    data["models"][0]["never_with"] = ["no-such-model"]
    broken = tmp_path / "models.json"
    broken.write_text(json.dumps(data), encoding="utf-8")
    with pytest.raises(ValueError, match="never_with unknown"):
        load_footprints(broken)


def test_comfyui_free_unloads_every_comfyui_model() -> None:
    log: list[str] = []
    a = Footprint("img-a", "image", "comfyui", 2000, 1.0, frozenset())
    b = Footprint("img-b", "image", "comfyui", 2000, 1.0, frozenset())
    llm = Footprint("llm", "language", "fake", 7000, 1.0, frozenset())
    residency = Residency(
        {"img-a": a, "img-b": b, "llm": llm},
        9000,
        {"comfyui": FakeDriver(log), "fake": FakeDriver(log)},
    )

    async def scenario() -> None:
        await residency.ensure("img-a")
        await residency.ensure("img-b")
        await residency.ensure("llm")  # needs room: unloading img-a frees img-b as well

    asyncio.run(scenario())
    assert residency.loaded() == ["llm"]
    assert log == ["load img-a", "load img-b", "unload img-a", "load llm"]


def test_release_all_tries_every_model_and_reports_failures() -> None:
    log: list[str] = []

    class Broken(FakeDriver):
        async def unload(self, model_id: str) -> None:
            raise ResidencyError("ComfyUI down")

    img = Footprint("img", "image", "comfyui", 2000, 1.0, frozenset())
    llm = Footprint("llm", "language", "fake", 3000, 1.0, frozenset())
    residency = Residency(
        {"img": img, "llm": llm}, 9000, {"comfyui": Broken(log), "fake": FakeDriver(log)}
    )
    asyncio.run(residency.ensure("img"))
    asyncio.run(residency.ensure("llm"))
    with pytest.raises(ResidencyError, match="img: ComfyUI down"):
        asyncio.run(residency.release_all())
    assert "unload llm" in log  # the language model was still unloaded (review GW1 N2)


def test_warm_up_never_loads_past_the_budget(settings: Settings) -> None:
    from glassworld_ai.app import _warm_up  # pyright: ignore[reportPrivateUsage]

    small = HardwareInfo(
        gpus=(Gpu("8 GB card", 8192),), cpu_name=None, logical_cpus=8, ram_mib=None
    )
    app = make_app(settings, hardware=small)
    calls: list[str] = []

    class FakeOllama:
        model = "qwen3.5:9b"

        async def warm_up(self, timeout: float) -> None:
            calls.append("warm_up")

    async def scenario() -> None:
        task = asyncio.ensure_future(_warm_up(app, FakeOllama(), 0.01))  # type: ignore[arg-type]
        await asyncio.sleep(0.05)
        task.cancel()

    asyncio.run(scenario())
    assert app.state.warm_up == "failed"  # budget 5632 < 6615: refused, not loaded anyway
    assert calls == []
