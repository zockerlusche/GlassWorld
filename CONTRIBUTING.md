# Contributing to GlassWorld

Thanks for your interest! GlassWorld is an experiment; please keep changes small and focused.

## Workflow

1. Create a short-lived branch from `main` (`feat/<topic>`, `fix/<topic>` or `docs/<topic>`);
   `main` must always build and run.
2. Commit in small steps using [Conventional Commits](https://www.conventionalcommits.org/) in English
   (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`, `ci:`).
3. Open a small, focused pull request with a description, a reference to the task and proof of
   testing. Every change needs a review by a second person; every merge into `main` goes through a
   pull request with green CI and is **squash-merged** after approval (no merge commits).
4. Add a line to the `[Unreleased]` section of [CHANGELOG.md](CHANGELOG.md) for every user- or
   developer-visible change.

## Definition of Done

- Acceptance criteria met and demonstrated.
- Tests written, CI green, headless smoke test passes.
- "Frame, not outcome": the change does not hard-code a result.
- License list (`LICENSES.md`), docs and, if needed, an ADR are up to date.

## Checks before a pull request

CI runs the same checks; running them locally saves a round trip.

```sh
dotnet build && dotnet test && dotnet format --verify-no-changes
uv run ruff check . && uv run ruff format --check .
uv run pyright
uv run pytest
uv run python tools/licenses/check_licenses.py
```

After changing a schema in `contracts/schemas/`, regenerate both sides and commit the result:

```sh
uv run python contracts/generate_python.py
dotnet run --project tools/ContractGen
```

Every message schema needs valid and invalid examples in `contracts/examples/`. After an intended
prompt change, regenerate the prompt snapshots with
`GW_UPDATE_SNAPSHOTS=1 uv run pytest ai-service/tests/test_prompts.py` and review the diff like code.

## Rules

- **Data, not code:** values belong in data files under `content/`, validated against a schema
  (`contracts/content-map.json` says which). Placeholders are fine; balancing is decided by the
  project owner.
- **Texts:** player-facing text is referenced by language-neutral keys
  (`vocab.*`, `ui.*`, `msg.*`, `time.*`); German and English live in `content/i18n/`.
- **Deterministic core:** `src/Simulation.Core` has no Godot dependency, uses a fixed tick and seeded
  random numbers; a state hash guards replays. The AI only proposes; the core checks every action.
- **Licences:** allowed without further approval are Apache-2.0, MIT, CC0, BSD-2/3-Clause, PSF-2.0,
  ISC and MPL-2.0 (MPL files unmodified). Anything else (also tools that are never shipped) needs the
  project owner's approval, written into its row as `approved F<number>`. Add every new dependency,
  asset and model to `LICENSES.md`; `tools/licenses/check_licenses.py` fails the build otherwise.
- **Local only:** no online services or cloud AI at runtime. Downloads at first start only from
  official sources, with a pinned checksum or digest, after the player's consent.
- **No secrets** (tokens, keys, personal paths) in the repository, logs or issues.
- **Ages 12+:** no depiction of violence or sexuality; death only rarely and gently.

## Code style

- C#: nullable enabled, warnings as errors, `dotnet format` clean.
- Python: `ruff` and `pyright` (strict) clean, tests with `pytest`.
- Text files use LF line endings (enforced by `.gitattributes`); binary assets go through Git LFS.
