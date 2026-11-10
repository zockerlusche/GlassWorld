"""B4a acceptance A1 (Bogen 28, A1403): N runs of the start island, headless with the real model.

A run passes when an inhabitant learns by itself whether a berry is edible or poisonous (core
event ``knowledge_gained`` with detail ``edible/experienced`` or ``poisonous/experienced``) and
later another inhabitant holds that belief as heard (``<property>/heard`` on the same object
type). Acceptance: at least 8 of 10 runs. The measure comes from the core's events only; the tool
decides nothing in the world (frame, not outcome).

Usage (from the repository root):
    uv run python tools/b4a/run_a1.py --ai live --gpu-queue --runs 10 --days 3 --out ../b4a-a1
    uv run python tools/b4a/run_a1.py --ai mock --runs 1 --days 1 --out ../b4a-probe
    (the mock run only checks the tooling)

A live run belongs in the AMW GPU queue (`amw lauf ausfuehren --gpu ...`). All runs use the same
world seed; live runs still differ, because answers arrive at real-time tick boundaries and the
model samples. The report (bericht.md) is German because Marcel reads it.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "Simulation.Headless"
LEARNED = ("edible", "poisonous")
RUNNER_EXIT = {
    0: "fertig",
    1: "falsche Argumente",
    2: "Kern-Abnahme fehlgeschlagen",
    3: "--until nicht erreicht",
    4: "KI-Dienst nicht verfuegbar oder ein Spiel laeuft",
    130: "abgebrochen",
}
MARKER = ".glassworld-b4a-a1"


@dataclass
class Finding:
    """What one run shows: who learned what by trying it, and who heard it afterwards."""

    learned: list[dict[str, str]] = field(default_factory=lambda: list[dict[str, str]]())
    passed: list[dict[str, str]] = field(default_factory=lambda: list[dict[str, str]]())

    @property
    def ok(self) -> bool:
        return bool(self.passed)


def parse_events(lines: list[str]) -> list[dict[str, str]]:
    """``EVENT a=b c=d`` lines of the runner as dicts (values are percent-encoded)."""
    events: list[dict[str, str]] = []
    for line in lines:
        if not line.startswith("EVENT "):
            continue
        fields: dict[str, str] = {}
        for part in line.split(" ")[1:]:
            if "=" in part:
                key, value = part.split("=", 1)
                fields[key] = unquote(value)
        events.append(fields)
    return events


def judge(events: list[dict[str, str]], subjects: set[str]) -> Finding:
    """Learned by trying (experienced) and later heard by somebody else, same type and property."""
    finding = Finding()
    first: dict[tuple[str, str], dict[str, str]] = {}
    for event in sorted(events, key=lambda e: int(e.get("tick", "0"))):
        if event.get("kind") != "knowledge_gained" or event.get("target") not in subjects:
            continue
        prop, _, source = event.get("detail", "").partition("/")
        if prop not in LEARNED:
            continue
        key = (event["target"], prop)
        if source == "experienced":
            finding.learned.append(event)
            first.setdefault(key, event)
        elif source == "heard" and key in first and first[key]["inhabitant"] != event["inhabitant"]:
            finding.passed.append(event)
    return finding


def subjects_from_catalog(names: list[str] | None) -> set[str]:
    """The object types counted as berries: given ones, else catalog ids ending in ``berry``."""
    if names:
        return set(names)
    catalog = json.loads((ROOT / "content/objects/start-catalog.json").read_text("utf-8"))
    return {t["id"] for t in catalog["types"] if str(t["id"]).endswith("berry")}


def report(seed: int, days: int, ai: str, runs: list[tuple[int, int, Finding]]) -> str:
    good = sum(1 for _, code, f in runs if code in (0, 3) and f.ok)
    lines = [
        f"# B4a Abnahme A1 - {len(runs)} Laeufe Startinsel ({ai}, Welt-Seed {seed}, {days} Tage)",
        "",
        f"**Ergebnis: {good} von {len(runs)} Laeufen** - Ziel mindestens 8 von 10 "
        f"({'erfuellt' if good >= 8 and len(runs) >= 10 else 'nicht erfuellt'}).",
        "",
        "Gezaehlt wird ein Lauf, wenn ein Bewohner selbst erlebt, ob eine Beere essbar oder giftig "
        "ist (`knowledge_gained` ... `/experienced`), und danach ein anderer Bewohner "
        "dasselbe ueber dieselbe Beere gehoert hat (`.../heard`). "
        "Grundlage sind nur die Ereignisse des Kerns.",
        "",
        "| Lauf | Exit | selbst gelernt | weitergegeben | erstes Weitergeben |",
        "| --- | --- | --- | --- | --- |",
    ]
    for index, code, finding in runs:
        first = finding.passed[0] if finding.passed else None
        where = (
            f"Tick {first['tick']}: {first['inhabitant']} hoerte {first['detail']} "
            f"ueber {first['target']}"
            if first
            else "-"
        )
        lines.append(
            f"| {index} | {code} ({RUNNER_EXIT.get(code, '?')}) | {len(finding.learned)} | "
            f"{len(finding.passed)} | {where} |"
        )
    lines += [
        "",
        "Hinweis: Alle Laeufe haben denselben Welt-Seed. Live-Laeufe unterscheiden sich trotzdem, "
        "weil die KI-Antworten an echten Tick-Grenzen eintreffen und das Modell sampelt. "
        "Jeder Lauf hat Spielstand und Protokoll (log.jsonl) in seinem Ordner.",
    ]
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--ai", choices=["live", "mock"], default="live")
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--runs", type=int, default=10)
    parser.add_argument("--days", type=int, default=3)
    parser.add_argument("--subjects", nargs="*", help="object types counted as berries")
    parser.add_argument("--out", type=Path, required=True, help="folder outside the repository")
    parser.add_argument("--gpu-queue", action="store_true", help="required with --ai live")
    options = parser.parse_args(argv)
    if options.ai == "live" and not options.gpu_queue:
        print("A1_FAIL: --ai live only as an AMW GPU-queue run (--gpu-queue)", file=sys.stderr)
        return 1
    out = options.out.resolve()
    if out == ROOT or ROOT in out.parents:
        print(f"A1_FAIL: output folder {out} is inside the repository", file=sys.stderr)
        return 1
    if out.exists() and any(out.iterdir()) and not (out / MARKER).is_file():
        print(f"A1_FAIL: {out} exists, is not empty and is not an A1 folder", file=sys.stderr)
        return 1
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    (out / MARKER).write_text("made by tools/b4a/run_a1.py", encoding="utf-8")
    build = ["dotnet", "build", str(PROJECT), "-c", "Release", "-v", "q", "-nologo"]
    if subprocess.run(build, cwd=ROOT).returncode != 0:
        print("A1_FAIL: build of Simulation.Headless failed", file=sys.stderr)
        return 1

    subjects = subjects_from_catalog(options.subjects)
    headless = ["dotnet", "run", "--project", str(PROJECT), "-c", "Release", "--no-build", "--"]
    results: list[tuple[int, int, Finding]] = []
    for index in range(1, options.runs + 1):
        folder = out / f"lauf-{index:02d}"
        folder.mkdir()
        env = dict(os.environ, GLASSWORLD_HEADLESS_DATA=str(folder / "service"))
        args = [
            *headless,
            "--preset", "start_island",
            "--seed", str(options.seed),
            "--ai", options.ai,
            "--days", str(options.days),
            "--events", "knowledge_gained",
            "--quiet",
            "--save", str(folder / "welt.sqlite"),
            "--log-json", str(folder / "log.jsonl"),
        ]  # fmt: skip
        done = subprocess.run(
            args, cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT
        )
        text = done.stdout.decode("utf-8", errors="replace")
        (folder / "lauf.log").write_text(text, encoding="utf-8", newline="\n")
        finding = judge(parse_events(text.splitlines()), subjects)
        results.append((index, done.returncode, finding))
        print(f"A1 run {index}: exit {done.returncode}, learned {len(finding.learned)}, "
              f"passed {len(finding.passed)}", flush=True)  # fmt: skip
        if done.returncode in (1, 4):
            break  # nothing can run (bad arguments, AI service unavailable)

    text = report(options.seed, options.days, options.ai, results)
    (out / "bericht.md").write_text(text, encoding="utf-8", newline="\n")
    good = sum(1 for _, code, f in results if code in (0, 3) and f.ok)
    print(f"A1_RESULT {good}/{len(results)}")
    return 0 if good >= 8 and len(results) >= 10 else 2


if __name__ == "__main__":
    raise SystemExit(main())
