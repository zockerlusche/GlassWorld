# Contracts

JSON Schemas (Draft 2020-12) for every message between the simulation core (C#) and the
AI service (Python). They are the single source of truth: C# and Python types are generated
from them, never written by hand.

## Layout

| Path | Content |
|---|---|
| `schemas/` | `common`, `decision-request`, `decision-response`, `service-error`, `health-status`, `vocabulary`, `i18n`, `hardware-tiers`, `safety-rules`; B6: `player-message`, `conversation-request`/`-response`, `help-suggestions-request`/`-response`, data files `favor-settings`, `gift-rules`, `message-kinds`, `world-options`; UI data files `ui-tokens`, `ui-frames` (D366); sound data files `sound-map`, `synth-recipes` (B8) |
| `examples/valid/`, `examples/invalid/` | Example messages named `<schema>.<case>.json`; both languages test against them |
| `python/` | Package `glassworld_contracts` with generated pydantic v2 models (checked in) |
| `generate_python.py` | Regenerates the Python models; `--check` fails if they are stale |
| `content-map.json` | Which schema validates which file under `content/` (shared with the C# tests); every JSON file in `content/` must match exactly one entry |
| `tests/` | Contract tests (pytest) |
| `../content/vocabulary/` | Vocabulary data files (actions, properties, needs, moods, traits, expressions, time of day, weather), validated by `schemas/vocabulary.schema.json` |
| `../content/i18n/` | Player-facing texts, one catalog per language (`de.json`, `en.json`), validated by `schemas/i18n.schema.json`; keys `vocab.*`, `ui.*`, `msg.*`, `time.*` (ADR-0007) |

## Rules

- Every message carries `contract_version` (currently `0.1`). Incompatible changes need a new
  version and an ADR.
- Conservative schema subset so that C# generation works: `type`, `properties`, `required`,
  `$ref`, `$defs`, `enum`, `const`, `pattern`, min/max, `anyOf` with `null`, `format: int64`.
  No `$dynamicRef`, `unevaluatedProperties` or `if`/`then`; next to `$ref` only `description`,
  `title` or `$comment` (C# reads the schemas as draft-07, which ignores `$ref` siblings).
  A test enforces this.
- `additionalProperties: false` everywhere. AI output is data and never executed.
- Vocabulary ids are only checked by pattern in the schemas; allowed values live in
  `content/vocabulary/`, so adding an action or property is a data change (B2460).
- Ids are language-neutral English; player-facing text uses translation keys
  `vocab.<kind>.<id>` (ADR-0007). Texts generated at runtime in the player's language are
  `thought.text` and every `GeneratedText` (`locale` + `text`) of the B6 contracts.
- `$defs` names are global for the generated types: the same name in two schemas must have the
  same content (`SharedDefinitionTests`); the schema file name is the title in kebab case
  (`SchemaNamingTests`).

## Decision flow (v0)

1. The core sends a `DecisionRequest` with the inhabitant's state, what it perceives
   (no season name: inhabitants have to learn that seasons exist) and the options it allows.
2. The AI service answers with a `DecisionResponse` naming one `option_id`, or with a
   `ServiceError`. It never makes substitute decisions.
3. The core checks the proposal against the world truth and applies it at a fixed tick
   (ADR-0002, ADR-0005, ADR-0006).

## Player flows (B6)

- **Conversation:** the player's text passes the core's deterministic content check, then the core
  sends a `ConversationRequest` (speaker state, understanding 0..1, relation to the player, recent
  lines). The answer proposes `reply`, `gesture`, `not_understood` (the core opens a question with a
  question mark, W1595/B2487) or `decline`; the core checks any reply text again.
- **Help and gifts (F5238):** after futile effort or a knock, the core sends a
  `HelpSuggestionsRequest` listing what is missing and which gifts it would allow; the service
  proposes 1–3 suggestions (`gift` or `answer`). Gifts are never a fixed list.
- **Mailbox:** `PlayerMessage` is the stored mailbox record (AI text or system key, urgency,
  repeats, status). Values for favour, gift rules, message kinds and new-world options live in
  `content/player/` and are placeholders until Marcel decides.

## Commands

```sh
uv run pytest contracts                        # contract tests
uv run python contracts/generate_python.py     # regenerate Python models after a schema change
```

Known generator gap: pydantic does not enforce `uniqueItems`; JSON Schema validation is the
authority (see `KNOWN_GENERATOR_GAPS` in `tests/test_python_models.py`).
