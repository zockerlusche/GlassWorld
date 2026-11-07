from __future__ import annotations

import asyncio
import time

import pytest

from glassworld_ai.queue import DeadlineExpiredError, ModelQueue, Priority, QueueFullError


def _deadline(seconds: float) -> float:
    return time.monotonic() + seconds


def test_runs_work_and_returns_its_result() -> None:
    async def scenario() -> int:
        queue = ModelQueue(max_waiting=2)

        async def work() -> int:
            return 42

        return await queue.run(Priority.DECISION, _deadline(1), work)

    assert asyncio.run(scenario()) == 42


def test_only_one_call_at_a_time_and_priority_order() -> None:
    async def scenario() -> list[str]:
        queue = ModelQueue(max_waiting=10)
        order: list[str] = []
        running = 0
        max_running = 0
        release = asyncio.Event()

        def job(name: str, hold: bool = False):
            async def work() -> None:
                nonlocal running, max_running
                running += 1
                max_running = max(max_running, running)
                if hold:
                    await release.wait()
                order.append(name)
                running -= 1

            return work

        first = asyncio.create_task(queue.run(Priority.BACKGROUND, _deadline(5), job("busy", True)))
        await asyncio.sleep(0.01)  # "busy" holds the model
        tasks = [
            asyncio.create_task(queue.run(Priority.BACKGROUND, _deadline(5), job("bg1"))),
            asyncio.create_task(queue.run(Priority.DECISION, _deadline(5), job("dec1"))),
            asyncio.create_task(queue.run(Priority.PLAYER, _deadline(5), job("player"))),
            asyncio.create_task(queue.run(Priority.DECISION, _deadline(5), job("dec2"))),
        ]
        await asyncio.sleep(0.01)
        assert queue.waiting == 4
        release.set()
        await asyncio.gather(first, *tasks)
        assert max_running == 1
        return order

    assert asyncio.run(scenario()) == ["busy", "player", "dec1", "dec2", "bg1"]


def test_full_queue_is_overloaded() -> None:
    async def scenario() -> None:
        queue = ModelQueue(max_waiting=1)
        release = asyncio.Event()

        async def hold() -> None:
            await release.wait()

        async def quick() -> None:
            return None

        busy = asyncio.create_task(queue.run(Priority.DECISION, _deadline(5), hold))
        await asyncio.sleep(0.01)
        waiting = asyncio.create_task(queue.run(Priority.DECISION, _deadline(5), quick))
        await asyncio.sleep(0.01)
        with pytest.raises(QueueFullError):
            await queue.run(Priority.DECISION, _deadline(5), quick)
        release.set()
        await asyncio.gather(busy, waiting)

    asyncio.run(scenario())


def test_deadline_expires_while_waiting_and_work_never_starts() -> None:
    async def scenario() -> None:
        queue = ModelQueue(max_waiting=5)
        release = asyncio.Event()
        started: list[str] = []

        async def hold() -> None:
            await release.wait()

        async def late() -> None:
            started.append("late")

        busy = asyncio.create_task(queue.run(Priority.DECISION, _deadline(5), hold))
        await asyncio.sleep(0.01)
        with pytest.raises(DeadlineExpiredError):
            await queue.run(Priority.DECISION, _deadline(0.05), late)
        assert queue.waiting == 0
        release.set()
        await busy
        assert started == []

    asyncio.run(scenario())


def test_deadline_expires_during_work() -> None:
    async def scenario() -> None:
        queue = ModelQueue(max_waiting=5)

        async def slow() -> None:
            await asyncio.sleep(1)

        with pytest.raises(DeadlineExpiredError):
            await queue.run(Priority.DECISION, _deadline(0.05), slow)
        assert not queue.busy

        async def quick() -> str:
            return "ok"

        assert await queue.run(Priority.DECISION, _deadline(1), quick) == "ok"

    asyncio.run(scenario())


def test_already_passed_deadline_is_rejected_immediately() -> None:
    async def scenario() -> None:
        queue = ModelQueue(max_waiting=5)

        async def work() -> None:
            raise AssertionError("must not run")

        with pytest.raises(DeadlineExpiredError):
            await queue.run(Priority.PLAYER, time.monotonic() - 1, work)

    asyncio.run(scenario())


def test_cancelled_waiter_leaves_the_queue() -> None:
    async def scenario() -> None:
        queue = ModelQueue(max_waiting=5)
        release = asyncio.Event()

        async def hold() -> None:
            await release.wait()

        async def quick() -> None:
            return None

        busy = asyncio.create_task(queue.run(Priority.DECISION, _deadline(5), hold))
        await asyncio.sleep(0.01)
        waiter = asyncio.create_task(queue.run(Priority.DECISION, _deadline(5), quick))
        await asyncio.sleep(0.01)
        waiter.cancel()
        with pytest.raises(asyncio.CancelledError):
            await waiter
        assert queue.waiting == 0
        release.set()
        await busy

    asyncio.run(scenario())


def test_failing_work_releases_the_model() -> None:
    async def scenario() -> None:
        queue = ModelQueue(max_waiting=5)

        async def boom() -> None:
            raise RuntimeError("model crashed")

        with pytest.raises(RuntimeError):
            await queue.run(Priority.DECISION, _deadline(1), boom)
        assert not queue.busy

    asyncio.run(scenario())
