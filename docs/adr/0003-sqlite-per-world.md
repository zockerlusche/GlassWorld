# ADR-0003: One SQLite file per world

- Status: accepted (effective with the merge of the PR that adds it)
- Date: 2026-11-07
- Deciders: TL, GW1

## Context

A world holds inhabitants, knowledge, memories, relationships, feelings,
objects, inventions, mailbox and chronicle. The game saves on exit (B2469),
autosaves every night and must load old saves.

## Decision

- Each world is **one SQLite database file**; it is the save game.
- Access from C# via `Microsoft.Data.Sqlite` (MIT) in `Simulation.Data`.
  The native library must ship in Godot exports (spike R11 verifies this).
- The file carries a **schema version**; every format change comes with a
  migration and a test. Old saves are migrated on load.
- **Crash-safe saving:** write to a copy, then swap atomically.
- Tables are keyed by entity ids (`inh-…`, `obj-…`); old memories are
  periodically condensed into summaries.
- The simulation state hash (ADR-0002) is identical before saving and after
  loading.

## Consequences

- Saves can be inspected with standard SQLite tools.
- One writer per world; the AI service never writes to the database.
- Risk: native SQLite library in Godot exports – mitigated by spike R11
  before B1.
