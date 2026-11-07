# ADR-0008: UI and presentation logic in a Godot-free C# library

- Status: accepted (effective with the merge of the PR that adds it)
- Date: 2026-11-07
- Deciders: TL, GW1, GW3

## Context

The concept (D333, "Technik") names GDScript for the UI, and the engineering
handbook (D334) allows GDScript "only for thin UI and scene logic". B6 and B8
bring a lot of presentation logic: view models for the inhabitant card,
lists, mailbox, open questions, favour, chronicle and statistics, text and
number formatting, sound resolution from data tables, and the commands the
player sends to the core. This logic needs fast tests without a renderer,
uses the generated contract types (ADR-0007, `src/Contracts`) and must pass
the CI text check (KR4).

GDScript would need its own lint and format chain (gdlint, gdformat), its own
test runner inside Godot and a second text scanner, and it cannot use the
generated C# contract types.

## Decision

- Presentation logic lives in **`src/Game.Presentation`**, a plain .NET class
  library **without a Godot reference** (same rule as ADR-0002). It contains
  view models, formatting, sound/chronicle resolution and the player commands,
  and talks to the core only through the core's read port, event stream and
  command port.
- **`game/`** keeps only thin C# node scripts and scenes: they bind controls
  to the view models, forward input and play audio. No decisions, no data
  lookups and no texts in node scripts.
- Tests for `Game.Presentation` are xUnit tests without Godot; scenes are
  covered by the headless smoke run and scene tests.
- GDScript is not used. Should a case ever need it, it needs a new ADR.
- The CI text check scans `game/` and `src/Game.Presentation`.

## Consequences

- One language next to the core, one analyzer and format setup, one text
  check; contract types are used directly.
- The deviation from D333 ("GDScript for UI") is intended; the TL updates the
  concept's technology line.
- Godot-specific types (Color, Vector2, Node) stay in `game/`; the view models
  expose plain values that the node scripts convert.
