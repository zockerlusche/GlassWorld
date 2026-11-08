# GlassWorld

GlassWorld is an experimental, open AI life simulation built with Godot (.NET).
Two inhabitants start without prior knowledge and learn on their own, using
local open-source AI only, until they grow a family and a culture.

> **Status: early development.** Two inhabitants live on a start island, decide with a local
> language model, learn, remember, invent and build. There is no release yet; the first public
> build is planned for the end of November 2026.

## Principles

- **Frame, not outcome** – the code sets rules (physics, logic), never results.
- **Data-driven** – values live in data files under `content/`, not in code.
- **Everything local** – no online services or cloud AI at runtime.
- **The world knows the truth, the AI guesses** – every AI proposal is checked against the world's rules.
- **Calm world** – no stress, no score, no game over. Suitable for ages 12+.
- **Reproducible** – every run can be replayed from seed and log.
- **Bilingual** – every visible text exists in German and English.

## Hardware tiers

The AI service detects the graphics card(s) at start and picks a tier. The thresholds are data
(`content/hardware/tiers.json`); they sit about 5 % below the nominal size because cards report
slightly less memory.

| Tier | Graphics memory (VRAM) | What runs |
|---|---|---|
| **minimum** | 8 GB | The language model (inhabitants think, talk, ask, invent). Buildings use the construction kit; no image or sound AI. |
| **standard** | 10–12 GB | The language model by day; image and sound AI in the "computing night" while the inhabitants sleep. |
| **comfort** | 24 GB, or two cards with at least 8 GB each | All AI models in parallel. |

Below the minimum the game still runs in routine mode: the inhabitants fall back to simple
rule-based decisions without the language model.

Further requirements: Windows 10/11 (64-bit), an NVIDIA graphics card (the reference PC has
10 GB VRAM and 32 GB RAM), and about 12 GB of free disk space for tier minimum: Ollama takes
about 2 GB, the language model about 6.6 GB, and the Ollama archive (1.6 GB) is removed again
after unpacking. Higher tiers need more space for their image and sound models.

## Installation (Windows)

1. Download the release archive from the GitHub releases page and unpack it into a folder of
   your choice. Nothing is installed system-wide.
2. Start `GlassWorld.exe`.
3. **First start:** the game shows what it still needs, with name, size, licence and purpose of
   each part, and asks for your consent before anything is downloaded:
   - **Ollama** (MIT), the local runtime for the language model. If a suitable Ollama is already
     installed on your PC, it is used instead (and only started with your consent).
   - **The language model** `qwen3.5:9b` (Apache-2.0, about 6.6 GB) through Ollama.

   Downloads come only from the official sources and are checked against a pinned checksum or
   digest. You can cancel at any time; the next start continues where it stopped.
4. **Image and sound AI (tiers standard and comfort)** use [ComfyUI](https://github.com/Comfy-Org/ComfyUI),
   which you install yourself; it is not part of the game. Without it the game runs with the
   language model only.

## Privacy

**Nothing leaves your computer.** In detail:

- All AI runs locally. The game never contacts a cloud AI or any online service while you play.
- The only network access is the first-start download of Ollama and the AI models described
  above, after your consent, from their official sources. There are no accounts, no telemetry,
  no crash reports and no advertising.
- The AI service listens only on `127.0.0.1` (your own computer) and accepts requests only with
  a per-session token from the game.
- Worlds, logs and downloaded files stay in your user data folder
  (`%APPDATA%\Godot\app_userdata\GlassWorld`). Delete that folder to remove everything.
- Sharing worlds (planned): an exported world will contain only the world itself, no paths
  and no tokens.

## Repository layout

| Path | Content |
|---|---|
| `src/` | .NET libraries, e.g. `Simulation.Core` (deterministic, no Godot dependency) |
| `tests/` | .NET test projects |
| `game/` | Godot 4.7 .NET project (presentation, input, UI) |
| `ai-service/` | Local AI service (Python, FastAPI) |
| `contracts/` | JSON Schemas and shared vocabulary between game and AI service |
| `content/` | Data files (objects, properties, texts DE/EN, hardware tiers, models) |
| `prompts/` | Prompt templates for the local language model |
| `tools/` | Developer scripts (contract generators, licence check, packaging) |
| `docs/adr/` | Architecture Decision Records |

## Development setup

Requirements (exact versions in [`docs/adr/0001-versions.md`](docs/adr/0001-versions.md)):

- Godot 4.7.2 (.NET build) and the .NET SDK pinned in `global.json`
- Python 3.12 with [uv](https://docs.astral.sh/uv/)
- [Ollama](https://ollama.com/) with the language model from `ai-service/config.toml`
- Optional: ComfyUI for image jobs

```sh
# .NET: build, test, format
dotnet build
dotnet test
dotnet format --verify-no-changes

# Python: AI service and contracts
uv sync
uv run ruff check . && uv run ruff format --check .
uv run pyright
uv run pytest

# Licence check (dependencies, assets, models against LICENSES.md)
uv run python tools/licenses/check_licenses.py
```

Running the game from the editor or with `GlassWorld.exe -- --seed=1` starts the AI service
when a world is created or loaded. See [CONTRIBUTING.md](CONTRIBUTING.md) for the workflow.

## License

MIT – see [LICENSE](LICENSE). Third-party components, models and assets are listed with their
licences in [LICENSES.md](LICENSES.md); release builds ship all licence notices.
