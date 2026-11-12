"""B4a R10 question test set (A1403): an inhabitant asks about something it does not understand.

Live measurement, only as an AMW GPU run (`amw lauf ausfuehren --gpu ...`):
    uv run python tools/testsets/measure_questions.py --gpu-queue --out ../b4a-questions [--runs 5]

Per answer it checks what a rule can check: valid by the answer schema (the handler enforces it)
and no thing guesses about a person. It reports what a rule cannot judge: how often the model
says "not understood", which properties it guesses beyond what the inhabitant notices, and how
many guesses it claims to be surer of than the core lets a guess be (guessed_cap of
content/knowledge/settings.json). The cases decide nothing in the world: they only say what the
inhabitant notices and remembers. The output folder must be outside the repository; an earlier
run's folder is kept under a new name (rule PR4). Exit code 0 only if every answer is valid and no
person got thing guesses.
"""

from __future__ import annotations

import argparse
import asyncio
import collections
import copy
import itertools
import json
import sys
import time
import uuid
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
MARKER = ".glassworld-b4a-questions"
# id, question, subject (kind, id), what the inhabitant notices, hunger (0 = very hungry), locale,
# memory summary (may name the thing: the prompt must not pass that name on, review GW1 #215)
CASES: list[tuple[str, str, tuple[str, str], list[str], float, str, str]] = [
    ("unknown_berry_hungry", "can_i_eat", ("type", "red_berry"), ["fruit"], 0.1, "de", ""),
    ("unknown_berry_fed", "what_is", ("type", "red_berry"), ["fruit"], 0.8, "de", ""),
    ("unknown_mushroom", "can_i_eat", ("type", "red_mushroom"), ["mushroom"], 0.2, "en", ""),
    ("unknown_stone", "what_can_i_do", ("type", "flint"), ["stone", "hard", "sharp"], 0.7, "de", ""),
    ("nothing_noticed", "what_is", ("type", "peat"), [], 0.7, "de", ""),
    ("unknown_water", "is_dangerous", ("type", "sea_water"), ["water"], 0.6, "en", ""),
    ("first_person", "what_is", ("inhabitant", "inh-000002"), [], 0.7, "de", ""),
    ("stone_named_in_memory", "what_can_i_do", ("type", "flint"), ["stone", "hard", "sharp"], 0.7,
     "de", "Woke up on a beach. Found a flint by the rocks and kept looking at the flint."),
    ("water_named_in_memory", "is_dangerous", ("type", "sea_water"), ["water"], 0.6, "en",
     "Walked along the shore and saw sea water everywhere."),
]  # fmt: skip


def job_data(
    case: tuple[str, str, tuple[str, str], list[str], float, str, str], run: int
) -> dict[str, Any]:
    """The question job of a case and run, built from the contract example."""
    cid, question, (kind, subject), notices, hunger, locale, summary = case
    base = json.loads(
        (ROOT / "contracts/examples/valid/generation-job.question-berries.json").read_text("utf-8")
    )
    data: dict[str, Any] = copy.deepcopy(base)
    data["request_id"] = str(uuid.uuid5(uuid.NAMESPACE_URL, f"b4a-question/{cid}/{run}"))
    data["seed"] = 14030 + run * 1000 + CASES.index(case)
    data["display_locale"] = locale
    payload = data["payload"]["question"]
    payload["question"] = question
    payload["subject"] = {"kind": kind, "id": subject}
    payload["perception"] = notices
    payload["inhabitant"]["knowledge"] = []
    payload["inhabitant"]["memory"] = {"summary": summary, "recent": []}
    for need in payload["inhabitant"]["needs"]:
        if need["id"] == "hunger":
            need["value"] = hunger
    return data


def archive_name(folder: Path) -> Path:
    """A free name next to `folder` for keeping an earlier run: <name>.alt-1, .alt-2, ..."""
    for n in itertools.count(1):
        candidate = folder.with_name(f"{folder.name}.alt-{n}")
        if not candidate.exists():
            return candidate
    raise AssertionError("unreachable")


def guard(out: Path, gpu_queue: bool) -> str | None:
    """Why the run must not start, or None: live only in the GPU queue, output outside the repo."""
    if not gpu_queue:
        return "live model runs only as an AMW GPU-queue run (pass --gpu-queue)"
    if out == ROOT or ROOT in out.parents:
        return f"output folder {out} is inside the repository"
    if out.exists() and any(out.iterdir()) and not (out / MARKER).is_file():
        return f"{out} exists, is not empty and is not a folder of this tool"
    return None


def summarize(rows: list[dict[str, Any]], cap: float) -> tuple[bool, list[str]]:
    """Whether the run passed (all valid, no thing guesses about a person) and the report lines."""
    ok = [r for r in rows if r["ok"]]
    person = [r for r in ok if r["person"] and r.get("guesses")]
    guesses = [(r, g) for r in ok for g in r.get("guesses", [])]
    beyond = [(r, g) for r, g in guesses if g["property"] not in r["notices"]]
    lines = [
        f"Q_RESULT valid {len(ok)}/{len(rows)}, retried {sum(r['attempts'] > 1 for r in ok)}, "
        f"not understood {sum(not r['understood'] for r in ok)}, "
        f"person with thing guesses {len(person)}, guesses {len(guesses)}, "
        f"beyond what it notices {len(beyond)}, "
        f"surer than the core's cap {cap}: {sum(g['confidence'] > cap for _, g in guesses)}"
    ]
    counted = collections.Counter((r["case"], g["property"], g["value"]) for r, g in beyond)
    lines += [
        f"  {case}: {prop}={value} x{n}" for (case, prop, value), n in sorted(counted.items())
    ]
    return len(ok) == len(rows) and not person, lines


async def run(out: Path, runs: int, night: bool = False) -> list[dict[str, Any]]:
    import httpx  # the service is imported only for a real run, so the tests need no settings

    from glassworld_ai.calllog import CallLog
    from glassworld_ai.config import load_settings
    from glassworld_ai.jobs import JobFailedError
    from glassworld_ai.llm.jobcall import ModelCallSettings
    from glassworld_ai.llm.ollama import OllamaClient
    from glassworld_ai.prompts import load_prompt
    from glassworld_ai.questions.handlers import OllamaQuestions
    from glassworld_ai.queue import ModelQueue
    from glassworld_ai.vocabulary import Vocabulary
    from glassworld_contracts import GenerationJob

    settings = load_settings()
    rows: list[dict[str, Any]] = []
    async with httpx.AsyncClient() as http:
        client = OllamaClient(
            str(settings.ollama_url), settings.decider_model, http, settings.keep_alive
        )
        try:
            await client.warm_up(timeout=300)
            ask = OllamaQuestions(
                client=client,
                queue=ModelQueue(settings.queue_size),
                template=load_prompt(
                    settings.prompts_dir, settings.question_prompt, settings.question_prompt_version
                ),
                vocab=Vocabulary.load(settings.content_dir / "vocabulary"),
                settings=ModelCallSettings(
                    temperature=settings.temperature,
                    num_ctx=settings.num_ctx,
                    max_retries=settings.max_retries,
                    call_timeout_s=settings.question_call_timeout_s,
                ),
                log=CallLog(out / "questions.jsonl"),
                is_night=lambda: night,  # v5 (B2652): asked in the computing night
            )
            for index in range(runs):
                for case in CASES:
                    started = time.perf_counter()
                    row: dict[str, Any] = {
                        "case": case[0],
                        "run": index,
                        "person": case[2][0] == "inhabitant",
                        "notices": case[3],
                    }
                    try:
                        outcome = await ask(GenerationJob.model_validate(job_data(case, index)))
                        result = outcome.result["question"]
                        row |= {"ok": True, "attempts": outcome.attempts, **result}  # type: ignore[dict-item]
                    except JobFailedError as error:
                        row |= {"ok": False, "error": f"{error.code}: {error}"}
                    row["ms"] = int((time.perf_counter() - started) * 1000)
                    rows.append(row)
                    print(json.dumps(row, ensure_ascii=False), flush=True)
        finally:
            await client.unload(timeout=30)  # never leave the model in the VRAM (review GW1 #215)
    return rows


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("--out", type=Path, required=True, help="folder outside the repository")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument(
        "--night", action="store_true", help="ask as in the computing night (question v5, B2652)"
    )
    parser.add_argument(
        "--gpu-queue", action="store_true", help="required: this is a GPU-queue run"
    )
    options = parser.parse_args(argv)
    out = options.out.resolve()
    problem = guard(out, options.gpu_queue)
    if problem is not None:
        print(f"Q_FAIL: {problem}", file=sys.stderr)
        return 1
    if out.exists():
        out.rename(archive_name(out))  # rule PR4: an earlier run is kept, never overwritten
    out.mkdir(parents=True)
    (out / MARKER).write_text("made by tools/testsets/measure_questions.py", encoding="utf-8")
    rows = asyncio.run(run(out, options.runs, options.night))
    (out / "questions.json").write_text(
        json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    knowledge = json.loads((ROOT / "content/knowledge/settings.json").read_text("utf-8"))
    passed, lines = summarize(rows, float(knowledge["guessed_cap"]))
    print("\n".join(lines))
    return 0 if passed else 2


if __name__ == "__main__":
    raise SystemExit(main())
