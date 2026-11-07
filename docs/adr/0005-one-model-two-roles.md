# ADR-0005: One language model for decider and rule guard; core has the final say

- Status: proposed (two-stage check confirmed by TL, AMW #18263)
- Date: 2026-11-07
- Deciders: TL, GW1, GW2

## Context

The concept defines three AI roles: decider (what an inhabitant wants),
rule guard (checks a wish against style, tech level and game mechanics, sets
build plan and material) and builder. VRAM on the reference PC (RTX 3080,
10 GB) allows one language model at a time. B2462 requires every interaction
with the world to be checked for logic.

## Decision

- Decider and rule guard use the **same local model** (`qwen3.5:9b` via
  Ollama, ADR-0001) with **separate, versioned prompts** in `prompts/`.
- The check is **two-staged**:
  1. **AI rule guard** (AI service, GW2): judges plausibility, style and tech
     level and proposes a `GuardVerdict` / build plan. It is a proposal.
  2. **Core rule check** (`Simulation.Core`, GW1): deterministic final check
     against the world truth – physics, vocabulary, resources, reachability,
     content protection. Only the core changes the world.
- The AI rule guard runs only for open-ended proposals (inventions, build
  plans, novel interactions). Choices among core-offered options go straight
  to the core rule check.
- The thinking mode of the model is off for both roles (latency); this is set
  in the prompt configuration.
- Every AI output is validated against its schema; invalid output is retried
  at most twice, then rejected and logged.

## Consequences

- No extra VRAM for the guard; model swaps are avoided.
- Routine decisions cost one model call, not two.
- "The world knows the truth, the AI guesses": an AI answer can never change
  the world without passing the core.
- Prompt changes are versioned and covered by AI test sets.
