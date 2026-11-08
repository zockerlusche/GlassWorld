"""Generation jobs (B7 R1): a volatile job store with one worker, run by priority.

The service keeps jobs only in memory. The core owns open jobs (B7 R4). The request id is the
job id: the same job sent twice is harmless, the same id with other content is refused (a core
id can come back after loading an older save), and a new attempt always uses a new id.
Every kind has a handler; a kind without a handler is not supported by this service version.
The day/night planner (B7 S4) will decide *when* jobs run; for now they run as soon as possible.
"""

from __future__ import annotations

import asyncio
import contextlib
import hashlib
import itertools
import json
from collections.abc import Awaitable, Callable, Mapping
from dataclasses import dataclass
from typing import Protocol

from glassworld_contracts import CONTRACT_VERSION, GenerationJob, JobStatus

OPEN_STATES = frozenset({"queued", "running", "deferred"})


class JobFailedError(Exception):
    """Raised by a handler: the job ends as failed with this ServiceError code."""

    def __init__(self, code: str, message: str, *, retryable: bool) -> None:
        super().__init__(message)
        self.code = code
        self.retryable = retryable


class JobsFullError(Exception):
    """Too many open jobs (ServiceError 'overloaded')."""


class JobConflictError(Exception):
    """Same job id, different content (ServiceError 'bad_request', not retryable)."""


@dataclass(frozen=True)
class JobOutcome:
    """What a handler returns: the result part for its kind and where it came from."""

    result: dict[str, object]
    source: str
    model: str
    prompt_version: str | None
    latency_ms: int
    attempts: int


Handler = Callable[[GenerationJob], Awaitable[JobOutcome]]


@dataclass
class _Record:
    job: GenerationJob
    sequence: int
    digest: str
    state: str = "queued"
    outcome: JobOutcome | None = None
    error: JobFailedError | None = None
    fetched: bool = False  # the core has read the final state at least once

    @property
    def job_id(self) -> str:
        return _text(self.job.request_id)

    @property
    def kind(self) -> str:
        return _text(self.job.kind)

    def order(self) -> tuple[int, int]:
        return (-self.job.priority, self.sequence)


def _digest(job: GenerationJob) -> str:
    """Hash of the canonical job JSON (sorted keys, no spaces)."""
    canonical = json.dumps(job.model_dump(mode="json"), sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def _text(value: object) -> str:
    """Plain string of a generated id/enum value (RootModel, Enum or str)."""
    inner: object = getattr(value, "root", value)
    return str(getattr(inner, "value", inner))


class Gate(Protocol):
    """Decides when a kind may run and prepares its model (the day/night planner, B7 S4)."""

    def allowed(self, kind: str) -> bool: ...
    def night_only(self, kind: str) -> bool: ...
    async def prepare(self, kind: str) -> None: ...
    def affinity(self, kind: str) -> int: ...
    def finished(self) -> None: ...


class JobRunner:
    """Holds jobs and runs them one at a time: highest priority first, then in arrival order.

    With a gate, a job runs only when its kind is allowed now; night-only kinds wait as "deferred"
    by day and when the night budget is used up (B7 R4).
    """

    def __init__(
        self, handlers: Mapping[str, Handler], capacity: int, gate: Gate | None = None
    ) -> None:
        self._gate = gate
        self._handlers = dict(handlers)
        self._capacity = capacity
        self._records: dict[str, _Record] = {}
        self._sequence = itertools.count()
        self._worker: asyncio.Task[None] | None = None
        self._current: tuple[str, asyncio.Task[JobOutcome]] | None = None
        self._closed = False

    def register(self, kind: str, handler: Handler) -> None:
        """Add the handler of a kind that needs resources from the app start (model client)."""
        self._handlers[kind] = handler

    def supports(self, kind: str) -> bool:
        return kind in self._handlers

    def submit(self, job: GenerationJob) -> tuple[str, bool]:
        """Add a job (or find the same job); returns (state, created). Starts the worker.

        Raises JobConflictError for a known id with other content, JobsFullError when only open
        jobs are held.
        """
        job_id = _text(job.request_id)
        digest = _digest(job)
        known = self._records.get(job_id)
        if known is not None:
            if known.digest != digest:
                raise JobConflictError(f"job {job_id} is known with other content")
            return known.state, False
        if len(self._records) >= self._capacity and not self._evict_one_finished():
            raise JobsFullError(f"{len(self._records)} jobs held")
        record = _Record(job, next(self._sequence), digest)
        self._records[job_id] = record
        self._defer_if_waiting(record)
        self._wake()
        return record.state, True

    def reschedule(self) -> None:
        """After a phase change: deferred jobs that may run now become queued, and night-only jobs
        that may not run now become deferred; then the worker continues."""
        for record in self._records.values():
            if record.state == "deferred" and (
                self._gate is None or self._gate.allowed(record.kind)
            ):
                record.state = "queued"
            elif record.state == "queued":
                self._defer_if_waiting(record)
        self._wake()

    def counts(self) -> dict[str, int]:
        result: dict[str, int] = {"queued": 0, "running": 0, "deferred": 0}
        for record in self._records.values():
            if record.state in result:
                result[record.state] += 1
        return result

    def _defer_if_waiting(self, record: _Record) -> None:
        gate = self._gate
        if gate is not None and gate.night_only(record.kind) and not gate.allowed(record.kind):
            record.state = "deferred"

    def status(self, job_id: str) -> JobStatus | None:
        record = self._records.get(job_id)
        if record is None:
            return None
        if record.state not in OPEN_STATES:
            record.fetched = True
        return _status(record)

    def cancel(self, job_id: str) -> bool:
        """Forget a job (e.g. orphaned after loading a save); a running one is stopped.

        Returns whether the job was known. Its model call may still finish inside Ollama, but the
        result is dropped.
        """
        record = self._records.pop(job_id, None)
        if record is None:
            return False
        if self._current is not None and self._current[0] == job_id:
            self._current[1].cancel()
        return True

    def listing(self, state: str | None) -> list[dict[str, object]]:
        """Short view (JobList.jobs) by priority, then arrival; all states if `state` is None."""
        records = [r for r in self._records.values() if state is None or r.state == state]
        return [_summary(r) for r in sorted(records, key=_Record.order)]

    async def close(self) -> None:
        """Stop the worker; a running job ends as failed (retryable), queued jobs stay queued."""
        self._closed = True
        if self._worker is not None:
            self._worker.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._worker
            self._worker = None

    # --- internals ------------------------------------------------------------------------

    def _evict_one_finished(self) -> bool:
        """Make room: the oldest finished job already read by the core, else the oldest finished."""
        finished = [r for r in self._records.values() if r.state not in OPEN_STATES]
        if not finished:
            return False
        oldest = min(finished, key=lambda r: (not r.fetched, r.sequence))
        del self._records[oldest.job_id]
        return True

    def _wake(self) -> None:
        if self._closed or (self._worker is not None and not self._worker.done()):
            return
        self._worker = asyncio.get_running_loop().create_task(self._drain())

    def _next(self) -> _Record | None:
        queued = [r for r in self._records.values() if r.state == "queued"]
        for record in queued:  # e.g. the night budget ran out meanwhile
            self._defer_if_waiting(record)
        ready = [r for r in queued if r.state == "queued"]
        if not ready:
            return None
        gate = self._gate
        if gate is None:
            return min(ready, key=_Record.order)
        # Jobs whose model is resident first (no reloading), then priority and arrival.
        return min(ready, key=lambda r: (gate.affinity(r.kind), *r.order()))

    async def _drain(self) -> None:
        while (record := self._next()) is not None:
            record.state = "running"
            if self._gate is not None:
                try:
                    await self._gate.prepare(record.kind)  # make the model resident (B7 S2)
                except Exception as error:
                    # A model larger than the budget never fits on this hardware: not retryable.
                    record.error = JobFailedError(
                        "model_unavailable",
                        f"model not available: {error}",
                        retryable=not getattr(error, "permanent", False),
                    )
                    record.state = "failed"
                    continue
            task = asyncio.ensure_future(self._handlers[record.kind](record.job))
            self._current = (record.job_id, task)
            try:
                record.outcome = await task
                record.state = "done"
                if self._gate is not None:
                    self._gate.finished()
            except asyncio.CancelledError:
                if self._records.get(record.job_id) is not record:
                    continue  # cancelled through DELETE: the record is gone, go on with the next
                record.error = JobFailedError(
                    "model_unavailable", "service is stopping", retryable=True
                )
                record.state = "failed"
                task.cancel()
                raise
            except JobFailedError as failure:
                record.error = failure
                record.state = "failed"
            except Exception as error:
                record.error = JobFailedError("internal", type(error).__name__, retryable=False)
                record.state = "failed"
            finally:
                self._current = None


def _status(record: _Record) -> JobStatus:
    outcome, error = record.outcome, record.error
    data: dict[str, object] = {
        "contract_version": CONTRACT_VERSION,
        "job_id": record.job_id,
        "tick": record.job.tick,
        "kind": record.kind,
        "state": record.state,
        "result": outcome.result if outcome is not None and record.state == "done" else None,
        "error": None
        if error is None
        else {"code": error.code, "message": str(error)[:500], "retryable": error.retryable},
        "provenance": None
        if outcome is None or record.state != "done"
        else {
            "source": outcome.source,
            "model": outcome.model,
            "prompt_version": outcome.prompt_version,
            "seed": record.job.seed,
            "latency_ms": outcome.latency_ms,
            "attempts": outcome.attempts,
        },
    }
    return JobStatus.model_validate(data)


def _summary(record: _Record) -> dict[str, object]:
    return {
        "job_id": record.job_id,
        "kind": record.kind,
        "state": record.state,
        "priority": record.job.priority,
        "tick": record.job.tick,
    }
