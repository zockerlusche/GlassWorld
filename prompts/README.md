# Prompts

Every instruction for a language model lives here as a versioned file. Prompts are code: they go
through pull requests and review, and each one has snapshot tests.

## Files

`prompts/<role>/<name>.v<N>.toml`, for example `decider/decide.v1.toml`.

```toml
name = "decider/decide"   # must match the path
version = 1               # must match the file name
description = "..."
system = """..."""        # English instructions
user = """..."""          # English, with $placeholders filled by the service

[settings]                # optional data the service uses while rendering (word bands, limits)
```

- Templates use `string.Template` (`$name`). Nothing in a prompt file is executed.
- Rendering is strict: every placeholder needs a value, otherwise it is an error.
- **Never edit a released version.** Copy it to `.v<N+1>` and change the copy. The version is in
  every model-call log line, so test results and replays stay traceable.

## Rules for content

- Instructions are in English. Only player-facing text (thought bubble, speech) is generated in
  the player's language (`display_locale`).
- The world is calm and friendly, suitable for ages 12+ (B2472). Prompts say this explicitly.
- Inhabitants know only what they have experienced. Prompts must not hand them real-world knowledge
  they have not learned (no season names, no "berries are edible").
- The model only proposes. Its output is constrained by a JSON Schema and checked again by the
  service and the core (ADR-0005).

## Tests

Snapshot tests render example requests and compare them with `ai-service/tests/snapshots/`.
After an intended change:

```sh
GW_UPDATE_SNAPSHOTS=1 uv run pytest ai-service/tests/test_prompts.py
```

Review the snapshot diff like code.
