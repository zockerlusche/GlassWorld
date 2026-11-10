"""Diagnostics report for the player (Marcel W1703 rule 4, Bogen 35 R11).

Versions, self-check, hardware tier, model states and the last errors from the logs, as Markdown.
The report never contains tokens or user paths: every text passes `scrub`, and the HF token file
is never read. It is written into the service's log folder and stays on the computer until the
player passes it on.
"""

from __future__ import annotations

import json
import os
import re
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, cast

from glassworld_ai import SERVICE_VERSION
from glassworld_contracts import CONTRACT_VERSION

MAX_ERRORS = 20
_SECRETS = [
    re.compile(r"hf_[A-Za-z0-9]{10,}"),  # Hugging Face tokens
    re.compile(r"(?i)(bearer|token|secret|password)(\s*[=:]\s*|\s+)\S+"),
]
# Any user folder on Windows, macOS or Linux, in either slash style.
_USER_DIR = re.compile(r"(?i)([a-z]:[\\/]+users[\\/]+|/users/|/home/)[^\\/\s\"']+")


def account_names() -> list[str]:
    """The names of the player's account (home folder name, USERNAME, USER), at least 3 letters."""
    names = {Path.home().name, os.environ.get("USERNAME", ""), os.environ.get("USER", "")}
    return sorted((n for n in names if len(n) >= 3), key=len, reverse=True)


def scrub(text: str, known_dirs: list[Path], user_names: list[str] | None = None) -> str:
    """Removes secrets and user paths: known folders become <dir>, user folders <user>, and the
    account name anywhere else (e.g. a games folder named after it in a traceback) as a whole
    word <user>."""
    for folder in sorted({str(d) for d in known_dirs if str(d)}, key=len, reverse=True):
        for spelling in {folder, folder.replace("\\", "/"), folder.replace("/", "\\")}:
            text = text.replace(spelling, "<dir>")
    text = _USER_DIR.sub(lambda m: m.group(1) + "<user>", text)
    for name in account_names() if user_names is None else user_names:
        text = re.sub(rf"(?i)(?<![^\W_]){re.escape(name)}(?![^\W_])", "<user>", text)
    for pattern in _SECRETS:
        text = pattern.sub(lambda m: m.group(0)[:3] + "<hidden>", text)
    return text


def last_errors(log_dir: Path, limit: int = MAX_ERRORS) -> list[dict[str, str]]:
    """The newest rows of the call logs whose outcome is not ok (role, outcome, time, detail)."""
    rows: list[dict[str, str]] = []
    for log in sorted(log_dir.glob("*.jsonl")):
        try:
            lines = log.read_text(encoding="utf-8", errors="replace").splitlines()[-500:]
        except OSError:
            continue
        for line in lines:
            try:
                row: Any = json.loads(line)
            except json.JSONDecodeError:
                continue
            if not isinstance(row, dict):
                continue
            entry = cast(dict[str, Any], row)
            if entry.get("outcome") not in (None, "ok"):
                rows.append(
                    {
                        "time": str(entry.get("time_utc", ""))[:19],
                        "log": log.stem,
                        "outcome": str(entry.get("outcome"))[:40],
                        "detail": str(entry.get("detail") or "")[:200],
                    }
                )
    rows.sort(key=lambda r: r["time"])
    return rows[-limit:]


def report(
    *,
    check: dict[str, Any],
    health: dict[str, Any],
    ollama: dict[str, Any],
    models: dict[str, Any],
    log_dir: Path,
    known_dirs: list[Path],
    now: datetime | None = None,
) -> dict[str, Any]:
    """The DiagnosticsReport contract message; also writes the file into `log_dir`."""
    moment = now or datetime.now(UTC)
    hardware = health.get("hardware", {})
    gpus = ", ".join(f"{g['name']} ({g['vram_mib']} MiB)" for g in hardware.get("gpus", []))
    lines = [
        "# GlassWorld diagnostics",
        "",
        f"- Created: {moment:%Y-%m-%d %H:%M:%S} UTC",
        f"- AI service {SERVICE_VERSION}, contract {CONTRACT_VERSION}",
        f"- Service status: {health.get('status')} ({health.get('detail') or '-'})",
        f"- Hardware tier: {hardware.get('tier') or 'below minimum'}; "
        f"GPUs: {gpus or 'none'}; RAM {hardware.get('ram_mib')} MiB",
        f"- Ollama: {ollama.get('state')} {ollama.get('version') or ''} "
        f"(required {ollama.get('required_version')}, source {ollama.get('source') or '-'})",
        "",
        "## Self-check",
        "",
        "| AI | State | Reason | Detail |",
        "|---|---|---|---|",
        *[
            f"| {c['ai']} | {c['state']} | {c['reason']} | {', '.join(c['detail']) or '-'} |"
            for c in check["checks"]
        ],
        "",
        "## Models",
        "",
        "| Model | Role | State | Error |",
        "|---|---|---|---|",
        *[
            f"| {m['id']} | {m['role']} | {m['state']} | {m.get('error') or '-'} |"
            for m in models.get("models", [])
        ],
        "",
        f"## Last errors (at most {MAX_ERRORS})",
        "",
    ]
    errors = last_errors(log_dir)
    lines += [f"- {e['time']} {e['log']}: {e['outcome']} - {e['detail']}" for e in errors] or [
        "- none"
    ]
    text = scrub("\n".join(lines) + "\n", known_dirs)[:65536]
    name = f"diagnostics-{moment:%Y%m%d-%H%M%S}.md"
    log_dir.mkdir(parents=True, exist_ok=True)
    (log_dir / name).write_text(text, encoding="utf-8", newline="\n")
    return {"contract_version": CONTRACT_VERSION, "file_name": name, "text": text}
