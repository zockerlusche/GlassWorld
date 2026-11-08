"""Measurement run (B7 S7, A1410): order through the residency, values, storage, planning, API."""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any

import httpx

from conftest import TEST_HOST, TOKEN, FakeProbes
from glassworld_ai.app import create_app
from glassworld_ai.benchmark import Benchmark, Generated
from glassworld_ai.config import Settings
from glassworld_ai.decider.backends import MockDecider
from glassworld_ai.planner import KindPlan, Planner
from glassworld_ai.residency import Footprint, Residency
from glassworld_ai.validation import ContractValidator
from glassworld_contracts import CONTRACT_VERSION


class Clock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


class Runner:
    def __init__(self, running: int = 0) -> None:
        self.log: list[str] = []
        self.running = running

    def hold(self) -> None:
        self.log.append("hold")

    def resume(self) -> None:
        self.log.append("resume")

    def counts(self) -> dict[str, int]:
        return {"queued": 0, "running": self.running, "deferred": 0}


class Setup:
    """A residency with fake drivers; loading takes time on the fake clock."""

    def __init__(self, clock: Clock) -> None:
        self.clock = clock
        self.log: list[str] = []
        setup = self

        class Driver:
            async def load(self, model_id: str) -> None:
                setup.log.append(f"load {model_id}")
                clock.now += {"llm": 6.0, "img": 0.0}.get(model_id, 0.0)

            async def unload(self, model_id: str) -> None:
                setup.log.append(f"unload {model_id}")

        self.residency = Residency(
            {
                "llm": Footprint("llm", "language", "d", 6000, 1.0, frozenset()),
                "img": Footprint("img", "image", "d", 7000, 1.0, frozenset()),
            },
            7680,
            {"d": Driver()},
        )

    def language(self, fail: bool = False) -> Any:
        setup = self

        class Probe:
            model = "llm"

            async def generate(self, tokens: int) -> Generated:
                if fail:
                    raise RuntimeError("ollama down")
                setup.log.append(f"generate {tokens}")
                setup.clock.now += 3.0
                return Generated(tokens, 2.0)  # 64 tokens/s

        return Probe()

    def image(self) -> Any:
        setup = self
        calls = {"n": 0}

        class Probe:
            model = "img"

            async def make(self) -> None:
                calls["n"] += 1
                setup.log.append("picture")
                setup.clock.now += 19.0 if calls["n"] == 1 else 10.0  # cold loads in ComfyUI

        return Probe()


def _benchmark(
    tmp_path: Path, setup: Setup, runner: Runner, language: Any, image: Any, seconds: dict[str, Any]
) -> tuple[Benchmark, list[str]]:
    events: list[str] = []

    async def restore() -> None:
        events.append("restore")

    def on_result(values: dict[str, float]) -> None:
        seconds.update(values)

    run = Benchmark(
        store=tmp_path / "benchmark.json",
        runner=runner,
        residency=lambda: setup.residency,
        hardware=lambda: ("standard", "NVIDIA GeForce RTX 3080", 10240),
        answer_tokens=128,
        language=language,
        image=image,
        sound=None,
        on_result=on_result,
        restore=restore,
        measuring=lambda on: events.append(f"measuring {on}"),
        idle_timeout_s=5,
        clock=setup.clock,
        wall=lambda: 1794189600.0,
    )
    return run, events


def _valid(settings: Settings, run: Benchmark) -> None:
    status = run.status().model_dump(mode="json")
    assert ContractValidator(settings.schemas_dir).errors("benchmark-status", status) == []


def test_measures_each_model_alone_and_saves(tmp_path: Path, settings: Settings) -> None:
    setup, runner = Setup(Clock()), Runner()
    seconds: dict[str, Any] = {}
    asyncio.run(setup.residency.ensure("llm"))  # the language model was resident
    setup.log.clear()
    run, events = _benchmark(tmp_path, setup, runner, setup.language(), setup.image(), seconds)
    asyncio.run(run.run())
    assert run.state == "done", run.error
    assert setup.log == [
        "unload llm",  # everything from unloaded
        "load llm",
        "generate 128",
        "unload llm",
        "load img",
        "picture",
        "picture",
        "unload img",
    ]
    assert run.result is not None
    assert run.result["language"] == {
        "model": "llm", "load_s": 6.0, "seconds_per_job": 2.0, "tokens_per_s": 64.0,
    }  # fmt: skip
    assert run.result["image"] == {
        "model": "img", "load_s": 9.0, "seconds_per_job": 10.0, "tokens_per_s": None,
    }  # fmt: skip
    assert seconds == {"llm": 2.0, "img": 10.0}
    assert runner.log == ["hold", "resume"]
    assert events == ["measuring True", "measuring False", "restore"]
    _valid(settings, run)
    # saved: a new service start knows the values without measuring again
    reloaded: dict[str, Any] = {}
    again, _ = _benchmark(tmp_path, Setup(Clock()), Runner(), None, None, reloaded)
    assert again.state == "done" and reloaded == {"llm": 2.0, "img": 10.0}
    assert json.loads((tmp_path / "benchmark.json").read_text("utf-8"))["gpu"].startswith("NVIDIA")


def test_a_failing_model_stays_unmeasured(tmp_path: Path, settings: Settings) -> None:
    setup = Setup(Clock())
    run, _ = _benchmark(tmp_path, setup, Runner(), setup.language(fail=True), setup.image(), {})
    asyncio.run(run.run())
    assert run.state == "done"
    assert run.result is not None and run.result["language"] is None
    assert run.result["image"] is not None
    assert run.error is not None and "ollama down" in run.error
    _valid(settings, run)


def test_nothing_measured_is_a_failure(tmp_path: Path, settings: Settings) -> None:
    setup = Setup(Clock())
    runner = Runner()
    run, _ = _benchmark(tmp_path, setup, runner, setup.language(fail=True), None, {})
    asyncio.run(run.run())
    assert run.state == "failed" and run.result is None
    assert runner.log == ["hold", "resume"]
    assert not (tmp_path / "benchmark.json").exists()
    _valid(settings, run)


def test_a_running_job_is_waited_for_then_given_up(tmp_path: Path) -> None:
    setup = Setup(Clock())
    runner = Runner(running=1)

    async def scenario() -> Benchmark:
        run, _ = _benchmark(tmp_path, setup, runner, setup.language(), None, {})

        async def tick() -> None:
            while run.state != "failed":
                setup.clock.now += 1
                await asyncio.sleep(0)

        ticking = asyncio.create_task(tick())
        await run.run()
        await ticking
        return run

    run = asyncio.run(scenario())
    assert run.state == "failed" and run.error is not None and "kept running" in run.error
    assert setup.log == [] and runner.log == ["hold", "resume"]


def test_a_night_job_starts_only_if_it_still_fits() -> None:
    clock = Clock()
    planner = Planner(
        100, {"image": KindPlan("night", "img")}, lambda: None, lambda: "standard", clock
    )
    planner.set_job_seconds({"img": 30.0})
    planner.start_night()
    clock.now += 60
    assert planner.allowed("image")  # 40 s left
    clock.now += 15
    assert not planner.allowed("image")  # 25 s left < 30 s
    planner.measuring = True
    assert planner.language_model_blocked()


def test_endpoints_without_models(settings: Settings, tmp_path: Path) -> None:
    app = create_app(
        settings.model_copy(
            update={"decider_backend": "mock", "benchmark_file": tmp_path / "benchmark.json"}
        ),
        allowed_hosts=frozenset({TEST_HOST}),
        probes=FakeProbes(),
        decider=MockDecider(),
    )
    validator = ContractValidator(settings.schemas_dir)

    async def scenario() -> None:
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(
            transport=transport, base_url=f"http://{TEST_HOST}", headers={"x-gw-token": TOKEN}
        ) as client:
            status = (await client.get("/benchmark")).json()
            assert status == {
                "contract_version": CONTRACT_VERSION,
                "state": "none",
                "result": None,
                "error": None,
            }
            assert validator.errors("benchmark-status", status) == []
            refused = await client.post("/benchmark")
            body = refused.json()
            assert (body["code"], body["retryable"]) == ("model_unavailable", False)

    asyncio.run(scenario())


def test_another_graphics_card_measures_again(tmp_path: Path) -> None:
    setup = Setup(Clock())
    run, _ = _benchmark(tmp_path, setup, Runner(), setup.language(), setup.image(), {})
    asyncio.run(run.run())
    assert run.state == "done"
    other = Benchmark(
        store=tmp_path / "benchmark.json",
        runner=Runner(),
        residency=lambda: None,
        hardware=lambda: ("comfort", "NVIDIA GeForce RTX 4090", 24564),
        answer_tokens=128,
        language=None,
        image=None,
        sound=None,
        on_result=lambda _: None,
        restore=_noop,
    )
    assert other.state == "none" and other.result is None


def test_a_run_has_an_overall_time_limit(tmp_path: Path) -> None:
    setup = Setup(Clock())

    class Slow:
        model = "llm"

        async def generate(self, tokens: int) -> Generated:
            await asyncio.sleep(10)
            return Generated(tokens, 1.0)

    run = Benchmark(
        store=tmp_path / "benchmark.json",
        runner=Runner(),
        residency=lambda: setup.residency,
        hardware=lambda: ("standard", "gpu", 10240),
        answer_tokens=128,
        language=Slow(),
        image=None,
        sound=None,
        on_result=lambda _: None,
        restore=_noop,
        overall_timeout_s=0.05,
    )
    asyncio.run(run.run())
    assert run.state == "failed" and run.error is not None and "longer than" in run.error


async def _noop() -> None:
    return None
