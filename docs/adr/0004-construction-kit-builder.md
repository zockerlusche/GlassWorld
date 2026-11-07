# ADR-0004: Construction-kit builder as the standard builder

- Status: accepted (effective with the merge of the PR that adds it)
- Date: 2026-11-07
- Deciders: TL, GW1, GW2

## Context

Inhabitants invent objects that were not in the starting content. The builder
turns an approved invention into a usable game object. Image-to-3D models
(TRELLIS.2, 16–24 GB VRAM, Linux/WSL only) do not run on the reference
hardware tier "Standard".

## Decision

- The **standard builder is a construction kit**: the language model describes
  an object as a `BuildPlan` of primitive parts (box, cylinder, cone, sphere …)
  with sizes, positions, colours and materials from the shared vocabulary.
- Godot assembles the mesh from the `BuildPlan`; the core stores the plan and
  the object's properties as data.
- A `BuildPlan` is data only: validated against its JSON Schema and size
  limits, never executed as code.
- Image-to-3D (TRELLIS.2) is an optional **comfort tier** only, behind the
  same interface.

## Consequences

- Works on every supported GPU and matches the low-poly toon style.
- Object properties (edible, hard, …) come from the vocabulary, not from the
  mesh; the world truth stays in the core.
- Needs schemas `BuildPlan` and `Invention` (contracts, B4b).
