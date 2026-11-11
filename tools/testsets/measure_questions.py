"""B4a R10 question test set (A1403): an inhabitant asks about something it does not understand.

Live measurement for an AMW GPU run:
    uv run python tools/testsets/measure_questions.py <repo> <out> [runs]
Checks per answer: valid by the answer schema (the handler enforces it), no thing guesses about a
person, and reports what cannot be judged by a rule: how often the model says "not understood",
which properties it guesses and how sure it claims to be about things nobody has tried (the core
caps a guess anyway, content/knowledge/settings.json guessed_cap). <out> gets questions.json and
the call log. The cases decide nothing in the world: they only say what the inhabitant notices.
"""

import asyncio
import collections
import copy
import json
import sys
import time
import uuid
from pathlib import Path

import httpx

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

REPO, OUT = Path(sys.argv[1]), Path(sys.argv[2])
RUNS = int(sys.argv[3]) if len(sys.argv) > 3 else 5
BASE = json.loads(
    (REPO / "contracts/examples/valid/generation-job.question-berries.json").read_text("utf-8")
)
# id, question, subject (kind, id), what the inhabitant notices, hunger (0 = very hungry), locale
CASES = [
    ("unknown_berry_hungry", "can_i_eat", ("type", "red_berry"), ["fruit"], 0.1, "de"),
    ("unknown_berry_fed", "what_is", ("type", "red_berry"), ["fruit"], 0.8, "de"),
    ("unknown_mushroom", "can_i_eat", ("type", "red_mushroom"), ["mushroom"], 0.2, "en"),
    ("unknown_stone", "what_can_i_do", ("type", "flint"), ["stone", "hard", "sharp"], 0.7, "de"),
    ("nothing_noticed", "what_is", ("type", "peat"), [], 0.7, "de"),
    ("unknown_water", "is_dangerous", ("type", "sea_water"), ["water"], 0.6, "en"),
    ("first_person", "what_is", ("inhabitant", "inh-000002"), [], 0.7, "de"),
]


def job(case: tuple, run: int) -> GenerationJob:
    cid, question, (kind, subject), notices, hunger, locale = case
    data = copy.deepcopy(BASE)
    data["request_id"] = str(uuid.uuid5(uuid.NAMESPACE_URL, f"b4a-question/{cid}/{run}"))
    data["seed"] = 14030 + run * 1000 + CASES.index(case)
    data["display_locale"] = locale
    payload = data["payload"]["question"]
    payload["question"] = question
    payload["subject"] = {"kind": kind, "id": subject}
    payload["perception"] = notices
    payload["inhabitant"]["knowledge"] = []
    payload["inhabitant"]["memory"] = {"summary": "", "recent": []}
    for need in payload["inhabitant"]["needs"]:
        if need["id"] == "hunger":
            need["value"] = hunger
    return GenerationJob.model_validate(data)


async def main() -> int:
    settings = load_settings()
    OUT.mkdir(parents=True, exist_ok=True)
    rows = []
    async with httpx.AsyncClient() as http:
        client = OllamaClient(
            str(settings.ollama_url), settings.decider_model, http, settings.keep_alive
        )
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
                call_timeout_s=120,
            ),
            log=CallLog(OUT / "questions.jsonl"),
        )
        for run in range(RUNS):
            for case in CASES:
                started = time.perf_counter()
                row = {
                    "case": case[0],
                    "run": run,
                    "person": case[2][0] == "inhabitant",
                    "notices": case[3],
                }
                try:
                    outcome = await ask(job(case, run))
                    result = outcome.result["question"]  # type: ignore[index]
                    row |= {"ok": True, "attempts": outcome.attempts, **result}  # type: ignore[arg-type]
                except JobFailedError as error:
                    row |= {"ok": False, "error": f"{error.code}: {error}"}
                row["ms"] = int((time.perf_counter() - started) * 1000)
                rows.append(row)
                print(json.dumps(row, ensure_ascii=False), flush=True)
        await client.unload(timeout=30)
    (OUT / "questions.json").write_text(
        json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    ok = [r for r in rows if r["ok"]]
    guesses = [(r["case"], g) for r in ok for g in r.get("guesses", [])]
    print(
        f"Q_RESULT valid {len(ok)}/{len(rows)}, retried {sum(r['attempts'] > 1 for r in ok)}, "
        f"not understood {sum(not r['understood'] for r in ok)}, "
        f"person with thing guesses {sum(1 for r in ok if r['person'] and r.get('guesses'))}, "
        f"guesses {len(guesses)}, of them sure >= 0.8: {sum(g['confidence'] >= 0.8 for _, g in guesses)}"
    )
    counted = collections.Counter((c, g["property"], g["value"]) for c, g in guesses)
    for (case, prop, value), n in sorted(counted.items()):
        print(f"  {case}: {prop}={value} x{n}")
    return 0


raise SystemExit(asyncio.run(main()))
