"""AI test sets (handbook "KI-Pruefsaetze"): fixed situations, N runs with the real model.

    uv run python -m glassworld_ai.testsets decider --runs 5 --out <folder>

Run only inside an AMW GPU run (`amw lauf ausfuehren --gpu ...`). Writes report.json and
report.md. Acceptance B3 A1: content-valid >= 95 %; latency is measured, not limited (B2487).
"""

from __future__ import annotations

import argparse
import asyncio
import json
import statistics
import time
import uuid
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

import httpx
from pydantic import BaseModel, ConfigDict

from glassworld_ai.calllog import CallLog
from glassworld_ai.config import load_settings
from glassworld_ai.decider.backends import Decider, DecisionError, ModelSettings, OllamaDecider
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import DecisionRequest

DEFAULT_CASES = Path(__file__).resolve().parents[2] / "testsets" / "decider" / "cases.v1.json"
_RUN_NAMESPACE = uuid.UUID("6f1c2d3e-4b5a-4c6d-8e7f-a1330a1330a1")


class TestCase(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str
    description: str
    request: dict[str, Any]
    plausible_option_ids: list[str]


class TestSet(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str
    version: int
    purpose: str
    cases: list[TestCase]


@dataclass(frozen=True)
class RunResult:
    case_id: str
    run: int
    outcome: str  # "ok" or a ServiceError code
    option_id: str | None
    plausible: bool
    latency_ms: int
    attempts: int
    thought: str | None


def load_testset(path: Path) -> TestSet:
    return TestSet.model_validate(json.loads(path.read_text(encoding="utf-8")))


def _request_for_run(case: TestCase, run: int) -> DecisionRequest:
    data = dict(case.request)
    data["request_id"] = str(uuid.uuid5(_RUN_NAMESPACE, f"{case.id}/{run}"))
    data["seed"] = (int(data["seed"]) + run * 1000) % 2_147_483_648
    data["deadline_ms"] = 60000
    return DecisionRequest.model_validate(data)


async def run_cases(decider: Decider, testset: TestSet, runs: int) -> list[RunResult]:
    results: list[RunResult] = []
    for run in range(runs):
        for case in testset.cases:
            request = _request_for_run(case, run)
            started = time.perf_counter()
            try:
                answer = await decider.decide(request, time.monotonic() + 60)
            except DecisionError as error:
                results.append(
                    RunResult(
                        case.id,
                        run,
                        error.code,
                        None,
                        False,
                        int((time.perf_counter() - started) * 1000),
                        0,
                        None,
                    )
                )
                continue
            option = str(getattr(answer.option_id, "root", answer.option_id))
            results.append(
                RunResult(
                    case_id=case.id,
                    run=run,
                    outcome="ok",
                    option_id=option,
                    plausible=option in case.plausible_option_ids,
                    latency_ms=int((time.perf_counter() - started) * 1000),
                    attempts=answer.meta.attempts,
                    thought=answer.thought.text if answer.thought else None,
                )
            )
    return results


def summarize(results: list[RunResult], wall_seconds: float) -> dict[str, Any]:
    total = len(results)
    ok = [r for r in results if r.outcome == "ok"]
    latencies = sorted(r.latency_ms for r in ok)
    errors: dict[str, int] = {}
    for r in results:
        if r.outcome != "ok":
            errors[r.outcome] = errors.get(r.outcome, 0) + 1
    p95 = latencies[max(0, round(0.95 * len(latencies)) - 1)] if latencies else None
    per_case: dict[str, dict[str, int]] = {}
    for r in results:
        entry = per_case.setdefault(r.case_id, {"runs": 0, "ok": 0, "plausible": 0})
        entry["runs"] += 1
        entry["ok"] += r.outcome == "ok"
        entry["plausible"] += r.plausible
    return {
        "total": total,
        "content_valid_rate": len(ok) / total if total else 0.0,
        "plausible_rate": sum(r.plausible for r in results) / total if total else 0.0,
        "retry_rate": sum(r.attempts > 1 for r in ok) / len(ok) if ok else 0.0,
        "thought_rate": sum(r.thought is not None for r in ok) / len(ok) if ok else 0.0,
        "latency_ms_mean": statistics.fmean(latencies) if latencies else None,
        "latency_ms_p50": statistics.median(latencies) if latencies else None,
        "latency_ms_p95": p95,
        "decisions_per_minute": len(ok) / (wall_seconds / 60) if wall_seconds > 0 else None,
        "errors": errors,
        "per_case": per_case,
    }


def write_report(
    out: Path, summary: dict[str, Any], results: list[RunResult], meta: dict[str, Any]
) -> None:
    out.mkdir(parents=True, exist_ok=True)
    (out / "report.json").write_text(
        json.dumps(
            {"meta": meta, "summary": summary, "results": [asdict(r) for r in results]},
            indent=2,
            ensure_ascii=False,
        )
        + "\n",
        encoding="utf-8",
        newline="\n",
    )
    s = summary
    lines = [
        f"# Decider test set - {meta['testset']} v{meta['version']}",
        "",
        f"Model {meta['model']}, prompt {meta['prompt']}, temperature {meta['temperature']}, "
        f"{meta['runs']} runs x {meta['cases']} cases = {s['total']} decisions.",
        "",
        "| Metric | Value |",
        "|---|---|",
        f"| Content-valid (acceptance >= 95 %) | {s['content_valid_rate']:.1%} |",
        f"| Plausible (expected options) | {s['plausible_rate']:.1%} |",
        f"| Answers needing a retry | {s['retry_rate']:.1%} |",
        f"| Answers with thought | {s['thought_rate']:.1%} |",
        "| Latency mean / p50 / p95 | "
        f"{s['latency_ms_mean']} / {s['latency_ms_p50']} / {s['latency_ms_p95']} ms |",
        f"| Decisions per minute | {s['decisions_per_minute']} |",
        f"| Errors | {s['errors'] or 'none'} |",
        "",
        "| Case | ok | plausible |",
        "|---|---|---|",
        *[
            f"| {cid} | {v['ok']}/{v['runs']} | {v['plausible']}/{v['runs']} |"
            for cid, v in s["per_case"].items()
        ],
        "",
    ]
    (out / "report.md").write_text("\n".join(lines), encoding="utf-8", newline="\n")


async def _main(args: argparse.Namespace) -> int:
    settings = load_settings()
    testset = load_testset(args.cases)
    async with httpx.AsyncClient() as http:
        client = OllamaClient(
            str(settings.ollama_url), settings.decider_model, http, settings.keep_alive
        )
        await client.warm_up(timeout=300)
        decider = OllamaDecider(
            client=client,
            queue=ModelQueue(settings.queue_size),
            template=load_prompt(
                settings.prompts_dir, settings.decider_prompt, settings.decider_prompt_version
            ),
            vocab=Vocabulary.load(settings.content_dir / "vocabulary"),
            settings=ModelSettings(
                settings.temperature,
                settings.num_ctx,
                settings.prompt_char_budget,
                settings.max_retries,
            ),
            log=CallLog(args.out / "calls.jsonl"),
        )
        started = time.perf_counter()
        results = await run_cases(decider, testset, args.runs)
        wall = time.perf_counter() - started
        await client.unload(timeout=30)
    summary = summarize(results, wall)
    meta = {
        "testset": testset.id,
        "version": testset.version,
        "cases": len(testset.cases),
        "runs": args.runs,
        "model": settings.decider_model,
        "prompt": f"{settings.decider_prompt}.v{settings.decider_prompt_version}",
        "temperature": settings.temperature,
    }
    write_report(args.out, summary, results, meta)
    print((args.out / "report.md").read_text(encoding="utf-8"), flush=True)
    return 0 if summary["content_valid_rate"] >= 0.95 else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="glassworld_ai.testsets", description=__doc__)
    parser.add_argument("kind", choices=["decider"])
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--cases", type=Path, default=DEFAULT_CASES)
    parser.add_argument("--out", type=Path, required=True)
    return asyncio.run(_main(parser.parse_args(argv)))


if __name__ == "__main__":
    raise SystemExit(main())
