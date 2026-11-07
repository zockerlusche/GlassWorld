# GlassWorld

GlassWorld is an experimental, open AI life simulation built with Godot (.NET).
Two inhabitants start without prior knowledge and learn on their own, using
local open-source AI only, until they grow a family and a culture.

> Status: **Phase 0 (foundation)** – no gameplay yet.

## Principles

- **Frame, not outcome** – the code sets rules (physics, logic), never results.
- **Data-driven** – values live in data files, not in code.
- **Everything local** – no online services or cloud AI at runtime.
- **The world knows the truth, the AI guesses** – every AI proposal is checked against the world's rules.
- **Calm world** – no stress, no score, no game over.
- **Reproducible** – every run can be replayed from seed and log.

## Repository layout

| Path | Content |
|---|---|
| `src/` | .NET libraries, e.g. `Simulation.Core` (no Godot dependency) |
| `tests/` | .NET test projects |
| `game/` | Godot 4.7 .NET project (presentation, input, UI) |
| `ai-service/` | Local AI service (Python, FastAPI) |
| `contracts/` | JSON Schemas and shared vocabulary between game and AI service |
| `content/` | Data files (objects, properties, texts DE/EN) |
| `prompts/` | Prompt templates for the local language model |
| `tools/` | Developer scripts |
| `docs/adr/` | Architecture Decision Records |

## Requirements

- Godot 4.7.2 (.NET build)
- .NET SDK matching Godot 4.7.2
- Python 3.12 with [uv](https://docs.astral.sh/uv/)
- [Ollama](https://ollama.com/) for the local language model

Exact versions are recorded in `docs/adr/0001-versions.md`.

## Build and test (.NET)

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes
```

## License

MIT – see [LICENSE](LICENSE). Third-party licenses are listed in [LICENSES.md](LICENSES.md).
