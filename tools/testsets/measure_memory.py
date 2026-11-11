"""B4a A3 measurement (A1403): memory condensing with the live model keeps the text budget and the
formative events. Run inside an AMW GPU run: uv run python tools/testsets/measure_memory.py <repo> <out>. Prints one line per call and a summary; <out> gets a3.json and the call log."""

import asyncio
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
from glassworld_ai.memory.condense import OllamaMemory
from glassworld_ai.prompts import load_prompt
from glassworld_ai.queue import ModelQueue
from glassworld_contracts import GenerationJob

REPO = Path(sys.argv[1])
OUT = Path(sys.argv[2])
BASE = json.loads(
    (REPO / "contracts/examples/valid/generation-job.memory-condense.json").read_text("utf-8")
)
KINDS = ["drank", "ate", "slept", "met_inhabitant", "felt_pain", "inspected", "walked"]
TYPES = ["spring_water", "blue_berry", "red_berry", "apple", "dry_grass", "fieldstone"]


def job(n: int, events: int, formative: int, max_chars: int) -> GenerationJob:
    data = copy.deepcopy(BASE)
    data["request_id"] = str(
        uuid.uuid5(uuid.NAMESPACE_URL, f"a3/{events}/{formative}/{max_chars}/{n}")
    )
    data["seed"] = 1403 + n * 1000 + events
    memory = data["payload"]["memory"]
    memory["max_chars"] = max_chars
    memory["events"] = [
        {
            "tick": 1000 + i * 10,
            "kind": KINDS[i % len(KINDS)],
            "about": {"kind": "type", "id": TYPES[i % len(TYPES)]},
            "formative": i % max(1, events // max(1, formative)) == 0
            and i // max(1, events // max(1, formative)) < formative,
        }
        for i in range(events)
    ]
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
        memory = OllamaMemory(
            client=client,
            queue=ModelQueue(settings.queue_size),
            template=load_prompt(
                settings.prompts_dir, settings.memory_prompt, settings.memory_prompt_version
            ),
            settings=ModelCallSettings(
                temperature=settings.temperature,
                num_ctx=settings.num_ctx,
                max_retries=settings.max_retries,
                call_timeout_s=120,
            ),
            log=CallLog(OUT / "memory.jsonl"),
        )
        for events, formative, max_chars in [
            (8, 2, 600),
            (32, 5, 600),
            (32, 5, 300),
            (32, 8, 1200),
        ]:
            for n in range(5):
                request = job(n, events, formative, max_chars)
                wanted = sorted(e.tick for e in request.payload.memory.events if e.formative)  # type: ignore[union-attr]
                started = time.perf_counter()
                try:
                    outcome = await memory(request)
                    result = outcome.result["memory"]  # type: ignore[index]
                    row = {
                        "case": f"{events} events/{formative} formative/{max_chars} chars",
                        "ok": True,
                        "chars": len(result["summary"]),
                        "within": len(result["summary"]) <= max_chars,  # type: ignore[index,arg-type]
                        "kept_all": sorted(result["kept"]) == wanted,
                        "attempts": outcome.attempts,  # type: ignore[index,arg-type]
                        "ms": int((time.perf_counter() - started) * 1000),
                        "summary": result["summary"],  # type: ignore[index]
                    }
                except JobFailedError as error:
                    row = {
                        "case": f"{events} events/{formative} formative/{max_chars} chars",
                        "ok": False,
                        "error": f"{error.code}: {error}",
                        "ms": int((time.perf_counter() - started) * 1000),
                    }
                rows.append(row)
                print(json.dumps({k: v for k, v in row.items() if k != "summary"}), flush=True)
        await client.unload(timeout=30)
    (OUT / "a3.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
    ok = [r for r in rows if r["ok"]]
    print(
        f"A3_RESULT ok {len(ok)}/{len(rows)}, within budget {sum(r['within'] for r in ok)}, "
        f"kept all {sum(r['kept_all'] for r in ok)}, retried {sum(r['attempts'] > 1 for r in ok)}"
    )
    return 0


raise SystemExit(asyncio.run(main()))
