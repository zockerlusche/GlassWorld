# AI service

Local AI service of GlassWorld: FastAPI on `127.0.0.1`, started and stopped by the game
(ADR-0006). It talks to Ollama and ComfyUI, which it detects but never starts. Messages follow
the JSON Schemas in `../contracts/`.

## Run

```sh
# from the repository root
export GW_AI_TOKEN=<random per-session token>      # set by the game in production
uv run python -m glassworld_ai --port 0
# -> prints: AI_SERVICE_READY port=<n>
curl http://127.0.0.1:<n>/health
```

- Binds `127.0.0.1` only. `--port 0` lets the OS choose and announces the port on stdout.
- Every endpoint except `GET /health` needs header `X-GW-Token` with the session token; requests
  with another `Host` than `127.0.0.1:<port>` are rejected. Without `GW_AI_TOKEN` all protected
  endpoints are closed.
- Every non-2xx answer is a `ServiceError` (`contracts/schemas/service-error.schema.json`).

## Configuration

`config.toml` holds the defaults (Ollama/ComfyUI endpoints, decider model pinned by digest,
probe timeout). Override with environment variables `GW_AI_<NAME>` or a local `.env`
(see `.env.example`). There are no endpoint or model defaults in code.

## Health

`GET /health` returns `HealthStatus`: `ready` when Ollama is up and the configured model with the
configured digest is installed, otherwise `degraded`. `starting` is reserved for model warm-up
(B3). The answer also lists Ollama/ComfyUI versions and whether the model is loaded in VRAM.

## Hardware tier

At start the service detects GPUs (`nvidia-smi`, optional), CPU name, logical CPUs and RAM using the
standard library only. The tier comes from `content/hardware/tiers.json` (thresholds are data:
`minimum` 8 GiB VRAM, `standard` 10 GiB, `comfort` 24 GiB or two GPUs, per the concept). The
highest tier whose requirements are met is reported in `/health` under `hardware.tier`; below
`minimum` it is `null`. Without `nvidia-smi` (e.g. AMD GPU or CI) the GPU list is empty.

## Tests

```sh
cd ai-service && uv run ruff check && uv run pyright && uv run pytest
```

Tests need no GPU and no network: health uses fake probes, the end-to-end start test points
Ollama/ComfyUI to a closed port.

## Not yet included (follow-ups)

Lifecycle details from ADR-0006 follow with the decider work: `--parent-pid` watch, lock file,
shutdown endpoint with model unload (`keep_alive=0`), bounded queue, JSONL model-call log.
