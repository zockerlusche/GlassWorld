"""Reference runs of the measuring net (A1808, B2833, D1202 step 1): N seeds of the start island without AI.

Each seed runs headless with the run trace and a save, is replayed (invariant 2: same seed plus log, same hash)
and checked against the invariants (invariants.py). The report (bericht.md, German for Marcel) lists per seed
and per invariant what held, what failed and what the trace cannot show yet. A run of 20 seeds is heavy:
only as an AMW queue run, by night (B2843); by day a short one (few seeds, few days).

Usage (from the repository root):
    uv run python tools/effect_map/reference_runs.py --seeds 1-20 --days 10 --out ../referenz --queue
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from collections import Counter
from pathlib import Path
from typing import Any, cast

sys.path.insert(0, str(Path(__file__).resolve().parent))
from invariants import INVARIANTS, check  # a tool beside invariants.py, not a package

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "Simulation.Headless"
HEADLESS = ["dotnet", "run", "--project", str(PROJECT), "-c", "Release", "--no-build", "--"]


def seeds_of(text: str) -> list[int]:
    first, _, last = text.partition("-")
    return list(range(int(first), int(last or first) + 1))


def run(args: list[str], log: Path) -> tuple[int, str]:
    done = subprocess.run(
        args, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False
    )
    text = done.stdout.decode("utf-8", errors="replace")
    log.write_text(text, encoding="utf-8", newline="\n")
    lines = [line for line in text.splitlines() if line.strip()]
    return done.returncode, lines[-1] if lines else ""


def one_seed(seed: int, days: int, out: Path, invariants: dict[str, Any]) -> dict[str, Any]:
    folder = out / f"seed-{seed:02d}"
    folder.mkdir(parents=True)
    save = folder / "welt.sqlite"
    code, last = run(
        [
            *HEADLESS,
            "--preset",
            "start_island",
            "--seed",
            str(seed),
            "--days",
            str(days),
            "--ai",
            "mock",
            "--quiet",
            "--save",
            str(save),
            "--run-trace",
            str(folder),
        ],
        folder / "lauf.log",
    )
    replay_code, replay = (
        run([*HEADLESS, "replay", "--save", str(save)], folder / "replay.log")
        if save.exists()
        else (-1, "no save")
    )
    results = check(folder, invariants) if (folder / "trace").is_dir() else []
    return {
        "seed": seed,
        "run_rc": code,
        "run_last": last,
        "replay_rc": replay_code,
        "replay": replay,
        "invariants": [{"id": i, "status": s, "text": t, "detail": d} for i, s, t, d in results],
    }


def report(rows: list[dict[str, Any]], days: int) -> str:
    lines = [f"# Messnetz – Referenzläufe ({len(rows)} Seeds, {days} Tage, ohne KI)", ""]
    lines += ["| Seed | Lauf | Replay (Inv. 2) | ok | FAIL | n/a |", "|---|---|---|---|---|---|"]
    fails: dict[str, list[str]] = {}
    texts: dict[str, str] = {}
    for row in rows:
        counts = Counter(cast(str, r["status"]) for r in row["invariants"])
        replay = "gleich" if row["replay_rc"] == 0 else f"ABWEICHUNG ({row['replay']})"
        lines.append(
            f"| {row['seed']} | rc {row['run_rc']} | {replay} | {counts['ok']} | {counts['FAIL']} | {counts['n/a']} |"
        )
        for r in row["invariants"]:
            texts[r["id"]] = r["text"]
            if r["status"] == "FAIL":
                fails.setdefault(r["id"], []).append(f"Seed {row['seed']}: {r['detail']}")
    lines += ["", "## Je Invariante", ""]
    for ident, text in sorted(texts.items()):
        failed = fails.get(ident, [])
        status = next(
            (r["status"] for row in rows for r in row["invariants"] if r["id"] == ident), "?"
        )
        if ident == "inv02":
            diff = [str(row["seed"]) for row in rows if row["replay_rc"] != 0]
            lines.append(
                f"- **{ident}** {text}: Replay gleich in {len(rows) - len(diff)} von {len(rows)} Seeds"
                + (f" (abweichend: Seed {', '.join(diff)})" if diff else "")
            )
        elif status == "n/a":
            detail = next(
                r["detail"] for row in rows for r in row["invariants"] if r["id"] == ident
            )
            lines.append(f"- **{ident}** {text}: nicht messbar – {detail}")
        else:
            lines.append(
                f"- **{ident}** {text}: verletzt in {len(failed)} von {len(rows)} Seeds"
                + (f" (z. B. {failed[0]})" if failed else "")
            )
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--seeds", default="1-20", help="FROM-TO or one seed")
    parser.add_argument("--days", type=int, default=10)
    parser.add_argument("--out", type=Path, required=True, help="folder outside the repository")
    parser.add_argument(
        "--queue", action="store_true", help="required: say this runs as an AMW queue run"
    )
    options = parser.parse_args(argv)
    out = cast(Path, options.out).resolve()
    if not options.queue:
        print("REF_FAIL: heavy - only as an AMW queue run (pass --queue)", file=sys.stderr)
        return 1
    if out == ROOT or ROOT in out.parents or out.exists():
        print(
            f"REF_FAIL: {out} is inside the repository or exists already (nothing is overwritten)",
            file=sys.stderr,
        )
        return 1
    if (
        subprocess.run(
            ["dotnet", "build", str(PROJECT), "-c", "Release", "-v", "q", "-nologo"],
            cwd=ROOT,
            check=False,
        ).returncode
        != 0
    ):
        print("REF_FAIL: build of Simulation.Headless failed", file=sys.stderr)
        return 1
    invariants = cast(dict[str, Any], json.loads(INVARIANTS.read_text(encoding="utf-8")))
    rows: list[dict[str, Any]] = []
    for seed in seeds_of(str(options.seeds)):
        row = one_seed(seed, int(options.days), out, invariants)
        rows.append(row)
        failed = [r["id"] for r in row["invariants"] if r["status"] == "FAIL"]
        print(
            f"REF seed={seed} run_rc={row['run_rc']} replay_rc={row['replay_rc']} fail={','.join(failed) or '-'}",
            flush=True,
        )
    (out / "bericht.md").write_text(report(rows, int(options.days)), encoding="utf-8", newline="\n")
    (out / "ergebnis.json").write_text(
        json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n"
    )
    print(f"REF_DONE {out / 'bericht.md'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
