# ADR-0007: Data file format, schema validation and DE/EN text keys

- Status: accepted (effective with the merge of the PR that adds it)
- Date: 2026-11-07
- Deciders: TL, GW1, GW2

## Context

Values, thresholds, rules, recipes, biomes and vocabulary belong in versioned
data files, not in code (principle "data-driven", B2460). The player chooses
German or English, also internally (B2470). Stored knowledge must stay
language-neutral.

## Decision

- **Format:** JSON (UTF-8, LF) under `content/`, one topic per folder
  (`content/vocabulary/`, `content/objects/`, `content/biomes/`, …). Large
  numeric tables may use CSV with a JSON Schema for the row shape.
- **Schemas:** every data file declares its schema; schemas live in
  `contracts/schemas/` (JSON Schema 2020-12, conservative subset as for the
  contracts). Every file carries a `contract_version`.
- **Validation:** CI validates all of `content/` against the schemas (C# test
  and pytest use the same files). The game validates on load and refuses
  invalid content with a clear log entry.
- **IDs:** language-neutral English snake_case, optionally dot-separated
  namespaces (e.g. `apple`, `edible`, `move_to`); never translated, never
  shown to the player. Text keys (below) are a separate namespace.
- **Texts:** player-facing text is referenced by key only. Translations live in
  `content/i18n/en.json` and `content/i18n/de.json` (flat key → text), and are
  imported into Godot translations at build time. Key scheme:
  `vocab.<kind>.<id>`, `ui.<screen>.<element>`, `msg.<topic>.<id>`.
- **Completeness:** a test checks that every key exists in both languages and
  that every vocabulary id has a display text.

## Consequences

- Adding an object, property or action is a data change, not a code change.
- AI prompts use the English vocabulary descriptions; only the thought bubble
  is generated in the player's language.
- Translators work on two flat files without touching code.
