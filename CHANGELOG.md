# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- AI service skeleton (`ai-service/`): FastAPI on 127.0.0.1 with OS-assigned port and ready line, `GET /health` (HealthStatus) probing Ollama and ComfyUI, session token and Host check, ServiceError bodies, configuration from `config.toml` / `GW_AI_*`.
- Contracts: `HealthStatus` schema; ServiceError codes `forbidden` and `not_found`.
- Contracts v0: JSON Schemas for DecisionRequest, DecisionResponse, ServiceError and vocabulary files, examples, vocabulary placeholders in `content/vocabulary/`, generated pydantic models (`glassworld_contracts`) and contract tests.
- uv workspace at the repository root (Python 3.12, `uv.lock`).
- Repository foundation: license, readme, contributing guide, folder layout, Git attributes and ignore rules.
- Architecture Decision Records ADR-0001 to ADR-0007 (versions, core without Godot, SQLite per world, construction-kit builder, one model for two roles, AI service lifecycle, data files and localization).
- .NET solution `GlassWorld.slnx` with `Simulation.Core` (no Godot) and xUnit v3 tests; shared build settings (net10.0, nullable, warnings as errors, analyzers, code style in build), central package versions, `.editorconfig`, `global.json` and repo-local `nuget.config`.
- Godot 4.7.2 .NET project in `game/` (start scene with camera, sun, island and water placeholders) referencing `Simulation.Core`; headless smoke test `tools/godot-smoke.sh` (600 frames, fails on errors, missing marker, timeout or exit code).
- CI (GitHub Actions): `dotnet` (format, build, test), `python` (uv locked sync, ruff, pyright, generated-model check, pytest with AI mock) and `godot-smoke` (verified Godot 4.7.2 mono download, 600 headless frames); actions pinned to commit SHAs.
- `game/GlassWorld.Game.sln` so Godot can export the C# project; the game runs with the invariant culture (module initializer) and culture-sensitive APIs are build errors (CA1304/1305/1307/1309/1310/1311).
- C# contract types generated from `contracts/schemas` (`tools/ContractGen`, `--check` in CI) and runtime validation: `ContractSerializer` checks every message against the embedded schemas before deserializing and after serializing; `contracts/content-map.json` assigns every `content/` file to its schema.
- Simulation core B1-1: world time without an earth calendar from `content/world/time.json` (1 game day = 20 real minutes at normal speed, 7 days per season), fixed tick with time boundary events, PCG32 with named seed streams, SHA-256 state hash with a cross-platform golden test, banned non-deterministic APIs, headless runner `src/Simulation.Headless`.
