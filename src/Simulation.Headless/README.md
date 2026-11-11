# Simulation.Headless

Runs a world without Godot: acceptance runs, long runs, seed series (`worldgen`), the D9 Durchstich
with the live AI, and replays of save games.

```
dotnet run --project src/Simulation.Headless -c Release -- [run options]
dotnet run --project src/Simulation.Headless -c Release -- replay --save FILE [--content DIR]
dotnet run --project src/Simulation.Headless -c Release -- worldgen --seed N | --seeds FROM-TO [--preset P] [--json] [--png FILE]
```

## Run options

| Option | Meaning |
|---|---|
| `--seed N` | World seed (default 1). |
| `--days N` | Game days to run (default 30). |
| `--content DIR` | Content folder (default: the nearest `content/`). |
| `--preset P` | Island from seed and preset (B2-2) instead of the placeholder world. |
| `--save FILE` | Autosave after every game day (and at the end of a partial last day); checks that loading gives the same hash. |
| `--quiet` | No day lines. |
| `--trace ID`, `--trace-days N` | Event trace of one inhabitant for the first N days. |
| `--ai live\|mock` | Decider (default `mock`). See below. |
| `--tick-ms N` | Real milliseconds per tick (default 0). See pacing. |
| `--until KIND[:COUNT]` | Stop after the tick in which the COUNT-th (default first) core event of KIND happened. A tick is never cut short, so the line `UNTIL kind=.. count=n/COUNT ..` may show more than COUNT. |
| `--events KIND,...` or `all` | One line per core event of those kinds, plus one line per new chronicle entry. |
| `--log-json FILE` | Decision log and job log as JSON lines, written as the entries appear. |

Kinds are the ids of `content/vocabulary/events.json`; unknown kinds are refused.

### Output lines

The existing lines (`day ..`, `done ..`, `neglect ..`, `time ..`, `save ..`, `ACCEPTANCE_OK`/`ACCEPTANCE_FAIL`)
are unchanged. On an island the line `explored  land <shown>/<all> cells (<p> %)  view <shown>/<all> cells` follows
`neglect ..`: how much of the land (cells without water) and of the whole grid the player's view shows (B2-6 R14). New:

```
EVENT tick=<t> kind=<kind> inhabitant=<id> action=<a> target=<id> detail=<d>
CHRONICLE tick=<t> kind=<chronicle kind> id=<entry number>
UNTIL kind=<kind> count=<seen>/<wanted> reached|not_reached tick=<t>
FINAL hash=<state hash> tick=<t>
```

Values use the invariant culture; a missing value is `-`; `%`, whitespace and control characters inside a
value are percent-encoded (a space is `%20`), so every line splits on single spaces. `FINAL` is always the
last line once the world exists (also after Ctrl+C or an exception).

JSON lines (`--log-json`): `log` (decision|job), `kind` (request, response, error, discarded, command,
job_request, job_result, job_error, job_discarded), `request_id`, `tick`, `note`, `request` (the request
JSON) or `response` (the received JSON, as the core logged it), and for outcomes `meta`: `request_tick`,
`wait_ticks`, `request_seed`, and `source`, `model`, `prompt_version`, `seed`, `latency_ms`, `attempts`
from the answer's `meta` (decisions) or `provenance` (jobs).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Done (condition reached or all days run) and acceptance OK. |
| 1 | Bad arguments. |
| 2 | Acceptance failed (before this change: 1). |
| 3 | `--until` not reached within `--days` (acceptance OK). |
| 4 | `--ai live`: the AI service could not be started, or a game's AI service is running (`AI_SERVICE_UNAVAILABLE ..`). |
| 5 | `replay`: `REPLAY_DIFF`. |
| 6 | `replay`: `REPLAY_CONTENT_CHANGED` (nothing replayed). |
| 130 | Stopped with Ctrl+C. |

## Live AI (`--ai live`)

The runner starts the local AI service exactly like the game does with `GLASSWORLD_AI=live`
(`LocalAiService` in `src/AiClient`, shared with `WorldPresenter`): `uv run python -m glassworld_ai` in the
repository root (`GLASSWORLD_AI_ROOT`, else the parent of the content folder), port from the ready line,
a session token per start, a Job Object and `--parent-pid` against orphans. It uses the same ports as the
game: `HttpDecisionPort` (questions) and `HttpJobPort` (generation jobs; with the mock the job port is
missing and inventions stay dormant, as in the game). Lock and logs of the service go to
`GLASSWORLD_HEADLESS_DATA` or `%LOCALAPPDATA%/GlassWorld/headless/ai-service`, apart from the game's, so a
headless run never ends a running game's service as a leftover. At the end, after Ctrl+C or an exception
the service is stopped (`POST /shutdown`, then kill after 20 s).

**Live runs go through the AMW GPU queue** (`amw lauf ausfuehren --gpu ...`), never next to other GPU work.
Before starting, the runner reads the game's lock file (`%APPDATA%/Godot/app_userdata/GlassWorld/ai-service/lock/ai-service.lock`,
on Linux under `~/.local/share/godot/app_userdata/GlassWorld/`). If it names a running service (pid alive,
on Windows also the same process start time), the run refuses with exit 4 and
`AI_SERVICE_UNAVAILABLE a game's AI service is running; close the game first (pid .., lock ..)`: two services
would share one Ollama and one GPU without knowing about each other (VRAM budget, B7/D564).

### Pacing: what the game does, and what the runner does

The game never waits for the AI (B2487): every frame it advances the world by real time × speed
(normal: one game day in 1200 s, i.e. 833 ms per tick; fast forward x4/x16), and the ports hand over the
answers that have arrived when the core calls `Receive(tick)`. An answer therefore takes effect after as
many ticks as passed in real time while the model was thinking.

The runner keeps this: the core runs on and answers take effect at the tick they are received. Only the
real time per tick is chosen differently:

- `--tick-ms N` (N > 0): every tick takes at least N ms (833 = the game at normal speed, 52 = at x16).
- `--tick-ms 0` (default): as fast as possible while no question or job is out with the AI; while one is
  out (live only), ticks pass at the game's fastest speed (normal speed × the largest fast-forward factor in
  `content/world/time.json`, x16 = 52 ms per tick). So a run never races through days while answers are on
  their way, and a live game day takes at most about 75 s of real time. With the mock nothing is
  ever out, so mock runs are exactly as fast and as deterministic as before.

A live run is not reproducible from the seed alone; record it with `--save` and replay it.

## Replay

`replay --save FILE [--content DIR] [--force]` rebuilds the world from the save's seed (and preset), plays the saved decision and
job logs back with `ReplayDecisionPort` / `ReplayJobPort` (answers, job outcomes and player commands at the
ticks they took effect), runs to the saved tick and compares the state hash with the saved world's hash:

- `REPLAY_OK hash=<hash> ticks=<n> content=<fingerprint>`, exit 0;
- `REPLAY_DIFF tick=<t> content=<fingerprint>`, exit 5: the replay's own log departed from the saved log at
  tick t (first entry that differs, is missing or is extra); `tick=end` if the logs agree and only the final
  hash differs;
- `REPLAY_CONTENT_CHANGED saved=<x> now=<y>`, exit 6, nothing replayed: the save was made with other content
  data. `--force` replays anyway; the result line then ends with `saved_content=<x>`.

Content fingerprint: saves from the headless runner and the game carry `content_fingerprint` in their meta
(`ContentFingerprint` in `Simulation.Data`: SHA-256 over all files of `content/`, sorted by relative path,
path + length + raw bytes; `sha256:` + 16 hex digits). It is not world state and not in the hash. Saves
without it (older ones) replay as before, with the current fingerprint printed.

Jobs are replayed only if the saved run had a job port (non-empty invention state).

## World generator (`worldgen`)

`worldgen --seed N [--preset P] [--content DIR] [--json] [--png FILE] [--png-flow FILE]` generates the start
island of a seed and prints its world hash and key figures: attempts, time, biome shares, every line of the
mandatory check (`ok` / `FAIL`; start supply lines that only report read `ok (report only)` / `short (report only)`),
objects, hydrology, relief, rock faces, start ground and underground. `--json` prints the same as one JSON
line. Exit code 0: the island passed its mandatory check; 2: it did not (after all attempts of the preset;
the `FAIL` lines name what was missing).

`--seeds FROM-TO` generates every seed of the range one after the other in one process (the content is
loaded once), one block or - with `--json` - one line per seed; exit code 2 if any island failed. The first
island of a process carries the start-up of the runtime in its time. `--png` and `--png-flow` draw one
island and need `--seed`.

The hydrology figure `stream dead ends` (`hydrology.stream_dead_ends`) counts the stream cells whose water
reaches neither the sea nor a lake when followed downstream (0 on a sound island; B2 acceptance).

The seed series of the acceptance is `tools/worldgen/seed_series.py`: it runs a range of seeds through
`worldgen --seeds FROM-TO --json` and writes the figures and a short German report with a verdict per
acceptance point (mandatory check and each of its lines, distinct hashes, biomes, cliffs and caves, streams,
time against `generation_budget_ms`). Build once, then run; the 1,000 seeds belong in the AMW run queue:

```
dotnet build src/Simulation.Headless -c Release -m:1
uv run python tools/worldgen/seed_series.py --out ../series --seeds 1-1000 --note "main <sha>"
```

## Tests and CI

`tests/Simulation.Headless.Tests` covers the option parser, `--until`, event lines, the JSON log, replay
(OK, a tampered answer → `REPLAY_DIFF`, changed content → exit 6 and `--force`), exit code 4 (also with a
fake game lock), Ctrl+C and the live path with a fake connection
that answers in real time on another thread; and `worldgen`: the report of an island without flint (exit 2, one
`FAIL` line), the same hash in two processes, seed ranges and the dead-end count. CI never starts the real AI service (no GPU); the live path
is checked by hand, only through the AMW GPU queue:

```
dotnet src/Simulation.Headless/bin/Release/net10.0/GlassWorld.Simulation.Headless.dll --ai live --preset start_island --seed 1 --days 1 --events thought,invented,built,first_use
```
