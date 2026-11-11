# Simulation.Headless

Runs a world without Godot: acceptance runs, long runs, seed series (`worldgen`), the D9 Durchstich
with the live AI, and replays of save games.

```
dotnet run --project src/Simulation.Headless -c Release -- [run options]
dotnet run --project src/Simulation.Headless -c Release -- replay --save FILE [--content DIR]
dotnet run --project src/Simulation.Headless -c Release -- worldgen --seed N [--preset P] [--json] [--png FILE]
dotnet run --project src/Simulation.Headless -c Release -- perf [--inhabitants N] [--days N] [--ai mock|silent] [--json]
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
| 7 | `perf`: `PERF_OVER_BUDGET` (mean or p95 of the step above `--budget-ms`). |
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

## Perf: how long the step takes

```
dotnet run --project src/Simulation.Headless -c Release -- perf [--inhabitants N|data] [--days N | --ticks N]
    [--ai mock|silent] [--seed N] [--preset P] [--warmup-ticks N] [--budget-ms X] [--json] [--content DIR]
```

Measures the budget of the engineering handbook (D334, "Performance und Beobachtbarkeit": *Simulationsschritt
unter 4 ms*) the way the B2 acceptance asks for it (item 3: *Der Simulationsschritt mit 100 Bewohnern bleibt
unter 4 ms*). The acceptance run is the default: `perf` alone is the start island of seed 1 with 100
inhabitants for one game day with the mock decider; add `--ai silent` for the second run. **Always `-c Release`**
(the report says `build=release`; a debug build prints a warning, its times mean nothing).

| Option | Meaning |
|---|---|
| `--inhabitants N` | How many inhabitants the world starts with (default 100); `data` = those `content/inhabitants/model.json` lists (the world the run command builds, same hash after the same ticks). |
| `--days N`, `--ticks N` | How long the world runs, warm-up included (default one game day; `--ticks` instead of whole days). |
| `--ai mock\|silent` | Decider, see below (default `mock`). |
| `--seed N`, `--preset P` | Island (default seed 1, `start_island`). |
| `--warmup-ticks N` | Ticks at the start that run but are not measured (default 120): the runtime compiles the hot code in them. |
| `--budget-ms X` | Budget check: `PERF_OK` and exit 0 when mean and p95 of the step are at most X, else `PERF_OVER_BUDGET` and exit 7. Without it the command only reports. |
| `--json` | One JSON object instead of the lines (same values, unrounded). |

**Where the 100 come from.** The world starts with the data's start inhabitants and repeats them as further
groups (`InhabitantsSystem.StartersFor`: pairs 3, 4, 5 ... after the data's pairs 1 and 2). They are start
inhabitants like the first four: traits and needs from the data ranges with one random stream each, an
appearance, a name, the start relationships of the knowledge data. A repeated group wakes up around the
start place of the group it repeats, on the nearest free dry cells of the same walkable area, every second
cell. Only `perf` starts a world this way; the game and the save games start with the data's inhabitants.

**What "the step" is.** One `World.Step()`: every world system of one tick and the listeners of the time
boundaries, without the time inside the decider port. The game advances the world by real time x speed (one
tick per 833 ms at normal speed, per 52 ms at x16), so a frame pays for one tick at most unless it has to catch
up; the 4 ms are that tick's share of the 16.7 ms of a frame. The port is left out because its work is not the
core's: with the mock it is the mock computing its answers inside `Send`/`Receive`, in the game it is the AI
service in its own process. `STEP_WITH_PORT` shows the step with it.

**The decider** sets how many questions travel, and building a request is the most expensive thing a tick
can do:

- `mock` answers every question at the next tick. With 100 inhabitants that is a flood of requests no local
  model produces (`REQUESTS per_step=`): the upper end.
- `silent` never answers. Every inhabitant soon has as many open questions as the data allows and asks
  nothing more: the lower end, the world without any question traffic.

The game lies between the two: its request rate is the answer rate of the model.

### The report

```
PERF preset=.. seed=.. inhabitants=.. decider=.. ticks=.. warmup_ticks=.. measured_ticks=.. build=release
STEP mean_ms=.. median_ms=.. p95_ms=.. p99_ms=.. max_ms=.. max_tick=..
STEP_WITH_PORT mean_ms=.. ...
CPU mean_ms=..
PORT name=decider_port mean_ms=.. max_ms=..
SYSTEM name=<system> mean_ms=.. share=.. max_ms=..
PART name=<system>/<part> mean_ms=.. share=.. max_ms=..
REQUESTS sent=.. per_step=.. answers=.. open_at_end=..
ALLOC kb_per_step=.. gc0=.. gc1=.. gc2=..
DOING on_the_way=.. acting=.. idle=..
PERF_OK|PERF_OVER_BUDGET budget_ms=.. mean_ms=.. p95_ms=..      (only with --budget-ms)
FINAL hash=<state hash> tick=<t>
```

- `STEP`: wall-clock milliseconds per step over the measured ticks; percentiles by nearest rank; `max_tick` is
  the world tick of the slowest step. Judge the budget by **mean and p95** (p95 as for the frame time, B2
  acceptance 5). The maximum is one step: on a desktop system it also holds whatever else the machine did
  in that moment.
- `CPU`: processor time of the whole process per measured step (port, garbage collector and JIT threads
  included). When the machine is busy it stays put while the wall-clock times grow, so compare both.
- `SYSTEM`: one line per world system in the order they run, then `boundaries` (the listeners of
  `World.BoundaryReached`); `share` is the percentage of the step's mean. `PART` lines are the parts a system
  names inside its own work (`system/(rest)` = the system outside them). No part overlaps another, so the
  lines add up to the step (the core tells a probe, `World.Probe`, when a part begins and ends; it reads no
  clock itself).
- `ALLOC`: managed memory allocated per step and the garbage collections during the measured ticks.
- `DOING`: inhabitants on their way, in an action and without one, mean per step (a sleeping village is cheap).
- `FINAL`: the state hash, the proof that two builds measured the same run.

### Reading it right, and a guard for CI

Wall-clock times depend on the machine and on what else runs on it. The budget is set for the reference PC
(D334); only a run there, alone on the machine, decides the acceptance. Elsewhere compare runs taken one
after the other, look at `CPU` next to `STEP`, and repeat a run that looks odd.

The test suite therefore judges no time: `PerfTests` checks that the measurement runs, reports every system,
adds up, is deterministic and that the budget check sets the exit code (with budgets no machine can miss or
meet). A guard against a regression is the command itself with a generous budget, as its own CI job (not a
unit test), e.g. three times the mark on the CI runner:

```
dotnet src/Simulation.Headless/bin/Release/net10.0/GlassWorld.Simulation.Headless.dll perf --ai silent --budget-ms 12
```

## Tests and CI

`tests/Simulation.Headless.Tests` covers the option parser, `--until`, event lines, the JSON log, replay
(OK, a tampered answer → `REPLAY_DIFF`, changed content → exit 6 and `--force`), exit code 4 (also with a
fake game lock), Ctrl+C and the live path with a fake connection
that answers in real time on another thread. CI never starts the real AI service (no GPU); the live path
is checked by hand, only through the AMW GPU queue:

```
dotnet src/Simulation.Headless/bin/Release/net10.0/GlassWorld.Simulation.Headless.dll --ai live --preset start_island --seed 1 --days 1 --events thought,invented,built,first_use
```
