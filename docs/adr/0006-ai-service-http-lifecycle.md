# ADR-0006: AI service over HTTP on localhost, started and stopped by the game

- Status: accepted (effective with the merge of the PR that adds it)
- Date: 2026-11-07
- Deciders: TL, GW1, GW2

## Context

The AI side (queue, Ollama, ComfyUI, sound model, hardware detection) is
written in Python; the game and core in C#. Everything runs locally (B2459).
The reference GPU has 10 GB VRAM, so a leftover service holding a model is a
real problem.

## Decision

- **Process:** the AI service is a separate Python process (FastAPI) speaking
  HTTP with JSON bodies as defined in `contracts/`.
- **Port:** the service binds 127.0.0.1 on an OS-assigned port (`--port 0`)
  and announces it on stdout as a single line `AI_SERVICE_READY port=<n>`; the
  game reads it before polling `/health`. No fixed port.
- **Auth:** the game passes a random per-session token via environment
  variable `GW_AI_TOKEN` (not on the command line); every endpoint except
  `GET /health` requires header `X-GW-Token`. Requests with a Host other than
  `127.0.0.1:<port>` are rejected.
- **Lifecycle:** the game starts the service at game start and stops it on
  exit (shutdown endpoint, then process kill after a timeout). No orphans:
  1. on Windows the game assigns the service to a Job Object with
     `KILL_ON_JOB_CLOSE`, so the service dies with the game;
  2. the service gets `--parent-pid` and exits when the parent is gone;
  3. a lock file in the user data folder (pid, port, start time) detects
     leftovers; a leftover is terminated before a new service starts.
  On shutdown the service unloads the model (Ollama `keep_alive=0`) to free VRAM.
- **External programs:** Ollama and ComfyUI are external processes; the
  service detects them. `/health` reports them separately.
  *Amendment (B10 R3, F5266 = A, 08.11.2026):* Ollama is not shipped. If no
  usable Ollama runs or is installed, the game offers the download in its
  first-start dialog; after the player agreed, the service loads the official
  release pinned in `content/ai/ollama-runtime.json` (SHA-256 checked) into the
  user data folder and starts `ollama serve` bound to the configured localhost
  address. It stops only an Ollama it started itself, never the player's own.
  ComfyUI is still never started by the service (B2514).
- **Health:** `GET /health` reports `starting | ready | degraded` (model missing
  or loading), each external dependency and the `contract_version`. The game
  refuses a service with a different contract version. Schema `HealthStatus`
  lives in `contracts/`.
- **Start-up:** loading the model takes 10–30 s; the game does not block and
  starts in routine mode (utility AI).
- **Queue:** the service keeps one bounded FIFO queue (one model call at a
  time, GPU discipline). When full it answers `ServiceError` `overloaded`;
  requests whose `deadline_ms` has expired before the model call are answered
  with `timeout` without calling the model. The core never queues
  indefinitely: requests past their tick budget are dropped and logged
  (ADR-0002).
- **No substitute decisions:** on any problem the service returns
  `ServiceError`; the core decides (fallback to utility AI).
- **Long jobs** (image, sound) are asynchronous: job id plus status query.
- **Logging:** the service writes every model call (prompt version, raw
  answer, attempts, latency) as JSONL. The authoritative replay log is kept by
  the core.
- CI runs the service with a mock model; no GPU in CI.

## Consequences

- Clear process boundary; the service can be tested and restarted alone.
- Other local processes or web pages cannot drive the service without the
  session token.
- Determinism is handled by the core (ADR-0002), not by the transport.
- Packaging must ship an embedded Python runtime (concept: Windows first).
