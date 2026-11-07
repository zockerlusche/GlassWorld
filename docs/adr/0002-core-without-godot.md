# ADR-0002: Simulation core without Godot dependency

- Status: accepted (effective with the merge of the PR that adds it)
- Date: 2026-11-07
- Deciders: TL, GW1

## Context

The simulation (needs, mood, will to live, knowledge, world truth, world time,
utility AI) must be testable fast and headless, replayable from seed and log,
and must run 1,000 game days without a renderer.

## Decision

- `src/Simulation.Core` is a plain .NET class library with **no reference to
  Godot**. `src/Simulation.Data` adds SQLite and data file loading.
- Dependency direction: `game/` depends on the core, never the other way.
- The core talks to the AI service only through a port interface; tests use a
  mock, replays use a log-backed implementation.
- The core runs on a **fixed tick**, independent of frame rate. Godot only
  interpolates the presentation.
- **Determinism:** all randomness comes from seeded RNG streams derived from
  the world seed; no wall-clock time, no `Random.Shared`, no unordered
  iteration over hash sets in decisions. Lists sent to the AI are sorted.
- A **state hash** over the canonical world state is the check value for
  determinism and save/load tests.
- AI answers take effect at a defined tick (request tick + budget in ticks);
  the outcome (answer, error or fallback) is logged with `request_id` and that
  tick, so a replay does not depend on machine speed.

## Consequences

- `dotnet test` covers the whole simulation without starting Godot.
- Godot-specific features (navigation, physics queries) reach the core only
  through interfaces with deterministic test doubles.
- Path finding via Godot's NavigationServer must be wrapped so that replays
  do not depend on it, or replaced by a core-side grid search.
