"""JSONL log of every model call (handbook: every AI request is logged).

One line per attempt. The authoritative replay log is kept by the core; this log serves
debugging, the AI test sets and the replay backend of the service.
"""

from __future__ import annotations

import json
import threading
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path


@dataclass(frozen=True)
class CallRecord:
    role: str  # e.g. "decider"
    request_id: str | None
    tick: int | None
    prompt_version: str
    model: str
    seed: int
    temperature: float
    attempt: int
    latency_ms: int
    outcome: str  # "ok" | "invalid" | "timeout" | "unavailable" | "error" | "cancelled"
    raw_output: str | None
    request: dict[str, object] | None = None
    detail: str | None = None
    prompt_tokens: int | None = None
    output_tokens: int | None = None
    prompt_digest: str | None = None  # the prompt file's content, so in-place edits stay visible


class CallLog:
    def __init__(self, path: Path) -> None:
        self._path = path
        self._lock = threading.Lock()
        path.parent.mkdir(parents=True, exist_ok=True)

    @property
    def path(self) -> Path:
        return self._path

    def write(self, record: CallRecord) -> None:
        line = {"time_utc": datetime.now(UTC).isoformat(timespec="milliseconds"), **asdict(record)}
        text = json.dumps(line, ensure_ascii=False, separators=(",", ":")) + "\n"
        with self._lock, self._path.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(text)

    def read(self) -> list[dict[str, object]]:
        if not self._path.exists():
            return []
        with self._path.open(encoding="utf-8") as handle:
            return [json.loads(line) for line in handle if line.strip()]
