# JSON lines of the core log are untyped data.
# pyright: reportUnknownMemberType=false, reportUnknownVariableType=false
# pyright: reportUnknownArgumentType=false
"""Durchstich D9 (D471, acceptance M2): a start island run up to a used invention, its replay,
and a report with the chain, the protocol trail (every request_id) and the replay hash.

Usage (from the repository root):
    uv run python tools/durchstich/run_d9.py --ai live --seed 1 --days 3 --out ../d9-run
    uv run python tools/durchstich/run_d9.py --ai mock --days 1 --out ../d9-probe   (tooling check)

A live run belongs in the AMW GPU queue (`amw lauf ausfuehren --gpu ...`). The output folder must be
outside the repository. The report (bericht.md) is German because Marcel reads it; it states facts
the run printed or logged and never decides which object should have been invented (frame, not
outcome).
"""

from __future__ import annotations

import argparse
import itertools
import json
import os
import subprocess
import sys
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "Simulation.Headless"
# The chain of D471 "Abnahme M2" point 1, as core event kinds (content/vocabulary/events.json).
CHAIN = ["invention_wanted", "invented", "build_planned", "build_step", "built", "first_use"]
CHAIN_DE = {
    "invention_wanted": "will etwas (Mangel ohne Loesung)",
    "invented": "Erfindung genehmigt (Waechter + Kern-Endpruefung)",
    "build_planned": "Bauplan aus Baukasten-Teilen",
    "build_step": "Bauschritte",
    "built": "gebaut",
    "first_use": "benutzt",
}
ASSET_SUFFIXES = (".png", ".webp", ".jpg", ".wav", ".ogg", ".flac", ".glb")
# Exit codes of Simulation.Headless (src/Simulation.Headless/README.md).
RUNNER_EXIT = {
    0: "fertig, Abnahme ok",
    1: "falsche Argumente",
    2: "Kern-Abnahme fehlgeschlagen",
    3: "--until nicht erreicht",
    4: "KI-Dienst nicht verfuegbar oder ein Spiel laeuft",
    130: "abgebrochen",
}
REPLAY_EXIT = {
    0: "REPLAY_OK",
    5: "REPLAY_DIFF",
    6: "REPLAY_CONTENT_CHANGED",
    1: "falsche Argumente",
}
# Without these the run never got going: no save to replay, no chain to judge.
RUNNER_NOT_STARTED = {1, 4}


@dataclass
class RunResult:
    exit_code: int
    lines: list[str]
    events: list[dict[str, str]] = field(default_factory=lambda: list[dict[str, str]]())
    chronicle: list[dict[str, str]] = field(default_factory=lambda: list[dict[str, str]]())
    final_hash: str | None = None
    final_tick: str | None = None
    until: str | None = None


def parse_fields(line: str) -> dict[str, str]:
    """`KEY a=b c=d` -> {"a": "b", ...}; values are percent-encoded by the runner."""
    from urllib.parse import unquote

    out: dict[str, str] = {}
    for part in line.split(" ")[1:]:
        if "=" in part:
            key, value = part.split("=", 1)
            out[key] = unquote(value)
    return out


def parse_run(lines: list[str], exit_code: int) -> RunResult:
    result = RunResult(exit_code, lines)
    for line in lines:
        if line.startswith("EVENT "):
            result.events.append(parse_fields(line))
        elif line.startswith("CHRONICLE "):
            result.chronicle.append(parse_fields(line))
        elif line.startswith("UNTIL "):
            result.until = line
        elif line.startswith("FINAL "):
            fields = parse_fields(line)
            result.final_hash, result.final_tick = fields.get("hash"), fields.get("tick")
    return result


def read_log(path: Path) -> list[dict[str, Any]]:
    if not path.is_file():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line]


def job_kind(entry: dict[str, Any]) -> str:
    request = entry.get("request")
    if isinstance(request, dict):
        kind = request.get("kind") or request.get("job_kind")
        if isinstance(kind, str):
            return kind
    return "-"


def asset_names(value: Any) -> list[str]:
    """File names of pictures, sounds and models mentioned anywhere in a job result."""
    found: list[str] = []
    if isinstance(value, dict):
        for item in value.values():
            found += asset_names(item)
    elif isinstance(value, list):
        for item in value:
            found += asset_names(item)
    elif isinstance(value, str) and value.lower().endswith(ASSET_SUFFIXES):
        found.append(value)
    return found


def chain_check(run: RunResult) -> list[tuple[str, str, bool]]:
    """(step, evidence, ok) per chain step, in order, each after the previous one."""
    rows: list[tuple[str, str, bool]] = []
    after = -1
    for kind in CHAIN:
        hits = [e for e in run.events if e.get("kind") == kind and int(e.get("tick", "0")) >= after]
        if kind == "build_step":
            ok = len(hits) >= 2
            evidence = f"{len(hits)} Schritte" + (f", ab Tick {hits[0]['tick']}" if hits else "")
        else:
            ok = bool(hits)
            evidence = (
                f"Tick {hits[0]['tick']}, Bewohner {hits[0].get('inhabitant', '-')}"
                if hits
                else "-"
            )
            if kind in {"built", "first_use", "invented"} and hits:
                evidence += f", Ziel {hits[0].get('target', '-')}"
        if hits:
            after = int(hits[0]["tick"])
        rows.append((kind, evidence, ok))
    chronicle = [c for c in run.chronicle if c.get("kind") in {"invented", "first_use"}]
    rows.append(
        (
            "chronicle",
            ", ".join(f"{c['kind']} (Tick {c['tick']})" for c in chronicle) or "-",
            bool(chronicle),
        )
    )
    return rows


def build_headless() -> int:
    return subprocess.run(
        ["dotnet", "build", str(PROJECT), "-c", "Release", "-v", "q", "-nologo"], cwd=ROOT
    ).returncode


def run_cmd(args: list[str], log: Path, env: dict[str, str]) -> tuple[int, list[str]]:
    with log.open("w", encoding="utf-8", newline="\n") as out:
        done = subprocess.run(
            args, cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT
        )
        text = done.stdout.decode("utf-8", errors="replace")
        out.write(text)
    return done.returncode, text.splitlines()


def report(
    out: Path,
    options: argparse.Namespace,
    run: RunResult,
    replay_code: int,
    replay_line: str,
    log: list[dict[str, Any]],
    assets: list[str],
) -> tuple[str, bool]:
    chain = chain_check(run)
    replay_hash = next(
        (p.split("=", 1)[1] for p in replay_line.split() if p.startswith("hash=")), None
    )
    same_hash = replay_code == 0 and replay_hash is not None and replay_hash == run.final_hash
    chain_ok = all(ok for _, _, ok in chain)
    decisions = [e for e in log if e["log"] == "decision" and e["kind"] == "response"]
    jobs = [e for e in log if e["log"] == "job"]
    requests = {e["request_id"]: e for e in jobs if e["kind"] == "job_request"}
    kinds = Counter(job_kind(e) for e in requests.values())
    outcome: dict[str, str] = {}
    for e in jobs:
        if e["kind"] in {"job_result", "job_error", "job_discarded"}:
            outcome[e["request_id"]] = e["kind"]
    sound = [r for r, e in requests.items() if job_kind(e) == "sound"]
    counts = Counter(e.get("kind", "-") for e in run.events if e.get("kind") in CHAIN)
    chronicle_counts = Counter(c.get("kind", "-") for c in run.chronicle)
    lines = [
        f"# Durchstich D9 - Lauf seed {options.seed} ({options.ai})",
        "",
        f"- Lauf: `--preset start_island --seed {options.seed} --ai {options.ai} "
        f"--days {options.days} --until {options.until}`, Exit-Code {run.exit_code} "
        f"({RUNNER_EXIT.get(run.exit_code, 'unbekannt')})",
        f"- {run.until or 'UNTIL: -'}",
        f"- Endstand: Tick {run.final_tick}, Hash `{run.final_hash}`",
        f"- Replay: `{replay_line or '-'}` (Exit-Code {replay_code}: "
        f"{REPLAY_EXIT.get(replay_code, 'unbekannt')})",
        "- Anzahl je Art: "
        + (", ".join(f"{k} {counts[k]}" for k in CHAIN if counts[k]) or "-")
        + "; Chronik: "
        + (", ".join(f"{k} {v}" for k, v in sorted(chronicle_counts.items())) or "-"),
        f"- **Replay-Hash gleich Lauf-Hash: {'ja' if same_hash else 'NEIN'}** (Abnahme M2 Punkt 2)",
        f"- **Kette vollstaendig: {'ja' if chain_ok else 'NEIN'}** (Abnahme M2 Punkt 1)",
        "",
        "## Kette (Kern-Ereignisse in Reihenfolge)",
        "",
        "| Schritt | Bedeutung | Beleg | erfuellt |",
        "|---|---|---|---|",
    ]
    for kind, evidence, ok in chain:
        meaning = CHAIN_DE.get(kind, "Chronik-Eintrag (Grundzeile DE/EN)")
        lines.append(f"| `{kind}` | {meaning} | {evidence} | {'ja' if ok else 'nein'} |")
    lines += [
        "",
        "## Generierungsjobs",
        "",
        "| Art | Anzahl | Ergebnisse |",
        "|---|---|---|",
    ]
    for kind, count in sorted(kinds.items()):
        results = Counter(
            outcome.get(r, "offen") for r, e in requests.items() if job_kind(e) == kind
        )
        lines.append(
            f"| {kind} | {count} | {', '.join(f'{k} {v}' for k, v in sorted(results.items()))} |"
        )
    lines += [
        "",
        "Klang (Rechennacht): "
        + (
            f"{len(sound)} Job(s), Ergebnis: {', '.join(outcome.get(r, 'offen') for r in sound)}"
            if sound
            else "kein Klang-Job im Protokoll "
            "(laut Waechter kein Klang noetig, oder keine Nacht im Lauf)"
        ),
        "",
        f"Bilder/Klaenge/Modelle aus Job-Ergebnissen: {', '.join(assets) if assets else '-'}",
        "",
        "## Protokollspur (alle request_ids)",
        "",
        f"Entscheidungen: {len(decisions)}, Jobs: {len(requests)}. Vollstaendig in `log.jsonl`.",
        "",
        "| Log | Art | request_id | Tick | Wartezeit (Ticks) | Seed | Quelle | Modell | Prompt "
        "| Latenz ms |",
        "|---|---|---|---|---|---|---|---|---|---|",
    ]
    for e in log:
        if e["kind"] in {"request", "job_request", "command"}:
            continue
        meta = e.get("meta") or {}
        art = job_kind(requests.get(e["request_id"], {})) if e["log"] == "job" else e["kind"]
        lines.append(
            f"| {e['log']} | {art} ({e['kind']}) | `{e['request_id']}` | {e['tick']} | "
            f"{meta.get('wait_ticks', '-')} | {meta.get('request_seed', meta.get('seed', '-'))} | "
            f"{meta.get('source', '-')} | "
            f"{meta.get('model', '-')} | "
            f"{meta.get('prompt_version', '-')} | {meta.get('latency_ms', '-')} |"
        )
    lines += ["", "## Chronik", ""]
    lines += [f"- Tick {c['tick']}: {c['kind']} (Eintrag {c['id']})" for c in run.chronicle] or [
        "- -"
    ]
    text = "\n".join(lines) + "\n"
    (out / "bericht.md").write_text(text, encoding="utf-8", newline="\n")
    return text, chain_ok and same_hash and run.exit_code == 0


def archive_name(folder: Path) -> Path:
    """A free name next to `folder` for keeping an earlier run: <name>.alt-1, .alt-2, ..."""
    for n in itertools.count(1):
        candidate = folder.with_name(f"{folder.name}.alt-{n}")
        if not candidate.exists():
            return candidate
    raise AssertionError("unreachable")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--ai", choices=["live", "mock"], default="live")
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--days", type=int, default=3)
    parser.add_argument("--until", default="first_use")
    parser.add_argument(
        "--out", type=Path, required=True, help="output folder outside the repository"
    )
    parser.add_argument(
        "--gpu-queue",
        action="store_true",
        help="required with --ai live: say that this runs as an AMW GPU-queue run",
    )
    options = parser.parse_args(argv)
    if options.ai == "live" and not options.gpu_queue:
        # Live runs share the GPU: only through `amw lauf ausfuehren --gpu ... -- ... --gpu-queue`.
        print("D9_FAIL: --ai live only as an AMW GPU-queue run (pass --gpu-queue)", file=sys.stderr)
        return 1
    out = options.out.resolve()
    if out == ROOT or ROOT in out.parents:
        print(f"D9_FAIL: output folder {out} is inside the repository", file=sys.stderr)
        return 1
    if out.exists() and any(out.iterdir()) and not (out / ".glassworld-d9").is_file():
        print(f"D9_FAIL: {out} exists, is not empty and is not a D9 output folder", file=sys.stderr)
        return 1
    if out.exists():
        # Rule PR4: nothing is deleted - an earlier run's folder is kept under a new name.
        out.rename(archive_name(out))
    out.mkdir(parents=True)
    (out / ".glassworld-d9").write_text("made by tools/durchstich/run_d9.py", encoding="utf-8")

    env = dict(os.environ, GLASSWORLD_HEADLESS_DATA=str(out / "service"))
    if build_headless() != 0:
        print("D9_FAIL: build of Simulation.Headless failed", file=sys.stderr)
        return 1
    headless = ["dotnet", "run", "--project", str(PROJECT), "-c", "Release", "--no-build", "--"]
    save, log_json = out / "welt.sqlite", out / "log.jsonl"
    code, lines = run_cmd(
        [
            *headless,
            "--preset",
            "start_island",
            "--seed",
            str(options.seed),
            "--ai",
            options.ai,
            "--days",
            str(options.days),
            "--until",
            options.until,
            "--events",
            "all",
            "--quiet",
            "--save",
            str(save),
            "--log-json",
            str(log_json),
        ],
        out / "lauf.log",
        env,
    )
    run = parse_run(lines, code)
    if code in RUNNER_NOT_STARTED or not save.is_file():
        # Nothing ran (AI service unavailable, a game's service running, bad arguments): say so
        # instead of replaying a missing save and reporting a "failed chain" (review GW1 #133).
        reason = next((x for x in lines if x.startswith("AI_SERVICE_UNAVAILABLE")), "")
        text = (
            f"# Durchstich D9 - Lauf seed {options.seed} ({options.ai}): NICHT GELAUFEN\n\n"
            f"- Exit-Code {code}: {RUNNER_EXIT.get(code, 'unbekannt')}\n"
            f"- {reason or 'kein Spielstand geschrieben'}\n"
            "- Kein Replay und keine Kettenbewertung: der Lauf hat nicht stattgefunden.\n"
        )
        (out / "bericht.md").write_text(text, encoding="utf-8", newline="\n")
        sys.stdout.buffer.write(text.encode("utf-8"))
        print(f"D9_FAIL: runner exit {code} ({RUNNER_EXIT.get(code, 'unknown')})")
        return 3
    replay_code, replay_lines = run_cmd(
        [*headless, "replay", "--save", str(save)], out / "replay.log", env
    )
    replay_line = next((x for x in replay_lines if x.startswith("REPLAY_")), "")
    log = read_log(log_json)
    assets = sorted(
        {a for e in log if e["kind"] == "job_result" for a in asset_names(e.get("response"))}
    )
    text, ok = report(out, options, run, replay_code, replay_line, log, assets)
    sys.stdout.buffer.write(text.encode("utf-8"))
    print("D9_OK" if ok else "D9_INCOMPLETE")
    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main())
