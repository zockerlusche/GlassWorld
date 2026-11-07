# Contributing to GlassWorld

Thanks for your interest! GlassWorld is an experiment; please keep changes small and focused.

## Workflow

1. Create a short-lived branch from `main` (`feat/<topic>` or `fix/<topic>`); `main` must always build and run.
2. Commit in small steps using [Conventional Commits](https://www.conventionalcommits.org/) in English
   (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`, `ci:`).
3. Open a small, focused pull request with a description, a reference to the task and proof of testing.
   Every merge into `main` goes through a pull request with green CI and is squash-merged after approval.

## Definition of Done

- Acceptance criteria met and demonstrated.
- Tests written, CI green, headless smoke test passes.
- "Frame, not outcome": the change does not hard-code a result.
- License list (`LICENSES.md`), docs and, if needed, an ADR are up to date.

## Rules

- Values belong in data files under `content/`, not in code.
- Player-facing text is referenced by language-neutral keys; German and English texts live in separate files.
- Only assets and models under Apache 2.0, MIT or CC0 (others need explicit approval). Add every new dependency or asset to `LICENSES.md`.
- No online services or cloud AI at runtime.
- Content must be suitable for ages 12+: no depiction of violence or sexuality.

## Code style

- C#: nullable enabled, warnings as errors, `dotnet format` clean.
- Python: `ruff` and `pyright` clean, tests with `pytest`.
- Text files use LF line endings (enforced by `.gitattributes`); binary assets go through Git LFS.
