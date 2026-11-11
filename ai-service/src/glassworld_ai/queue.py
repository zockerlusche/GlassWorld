"""Model queue: one model call at a time, ordered by priority, bounded, deadline-aware.

Shared by every role that calls a model (decider, conversation, chronicle ...). GPU discipline:
the reference PC has 10 GB VRAM, so model calls never run in parallel (ADR-0006).
"""

from __future__ import annotations

import asyncio
import collections
import heapq
import itertools
import statistics
import time
from collections.abc import Awaitable, Callable
from enum import IntEnum


class Priority(IntEnum):
    """Lower value = served first. Equal priority = first come, first served."""

    PLAYER = 0  # the player waits for an answer (conversation)
    DECISION = 1  # inhabitant decisions
    BACKGROUND = 2  # computing-night work, chronicle


class QueueClosedError(Exception):
    """The service is stopping; no model call starts any more (ServiceError 'model_unavailable')."""


class QueueFullError(Exception):
    """The queue holds its maximum number of waiting calls (ServiceError 'overloaded')."""


class DeadlineExpiredError(Exception):
    """The deadline passed before or during the call (ServiceError 'timeout')."""


Clock = Callable[[], float]


class TypicalDuration:
    """How long a call of one kind usually takes: the median of the last calls that came through.

    Measured, not configured (A1487, D713 f): with it the queue does not begin a call that could
    not finish before its deadline. Empty = 0, so nothing is held back before the first calls.
    The window is a technical size, not a balancing value.
    """

    def __init__(self, window: int = 21) -> None:
        self._seconds: collections.deque[float] = collections.deque(maxlen=window)

    def note(self, seconds: float) -> None:
        self._seconds.append(seconds)

    @property
    def seconds(self) -> float:
        return statistics.median(self._seconds) if self._seconds else 0.0


class ModelQueue:
    def __init__(self, max_waiting: int, clock: Clock = time.monotonic) -> None:
        if max_waiting < 1:
            raise ValueError("max_waiting must be at least 1")
        self._max_waiting = max_waiting
        self._clock = clock
        self._waiting: list[tuple[int, int]] = []
        self._sequence = itertools.count()
        self._busy = False
        self._closed = False
        self._condition = asyncio.Condition()

    @property
    def waiting(self) -> int:
        return len(self._waiting)

    @property
    def busy(self) -> bool:
        return self._busy

    @property
    def closed(self) -> bool:
        return self._closed

    async def close(self) -> None:
        """Stop accepting calls; waiting calls end with QueueClosedError at once (the running
        call finishes). Used on shutdown so nothing loads the model again before it is unloaded.
        """
        async with self._condition:
            self._closed = True
            self._condition.notify_all()

    def _remaining(self, deadline: float) -> float:
        return deadline - self._clock()

    async def run[T](
        self,
        priority: Priority,
        deadline: float,
        work: Callable[[], Awaitable[T]],
        needs_s: float = 0.0,
    ) -> T:
        """Wait for the turn, then run `work`. `deadline` is an absolute time of `clock`.

        Raises QueueFullError if too many calls wait, DeadlineExpiredError if the deadline passes
        while waiting (work is then never started) or while working (work is cancelled). With
        `needs_s` (how long such a call usually takes) the work is also not started when less
        than that is left at its turn: the model would run for an answer that comes too late
        and keep the next caller waiting (A1487).
        """
        if self._closed:
            raise QueueClosedError("service is stopping")
        if self._remaining(deadline) <= 0:
            raise DeadlineExpiredError("deadline already passed")
        if len(self._waiting) >= self._max_waiting:
            raise QueueFullError(f"{len(self._waiting)} calls waiting")

        ticket = (int(priority), next(self._sequence))
        heapq.heappush(self._waiting, ticket)
        try:
            async with self._condition:
                await asyncio.wait_for(
                    self._condition.wait_for(
                        lambda: self._closed or (not self._busy and self._waiting[0] == ticket)
                    ),
                    timeout=self._remaining(deadline),
                )
                if self._closed:
                    raise QueueClosedError("service is stopping")
                heapq.heappop(self._waiting)
                self._busy = True
        except BaseException as error:
            if ticket in self._waiting:
                self._waiting.remove(ticket)
                heapq.heapify(self._waiting)
                async with self._condition:
                    self._condition.notify_all()
            if isinstance(error, TimeoutError):
                raise DeadlineExpiredError("deadline passed while waiting") from None
            raise

        try:
            remaining = self._remaining(deadline)
            if remaining <= 0:
                raise DeadlineExpiredError("deadline passed while waiting")
            if remaining < needs_s:
                raise DeadlineExpiredError(
                    f"not begun: {remaining:.1f} s left, such a call takes about {needs_s:.1f} s"
                )
            try:
                return await asyncio.wait_for(work(), timeout=remaining)
            except TimeoutError:
                raise DeadlineExpiredError("deadline passed during the model call") from None
        finally:
            async with self._condition:
                self._busy = False
                self._condition.notify_all()
