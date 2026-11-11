"""Seed series of the world generator (B2 acceptance): `worldgen --json` over many seeds, judged.

Runs the built headless program for a range of seeds (`worldgen --seeds FROM-TO --json`: one process
generates the whole range and loads the content once; with `--jobs N` the range is split over N
processes side by side) and judges what it printed against the acceptance of B2:
- every island passes its mandatory check, and how many attempts that took;
- every line of the check on its own (food ... shelter, the start supply, `start_dry_ground`);
- different seeds give different worlds (distinct world hashes);
- every biome of the table on every island, at least one cliff and one cave;
- streams exist and end in the sea or a lake (`hydrology.stream_dead_ends`);
- the generation time (p50, p95, max of `time_ms`) against `generation_budget_ms` of the preset.
It writes `seed_series.jsonl` (the raw lines), `seed_series.json` (the figures) and
`seed_series.md` (a short German report for an AMW document) into the output folder.

Build once, then run (from the repository root; the output folder must be outside the repository):

    dotnet build src/Simulation.Headless -c Release -m:1
    uv run python tools/worldgen/seed_series.py --out ../series                    # seeds 1-1000
    uv run python tools/worldgen/seed_series.py --out ../series20 --seeds 1-20
    uv run python tools/worldgen/seed_series.py --out ../series --evaluate-only    # judge again

The 1,000 seeds are a long run: register it in the AMW run queue (`amw lauf start --warten`).
`--jobs` above 1 is only for a machine that is free; times measured side by side are slower.
The first island of every process pays the start-up of the runtime (JIT) in its `time_ms`; the
report names those seeds, and the verdict on the time counts them like every other.
An earlier report in the output folder is kept under a new name, never overwritten.
Exit code 0: every verdict holds. 3: measured, but a verdict fails. 1: the run failed or seeds are
missing. (2 is a usage error.)
"""

from __future__ import annotations

import argparse
import datetime
import json
import math
import shutil
import subprocess
import sys
import threading
import time
from collections import Counter
from collections.abc import Callable
from pathlib import Path
from typing import Any, TextIO

ROOT = Path(__file__).resolve().parents[2]
ASSEMBLY = "GlassWorld.Simulation.Headless.dll"
RAW, FIGURES, REPORT = "seed_series.jsonl", "seed_series.json", "seed_series.md"
EXIT_OK, EXIT_FAILED, EXIT_MISSED = 0, 1, 3
LISTED = 20
YES, NO, UNKNOWN = "erfüllt", "NICHT erfüllt", "nicht gemessen"


def parse_range(text: str) -> tuple[int, int]:
    """`FROM-TO`, both included."""
    first, sep, last = text.partition("-")
    if not sep or not first.isdecimal() or not last.isdecimal() or int(first) > int(last):
        raise argparse.ArgumentTypeError(f"wants FROM-TO with FROM not above TO, got '{text}'")
    return int(first), int(last)


def chunks(first: int, last: int, jobs: int) -> list[tuple[int, int]]:
    """The range in at most `jobs` contiguous parts of nearly equal size."""
    count = last - first + 1
    jobs = max(1, min(jobs, count))
    size, rest = divmod(count, jobs)
    parts: list[tuple[int, int]] = []
    start = first
    for index in range(jobs):
        end = start + size + (1 if index < rest else 0) - 1
        parts.append((start, end))
        start = end + 1
    return parts


def percentile(values: list[int], share: float) -> int:
    """Nearest-rank percentile (the value at or above the share of all values)."""
    ordered = sorted(values)
    return ordered[max(0, math.ceil(share * len(ordered)) - 1)]


def find_assembly(configuration: str) -> Path | None:
    """The built headless program of a configuration (any target framework folder)."""
    folder = ROOT / "src" / "Simulation.Headless" / "bin" / configuration
    found = sorted(folder.glob(f"*/{ASSEMBLY}"))
    return found[-1] if found else None


def evaluate(
    reports: list[dict[str, Any]],
    expected: tuple[int, int],
    budget_ms: int,
    warm_up: list[int] | None = None,
) -> dict[str, Any]:
    """The figures of a series and a verdict per acceptance bullet, from the `--json` reports."""
    by_seed = {int(r["seed"]): r for r in reports}
    seeds = sorted(by_seed)
    rows = [by_seed[s] for s in seeds]
    missing = [s for s in range(expected[0], expected[1] + 1) if s not in by_seed]
    total = len(rows)

    def lacking(test: Callable[[dict[str, Any]], bool]) -> list[int]:
        return [int(r["seed"]) for r in rows if not test(r)]

    failed = lacking(lambda r: r["passed"])
    attempts = Counter(int(r["attempts"]) for r in rows)

    # Every line of the check on its own; a line a report does not have counts as not passed there.
    line_ids: list[str] = []
    for r in rows:
        line_ids.extend(c["id"] for c in r["check"] if c["id"] not in line_ids)
    lines: list[dict[str, Any]] = []
    for line_id in line_ids:
        bad: list[int] = []
        report_only = False
        for r in rows:
            line = next((c for c in r["check"] if c["id"] == line_id), None)
            report_only = report_only or bool(line and line["report_only"])
            if line is None or not line["passed"]:
                bad.append(int(r["seed"]))
        lines.append(
            {
                "id": line_id,
                "report_only": report_only,
                "passed": total - len(bad),
                "failed_seeds": bad,
            }
        )

    hashes = Counter(str(r["hash"]) for r in rows)
    repeated = sorted(h for h, n in hashes.items() if n > 1)

    biome_ids: list[str] = []
    for r in rows:
        biome_ids.extend(b for b in r["biome_cells"] if b not in biome_ids)
    biomes = {
        b: lacking(lambda r, biome=b: int(r["biome_cells"].get(biome, 0)) > 0) for b in biome_ids
    }
    without_biome = sorted({s for missed in biomes.values() for s in missed})

    no_cliff = lacking(lambda r: int(r["cliff_cells"]) >= 1)
    no_cave = lacking(lambda r: int(r["faces"]["caves"]) >= 1)
    cave_out_of_reach = lacking(lambda r: int(r["faces"]["caves_reachable"]) >= 1)
    no_stream = lacking(lambda r: int(r["hydrology"]["stream_cells"]) >= 1)
    no_lake = lacking(lambda r: int(r["hydrology"]["lakes"]) >= 1)
    measured = all("stream_dead_ends" in r["hydrology"] for r in rows)
    dead_ends = lacking(lambda r: int(r["hydrology"].get("stream_dead_ends", 0)) == 0)

    times = [int(r["time_ms"]) for r in rows]
    over = lacking(lambda r: int(r["time_ms"]) <= budget_ms)
    time_figures: dict[str, Any] = {"budget_ms": budget_ms, "over_budget_seeds": over}
    if times:
        time_figures |= {
            "p50_ms": percentile(times, 0.5),
            "p95_ms": percentile(times, 0.95),
            "max_ms": max(times),
            "max_seed": int(rows[times.index(max(times))]["seed"]),
        }

    complete = total > 0 and not missing
    deciding = [line for line in lines if not line["report_only"]]

    def verdict(key: str, text: str, holds: bool | None, detail: str) -> dict[str, Any]:
        return {"id": key, "text": text, "holds": holds if complete else None, "detail": detail}

    verdicts = [
        verdict(
            "mandatory_check",
            "Jede Startinsel besteht die Pflichtprüfung",
            not failed,
            f"{total - len(failed)} von {total} bestanden",
        ),
        verdict(
            "check_lines",
            "Jede Zeile der Pflichtprüfung ist bei jedem Seed erfüllt (auch start_dry_ground)",
            all(not line["failed_seeds"] for line in deciding)
            and any(line["id"] == "start_dry_ground" for line in deciding),
            f"{sum(1 for line in deciding if not line['failed_seeds'])} von {len(deciding)}"
            " entscheidenden Zeilen bei allen Seeds erfüllt",
        ),
        verdict(
            "distinct_worlds",
            "Verschiedene Seeds ergeben verschiedene Welten",
            len(hashes) == total,
            f"{len(hashes)} verschiedene Welt-Hashes bei {total} Seeds",
        ),
        verdict(
            "biomes",
            "Jedes Biom der Tabelle kommt auf jeder Insel vor",
            not without_biome,
            f"{total - len(without_biome)} von {total} Seeds mit allen {len(biome_ids)} Biomen",
        ),
        verdict(
            "cliff_and_cave",
            "Jede Insel hat mindestens eine Klippe und eine Höhle",
            not no_cliff and not no_cave,
            f"ohne Klippe: {len(no_cliff)}, ohne Höhle: {len(no_cave)}",
        ),
        verdict(
            "streams",
            "Jede Insel hat einen Bach, und jeder Bach endet im Meer oder in einem See",
            (not no_stream and not dead_ends) if measured else None,
            f"ohne Bach: {len(no_stream)}, mit Bach ohne Ende im Wasser: {len(dead_ends)}"
            if measured
            else "stream_dead_ends fehlt in der Ausgabe (älterer Stand von worldgen)",
        ),
        verdict(
            "time",
            "Die Erzeugung bleibt im Zeitbudget des Presets",
            not over,
            f"max {time_figures.get('max_ms', '-')} ms, Budget {budget_ms} ms,"
            f" darüber: {len(over)}",
        ),
    ]
    return {
        "seeds": {
            "from": expected[0],
            "to": expected[1],
            "expected": expected[1] - expected[0] + 1,
            "reported": total,
            "missing": missing,
        },
        "generator_versions": sorted({int(r["generator_version"]) for r in rows}),
        "presets": sorted({str(r["preset"]) for r in rows}),
        "passed": total - len(failed),
        "failed_seeds": failed,
        "attempts": {str(k): attempts[k] for k in sorted(attempts)},
        "check_lines": lines,
        "distinct_hashes": len(hashes),
        "repeated_hashes": repeated,
        "biomes": {"ids": biome_ids, "missing_seeds": biomes, "seeds_missing_any": without_biome},
        "cliffs": {"seeds_without": no_cliff},
        "caves": {"seeds_without": no_cave, "seeds_without_reachable": cave_out_of_reach},
        "streams": {
            "seeds_without": no_stream,
            "seeds_without_lake": no_lake,
            "dead_ends_measured": measured,
            "seeds_with_dead_ends": dead_ends if measured else [],
        },
        "time_ms": time_figures,
        "warm_up_seeds": sorted(warm_up or []),
        "verdicts": verdicts,
        "all_hold": all(v["holds"] is True for v in verdicts),
    }


def listed(seeds: list[int]) -> str:
    """Seeds for a table cell: the first few, then how many more."""
    if not seeds:
        return "-"
    shown = ", ".join(str(s) for s in seeds[:LISTED])
    return shown if len(seeds) <= LISTED else f"{shown} und {len(seeds) - LISTED} weitere"


def report(figures: dict[str, Any], run: dict[str, Any]) -> str:
    """The German report (Markdown) for an AMW document."""
    seeds = figures["seeds"]
    total = seeds["reported"]
    time_ms = figures["time_ms"]
    out = [
        "# Seed-Serie des Weltgenerators (B2-Abnahme)",
        "",
        f"- Seeds: {seeds['from']} bis {seeds['to']} ({total} von {seeds['expected']} gemeldet)",
        f"- Preset: {', '.join(figures['presets']) or '-'},"
        f" Generator-Version: {', '.join(str(v) for v in figures['generator_versions']) or '-'}",
        f"- Stand: {run.get('note') or '-'}",
        f"- Lauf: {run.get('started') or '-'}, Dauer {run.get('seconds', '-')} s,"
        f" Prozesse nebeneinander: {run.get('jobs', '-')}",
        f"- Programm: {run.get('assembly') or '-'}",
        "",
    ]
    if seeds["missing"]:
        out += [f"**Unvollständig: es fehlen die Seeds {listed(seeds['missing'])}.**", ""]
    out += ["## Urteil je Abnahmepunkt", "", "| Abnahmepunkt | Urteil | Zahlen |", "|---|---|---|"]
    for v in figures["verdicts"]:
        word = UNKNOWN if v["holds"] is None else YES if v["holds"] else NO
        out.append(f"| {v['text']} | {word} | {v['detail']} |")
    out += [
        "",
        "## Pflichtprüfung",
        "",
        f"Bestanden: {figures['passed']} von {total}."
        f" Nicht bestanden: {listed(figures['failed_seeds'])}.",
        "",
        "| Versuche | Seeds |",
        "|---|---|",
    ]
    out += [f"| {tries} | {count} |" for tries, count in figures["attempts"].items()]
    out += ["", "| Zeile | erfüllt | nicht erfüllt bei Seed |", "|---|---|---|"]
    for line in figures["check_lines"]:
        name = f"{line['id']} (nur Bericht)" if line["report_only"] else line["id"]
        out.append(f"| {name} | {line['passed']} von {total} | {listed(line['failed_seeds'])} |")
    biomes = figures["biomes"]
    out += [
        "",
        "## Welt",
        "",
        f"- Verschiedene Welt-Hashes: {figures['distinct_hashes']} bei {total} Seeds.",
        f"- Seeds, denen ein Biom fehlt: {listed(biomes['seeds_missing_any'])}.",
        f"- Seeds ohne Klippe: {listed(figures['cliffs']['seeds_without'])}.",
        f"- Seeds ohne Höhle: {listed(figures['caves']['seeds_without'])};"
        f" ohne erreichbare Höhle: {listed(figures['caves']['seeds_without_reachable'])}.",
        f"- Seeds ohne Bach: {listed(figures['streams']['seeds_without'])};"
        f" ohne See: {listed(figures['streams']['seeds_without_lake'])}.",
    ]
    if figures["streams"]["dead_ends_measured"]:
        ends = listed(figures["streams"]["seeds_with_dead_ends"])
        out.append(f"- Seeds mit einem Bach, der nicht im Meer oder in einem See endet: {ends}.")
    else:
        out.append("- Bachenden: nicht gemessen (stream_dead_ends fehlt in der Ausgabe).")
    out += ["", "| Biom | fehlt bei Seeds | welche |", "|---|---|---|"]
    out += [
        f"| {b} | {len(biomes['missing_seeds'][b])} | {listed(biomes['missing_seeds'][b])} |"
        for b in biomes["ids"]
    ]
    out += [
        "",
        "## Zeit",
        "",
        f"- time_ms: p50 {time_ms.get('p50_ms', '-')}, p95 {time_ms.get('p95_ms', '-')},"
        f" max {time_ms.get('max_ms', '-')} (Seed {time_ms.get('max_seed', '-')});"
        f" Budget des Presets (generation_budget_ms): {time_ms['budget_ms']}.",
        f"- Über dem Budget: {listed(time_ms['over_budget_seeds'])}.",
        f"- Erster Seed je Prozess (mit dem Anlauf der Laufzeit in time_ms):"
        f" {listed(figures['warm_up_seeds'])}.",
        "",
    ]
    return "\n".join(out)


def generate(
    assembly: Path,
    parts: list[tuple[int, int]],
    preset: str,
    content: Path,
    raw: Path,
) -> tuple[list[dict[str, Any]], list[str]]:
    """Runs one process per part, side by side; the raw lines go to `raw` as they come."""
    reports: list[dict[str, Any]] = []
    problems: list[str] = []
    lock = threading.Lock()
    wanted = sum(last - first + 1 for first, last in parts)

    def one(first: int, last: int, sink: TextIO) -> None:
        command = ["dotnet", str(assembly), "worldgen", "--seeds", f"{first}-{last}", "--json"]
        command += ["--preset", preset, "--content", str(content)]
        with subprocess.Popen(
            command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8"
        ) as process:
            assert process.stdout is not None and process.stderr is not None
            for line in process.stdout:
                if not line.strip():
                    continue
                try:
                    parsed = json.loads(line)
                except json.JSONDecodeError:
                    with lock:
                        problems.append(f"seeds {first}-{last}: no JSON line: {line.strip()[:120]}")
                    continue
                with lock:
                    reports.append(parsed)
                    sink.write(line if line.endswith("\n") else line + "\n")
                    sink.flush()
                    if len(reports) % 25 == 0 or len(reports) == wanted:
                        print(f"  {len(reports)} of {wanted} seeds", file=sys.stderr, flush=True)
            errors = process.stderr.read()
            code = process.wait()
        # 0: every island passed; 2: at least one did not (the lines say which); else it failed.
        if code not in (0, 2):
            with lock:
                problems.append(
                    f"seeds {first}-{last}: worldgen ended with {code}: {errors[-400:]}"
                )

    with raw.open("w", encoding="utf-8", newline="\n") as sink:
        workers = [threading.Thread(target=one, args=(*part, sink)) for part in parts]
        for worker in workers:
            worker.start()
        for worker in workers:
            worker.join()
    return reports, problems


def keep_earlier(out: Path) -> None:
    """An earlier series in the folder is kept under a new name (never overwritten)."""
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    for name in (RAW, FIGURES, REPORT):
        file = out / name
        if file.exists():
            file.rename(file.with_name(f"{file.stem}.before-{stamp}{file.suffix}"))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--out", type=Path, required=True, help="folder outside the repository")
    parser.add_argument("--seeds", type=parse_range, default=(1, 1000), help="FROM-TO (1-1000)")
    parser.add_argument("--jobs", type=int, default=1, help="processes side by side (default 1)")
    parser.add_argument("--preset", default="start_island")
    parser.add_argument("--content", type=Path, default=ROOT / "content", help="content folder")
    parser.add_argument("--configuration", default="Release", help="build to run (Release)")
    parser.add_argument("--assembly", type=Path, help=f"path of {ASSEMBLY} (else the build's)")
    parser.add_argument("--note", default="", help="which state is measured (commit, branch)")
    parser.add_argument("--evaluate-only", action="store_true", help="judge again what is there")
    options = parser.parse_args(argv)
    out: Path = options.out.resolve()
    if out == ROOT or ROOT in out.parents:
        print("SEED_SERIES_FAIL: --out must be a folder outside the repository", file=sys.stderr)
        return EXIT_FAILED
    preset_file = options.content / "world" / "presets" / f"{options.preset}.json"
    if not preset_file.is_file():
        print(f"SEED_SERIES_FAIL: preset file {preset_file} not found", file=sys.stderr)
        return EXIT_FAILED
    budget_ms = int(json.loads(preset_file.read_text("utf-8"))["generation_budget_ms"])

    run: dict[str, Any]
    problems: list[str] = []
    if options.evaluate_only:
        if not (out / RAW).is_file():
            print(f"SEED_SERIES_FAIL: {out / RAW} not found", file=sys.stderr)
            return EXIT_FAILED
        earlier: dict[str, Any] = {}
        if (out / FIGURES).is_file():
            earlier = json.loads((out / FIGURES).read_text("utf-8"))
        run = earlier.get("run", {})
        text = (out / RAW).read_text("utf-8")
        reports = [json.loads(line) for line in text.splitlines() if line.strip()]
        expected = (int(run["seeds"][0]), int(run["seeds"][1])) if "seeds" in run else options.seeds
        warm_up = [int(s) for s in run.get("warm_up_seeds", [])]
    else:
        assembly = options.assembly or find_assembly(options.configuration)
        if assembly is None or not assembly.is_file():
            print(
                "SEED_SERIES_FAIL: the headless program is not built; run"
                f" `dotnet build src/Simulation.Headless -c {options.configuration} -m:1` first",
                file=sys.stderr,
            )
            return EXIT_FAILED
        if shutil.which("dotnet") is None:
            print("SEED_SERIES_FAIL: dotnet not found", file=sys.stderr)
            return EXIT_FAILED
        expected = options.seeds
        parts = chunks(expected[0], expected[1], options.jobs)
        out.mkdir(parents=True, exist_ok=True)
        keep_earlier(out)
        started = datetime.datetime.now().astimezone().isoformat(timespec="seconds")
        clock = time.monotonic()
        reports, problems = generate(
            assembly.resolve(), parts, options.preset, options.content.resolve(), out / RAW
        )
        warm_up = [first for first, _ in parts]
        run = {
            "note": options.note,
            "started": started,
            "seconds": round(time.monotonic() - clock, 1),
            "jobs": len(parts),
            "assembly": str(assembly.resolve()),
            "seeds": list(expected),
            "warm_up_seeds": warm_up,
        }

    figures = evaluate(reports, expected, budget_ms, warm_up)
    figures["run"] = run
    figures["problems"] = problems
    (out / FIGURES).write_text(
        json.dumps(figures, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n"
    )
    (out / REPORT).write_text(report(figures, run), encoding="utf-8", newline="\n")
    for v in figures["verdicts"]:
        word = "not measured" if v["holds"] is None else "holds" if v["holds"] else "FAILS"
        print(f"SEED_SERIES {v['id']}: {word}")
    print(f"SEED_SERIES report: {out / REPORT}")
    for problem in problems:
        print(f"SEED_SERIES_FAIL: {problem}", file=sys.stderr)
    if problems or figures["seeds"]["missing"] or not reports:
        return EXIT_FAILED
    return EXIT_OK if figures["all_hold"] else EXIT_MISSED


if __name__ == "__main__":
    raise SystemExit(main())
