# CLAUDE.md – GlassWorld

Open AI life simulation (Godot 4.7.2 .NET, MIT). Two inhabitants learn on their own with local open-source AI.

## Source of truth

All project knowledge lives in the AMW database, not in this file:

- Mission (Leitbild): `python F:/amw/amw-cli.py leitbild zeigen`
- Decisions: `python F:/amw/amw-cli.py zeige B2456` … (B2456–B2473 and later)
- Rules: `python F:/amw/amw-cli.py regeln`
- Concept and engineering handbook: AMW document D324

Always pass `--von <agent>` to AMW commands.

## Key rules (summary, AMW wins on conflict)

- Frame, not outcome: no hard-coded behaviour, interactions or effects (B2460).
- Data-driven: values in `content/`, validated against schemas.
- Local only at runtime: no cloud AI, no online services (B2459).
- The simulation core (`src/Simulation.Core`) has no Godot dependency and is deterministic (fixed tick, seeded RNG, state hash).
- AI only proposes; the rule checker in the core validates every interaction (B2462).
- Texts via language-neutral keys, DE/EN (B2470).
- Ages 12+, calm world (B2466, B2472).
- No push without review and TL approval; every merge via pull request.

## Layout

`src/` .NET libraries · `tests/` .NET tests · `game/` Godot project · `ai-service/` Python FastAPI ·
`contracts/` JSON Schemas + vocabulary · `content/` data files · `prompts/` · `tools/` · `docs/adr/`.
