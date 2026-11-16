"""A1582 measurement (D827 R26a/b, R29): the nightly look back with the live model - valid answers, ideas
and thoughts left out, and world-knowledge leaks: bait terms in idea texts (EN), idea thoughts, morning
thoughts and dreams (DE). Run inside an AMW GPU run:

    uv run python tools/testsets/measure_review.py <repo> <out> [--version N] [--runs R]

<repo> is the tree to measure (its config, prompts, content), <out> gets review.json and the call log.
Prints one line per call and a summary line REVIEW_RESULT. Exit code 0 only if every call gave a valid
answer and no bait term reached a shown text (thoughts and dreams); bait terms in the internal English
idea text are counted, not failed - the shown text is what a player sees. It never prescribes an idea.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
import time
from pathlib import Path
from typing import Any, cast

import httpx

from glassworld_ai.calllog import CallLog
from glassworld_ai.config import load_settings
from glassworld_ai.jobs import JobFailedError
from glassworld_ai.llm.jobcall import ModelCallSettings, ask_model
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_ai.review import ROLE, build_values, output_schema, parse_review, retry_feedback
from glassworld_ai.safety import TextGuard
from glassworld_ai.texts import TextCatalog
from glassworld_ai.vocabulary import Vocabulary
from glassworld_contracts import GenerationJob

sys.path.insert(0, str(Path(__file__).resolve().parent))
from measure_inventions import bait_terms


def shown_texts(review: dict[str, Any]) -> list[str]:
    """What a player can see of a look back: the dream, the idea thoughts, the morning thoughts."""
    texts: list[str] = []
    if review.get("dream"):
        texts.append(str(review["dream"]["text"]))
    for item in [*review.get("ideas", []), *review.get("intentions", [])]:
        thought = item.get("thought")
        if thought:
            texts.append(
                str(cast(dict[str, Any], thought)["text"])
                if isinstance(thought, dict)
                else str(thought)
            )
    return texts


def judge(
    review: dict[str, Any], notes: list[str], terms: dict[str, list[str]], locale: str
) -> dict[str, Any]:
    """The counts of one valid answer."""
    ideas = review.get("ideas", [])
    shown = shown_texts(review)
    idea_text = " ".join(str(i["text"]) for i in ideas)
    return {
        "ideas": len(ideas),
        "idea_thoughts_left_out": sum(1 for i in ideas if not i.get("thought")),
        "ideas_without_root": sum(1 for n in notes if "no root of its own knowledge" in n),
        "unknown_left_out": sum(1 for n in notes if "unknown:" in n),
        "dream": review.get("dream") is not None,
        "bait_shown": sorted({h for t in shown for h in bait_terms(t, terms.get(locale, []))}),
        "bait_idea_text": sorted(set(bait_terms(idea_text, terms.get("en", [])))),
        "notes": notes,
    }


async def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("repo", type=Path)
    parser.add_argument("out", type=Path)
    parser.add_argument(
        "--version", type=int, default=None, help="review prompt version (default: config)"
    )
    parser.add_argument("--runs", type=int, default=1, help="calls per case (TL #28359: once each)")
    parser.add_argument("--cases", default="ai-service/testsets/review/cases.v2.json")
    args = parser.parse_args(argv)
    settings = load_settings()
    args.out.mkdir(parents=True, exist_ok=True)
    data = json.loads((args.repo / args.cases).read_text(encoding="utf-8"))
    terms: dict[str, list[str]] = data.get("bait_terms", {})
    version = args.version or settings.review_prompt_version
    template = load_prompt(settings.prompts_dir, settings.review_prompt, version)
    texts = TextCatalog.load(settings.content_dir / "i18n")
    moods = Vocabulary.load(settings.content_dir / "vocabulary").ids("mood")
    guard = TextGuard.from_file(settings.content_dir / "safety" / "rules.json")
    call = ModelCallSettings(
        temperature=settings.temperature,
        num_ctx=settings.num_ctx,
        max_retries=settings.max_retries,
        call_timeout_s=180,
    )
    rows: list[dict[str, Any]] = []
    async with httpx.AsyncClient() as http:
        client = OllamaClient(
            str(settings.ollama_url), settings.decider_model, http, settings.keep_alive
        )
        await client.warm_up(timeout=300)
        queue, log = ModelQueue(settings.queue_size), CallLog(args.out / "review.jsonl")
        for case in data["cases"]:
            for run in range(args.runs):
                raw = dict(case["job"], seed=int(case["job"]["seed"]) + run)
                job = GenerationJob.model_validate(raw)
                locale = str(raw["display_locale"])
                started = time.perf_counter()
                row: dict[str, Any] = {"case": case["id"], "bait": case.get("bait"), "run": run}
                try:
                    answer = await ask_model(
                        role=ROLE,
                        job=job,
                        rendered=template.render(build_values(job, template, texts)),
                        schema=output_schema(job, moods, texts, template.settings),
                        parse=lambda text, job=job: parse_review(
                            text, job, guard, moods, texts, template.settings
                        ),
                        client=client,
                        queue=queue,
                        settings=call,
                        log=log,
                        feedback=retry_feedback(template.settings),
                    )
                    review, notes = answer.value
                    row |= {"ok": True, "attempts": answer.attempts} | judge(
                        review, notes, terms, locale
                    )
                except JobFailedError as error:
                    row |= {"ok": False, "error": f"{error.code}: {error}"}
                row["ms"] = int((time.perf_counter() - started) * 1000)
                rows.append(row)
                print(
                    json.dumps({k: v for k, v in row.items() if k != "notes"}, ensure_ascii=False),
                    flush=True,
                )
        await client.unload(timeout=30)
    (args.out / "review.json").write_text(
        json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8"
    )
    ok = [r for r in rows if r["ok"]]
    baited = [r for r in ok if r["bait"]]
    shown_hits = [r for r in ok if r["bait_shown"]]
    print(
        f"REVIEW_RESULT v{version} valid {len(ok)}/{len(rows)}, ideas {sum(r['ideas'] for r in ok)} "
        f"(thought left out {sum(r['idea_thoughts_left_out'] for r in ok)}, no own root "
        f"{sum(r['ideas_without_root'] for r in ok)}), texts left out as unknown "
        f"{sum(r['unknown_left_out'] for r in ok)}, dreams {sum(r['dream'] for r in ok)}/{len(ok)}; "
        f"bait cases {len(baited)}: bait term in a shown text {len(shown_hits)}, "
        f"in the English idea text {sum(1 for r in ok if r['bait_idea_text'])}",
        flush=True,
    )
    for r in shown_hits:
        print(f"  SHOWN {r['case']} run {r['run']}: {r['bait_shown']}", flush=True)
    return 0 if len(ok) == len(rows) and not shown_hits else 2


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
