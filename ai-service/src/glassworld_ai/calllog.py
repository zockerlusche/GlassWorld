"""JSONL log of every model call (handbook: every AI request is logged).

One line per attempt. The authoritative replay log is kept by the core; this log serves
debugging, the AI test sets and the replay backend of the service.

A log can be capped (D1192 #10): once a line would take the file over max_bytes, the file is
rolled to `<name>.1.jsonl` (the older parts move one up, the oldest beyond `keep` goes), so a
log never holds more than about (keep + 1) x max_bytes. Reading covers the parts, oldest first.
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
    def __init__(self, path: Path, *, max_bytes: int | None = None, keep: int = 1) -> None:
        if max_bytes is not None and max_bytes <= 0:
            raise ValueError("max_bytes must be positive")
        if keep < 1:
            raise ValueError("keep must be at least 1")
        self._path = path
        self._max_bytes = max_bytes
        self._keep = keep
        self._lock = threading.Lock()
        path.parent.mkdir(parents=True, exist_ok=True)

    @property
    def path(self) -> Path:
        return self._path

    def write(self, record: CallRecord) -> None:
        line = {"time_utc": datetime.now(UTC).isoformat(timespec="milliseconds"), **asdict(record)}
        text = json.dumps(line, ensure_ascii=False, separators=(",", ":")) + "\n"
        with self._lock:
            self._roll(len(text.encode("utf-8")))
            with self._path.open("a", encoding="utf-8", newline="\n") as handle:
                handle.write(text)

    def part(self, number: int) -> Path:
        """A rolled part: 1 is the newest, `keep` the oldest."""
        return self._path.with_name(f"{self._path.stem}.{number}{self._path.suffix}")

    def read(self) -> list[dict[str, object]]:
        rows: list[dict[str, object]] = []
        files = [self.part(n) for n in range(self._keep, 0, -1)] if self._max_bytes else []
        for file in [*files, self._path]:
            if file.exists():
                with file.open(encoding="utf-8") as handle:
                    rows.extend(json.loads(line) for line in handle if line.strip())
        return rows

    def _roll(self, adding: int) -> None:
        if self._max_bytes is None:
            return
        try:
            size = self._path.stat().st_size
        except FileNotFoundError:
            return
        if size == 0 or size + adding <= self._max_bytes:
            return
        try:
            self.part(self._keep).unlink(missing_ok=True)
            for number in range(self._keep - 1, 0, -1):
                if self.part(number).exists():
                    self.part(number).replace(self.part(number + 1))
            self._path.replace(self.part(1))
        except OSError:
            # A part held open elsewhere (Windows): keep writing, try again at the next line.
            return
